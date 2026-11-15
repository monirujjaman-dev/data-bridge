using DataBridge.Core.Comparison;
using DataBridge.Core.Connections;
using DataBridge.Core.Schema;
using DataBridge.Core.Synchronization;
using DataBridge.Infrastructure.Providers.SqlServer.Synchronization;

namespace DataBridge.Infrastructure.Tests.Providers.SqlServer;

public sealed class SqlServerSqlGeneratorTests
{
    private const char LineSeparator = (char)0x2028;
    private const char NextLine = (char)0x0085;
    private static readonly IndexOptions _options = new(false, 0, false, true, true, false, "NONE");
    private static readonly DatabaseObjectId _table = new("dbo", "Odd]Name", DatabaseObjectType.Table);
    private readonly SqlServerSqlGenerator _generator = new();

    private static ColumnDefinition Column(string name, string type = "int", int? length = null, bool nullable = false, string? collation = null) =>
        new(name, type, length, false, null, null, nullable, null, null, collation, 1);

    private IReadOnlyList<string> Generate(SchemaChangeDetail detail, SynchronizationOperationType type = SynchronizationOperationType.CreateObject)
    {
        var result = _generator.Generate(new PlannedChange(_table, type, OperationRisk.Low, false, "test", [], 0, detail));
        Assert.True(result.IsSuccess, string.Join("; ", result.Errors.Select(error => error.Message)));
        return result.Value;
    }

    [Fact]
    public void Tables_are_created_with_quoted_names_facets_inline_constraints_and_engine_named_system_constraints()
    {
        var table = new TableDefinition(
            _table,
            [
                Column("Id") with { Identity = new IdentitySpecification(1, 1) },
                Column("Name", "nvarchar", 50, nullable: true, collation: "Latin1_General_CI_AS"),
                Column("Amount", "decimal") with { Precision = 18, Scale = 4, OrdinalPosition = 3 },
            ],
            new PrimaryKeyDefinition("PK__Odd__123", true, "CLUSTERED", [new IndexColumnDefinition("Id", 1, false, false)], _options),
            [],
            [],
            [new CheckConstraintDefinition("CK_Amount", false, "([Amount]>=(0))", false, false)],
            [new DefaultConstraintDefinition("DF_Amount", false, "Amount", "((0))")],
            []);

        var sql = Assert.Single(Generate(new CreateTableDetail(table)));

        Assert.Contains("CREATE TABLE [dbo].[Odd]]Name] (", sql, StringComparison.Ordinal);
        Assert.Contains("[Id] int IDENTITY(1, 1) NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("[Name] nvarchar(50) COLLATE Latin1_General_CI_AS NULL", sql, StringComparison.Ordinal);
        Assert.Contains("[Amount] decimal(18, 4) CONSTRAINT [DF_Amount] DEFAULT ((0)) NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("PRIMARY KEY CLUSTERED ([Id] ASC)", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("PK__Odd__123", sql, StringComparison.Ordinal);
        Assert.Contains("CONSTRAINT [CK_Amount] CHECK ([Amount]>=(0))", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_read_from_a_database_cannot_inject_into_type_or_collation_names()
    {
        var change = new PlannedChange(
            _table, SynchronizationOperationType.CreateObject, OperationRisk.Low, false, "test", [], 0,
            new AddColumnDetail(_table, Column("X", "int", collation: "Latin1; DROP TABLE x"), null));
        var badType = change with { Detail = new AddColumnDetail(_table, Column("X", "int); DROP TABLE x; --"), null) };

        Assert.True(_generator.Generate(change).IsFailure);
        Assert.True(_generator.Generate(badType).IsFailure);
    }

    [Theory]
    [InlineData("CREATE VIEW v AS SELECT 1", "CREATE OR ALTER VIEW v AS SELECT 1")]
    [InlineData("  -- header\n/* block /* nested */ */ create procedure p AS SELECT 1", "-- header\n/* block /* nested */ */ create OR ALTER procedure p AS SELECT 1")]
    [InlineData("CREATE OR ALTER FUNCTION f() RETURNS int AS BEGIN RETURN 1 END", "CREATE OR ALTER FUNCTION f() RETURNS int AS BEGIN RETURN 1 END")]
    public void Altering_a_module_turns_its_create_into_create_or_alter(string definition, string expected) =>
        Assert.Equal(expected, SqlServerSqlGenerator.CreateOrAlter(definition));

    [Fact]
    public void A_definition_that_does_not_start_with_create_is_refused()
    {
        var detail = new CreateModuleDetail(new DatabaseObjectId("dbo", "v", DatabaseObjectType.View), new ModuleDefinition("SELECT 1", false, false, true, true), IsAlter: true);

        Assert.True(_generator.Generate(new PlannedChange(_table, SynchronizationOperationType.AlterObject, OperationRisk.Medium, false, "test", [], 0, detail)).IsFailure);
    }

    [Fact]
    public void Modules_run_their_set_options_in_a_batch_before_the_definition()
    {
        var detail = new CreateModuleDetail(
            new DatabaseObjectId("dbo", "p", DatabaseObjectType.StoredProcedure),
            new ModuleDefinition("CREATE PROCEDURE p AS SELECT 1", false, false, UsesAnsiNulls: false, UsesQuotedIdentifier: true),
            IsAlter: false);

        var batches = Generate(detail);

        Assert.Equal(2, batches.Count);
        Assert.Equal("SET ANSI_NULLS OFF;\nSET QUOTED_IDENTIFIER ON;", batches[0]);
        Assert.Equal("CREATE PROCEDURE [dbo].p AS SELECT 1", batches[1]);
    }

    [Theory]
    [InlineData(DatabaseObjectType.View, "sales", "v", null, "CREATE VIEW v AS SELECT 1", "CREATE VIEW [sales].v AS SELECT 1")]
    [InlineData(DatabaseObjectType.View, "sales", "v", null, "create view [Sales].[v] as select 1", "create view [Sales].[v] as select 1")]
    [InlineData(DatabaseObjectType.StoredProcedure, "dbo", "p", null, "/* c */ CREATE OR ALTER PROC \"p\" AS SELECT 1", "/* c */ CREATE OR ALTER PROC [dbo].\"p\" AS SELECT 1")]
    [InlineData(DatabaseObjectType.ScalarFunction, "dbo", "Odd]Name", null, "CREATE FUNCTION [Odd]]Name]() RETURNS int AS BEGIN RETURN 1 END", "CREATE FUNCTION [dbo].[Odd]]Name]() RETURNS int AS BEGIN RETURN 1 END")]
    [InlineData(DatabaseObjectType.Trigger, "dbo", "tr", "T", "CREATE TRIGGER tr ON T AFTER INSERT AS SELECT 1", "CREATE TRIGGER [dbo].tr ON [dbo].T AFTER INSERT AS SELECT 1")]
    [InlineData(DatabaseObjectType.Trigger, "dbo", "tr", "T", "CREATE TRIGGER dbo . tr ON dbo.T AFTER INSERT AS SELECT 1", "CREATE TRIGGER dbo . tr ON dbo.T AFTER INSERT AS SELECT 1")]
    public void Module_headers_are_qualified_with_the_planned_schema(
        DatabaseObjectType type, string schema, string name, string? parent, string definition, string expected) =>
        Assert.Equal(expected, SqlServerSqlGenerator.QualifyHeader(definition, new DatabaseObjectId(schema, name, type, parent)));

    [Theory]
    [InlineData(DatabaseObjectType.View, null, "CREATE VIEW old_name AS SELECT 1")]
    [InlineData(DatabaseObjectType.View, null, "CREATE VIEW other.v AS SELECT 1")]
    [InlineData(DatabaseObjectType.View, null, "CREATE PROCEDURE v AS SELECT 1")]
    [InlineData(DatabaseObjectType.Trigger, "T", "CREATE TRIGGER v ON Other AFTER INSERT AS SELECT 1")]
    [InlineData(DatabaseObjectType.View, null, "CREATE VIEW dbo.V AS SELECT 1")]
    public void A_module_whose_header_names_another_object_is_refused(DatabaseObjectType type, string? parent, string definition)
    {
        var id = new DatabaseObjectId("dbo", "v", type, parent);
        var detail = new CreateModuleDetail(id, new ModuleDefinition(definition, false, false, true, true), IsAlter: false);

        Assert.True(_generator.Generate(new PlannedChange(id, SynchronizationOperationType.CreateObject, OperationRisk.Low, false, "test", [], 0, detail)).IsFailure);
    }

    [Fact]
    public void Lines_that_ssms_or_sqlcmd_would_act_on_are_script_hazards()
    {
        string[] hazards =
        [
            "SELECT 1\nGO\nDROP TABLE x",
            "SELECT 1\r\n  go 2  \r\nSELECT 2",
            "SELECT 1\nGO -- next",
            "SELECT 1\rGO\rDROP TABLE x",
            $"SELECT 1{LineSeparator}GO{LineSeparator}DROP TABLE x",
            $"SELECT 1{NextLine}GO",
            "SELECT 1\n:connect other",
            "SELECT 1\n  !! del *",
            "SELECT 1\nexit",
            "SELECT N'$(Password)'",
        ];
        string[] safe =
        [
            "SELECT 1\nGOTO done\ndone: SELECT 1",
            "SELECT 1 -- GO\nSELECT 2",
            "SELECT [GO] FROM t",
            "SELECT 'a:b' AS exited",
        ];

        Assert.All(hazards, batch => Assert.NotNull(SqlServerSqlGenerator.ScriptHazard(batch)));
        Assert.All(safe, batch => Assert.Null(SqlServerSqlGenerator.ScriptHazard(batch)));
    }

    [Fact]
    public void A_module_containing_a_go_line_is_still_generated_for_batch_by_batch_execution()
    {
        var id = new DatabaseObjectId("dbo", "p", DatabaseObjectType.StoredProcedure);
        var detail = new CreateModuleDetail(id, new ModuleDefinition("CREATE PROCEDURE p AS\nSELECT 'x\nGO\n'", false, false, true, true), IsAlter: false);

        Assert.True(_generator.Generate(new PlannedChange(id, SynchronizationOperationType.CreateObject, OperationRisk.Low, false, "test", [], 0, detail)).IsSuccess);
    }

    [Fact]
    public void A_script_with_a_hazardous_batch_stops_at_the_top_and_shows_that_batch_commented_out()
    {
        var hostile = new DatabaseObjectId("dbo", "x\nGO\nDROP TABLE dbo.Victim;\n--", DatabaseObjectType.Table);
        var table = new TableDefinition(hostile, [Column("Id")], null, [], [], [], [], []);
        var create = Generate(new CreateTableDetail(table));
        var plan = SynchronizationPlan.Create(
            Guid.NewGuid(), Guid.NewGuid(), SynchronizationDirection.SourceToTarget, Guid.NewGuid(),
            ResolvedDatabaseIdentity.Create(DatabaseProviderType.SqlServer, "db01", "Sales", 5, "SQL Server 2022", null), EnvironmentLabel.None,
            "schema", SynchronizationPlanOptions.Default,
            [new SynchronizationOperation(Guid.NewGuid(), 0, hostile, SynchronizationOperationType.CreateObject, OperationRisk.Low, false, [], "Create table", create, null, [])],
            [], DateTimeOffset.UnixEpoch);

        var lines = _generator.BuildScript(plan).ReplaceLineEndings("\n").Split('\n');

        var firstStatement = lines.First(line => line.Length > 0 && !line.StartsWith("--", StringComparison.Ordinal));
        Assert.StartsWith("RAISERROR(N'This script contains definitions that cannot be shown", firstStatement, StringComparison.Ordinal);
        Assert.Equal("SET NOEXEC ON;", lines[Array.IndexOf(lines, firstStatement) + 1]);
        Assert.DoesNotContain(lines, line => line.StartsWith("DROP TABLE", StringComparison.Ordinal) || line.StartsWith("CREATE TABLE", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("Not runnable from this script", StringComparison.Ordinal));
    }

    [Fact]
    public void Untrusted_foreign_keys_are_added_with_nocheck_and_indexes_keep_their_options()
    {
        var foreignKey = new ForeignKeyDefinition(
            "FK_A_B", false, "dbo", "B", [new ForeignKeyColumnMapping("BId", "Id")], ReferentialAction.Cascade, ReferentialAction.NoAction, false, false, IsNotTrusted: true);
        var index = new IndexDefinition(
            "IX_A", "NONCLUSTERED", true,
            [new IndexColumnDefinition("A", 1, true, false), new IndexColumnDefinition("B", 0, false, true)],
            "([A]>(0))",
            _options with { FillFactor = 80, DataCompression = "PAGE" });

        Assert.Equal(
            "ALTER TABLE [dbo].[Odd]]Name] WITH NOCHECK ADD CONSTRAINT [FK_A_B] FOREIGN KEY ([BId]) REFERENCES [dbo].[B] ([Id]) ON DELETE CASCADE ON UPDATE NO ACTION;",
            Assert.Single(Generate(new AddConstraintDetail(_table, foreignKey, WithNoCheck: true))));
        Assert.Equal(
            "CREATE UNIQUE NONCLUSTERED INDEX [IX_A] ON [dbo].[Odd]]Name] ([A] DESC) INCLUDE ([B]) WHERE ([A]>(0)) WITH (PAD_INDEX = OFF, FILLFACTOR = 80, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON, DATA_COMPRESSION = PAGE);",
            Assert.Single(Generate(new CreateIndexDetail(_table, index))));
    }

    [Fact]
    public void Data_templates_are_parameterized_and_scope_identity_insert_to_the_table()
    {
        var table = new TableDefinition(_table, [Column("Id"), Column("Name", "nvarchar", 10)], null, [], [], [], [], []);
        RowChangeSet Rows(params string[] columns) => new("0123456789abcdef0123456789abcdef", DifferenceStatus.SourceOnly, ["Id"], columns, 3, "hash");

        Assert.Equal(
            "SET IDENTITY_INSERT [dbo].[Odd]]Name] ON;\nINSERT INTO [dbo].[Odd]]Name] ([Id], [Name]) VALUES (@p0, @p1);\nSET IDENTITY_INSERT [dbo].[Odd]]Name] OFF;",
            Assert.Single(Generate(new RowsDetail(table, Rows("Id", "Name"), UsesIdentityInsert: true), SynchronizationOperationType.InsertRows)));
        Assert.Equal(
            "UPDATE [dbo].[Odd]]Name] SET [Name] = @p0 WHERE [Id] = @k0;",
            Assert.Single(Generate(new RowsDetail(table, Rows("Name"), false), SynchronizationOperationType.UpdateRows)));
        Assert.Equal(
            "DELETE FROM [dbo].[Odd]]Name] WHERE [Id] = @k0;",
            Assert.Single(Generate(new RowsDetail(table, Rows("Id"), false), SynchronizationOperationType.DeleteRows)));
    }

    [Fact]
    public void Hostile_database_and_server_names_cannot_add_lines_to_the_script()
    {
        const string hostile = "Sales\nGO\nDROP TABLE dbo.Orders;\n:connect other\n$(Secret)\r--";
        var identity = ResolvedDatabaseIdentity.Create(DatabaseProviderType.SqlServer, hostile, hostile, 5, "SQL Server 2022", null);
        var plan = SynchronizationPlan.Create(
            Guid.NewGuid(), Guid.NewGuid(), SynchronizationDirection.SourceToTarget, Guid.NewGuid(), identity, EnvironmentLabel.None,
            "schema", SynchronizationPlanOptions.Default,
            [new SynchronizationOperation(Guid.NewGuid(), 0, _table, SynchronizationOperationType.CreateObject, OperationRisk.Low, false, [], "Create table", ["CREATE TABLE x (Id int);"], null, [])],
            [], DateTimeOffset.UnixEpoch);

        var script = _generator.BuildScript(plan);
        var lines = script.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);

        var runnable = lines.Where(line => !line.TrimStart().StartsWith("--", StringComparison.Ordinal)).ToArray();
        Assert.All(runnable.Where(line => !string.Equals(line.Trim(), "GO", StringComparison.Ordinal)), line => Assert.Null(SqlServerSqlGenerator.ScriptHazard(line)));
        Assert.Equal(2, runnable.Count(line => line.Trim() == "GO"));
        Assert.DoesNotContain(lines, line => line.StartsWith("DROP TABLE", StringComparison.Ordinal));
        Assert.DoesNotContain("$(", script, StringComparison.Ordinal);
        Assert.Contains(SqlServerSqlGenerator.HexText(hostile, 128), script, StringComparison.Ordinal);
    }

    [Fact]
    public void Hex_text_is_the_utf16_bytes_of_the_value()
    {
        Assert.Equal("CAST(0x41002820 AS nvarchar(10))", SqlServerSqlGenerator.HexText($"A{LineSeparator}", 10));
        Assert.Equal("CAST(0x AS nvarchar(1))", SqlServerSqlGenerator.HexText("", 1));
    }

    [Fact]
    public void The_script_stops_in_the_wrong_database_and_comments_out_data_operations()
    {
        var databaseGuid = Guid.Parse("4f1c1a52-0d3e-4c3c-9a4b-2b7f6d1e8a90");
        var identity = ResolvedDatabaseIdentity.Create(DatabaseProviderType.SqlServer, "db01", "Sales'DB", 5, "SQL Server 2022", null, databaseGuid);
        var operations = new[]
        {
            new SynchronizationOperation(Guid.NewGuid(), 0, _table, SynchronizationOperationType.CreateObject, OperationRisk.Low, false, [], "Create table", ["CREATE TABLE x (Id int);"], null, []),
            new SynchronizationOperation(Guid.NewGuid(), 1, _table, SynchronizationOperationType.InsertRows, OperationRisk.Low, false, [], "Insert 2 rows", ["INSERT INTO x VALUES (@p0);"], 2, ["Triggers fire"]),
        };
        var plan = SynchronizationPlan.Create(
            Guid.NewGuid(), Guid.NewGuid(), SynchronizationDirection.SourceToTarget, Guid.NewGuid(), identity, EnvironmentLabel.Production,
            "schema", SynchronizationPlanOptions.Default, operations, [new PlanIssue(null, $"Not this one{LineSeparator}DROP TABLE x{NextLine}--\rSELECT 1")], DateTimeOffset.UnixEpoch);

        var script = _generator.BuildScript(plan);

        // "Sales'DB" and "db01" as UTF-16LE hex.
        Assert.Contains("DECLARE @Database nvarchar(128) = CAST(0x530061006C0065007300270044004200 AS nvarchar(128)),", script, StringComparison.Ordinal);
        Assert.Contains("@Server nvarchar(256) = CAST(0x6400620030003100 AS nvarchar(256));", script, StringComparison.Ordinal);
        Assert.Contains("IF DB_NAME() COLLATE Latin1_General_BIN2 <> @Database COLLATE Latin1_General_BIN2", script, StringComparison.Ordinal);
        Assert.Contains("<> UPPER(@Server)", script, StringComparison.Ordinal);
        Assert.Contains("16, 1, @Database, @Server);", script, StringComparison.Ordinal);
        Assert.Contains($"service_broker_guid = '{databaseGuid:D}'", script, StringComparison.Ordinal);
        Assert.Contains("-- - Not this one DROP TABLE x -- SELECT 1", script, StringComparison.Ordinal);
        Assert.DoesNotContain(LineSeparator, script);
        Assert.DoesNotContain(NextLine, script);
        Assert.Contains("SET NOEXEC ON;", script, StringComparison.Ordinal);
        Assert.Contains(plan.ContentHash, script, StringComparison.Ordinal);
        Assert.Contains("(Production)", script, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE x (Id int);\nGO", script.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("--    INSERT INTO x VALUES (@p0);", script, StringComparison.Ordinal);
        Assert.DoesNotContain("\nINSERT INTO x", script.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("-- - Not this one", script, StringComparison.Ordinal);
    }
}
