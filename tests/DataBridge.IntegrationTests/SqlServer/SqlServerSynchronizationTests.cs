using DataBridge.Application.Abstractions;
using DataBridge.Core.Comparison;
using DataBridge.Core.Connections;
using DataBridge.Core.Schema;
using DataBridge.Core.Synchronization;
using DataBridge.Infrastructure.Providers.SqlServer.Schema;
using DataBridge.Infrastructure.Providers.SqlServer.Synchronization;
using Microsoft.Data.SqlClient;

namespace DataBridge.IntegrationTests.SqlServer;

/// <summary>
/// Phase 5 against a real SQL Server: generated schema SQL runs and makes the write target compare equal, plans are
/// built without touching either database, validation finds rows that would make operations fail and missing
/// permissions without changing anything, and data templates run with parameters.
/// </summary>
public sealed class SqlServerSynchronizationTests(SqlServerFixture server)
{
    private const string Baseline = """
        CREATE SCHEMA sales;
        GO
        CREATE TABLE sales.Customers (
            Id int IDENTITY(1, 1) NOT NULL PRIMARY KEY,
            Name nvarchar(100) NOT NULL,
            Email varchar(200) NULL UNIQUE,
            Notes nvarchar(max) NULL,
            CreatedAt datetime2(3) NOT NULL DEFAULT (sysutcdatetime()),
            Balance decimal(18, 4) NOT NULL DEFAULT (0) CHECK (Balance >= 0));
        CREATE TABLE sales.Orders (
            OrderId bigint NOT NULL,
            LineNumber smallint NOT NULL,
            CustomerId int NOT NULL,
            Quantity int NOT NULL,
            Price decimal(10, 2) NOT NULL,
            Total AS (Quantity * Price) PERSISTED,
            Status char(1) COLLATE Latin1_General_BIN2 NOT NULL CONSTRAINT DF_Orders_Status DEFAULT ('N'),
            CONSTRAINT PK_Orders PRIMARY KEY CLUSTERED (OrderId ASC, LineNumber DESC),
            CONSTRAINT FK_Orders_Customers FOREIGN KEY (CustomerId) REFERENCES sales.Customers (Id) ON DELETE CASCADE,
            CONSTRAINT CK_Orders_Quantity CHECK (Quantity > 0));
        CREATE NONCLUSTERED INDEX IX_Orders_Customer ON sales.Orders (CustomerId) INCLUDE (Quantity, Price) WHERE Status <> 'X';
        CREATE SEQUENCE sales.OrderNumbers AS bigint START WITH 1000 INCREMENT BY 1 CACHE 50;
        GO
        CREATE VIEW sales.OrderTotals WITH SCHEMABINDING AS
            SELECT CustomerId, COUNT_BIG(*) AS Orders, SUM(Quantity) AS Quantity
            FROM sales.Orders
            GROUP BY CustomerId;
        GO
        CREATE UNIQUE CLUSTERED INDEX IX_OrderTotals ON sales.OrderTotals (CustomerId);
        GO
        CREATE PROCEDURE sales.GetOrders @CustomerId int AS
        BEGIN
            SELECT OrderId, 'a  b' AS Spaced FROM sales.Orders WHERE CustomerId = @CustomerId;
        END;
        GO
        CREATE FUNCTION sales.OrderCount (@CustomerId int) RETURNS int AS
        BEGIN
            RETURN (SELECT COUNT(*) FROM sales.Orders WHERE CustomerId = @CustomerId);
        END;
        GO
        CREATE FUNCTION sales.OrdersOf (@CustomerId int) RETURNS TABLE AS
            RETURN SELECT OrderId FROM sales.Orders WHERE CustomerId = @CustomerId;
        GO
        CREATE TRIGGER sales.Orders_Audit ON sales.Orders AFTER INSERT AS SET NOCOUNT ON;
        """;

    private readonly SqlServerSchemaReader _reader = new(TimeProvider.System);
    private readonly SqlServerSqlGenerator _generator = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_generated_plan_builds_every_supported_object_in_an_empty_database()
    {
        server.RequireServer();
        var source = await CreateDatabaseAsync(Baseline);
        var target = await CreateDatabaseAsync("");

        var (plan, _) = await PlanAsync(source, target, allowDestructive: false);
        Assert.True(plan.NotIncluded.Count == 0, Issues(plan));
        await ApplyAsync(target, plan);

        var after = await CompareAsync(source, target);
        Assert.False(after.HasDifferences, Describe(after));
    }

    [Fact]
    public async Task Tables_are_created_after_the_functions_and_sequences_their_columns_checks_and_defaults_use()
    {
        server.RequireServer();
        var source = await CreateDatabaseAsync("""
            CREATE FUNCTION dbo.Twice (@Value int) RETURNS int WITH SCHEMABINDING AS BEGIN RETURN @Value * 2 END;
            GO
            CREATE FUNCTION dbo.IsPositive (@Value int) RETURNS bit WITH SCHEMABINDING AS BEGIN RETURN CASE WHEN @Value > 0 THEN 1 ELSE 0 END END;
            GO
            CREATE SEQUENCE dbo.Numbers AS int START WITH 1;
            CREATE TABLE dbo.Ledger (
                Id int NOT NULL CONSTRAINT DF_Ledger_Id DEFAULT (NEXT VALUE FOR dbo.Numbers),
                Amount int NOT NULL CONSTRAINT CK_Ledger_Amount CHECK (dbo.IsPositive(Amount) = 1),
                Doubled AS (dbo.Twice(Amount)));
            """);
        var target = await CreateDatabaseAsync("");

        var ledger = (await ReadAsync(source)).Schemas.SelectMany(item => item.Tables).Single(table => table.Id.ObjectName == "Ledger");
        Assert.Equal(["IsPositive", "Numbers", "Twice"], ledger.EffectiveExpressionReferences.Select(reference => reference.ObjectName));

        var (plan, _) = await PlanAsync(source, target, allowDestructive: false);
        Assert.True(plan.NotIncluded.Count == 0, Issues(plan));
        var createTable = plan.Operations.Single(operation => operation.ObjectId.ObjectType == DatabaseObjectType.Table);
        Assert.All(
            plan.Operations.Where(operation => operation.ObjectId.ObjectType is DatabaseObjectType.ScalarFunction or DatabaseObjectType.Sequence),
            prerequisite => Assert.True(prerequisite.Order < createTable.Order, prerequisite.Description));
        await ApplyAsync(target, plan);

        var after = await CompareAsync(source, target);
        Assert.False(after.HasDifferences, Describe(after));
    }

    [Fact]
    public async Task A_generated_plan_alters_and_drops_until_the_schemas_match()
    {
        server.RequireServer();
        var source = await CreateDatabaseAsync(Baseline + """

            GO
            ALTER TABLE sales.Orders ADD Note nvarchar(20) NULL;
            """);
        var target = await CreateDatabaseAsync(Baseline + """

            GO
            ALTER TABLE sales.Customers ALTER COLUMN Name nvarchar(50) NOT NULL;
            ALTER TABLE sales.Customers ADD Obsolete int NULL;
            ALTER TABLE sales.Orders DROP CONSTRAINT CK_Orders_Quantity;
            ALTER TABLE sales.Orders ADD CONSTRAINT CK_Orders_Quantity CHECK (Quantity >= 0);
            ALTER SEQUENCE sales.OrderNumbers INCREMENT BY 2;
            CREATE TABLE dbo.Extra (Id int NOT NULL);
            GO
            ALTER PROCEDURE sales.GetOrders @CustomerId int AS SELECT 1 AS Changed;
            """);

        var (plan, _) = await PlanAsync(source, target, allowDestructive: true);
        Assert.True(plan.NotIncluded.Count == 0, Issues(plan));
        Assert.Contains(plan.Operations, operation => operation.Type == SynchronizationOperationType.DropObject && operation.ObjectId.ObjectName == "Extra");
        await ApplyAsync(target, plan);

        var after = await CompareAsync(source, target);
        Assert.False(after.HasDifferences, Describe(after));
    }

    [Fact]
    public async Task Planning_reads_nothing_and_validation_changes_nothing()
    {
        server.RequireServer();
        var source = await CreateDatabaseAsync("CREATE TABLE dbo.T (Id int NOT NULL PRIMARY KEY, Code int NOT NULL, Extra int NOT NULL); CREATE UNIQUE INDEX IX_T_Code ON dbo.T (Code);");
        var target = await CreateDatabaseAsync("CREATE TABLE dbo.T (Id int NOT NULL PRIMARY KEY, Code int NULL); INSERT dbo.T VALUES (1, NULL), (2, 5), (3, 5);");
        var before = SchemaFingerprint.Compute(await ReadAsync(target));

        var (plan, assembled) = await PlanAsync(source, target, allowDestructive: false);
        var findings = await new SqlServerTargetInspector().InspectAsync(server.Request(target), assembled, Token);

        Assert.True(findings.IsSuccess);
        var blocking = findings.Value.Where(finding => finding.Severity == ValidationSeverity.Blocking).Select(finding => finding.Message).ToArray();
        Assert.Contains(blocking, message => message.Contains("has NULLs", StringComparison.Ordinal));
        Assert.Contains(blocking, message => message.Contains("adding NOT NULL column Extra", StringComparison.Ordinal));
        Assert.Contains(blocking, message => message.Contains("duplicate values", StringComparison.Ordinal));
        Assert.Equal(3, blocking.Length);
        Assert.Equal(before, SchemaFingerprint.Compute(await ReadAsync(target)));
        Assert.Equal("3", await server.QueryRowAsync(target, "SELECT COUNT(*) FROM dbo.T;", Token));
        Assert.NotEmpty(plan.Operations);
    }

    [Fact]
    public async Task Validation_reports_permissions_the_login_lacks()
    {
        server.RequireServer();
        var source = await CreateDatabaseAsync("CREATE TABLE dbo.NewTable (Id int NOT NULL);");
        var target = await CreateDatabaseAsync("");
        var login = $"reader_{Guid.NewGuid():N}"[..20];
        var password = $"Pw!{Guid.NewGuid():N}aA1";
        await server.ExecuteAsync($"CREATE LOGIN [{login}] WITH PASSWORD = '{password}', CHECK_POLICY = OFF;", Token);
        await server.ExecuteInAsync(target, $"CREATE USER [{login}] FOR LOGIN [{login}]; ALTER ROLE db_datareader ADD MEMBER [{login}];", Token);

        var (_, assembled) = await PlanAsync(source, target, allowDestructive: false);
        var findings = await new SqlServerTargetInspector().InspectAsync(server.Request(target, login, password), assembled, Token);

        Assert.True(findings.IsSuccess);
        Assert.Contains(findings.Value, finding => finding.Severity == ValidationSeverity.Blocking && finding.Message.Contains("CREATE TABLE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Data_templates_run_with_parameters_and_keep_identity_values()
    {
        server.RequireServer();
        var database = await CreateDatabaseAsync("CREATE TABLE dbo.People (Id int IDENTITY(1, 1) NOT NULL PRIMARY KEY, [Na]]me] nvarchar(50) NULL);");
        var table = (await ReadAsync(database)).Schemas.Single(schema => schema.Name == "dbo").Tables.Single();
        string[] keys = ["Id"];
        string[] all = ["Id", "Na]me"];

        await RunAsync(database, Template(SynchronizationOperationType.InsertRows, table, keys, all, identity: true), ("@p0", 42), ("@p1", "Ann"));
        await RunAsync(database, Template(SynchronizationOperationType.UpdateRows, table, keys, ["Na]me"], identity: false), ("@p0", "Bea"), ("@k0", 42));
        Assert.Equal("42|Bea", await server.QueryRowAsync(database, "SELECT Id, [Na]]me] FROM dbo.People;", Token));

        await RunAsync(database, Template(SynchronizationOperationType.DeleteRows, table, keys, keys, identity: false), ("@k0", 42));
        Assert.Equal("0", await server.QueryRowAsync(database, "SELECT COUNT(*) FROM dbo.People;", Token));
    }

    private string Template(SynchronizationOperationType type, TableDefinition table, string[] keys, string[] columns, bool identity)
    {
        var rows = new RowChangeSet("0123456789abcdef0123456789abcdef", DifferenceStatus.SourceOnly, keys, columns, 1, "hash");
        var change = new PlannedChange(table.Id, type, OperationRisk.Low, false, "test", [], 0, new RowsDetail(table, rows, identity));
        return Assert.Single(_generator.Generate(change).Value);
    }

    private async Task RunAsync(string database, string sql, params (string Name, object Value)[] parameters)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = server.Server,
            InitialCatalog = database,
            UserID = "sa",
            Password = server.AdminPassword,
            TrustServerCertificate = true,
        };
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        Assert.True(await command.ExecuteNonQueryAsync(Token) >= 1, "The template changed no row.");
    }

    [Fact]
    public async Task The_exported_script_runs_only_in_its_write_target_and_not_at_all_with_a_hazardous_operation()
    {
        server.RequireServer();
        var target = $"DataBridgeTest_{Guid.NewGuid():N}_O'Brien_Ünï";
        await server.CreateNamedDatabaseAsync(target, null, Token);
        try
        {
            var other = await CreateDatabaseAsync("");
            var identity = await IdentityAsync(target);
            var safe = ScriptFor(identity, "CREATE TABLE dbo.Probe (Id int);");
            var hazardous = ScriptFor(identity, "CREATE TABLE dbo.Probe (Id int);", "CREATE TABLE dbo.Other (Id int CONSTRAINT DF_Other DEFAULT (0x0A) /*\nGO\n*/);");

            Assert.Contains(await RunLikeSsmsAsync(other, safe), message => message.Contains("This script must run in database", StringComparison.Ordinal));
            Assert.Equal(0, await TableCountAsync(other));

            Assert.Contains(await RunLikeSsmsAsync(target, hazardous), message => message.Contains("cannot be shown as runnable SQL", StringComparison.Ordinal));
            Assert.Equal(0, await TableCountAsync(target));

            Assert.Empty(await RunLikeSsmsAsync(target, safe));
            Assert.Equal(1, await TableCountAsync(target, "Probe"));
        }
        finally
        {
            await server.DropDatabaseAsync(target, Token);
        }
    }

    [Fact]
    public async Task A_hostile_database_name_cannot_add_statements_to_the_exported_script()
    {
        server.RequireServer();
        var target = $"DataBridgeTest_{Guid.NewGuid():N}\nGO\nCREATE TABLE dbo.Injected (Id int)\n-- $(Secret) 50% {char.ConvertFromUtf32(0x1F600)}";
        await server.CreateNamedDatabaseAsync(target, null, Token);
        try
        {
            var other = await CreateDatabaseAsync("");
            var script = ScriptFor(await IdentityAsync(target), "CREATE TABLE dbo.Probe (Id int);");

            Assert.Contains(await RunLikeSsmsAsync(other, script), message => message.Contains("This script must run in database", StringComparison.Ordinal));
            Assert.Equal(0, await TableCountAsync(other));

            Assert.Empty(await RunLikeSsmsAsync(target, script));
            Assert.Equal(1, await TableCountAsync(target, "Probe"));
            foreach (var database in new[] { other, target, "master" })
            {
                Assert.Equal(0, await TableCountAsync(database, "Injected"));
            }
        }
        finally
        {
            await server.DropDatabaseAsync(target, Token);
        }
    }

    [Fact]
    public async Task The_guard_refuses_another_server_or_database_guid_and_ignores_server_name_case()
    {
        server.RequireServer();
        var target = await CreateDatabaseAsync("");
        var identity = await IdentityAsync(target);
        ResolvedDatabaseIdentity With(string serverName, Guid? guid) =>
            ResolvedDatabaseIdentity.Create(DatabaseProviderType.SqlServer, serverName, target, 5, "SQL Server 2022", null, guid);

        Assert.NotEmpty(await RunLikeSsmsAsync(target, ScriptFor(With("another-server", identity.DatabaseGuid), "CREATE TABLE dbo.WrongServer (Id int);")));
        Assert.NotEmpty(await RunLikeSsmsAsync(target, ScriptFor(With(identity.ServerName, Guid.NewGuid()), "CREATE TABLE dbo.WrongGuid (Id int);")));
        Assert.Equal(0, await TableCountAsync(target));

        Assert.Empty(await RunLikeSsmsAsync(target, ScriptFor(With(identity.ServerName.ToUpperInvariant(), identity.DatabaseGuid), "CREATE TABLE dbo.Upper (Id int);")));
        Assert.Empty(await RunLikeSsmsAsync(target, ScriptFor(With(identity.ServerName.ToLowerInvariant(), identity.DatabaseGuid), "CREATE TABLE dbo.Lower (Id int);")));
        Assert.Equal(2, await TableCountAsync(target));
    }

    [Fact]
    public async Task The_guard_upper_cases_both_server_names_the_same_way_in_a_turkish_database()
    {
        server.RequireServer();
        var target = $"DataBridgeTest_{Guid.NewGuid():N}";
        await server.CreateNamedDatabaseAsync(target, "Turkish_CI_AS", Token);
        try
        {
            // The database's own collation maps i to dotted I; the fixed collation in the guard maps it to I.
            Assert.Equal("İ|I", await server.QueryRowAsync(target, "SELECT UPPER(N'i'), UPPER(N'i' COLLATE Latin1_General_100_BIN2)", Token));

            var identity = await IdentityAsync(target);
            Assert.Empty(await RunLikeSsmsAsync(target, ScriptFor(identity, "CREATE TABLE dbo.Probe (Id int);")));
            Assert.Equal(1, await TableCountAsync(target, "Probe"));
        }
        finally
        {
            await server.DropDatabaseAsync(target, Token);
        }
    }

    [Fact]
    public async Task Sqlcmd_runs_the_exported_script_only_in_its_write_target_and_not_at_all_with_a_hazardous_operation()
    {
        server.RequireServer();
        var target = await CreateDatabaseAsync("");
        var other = await CreateDatabaseAsync("");
        var identity = await IdentityAsync(target);
        var safe = ScriptFor(identity, "CREATE TABLE dbo.Probe (Id int);");
        var hazardous = ScriptFor(
            identity,
            "CREATE TABLE dbo.Probe (Id int);",
            "CREATE TABLE dbo.Other (Id int CONSTRAINT DF_Other DEFAULT (0x0A) /*\nGO 2\n:connect elsewhere\n$(Secret)\n*/);");

        var plain = await server.RunSqlcmdAsync(other, safe, abortOnError: false, Token);
        Assert.Contains("This script must run in database", plain.Output, StringComparison.Ordinal);
        var aborted = await server.RunSqlcmdAsync(other, safe, abortOnError: true, Token);
        Assert.NotEqual(0, aborted.ExitCode);
        Assert.Equal(0, await TableCountAsync(other));

        var stopped = await server.RunSqlcmdAsync(target, hazardous, abortOnError: false, Token);
        Assert.Contains("cannot be shown as runnable SQL", stopped.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("scripting variable", stopped.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await TableCountAsync(target));

        var ran = await server.RunSqlcmdAsync(target, safe, abortOnError: true, Token);
        Assert.True(ran.ExitCode == 0, ran.Output);
        Assert.Equal(1, await TableCountAsync(target, "Probe"));
    }

    /// <summary>The identity of <paramref name="database"/> as the server reports it, as a plan records it.</summary>
    private async Task<ResolvedDatabaseIdentity> IdentityAsync(string database)
    {
        var literal = database.Replace("'", "''", StringComparison.Ordinal);
        var row = (await server.QueryRowAsync(
            "master",
            $"SELECT CONVERT(nvarchar(256), SERVERPROPERTY('ServerName')), (SELECT service_broker_guid FROM sys.databases WHERE name = N'{literal}')",
            Token)).Split('|');
        return ResolvedDatabaseIdentity.Create(DatabaseProviderType.SqlServer, row[0], database, 5, "SQL Server 2022", null, Guid.Parse(row[1]));
    }

    /// <summary>An exported script for a plan writing to <paramref name="identity"/>, one operation per batch.</summary>
    private string ScriptFor(ResolvedDatabaseIdentity identity, params string[] batches) => _generator.BuildScript(SynchronizationPlan.Create(
        Guid.NewGuid(), Guid.NewGuid(), SynchronizationDirection.SourceToTarget, Guid.NewGuid(), identity, EnvironmentLabel.None, "schema",
        SynchronizationPlanOptions.Default,
        batches.Select((batch, order) => new SynchronizationOperation(
            Guid.NewGuid(), order, new DatabaseObjectId("dbo", $"Probe{order}", DatabaseObjectType.Table), SynchronizationOperationType.CreateObject,
            OperationRisk.Low, false, [], "Create probe", [batch], null, [])),
        [], DateTimeOffset.UtcNow));

    private async Task<int> TableCountAsync(string database, string? name = null) => int.Parse(
        await server.QueryRowAsync(
            database,
            name is null ? "SELECT COUNT(*) FROM sys.tables" : $"SELECT COUNT(*) FROM sys.tables WHERE name = N'{name}'",
            Token),
        System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Runs a script as SSMS does: batches split on lines that are only <c>GO</c>, all on one connection (so
    /// <c>SET NOEXEC ON</c> carries over), carrying on after a failed batch. Returns the error messages.
    /// </summary>
    private async Task<IReadOnlyList<string>> RunLikeSsmsAsync(string database, string script)
    {
        var errors = new List<string>();
        await using var connection = new SqlConnection(server.ConnectionString(database));
        await connection.OpenAsync(Token);
        var batch = new System.Text.StringBuilder();
        foreach (var line in script.Split(["\r\n", "\n"], StringSplitOptions.None).Append("GO"))
        {
            if (!string.Equals(line.Trim(), "GO", StringComparison.OrdinalIgnoreCase))
            {
                batch.AppendLine(line);
                continue;
            }

            if (batch.ToString().Trim().Length > 0)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = batch.ToString();
                try
                {
                    await command.ExecuteNonQueryAsync(Token);
                }
                catch (SqlException exception)
                {
                    errors.Add(exception.Message);
                }
            }

            batch.Clear();
        }

        return errors;
    }

    private async Task<(SynchronizationPlan Plan, AssembledPlan Assembled)> PlanAsync(string source, string target, bool allowDestructive)
    {
        var sourceSchema = await ReadAsync(source);
        var targetSchema = await ReadAsync(target);
        var comparison = new SchemaComparer(_reader.DefinitionNormalizer).Compare(sourceSchema, targetSchema, Token);
        var selected = comparison.Differences
            .Where(difference => difference.Status != DifferenceStatus.Equal && !difference.ObjectId.IsChild)
            .Select(difference => difference.ObjectId)
            .ToArray();
        var draft = new PlanDraft();
        SchemaSynchronizationPlanner.Plan(
            new SchemaPlanningInput(sourceSchema, targetSchema, comparison, SynchronizationDirection.SourceToTarget, selected, allowDestructive), draft);
        var identity = ResolvedDatabaseIdentity.Create(DatabaseProviderType.SqlServer, "test", target, 5, "SQL Server 2022", null);
        var assembled = PlanAssembler.Assemble(
            draft,
            new PlanHeader(Guid.NewGuid(), Guid.NewGuid(), SynchronizationDirection.SourceToTarget, Guid.NewGuid(), identity, EnvironmentLabel.None,
                SchemaFingerprint.Compute(targetSchema), new SynchronizationPlanOptions(allowDestructive), DateTimeOffset.UtcNow),
            _generator.Generate,
            Guid.NewGuid);
        return (assembled.Plan, assembled);
    }

    private async Task ApplyAsync(string database, SynchronizationPlan plan)
    {
        foreach (var operation in plan.Operations)
        {
            foreach (var batch in operation.SqlBatches)
            {
                try
                {
                    await server.ExecuteInAsync(database, batch, Token);
                }
                catch (SqlException exception)
                {
                    Assert.Fail($"Operation {operation.Order} ({operation.Description}) failed: {exception.Message}\n{batch}");
                }
            }
        }
    }

    private async Task<SchemaComparisonResult> CompareAsync(string source, string target) =>
        new SchemaComparer(_reader.DefinitionNormalizer).Compare(await ReadAsync(source), await ReadAsync(target), Token);

    private async Task<DatabaseSchema> ReadAsync(string database)
    {
        var result = await _reader.ReadSchemaAsync(server.Request(database), Token);
        Assert.True(result.IsSuccess, string.Join("; ", result.Errors.Select(error => error.Message)));
        return result.Value;
    }

    private async Task<string> CreateDatabaseAsync(string script)
    {
        var database = await server.CreateDatabaseAsync(Token);
        if (script.Length > 0)
        {
            await server.ExecuteInAsync(database, script, Token);
        }

        return database;
    }

    private static string Issues(SynchronizationPlan plan) => string.Join(Environment.NewLine, plan.NotIncluded.Select(issue => issue.Message));

    private static string Describe(SchemaComparisonResult result) => string.Join(
        "\n",
        result.Differences
            .Where(difference => difference.Status != DifferenceStatus.Equal)
            .Select(difference => $"{difference.Status} {difference.ObjectId}: {difference.Reason} "
                + string.Join(", ", difference.Properties.Select(property => $"{property.PropertyName} {property.SourceValue} -> {property.TargetValue}"))));
}
