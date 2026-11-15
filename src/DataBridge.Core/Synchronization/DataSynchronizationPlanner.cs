using DataBridge.Core.Comparison;
using DataBridge.Core.Schema;

namespace DataBridge.Core.Synchronization;

/// <summary>The rows recorded for one status of one table during comparison (ADR-0016).</summary>
/// <param name="RowCount">How many rows.</param>
/// <param name="ContentHash">SHA-256 over their keys and fingerprints, in key order.</param>
public sealed record RecordedRows(long RowCount, string ContentHash);

/// <summary>Where one table's differing rows were recorded, by status.</summary>
/// <param name="StoreId">The local store holding the keys and fingerprints.</param>
/// <param name="Rows">Counts and hashes for SourceOnly, TargetOnly and Modified rows.</param>
public sealed record RecordedChangeSet(string StoreId, IReadOnlyDictionary<DifferenceStatus, RecordedRows> Rows);

/// <summary>A compared table selected for data synchronization.</summary>
/// <param name="Summary">The table's comparison summary; its key column names are the source's.</param>
/// <param name="ValueColumns">The compared non-key columns, as named in the source.</param>
/// <param name="ChangeSet">The recorded differing rows.</param>
public sealed record DataTableSelection(
    TableDataComparisonSummary Summary,
    IReadOnlyList<string> ValueColumns,
    RecordedChangeSet ChangeSet);

/// <summary>What the data planner needs.</summary>
/// <param name="Source">The source schema as compared.</param>
/// <param name="Target">The target schema as compared.</param>
/// <param name="Direction">Which side is copied and which is written.</param>
/// <param name="Tables">The tables to synchronize.</param>
/// <param name="Options">Destructive opt-in and the large row count threshold.</param>
/// <param name="TablesWithSchemaChanges">Tables the plan also changes the schema of; their data is not planned.</param>
public sealed record DataPlanningInput(
    DatabaseSchema Source,
    DatabaseSchema Target,
    SynchronizationDirection Direction,
    IReadOnlyList<DataTableSelection> Tables,
    SynchronizationPlanOptions Options,
    IReadOnlyCollection<DatabaseObjectId> TablesWithSchemaChanges);

/// <summary>
/// Plans inserts, updates and deletes for compared tables (FR-PLN-004). Each operation references its rows through a
/// recorded change set instead of holding them, and is ordered by the write target's foreign keys: parents are
/// inserted before children, children deleted before parents (architecture §10). Never touches a database.
/// </summary>
/// <remarks>
/// Rows only in the copied side are inserted, rows only in the write target deleted (destructive, only when allowed),
/// and modified rows updated: every compared non-key column is set, never the key or identity columns. Excluded
/// columns are neither compared nor copied, and inserts are planned only when every write-target column left out
/// can be filled by the engine (nullable, default, identity, computed or rowversion).
/// </remarks>
public static class DataSynchronizationPlanner
{
    internal const int RankInsert = 30;
    internal const int RankUpdate = 31;
    internal const int RankDelete = 32;

    public static void Plan(DataPlanningInput input, PlanDraft draft)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(draft);

        var (copy, write) = input.Direction == SynchronizationDirection.SourceToTarget
            ? (input.Source, input.Target)
            : (input.Target, input.Source);
        var names = input.Source.IdentifiersAreCaseSensitive || input.Target.IdentifiersAreCaseSensitive
            ? StringComparer.Ordinal
            : StringComparer.OrdinalIgnoreCase;
        var writeTables = write.Schemas.SelectMany(schema => schema.Tables).ToArray();
        var copyTables = copy.Schemas.SelectMany(schema => schema.Tables).ToArray();
        TableDefinition? Find(IEnumerable<TableDefinition> tables, DatabaseObjectId id) =>
            tables.FirstOrDefault(table => names.Equals(table.Id.SchemaName, id.SchemaName) && names.Equals(table.Id.ObjectName, id.ObjectName));

        var planned = new List<(TableDefinition Table, SynchronizationOperationType Type, int Index)>();
        foreach (var selection in input.Tables)
        {
            var summary = selection.Summary;
            var id = summary.TableId;
            if (summary.Outcome != TableDataComparisonOutcome.Compared || summary.Key is null)
            {
                draft.Report(id, $"Rows of {id.DisplayName} cannot be synchronized: {summary.Reason ?? "the table was not compared."}");
                continue;
            }

            if (input.TablesWithSchemaChanges.Any(table => names.Equals(table.SchemaName, id.SchemaName) && names.Equals(table.ObjectName, id.ObjectName)))
            {
                draft.Report(id, $"Rows of {id.DisplayName} are not synchronized because the plan also changes its schema. Synchronize the schema first, then compare the data again.");
                continue;
            }

            if (Find(writeTables, id) is not { } writeTable || Find(copyTables, id) is null)
            {
                draft.Report(id, $"Rows of {id.DisplayName} cannot be synchronized: the table is not on both sides.");
                continue;
            }

            var table = new TablePlan(input, draft, names, selection, writeTable, writeTables);
            planned.AddRange(table.Plan());
        }

        OrderByForeignKeys(draft, planned, names);
    }

    /// <summary>Inserts and updates after the parents' inserts; deletes after the children's updates and deletes.</summary>
    private static void OrderByForeignKeys(
        PlanDraft draft,
        List<(TableDefinition Table, SynchronizationOperationType Type, int Index)> planned,
        StringComparer names)
    {
        bool Same(DatabaseObjectId left, string? schema, string? name) =>
            name is not null && names.Equals(left.SchemaName, schema) && names.Equals(left.ObjectName, name);

        foreach (var (child, childType, childIndex) in planned)
        {
            foreach (var foreignKey in child.ForeignKeys)
            {
                if (Same(child.Id, foreignKey.ReferencedSchemaName, foreignKey.ReferencedTableName))
                {
                    continue;
                }

                foreach (var (parent, parentType, parentIndex) in planned.Where(item => Same(item.Table.Id, foreignKey.ReferencedSchemaName, foreignKey.ReferencedTableName)))
                {
                    if (childType is SynchronizationOperationType.InsertRows or SynchronizationOperationType.UpdateRows
                        && parentType == SynchronizationOperationType.InsertRows)
                    {
                        draft.DependOn(childIndex, parentIndex);
                    }

                    if (parentType == SynchronizationOperationType.DeleteRows
                        && childType is SynchronizationOperationType.DeleteRows or SynchronizationOperationType.UpdateRows)
                    {
                        draft.DependOn(parentIndex, childIndex);
                    }
                }
            }
        }
    }

    private sealed class TablePlan(
        DataPlanningInput input,
        PlanDraft draft,
        StringComparer names,
        DataTableSelection selection,
        TableDefinition writeTable,
        TableDefinition[] writeTables)
    {
        private DatabaseObjectId Id => writeTable.Id;

        public IEnumerable<(TableDefinition, SynchronizationOperationType, int)> Plan()
        {
            var summary = selection.Summary;
            var keyColumns = WriteNames(summary.Key!.OrderedColumnNames);
            var valueColumns = WriteNames(selection.ValueColumns);
            if (keyColumns is null || valueColumns is null)
            {
                draft.Report(Id, $"Rows of {Id.DisplayName} cannot be synchronized: the write target's columns no longer match the comparison.");
                yield break;
            }

            var common = CommonWarnings();
            var (insertStatus, deleteStatus) = input.Direction == SynchronizationDirection.SourceToTarget
                ? (DifferenceStatus.SourceOnly, DifferenceStatus.TargetOnly)
                : (DifferenceStatus.TargetOnly, DifferenceStatus.SourceOnly);

            if (Rows(insertStatus) is { RowCount: > 0 } inserts && PlanInsert(inserts, insertStatus, keyColumns, valueColumns, common) is { } insert)
            {
                yield return (writeTable, SynchronizationOperationType.InsertRows, insert);
            }

            if (Rows(DifferenceStatus.Modified) is { RowCount: > 0 } updates && PlanUpdate(updates, keyColumns, valueColumns, common) is { } update)
            {
                yield return (writeTable, SynchronizationOperationType.UpdateRows, update);
            }

            if (Rows(deleteStatus) is { RowCount: > 0 } deletes && PlanDelete(deletes, deleteStatus, keyColumns, common) is { } delete)
            {
                yield return (writeTable, SynchronizationOperationType.DeleteRows, delete);
            }
        }

        private RecordedRows? Rows(DifferenceStatus status) => selection.ChangeSet.Rows.GetValueOrDefault(status);

        private int? PlanInsert(RecordedRows rows, DifferenceStatus status, string[] keyColumns, string[] valueColumns, List<string> common)
        {
            var columns = keyColumns.Concat(valueColumns).ToArray();
            var missing = writeTable.Columns
                .Where(column => !columns.Contains(column.Name, names) && !CanBeOmitted(column))
                .Select(column => column.Name)
                .ToArray();
            if (missing.Length > 0)
            {
                draft.Report(Id, $"Rows cannot be inserted into {Id.DisplayName}: {string.Join(", ", missing)} would have no value because they were not compared. Compare them, or give them defaults.");
                return null;
            }

            var identity = writeTable.Columns.Any(column => column.Identity is not null && columns.Contains(column.Name, names));
            var warnings = new List<string>(common);
            if (identity)
            {
                warnings.Add("Rows keep their identity values: IDENTITY_INSERT is turned on for this table only while it is inserted into.");
            }

            if (writeTable.ForeignKeys.Any(foreignKey => SelfReference(foreignKey)))
            {
                warnings.Add("Rows in this table reference other rows in it; inserting a row before the row it references fails.");
            }

            var risk = rows.RowCount > input.Options.LargeRowCountThreshold ? OperationRisk.High : OperationRisk.Low;
            return Add(
                SynchronizationOperationType.InsertRows, risk, false, $"Insert {rows.RowCount:N0} rows into {Id.DisplayName}",
                warnings, DataSynchronizationPlanner.RankInsert, status, keyColumns, columns, rows, identity);
        }

        private int? PlanUpdate(RecordedRows rows, string[] keyColumns, string[] valueColumns, List<string> common)
        {
            var identityColumns = writeTable.Columns.Where(column => column.Identity is not null).Select(column => column.Name).ToArray();
            var columns = valueColumns.Where(column => !identityColumns.Contains(column, names)).ToArray();
            if (columns.Length == 0)
            {
                draft.Report(Id, $"Changed rows of {Id.DisplayName} cannot be updated: it has no compared columns outside the key and identity columns.");
                return null;
            }

            var warnings = new List<string>(common)
            {
                "Every compared column of a changed row is set to the copied value; key columns are never changed.",
            };
            if (valueColumns.Length != columns.Length)
            {
                warnings.Add("Identity columns cannot be updated; differences in them remain.");
            }

            var risk = rows.RowCount > input.Options.LargeRowCountThreshold ? OperationRisk.High : OperationRisk.Medium;
            return Add(
                SynchronizationOperationType.UpdateRows, risk, false, $"Update {rows.RowCount:N0} changed rows in {Id.DisplayName}",
                warnings, DataSynchronizationPlanner.RankUpdate, DifferenceStatus.Modified, keyColumns, columns, rows, false);
        }

        private int? PlanDelete(RecordedRows rows, DifferenceStatus status, string[] keyColumns, List<string> common)
        {
            if (!input.Options.AllowDestructiveOperations)
            {
                draft.Report(Id, $"{rows.RowCount:N0} rows only in the write target's {Id.DisplayName} are not deleted: destructive operations are not allowed. Allow them to include the delete.");
                return null;
            }

            var warnings = new List<string>(common);
            foreach (var (other, foreignKey) in writeTables
                .SelectMany(other => other.ForeignKeys.Select(foreignKey => (Table: other, ForeignKey: foreignKey)))
                .Where(pair => pair.ForeignKey.OnDelete != ReferentialAction.NoAction && References(pair.ForeignKey)))
            {
                warnings.Add($"Foreign key {foreignKey.Name} on {other.Id.DisplayName} is ON DELETE {Describe(foreignKey.OnDelete)}: deleting rows also changes rows there.");
            }

            return Add(
                SynchronizationOperationType.DeleteRows, OperationRisk.Critical, true, $"Delete {rows.RowCount:N0} rows from {Id.DisplayName}",
                warnings, DataSynchronizationPlanner.RankDelete, status, keyColumns, keyColumns, rows, false);
        }

        private int Add(
            SynchronizationOperationType type,
            OperationRisk risk,
            bool destructive,
            string description,
            List<string> warnings,
            int rank,
            DifferenceStatus status,
            string[] keyColumns,
            string[] columns,
            RecordedRows rows,
            bool identityInsert)
        {
            var changeSet = new RowChangeSet(selection.ChangeSet.StoreId, status, keyColumns, columns, rows.RowCount, rows.ContentHash);
            return draft.Add(new PlannedChange(
                Id, type, risk, destructive, description, warnings, rank, new RowsDetail(writeTable, changeSet, identityInsert), rows.RowCount));
        }

        private List<string> CommonWarnings()
        {
            var warnings = new List<string>
            {
                "Before each row is written, both databases are read again by key; a row that changed since the comparison is skipped and reported, not written.",
            };

            var summary = selection.Summary;
            if (summary.ExcludedColumns.Count > 0)
            {
                warnings.Add($"Not compared, so not copied: {string.Join(", ", summary.ExcludedColumns.Select(column => column.ColumnName))}.");
            }

            if (summary.SourceConsistency == ReadConsistencyMode.ReadCommitted || summary.TargetConsistency == ReadConsistencyMode.ReadCommitted)
            {
                warnings.Add("The table was not read from a snapshot, so the comparison may mix rows from different moments.");
            }

            var triggers = WriteTriggers();
            if (triggers.Length > 0)
            {
                warnings.Add($"Triggers on the table fire for every row written: {string.Join(", ", triggers)}. DataBridge never disables them.");
            }

            return warnings;
        }

        private string[] WriteTriggers()
        {
            var schema = input.Direction == SynchronizationDirection.SourceToTarget ? input.Target : input.Source;
            return [.. schema.Schemas
                .SelectMany(item => item.ProgrammableObjects)
                .Where(module => module.Trigger is { IsDisabled: false }
                    && names.Equals(module.Id.SchemaName, Id.SchemaName)
                    && names.Equals(module.Id.ParentObjectName, Id.ObjectName))
                .Select(module => module.Id.ObjectName)];
        }

        /// <summary>The write target's names for source column names, or <see langword="null"/> when one is missing.</summary>
        private string[]? WriteNames(IReadOnlyList<string> sourceNames)
        {
            var result = new string[sourceNames.Count];
            for (var index = 0; index < sourceNames.Count; index++)
            {
                if (writeTable.Columns.FirstOrDefault(column => names.Equals(column.Name, sourceNames[index])) is not { } column)
                {
                    return null;
                }

                result[index] = column.Name;
            }

            return result;
        }

        private bool CanBeOmitted(ColumnDefinition column) =>
            column.IsNullable
            || column.Identity is not null
            || column.Computed is not null
            || string.Equals(column.ProviderBaseTypeName ?? column.SqlTypeName, "timestamp", StringComparison.OrdinalIgnoreCase)
            || string.Equals(column.ProviderBaseTypeName ?? column.SqlTypeName, "rowversion", StringComparison.OrdinalIgnoreCase)
            || writeTable.DefaultConstraints.Any(item => names.Equals(item.ColumnName, column.Name));

        private bool References(ForeignKeyDefinition foreignKey) =>
            names.Equals(foreignKey.ReferencedSchemaName, Id.SchemaName) && names.Equals(foreignKey.ReferencedTableName, Id.ObjectName);

        private bool SelfReference(ForeignKeyDefinition foreignKey) => References(foreignKey);

        private static string Describe(ReferentialAction action) => action switch
        {
            ReferentialAction.Cascade => "CASCADE",
            ReferentialAction.SetNull => "SET NULL",
            ReferentialAction.SetDefault => "SET DEFAULT",
            _ => "NO ACTION",
        };
    }
}
