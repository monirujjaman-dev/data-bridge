namespace DataBridge.Infrastructure.Providers.SqlServer.Schema;

/// <summary>
/// The catalog batch the schema reader runs: one round trip, one result set per catalog area, in the order of
/// <see cref="ResultSet"/>. Only <c>SELECT</c>s against catalog views and metadata functions, so it never modifies
/// the database, and only columns available in SQL Server 2017 (ADR-0010).
/// </summary>
internal static class SqlServerCatalogQuery
{
    /// <summary>The result sets of <see cref="Text"/>, in order.</summary>
    public enum ResultSet
    {
        Database,
        Schemas,
        Objects,
        Tables,
        Columns,
        Indexes,
        IndexColumns,
        ForeignKeys,
        ForeignKeyColumns,
        CheckConstraints,
        DefaultConstraints,
        Modules,
        ModuleReferences,
        Triggers,
        Sequences,
        OtherUnsupported,
    }

    public const string Text = """
        SET NOCOUNT ON;

        -- Database: whether names compare case-insensitively (ComparisonStyle bit 1) and whether every object is visible.
        SELECT
            CAST(ISNULL(COLLATIONPROPERTY(CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation')), 'ComparisonStyle'), 0) AS int),
            CAST(ISNULL(HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW DEFINITION'), 0) AS int);

        -- Schemas, without sys, INFORMATION_SCHEMA and the empty fixed-role schemas every database has.
        SELECT s.schema_id, s.name
        FROM sys.schemas AS s
        WHERE s.schema_id NOT IN (3, 4)
          AND (s.schema_id NOT BETWEEN 16384 AND 16399
               OR EXISTS (SELECT 1 FROM sys.objects AS o WHERE o.schema_id = s.schema_id AND o.is_ms_shipped = 0));

        -- Schema-scoped user objects and DML triggers; constraints are read with their tables.
        SELECT o.object_id, o.schema_id, o.name, RTRIM(o.type), o.type_desc, o.parent_object_id
        FROM sys.objects AS o
        WHERE o.is_ms_shipped = 0
          AND (o.type NOT IN ('PK', 'UQ', 'F', 'C', 'D', 'EC', 'IT', 'S', 'TT')
               OR (o.type = 'D' AND o.parent_object_id = 0));

        -- Table kinds that are not compared, and partitioning.
        SELECT t.object_id, t.temporal_type, t.is_memory_optimized, t.is_node, t.is_edge, t.is_filetable,
            CAST(CASE WHEN EXISTS (
                SELECT 1 FROM sys.indexes AS i
                JOIN sys.data_spaces AS ds ON ds.data_space_id = i.data_space_id
                WHERE i.object_id = t.object_id AND ds.type = 'PS') THEN 1 ELSE 0 END AS bit),
            (SELECT p.data_compression_desc FROM sys.partitions AS p
             WHERE p.object_id = t.object_id AND p.index_id = 0 AND p.partition_number = 1)
        FROM sys.tables AS t
        WHERE t.is_ms_shipped = 0;

        -- Columns of user tables, with identity and computed specifications. Values are converted from sql_variant
        -- to text because identity seeds can have 38 digits.
        SELECT c.object_id, c.name, ty.name, SCHEMA_NAME(ty.schema_id), ty.is_user_defined, TYPE_NAME(c.system_type_id),
            c.max_length, c.precision, c.scale, c.is_nullable, c.collation_name,
            CONVERT(varchar(40), ic.seed_value), CONVERT(varchar(40), ic.increment_value),
            cc.definition, cc.is_persisted, c.is_computed,
            c.is_rowguidcol, c.is_sparse, c.is_column_set, c.is_filestream,
            CASE WHEN xsc.xml_collection_id IS NULL THEN NULL ELSE SCHEMA_NAME(xsc.schema_id) + N'.' + xsc.name END,
            mc.masking_function, c.encryption_type_desc,
            CASE WHEN c.generated_always_type = 0 THEN NULL ELSE c.generated_always_type_desc END,
            c.is_hidden, ic.is_not_for_replication
        FROM sys.columns AS c
        JOIN sys.objects AS o ON o.object_id = c.object_id
        JOIN sys.types AS ty ON ty.user_type_id = c.user_type_id
        LEFT JOIN sys.identity_columns AS ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
        LEFT JOIN sys.computed_columns AS cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
        LEFT JOIN sys.xml_schema_collections AS xsc ON xsc.xml_collection_id = c.xml_collection_id AND c.xml_collection_id > 0
        LEFT JOIN sys.masked_columns AS mc ON mc.object_id = c.object_id AND mc.column_id = c.column_id
        WHERE o.type = 'U' AND o.is_ms_shipped = 0
        ORDER BY c.object_id, c.column_id;

        -- Indexes of user tables and views (not heaps or hypothetical indexes), with their key constraints.
        SELECT i.object_id, i.index_id, i.name, i.type, i.type_desc, i.is_unique, i.is_primary_key, i.is_unique_constraint,
            CAST(ISNULL(kc.is_system_named, 0) AS bit),
            i.ignore_dup_key, i.fill_factor, i.is_padded, i.allow_row_locks, i.allow_page_locks, i.is_disabled,
            i.filter_definition,
            (SELECT p.data_compression_desc FROM sys.partitions AS p
             WHERE p.object_id = i.object_id AND p.index_id = i.index_id AND p.partition_number = 1)
        FROM sys.indexes AS i
        JOIN sys.objects AS o ON o.object_id = i.object_id
        LEFT JOIN sys.key_constraints AS kc ON kc.parent_object_id = i.object_id AND kc.unique_index_id = i.index_id
            AND (i.is_primary_key = 1 OR i.is_unique_constraint = 1)
        WHERE o.type IN ('U', 'V') AND o.is_ms_shipped = 0 AND i.type > 0 AND i.is_hypothetical = 0;

        -- Index columns: key columns in key order, then included ones. Partitioning columns the engine adds are skipped.
        SELECT ic.object_id, ic.index_id, c.name, ic.key_ordinal, ic.is_descending_key, ic.is_included_column
        FROM sys.index_columns AS ic
        JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        JOIN sys.objects AS o ON o.object_id = ic.object_id
        WHERE o.type IN ('U', 'V') AND o.is_ms_shipped = 0
          AND NOT (ic.key_ordinal = 0 AND ic.is_included_column = 0 AND ic.partition_ordinal > 0)
        ORDER BY ic.object_id, ic.index_id, CASE WHEN ic.key_ordinal = 0 THEN 1 ELSE 0 END, ic.key_ordinal, ic.index_column_id;

        SELECT fk.object_id, fk.parent_object_id, fk.name, fk.is_system_named, SCHEMA_NAME(ro.schema_id), ro.name,
            fk.delete_referential_action, fk.update_referential_action, fk.is_disabled, fk.is_not_for_replication,
            fk.is_not_trusted
        FROM sys.foreign_keys AS fk
        -- LEFT JOIN: a referenced table the login cannot see must not make the foreign key disappear.
        LEFT JOIN sys.objects AS ro ON ro.object_id = fk.referenced_object_id
        WHERE fk.is_ms_shipped = 0;

        SELECT fkc.constraint_object_id, pc.name, rc.name
        FROM sys.foreign_key_columns AS fkc
        JOIN sys.columns AS pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
        JOIN sys.columns AS rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
        ORDER BY fkc.constraint_object_id, fkc.constraint_column_id;

        SELECT cc.parent_object_id, cc.name, cc.is_system_named, cc.definition, cc.is_disabled, cc.is_not_for_replication,
            cc.is_not_trusted
        FROM sys.check_constraints AS cc
        WHERE cc.is_ms_shipped = 0;

        SELECT dc.parent_object_id, dc.name, dc.is_system_named, c.name, dc.definition
        FROM sys.default_constraints AS dc
        JOIN sys.columns AS c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id
        WHERE dc.is_ms_shipped = 0;

        -- Module definitions: NULL when encrypted or not visible to this login.
        SELECT o.object_id, m.definition,
            CAST(ISNULL(OBJECTPROPERTY(o.object_id, 'IsEncrypted'), 0) AS bit),
            CAST(ISNULL(m.is_schema_bound, 0) AS bit),
            CAST(ISNULL(m.uses_ansi_nulls, 1) AS bit),
            CAST(ISNULL(m.uses_quoted_identifier, 1) AS bit)
        FROM sys.objects AS o
        LEFT JOIN sys.sql_modules AS m ON m.object_id = o.object_id
        WHERE o.is_ms_shipped = 0 AND o.type IN ('V', 'P', 'FN', 'IF', 'TF', 'TR');

        -- Objects each module references in this database, as resolved by the engine (for synchronization order).
        SELECT DISTINCT d.referencing_id, SCHEMA_NAME(ro.schema_id), ro.name
        FROM sys.sql_expression_dependencies AS d
        JOIN sys.objects AS ro ON ro.object_id = d.referenced_id
        WHERE d.referencing_class = 1 AND d.referenced_class = 1 AND d.referenced_id IS NOT NULL
          AND d.referenced_server_name IS NULL AND d.referenced_database_name IS NULL
          AND d.referenced_id <> d.referencing_id AND ro.is_ms_shipped = 0;

        SELECT t.object_id, t.is_disabled, t.is_instead_of_trigger
        FROM sys.triggers AS t
        WHERE t.parent_class = 1 AND t.is_ms_shipped = 0;

        SELECT s.object_id, TYPE_NAME(s.user_type_id),
            CONVERT(varchar(40), s.start_value), CONVERT(varchar(40), s.increment),
            CONVERT(varchar(40), s.minimum_value), CONVERT(varchar(40), s.maximum_value),
            s.is_cycling, s.is_cached, s.cache_size
        FROM sys.sequences AS s
        WHERE s.is_ms_shipped = 0;

        -- Objects outside sys.objects that are not compared (ADR-0012).
        SELECT SCHEMA_NAME(t.schema_id), t.name,
            CASE WHEN t.is_table_type = 1 THEN 'TABLE_TYPE' WHEN t.is_assembly_type = 1 THEN 'CLR_TYPE' ELSE 'ALIAS_TYPE' END,
            'User-defined types are not compared; columns that use them are compared by type name.'
        FROM sys.types AS t WHERE t.is_user_defined = 1
        UNION ALL
        SELECT N'', pf.name, 'PARTITION_FUNCTION', 'Partition functions are not compared.' FROM sys.partition_functions AS pf
        UNION ALL
        SELECT N'', ps.name, 'PARTITION_SCHEME', 'Partition schemes are not compared.' FROM sys.partition_schemes AS ps
        UNION ALL
        SELECT SCHEMA_NAME(x.schema_id), x.name, 'XML_SCHEMA_COLLECTION', 'XML schema collections are not compared.'
        FROM sys.xml_schema_collections AS x WHERE x.xml_collection_id > 1
        UNION ALL
        SELECT N'', fc.name, 'FULLTEXT_CATALOG', 'Full-text catalogs are not compared.' FROM sys.fulltext_catalogs AS fc
        UNION ALL
        SELECT SCHEMA_NAME(o.schema_id), o.name, 'FULLTEXT_INDEX', 'Full-text indexes are not compared.'
        FROM sys.fulltext_indexes AS fi JOIN sys.objects AS o ON o.object_id = fi.object_id
        UNION ALL
        SELECT N'', a.name, 'ASSEMBLY', 'CLR assemblies are not compared.' FROM sys.assemblies AS a WHERE a.is_user_defined = 1
        UNION ALL
        SELECT N'', t.name, 'DATABASE_DDL_TRIGGER', 'Database DDL triggers are not compared.'
        FROM sys.triggers AS t WHERE t.parent_class = 0 AND t.is_ms_shipped = 0
        UNION ALL
        SELECT N'', p.name, p.type_desc, 'Users, roles and permissions are not compared.'
        FROM sys.database_principals AS p
        WHERE p.principal_id > 4 AND p.is_fixed_role = 0 AND p.type IN ('S', 'U', 'G', 'R', 'A', 'E', 'X', 'C', 'K')
        ORDER BY 1, 2;
        """;
}
