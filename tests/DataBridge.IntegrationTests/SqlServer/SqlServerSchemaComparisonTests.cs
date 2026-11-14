using DataBridge.Core.Comparison;
using DataBridge.Core.Schema;
using DataBridge.Infrastructure.Providers.SqlServer.Schema;

namespace DataBridge.IntegrationTests.SqlServer;

/// <summary>
/// Phase 3 acceptance against a real SQL Server: identical schemas compare equal, controlled changes produce the
/// expected object and property differences, unsupported and unreadable objects are reported, and reading never
/// modifies the database.
/// </summary>
public sealed class SqlServerSchemaComparisonTests(SqlServerFixture server)
{
    /// <summary>Every supported object kind, with system-named constraints whose names differ per database.</summary>
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
            -- The literal keeps its spacing.
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

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Identical_schemas_produce_no_differences()
    {
        server.RequireServer();
        var source = await CreateDatabaseAsync(Baseline);
        var target = await CreateDatabaseAsync(Baseline.Replace("\n", "\r\n", StringComparison.Ordinal));

        var result = await CompareAsync(source, target);

        Assert.False(result.HasDifferences, Describe(result));
        Assert.Empty(result.SourceUnsupportedObjects);
        var types = result.Differences.Select(difference => difference.ObjectId.ObjectType).ToHashSet();
        Assert.Equal(Enum.GetValues<DatabaseObjectType>().Length, types.Count);
    }

    [Fact]
    public async Task Reader_preserves_sql_server_metadata()
    {
        server.RequireServer();
        var database = await CreateDatabaseAsync(Baseline);

        var schema = (await _reader.ReadSchemaAsync(server.Request(database), Token)).Value;

        Assert.True(schema.HasCompleteMetadata);
        Assert.False(schema.IdentifiersAreCaseSensitive);
        var sales = Assert.Single(schema.Schemas, item => item.Name == "sales");
        var customers = Assert.Single(sales.Tables, table => table.Id.ObjectName == "Customers");
        Assert.Equal(new IdentitySpecification(1, 1), customers.Columns[0].Identity);
        Assert.Equal((100, false), (customers.Columns[1].MaxLength, customers.Columns[1].IsMaxLength));
        Assert.Equal((null, true), (customers.Columns[3].MaxLength, customers.Columns[3].IsMaxLength));
        Assert.Equal((byte?)3, customers.Columns[4].Scale);
        Assert.Equal(((byte?)18, (byte?)4), (customers.Columns[5].Precision, customers.Columns[5].Scale));
        Assert.True(customers.PrimaryKey!.IsSystemNamed);

        var orders = Assert.Single(sales.Tables, table => table.Id.ObjectName == "Orders");
        Assert.Equal(
            [new("OrderId", 1, false, false), new("LineNumber", 2, true, false)],
            orders.PrimaryKey!.Columns);
        Assert.Equal(new ComputedSpecification("([Quantity]*[Price])", true), orders.Columns[5].Computed);
        Assert.Equal("Latin1_General_BIN2", orders.Columns[6].CollationName);
        Assert.Equal(ReferentialAction.Cascade, Assert.Single(orders.ForeignKeys).OnDelete);
        var index = Assert.Single(orders.Indexes);
        Assert.Equal(["CustomerId", "Quantity", "Price"], index.Columns.Select(column => column.ColumnName));
        Assert.Equal("([Status]<>'X')", index.FilterDefinition);

        var sequence = Assert.Single(sales.Sequences);
        Assert.Equal((1000, true, 50), ((long)sequence.StartValue, sequence.IsCached, sequence.CacheSize));
        Assert.Single(Assert.Single(sales.Views).Indexes);
        var trigger = Assert.Single(sales.ProgrammableObjects, module => module.Id.ObjectType == DatabaseObjectType.Trigger);
        Assert.Equal("Orders", trigger.Id.ParentObjectName);
    }

    [Fact]
    public async Task Controlled_changes_produce_the_expected_differences()
    {
        server.RequireServer();
        var source = await CreateDatabaseAsync(Baseline);
        var target = await CreateDatabaseAsync(Baseline + """

            GO
            ALTER TABLE sales.Customers ALTER COLUMN Name nvarchar(200) NULL;
            ALTER TABLE sales.Orders DROP CONSTRAINT FK_Orders_Customers;
            ALTER TABLE sales.Orders ADD CONSTRAINT FK_Orders_Customers FOREIGN KEY (CustomerId) REFERENCES sales.Customers (Id);
            ALTER TABLE sales.Orders DROP CONSTRAINT CK_Orders_Quantity;
            ALTER TABLE sales.Orders ADD CONSTRAINT CK_Orders_Quantity CHECK (Quantity >= 0);
            DROP INDEX IX_Orders_Customer ON sales.Orders;
            CREATE NONCLUSTERED INDEX IX_Orders_Customer ON sales.Orders (CustomerId DESC) INCLUDE (Quantity);
            ALTER SEQUENCE sales.OrderNumbers INCREMENT BY 10;
            ALTER TABLE sales.Orders DISABLE TRIGGER Orders_Audit;
            CREATE TABLE sales.TargetOnly (Id int NOT NULL);
            GO
            ALTER PROCEDURE sales.GetOrders @CustomerId int AS
            BEGIN
                -- The literal keeps its spacing.
                SELECT OrderId, 'a b' AS Spaced FROM sales.Orders WHERE CustomerId = @CustomerId;
            END;
            """);
        await server.ExecuteInAsync(source, "CREATE TABLE sales.SourceOnly (Id int NOT NULL);", Token);

        var result = await CompareAsync(source, target);

        Assert.Equal(DifferenceStatus.SourceOnly, Find(result, DatabaseObjectType.Table, "SourceOnly").Status);
        Assert.Equal(DifferenceStatus.TargetOnly, Find(result, DatabaseObjectType.Table, "TargetOnly").Status);
        Assert.Equal(
            [new("Length", "100", "200"), new("Nullable", "No", "Yes")],
            Find(result, DatabaseObjectType.Column, "Name").Properties);
        Assert.Equal([new("OnDelete", "CASCADE", "NO ACTION")], Find(result, DatabaseObjectType.ForeignKey, "FK_Orders_Customers").Properties);
        Assert.Equal(
            [new("Expression", "([Quantity]>(0))", "([Quantity]>=(0))")],
            Find(result, DatabaseObjectType.CheckConstraint, "CK_Orders_Quantity").Properties);
        Assert.Equal(
            ["KeyColumns", "IncludedColumns", "Filter"],
            Find(result, DatabaseObjectType.Index, "IX_Orders_Customer").Properties.Select(property => property.PropertyName));
        Assert.Equal([new("Increment", "1", "10")], Find(result, DatabaseObjectType.Sequence, "OrderNumbers").Properties);
        Assert.Equal([new("Disabled", "No", "Yes")], Find(result, DatabaseObjectType.Trigger, "Orders_Audit").Properties);
        Assert.Equal("Definition", Assert.Single(Find(result, DatabaseObjectType.StoredProcedure, "GetOrders").Properties).PropertyName);
        Assert.Equal(DifferenceStatus.Modified, Find(result, DatabaseObjectType.Table, "Orders").Status);
        Assert.Equal(DifferenceStatus.Equal, Find(result, DatabaseObjectType.View, "OrderTotals").Status);
        Assert.Equal(DifferenceStatus.Equal, Find(result, DatabaseObjectType.ScalarFunction, "OrderCount").Status);
    }

    [Fact]
    public async Task Same_name_with_a_different_object_kind_is_incompatible()
    {
        server.RequireServer();
        var source = await CreateDatabaseAsync("CREATE TABLE dbo.Thing (Id int NOT NULL);");
        var target = await CreateDatabaseAsync("CREATE VIEW dbo.Thing AS SELECT 1 AS Id;");

        Assert.Equal(DifferenceStatus.Incompatible, Find(await CompareAsync(source, target), DatabaseObjectType.Table, "Thing").Status);
    }

    [Fact]
    public async Task Unsupported_and_encrypted_objects_are_reported()
    {
        server.RequireServer();
        var database = await CreateDatabaseAsync("""
            CREATE SYNONYM dbo.CustomerAlias FOR dbo.Customers;
            CREATE TYPE dbo.IdList AS TABLE (Id int NOT NULL);
            CREATE PARTITION FUNCTION PF_Year (int) AS RANGE RIGHT FOR VALUES (2025, 2026);
            CREATE TABLE dbo.Prices (
                Id int NOT NULL PRIMARY KEY,
                ValidFrom datetime2 GENERATED ALWAYS AS ROW START NOT NULL,
                ValidTo datetime2 GENERATED ALWAYS AS ROW END NOT NULL,
                PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo))
                WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.PricesHistory));
            CREATE ROLE Auditors;
            GO
            CREATE PROCEDURE dbo.Secret WITH ENCRYPTION AS SELECT 1;
            """);

        var schema = (await _reader.ReadSchemaAsync(server.Request(database), Token)).Value;

        var unsupported = schema.UnsupportedObjects.ToDictionary(item => item.DisplayName, item => item.Reason);
        Assert.Contains("SYNONYM", schema.UnsupportedObjects.Single(item => item.ObjectName == "CustomerAlias").ObjectTypeDescription, StringComparison.Ordinal);
        Assert.Contains("dbo.IdList", unsupported.Keys);
        Assert.Contains("PF_Year", unsupported.Keys);
        Assert.Equal("System-versioned temporal tables are not compared.", unsupported["dbo.Prices"]);
        Assert.Equal("Temporal history tables are not compared.", unsupported["dbo.PricesHistory"]);
        Assert.Equal("Users, roles and permissions are not compared.", unsupported["Auditors"]);
        Assert.Equal("The definition is encrypted (WITH ENCRYPTION).", unsupported["dbo.Secret"]);
        Assert.DoesNotContain(schema.Schemas.SelectMany(item => item.Tables), table => table.Id.ObjectName.StartsWith("Prices", StringComparison.Ordinal));
        var secret = Assert.Single(schema.Schemas.Single(item => item.Name == "dbo").ProgrammableObjects);
        Assert.True(secret.Module.IsEncrypted);
        Assert.Null(secret.Module.Text);
    }

    [Fact]
    public async Task Login_without_view_definition_gets_unknown_results_not_equal_or_missing()
    {
        server.RequireServer();
        const string script = """
            CREATE TABLE dbo.Hidden (Id int NOT NULL);
            GO
            CREATE PROCEDURE dbo.Visible AS SELECT 1;
            """;
        var source = await CreateDatabaseAsync(script);
        var target = await CreateDatabaseAsync(script);
        var login = $"reader_{Guid.NewGuid():N}"[..20];
        var password = $"Lg!{Guid.NewGuid():N}aA1";

        // Generated identifiers and password only; these statements cannot take parameters.
        await server.ExecuteAsync($"CREATE LOGIN [{login}] WITH PASSWORD = N'{password}', CHECK_POLICY = OFF;", Token);
        await server.ExecuteInAsync(target, $"CREATE USER [{login}] FOR LOGIN [{login}]; GRANT EXECUTE ON dbo.Visible TO [{login}];", Token);

        var sourceSchema = (await _reader.ReadSchemaAsync(server.Request(source), Token)).Value;
        var targetRead = await _reader.ReadSchemaAsync(server.Request(target, login, password), Token);
        var targetSchema = targetRead.Value;
        var result = new SchemaComparer(_reader.DefinitionNormalizer).Compare(sourceSchema, targetSchema, Token);

        Assert.False(targetSchema.HasCompleteMetadata);
        Assert.Equal(DifferenceStatus.Unknown, Find(result, DatabaseObjectType.Table, "Hidden").Status);
        var visible = Find(result, DatabaseObjectType.StoredProcedure, "Visible");
        Assert.Equal(DifferenceStatus.Unknown, visible.Status);
        Assert.Contains("VIEW DEFINITION", visible.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hidden_constraint_and_computed_definitions_are_unknown_not_equal()
    {
        server.RequireServer();
        const string table = """
            CREATE TABLE dbo.Orders (
                Id int NOT NULL,
                Qty int NOT NULL CONSTRAINT DF_Orders_Qty DEFAULT (1) CONSTRAINT CK_Orders_Qty CHECK (Qty > 0),
                Doubled AS (Qty * 2));
            """;
        var source = await CreateDatabaseAsync(table);
        var target = await CreateDatabaseAsync(table.Replace("Qty > 0", "Qty > 5", StringComparison.Ordinal).Replace("Qty * 2", "Qty * 3", StringComparison.Ordinal));
        var login = $"select_{Guid.NewGuid():N}"[..20];
        var password = $"Lg!{Guid.NewGuid():N}aA1";

        // SELECT makes the table visible, but its definitions stay hidden without VIEW DEFINITION.
        await server.ExecuteAsync($"CREATE LOGIN [{login}] WITH PASSWORD = N'{password}', CHECK_POLICY = OFF;", Token);
        await server.ExecuteInAsync(target, $"CREATE USER [{login}] FOR LOGIN [{login}]; GRANT SELECT ON dbo.Orders TO [{login}];", Token);

        var sourceSchema = (await _reader.ReadSchemaAsync(server.Request(source), Token)).Value;
        var targetSchema = (await _reader.ReadSchemaAsync(server.Request(target, login, password), Token)).Value;
        var result = new SchemaComparer(_reader.DefinitionNormalizer).Compare(sourceSchema, targetSchema, Token);

        Assert.False(targetSchema.HasCompleteMetadata);
        Assert.Equal(DifferenceStatus.Unknown, Find(result, DatabaseObjectType.CheckConstraint, "CK_Orders_Qty").Status);
        Assert.Equal(DifferenceStatus.Unknown, Find(result, DatabaseObjectType.DefaultConstraint, "DF_Orders_Qty").Status);
        Assert.Equal(DifferenceStatus.Unknown, Find(result, DatabaseObjectType.Column, "Doubled").Status);
        Assert.NotEqual(DifferenceStatus.Equal, Find(result, DatabaseObjectType.Table, "Orders").Status);
    }

    [Fact]
    public async Task Engine_attributes_key_options_and_trust_are_compared()
    {
        server.RequireServer();
        const string baseline = """
            CREATE TABLE dbo.Parents (Id int NOT NULL CONSTRAINT PK_Parents PRIMARY KEY);
            CREATE TABLE dbo.People (
                Id int IDENTITY(1, 1) NOT NULL CONSTRAINT PK_People PRIMARY KEY,
                ParentId int NULL CONSTRAINT FK_People_Parents REFERENCES dbo.Parents (Id),
                Email varchar(200) NULL,
                Age int NULL CONSTRAINT CK_People_Age CHECK (Age >= 0));
            CREATE TABLE dbo.Log (Message nvarchar(200) NOT NULL);
            CREATE TABLE dbo.Notes (Id int NOT NULL, Note varchar(100) NULL);
            """;
        var source = await CreateDatabaseAsync(baseline);
        var target = await CreateDatabaseAsync(baseline + """

            GO
            ALTER TABLE dbo.People ALTER COLUMN Email ADD MASKED WITH (FUNCTION = 'email()');
            ALTER TABLE dbo.Notes ALTER COLUMN Note ADD SPARSE;
            ALTER TABLE dbo.People REBUILD WITH (DATA_COMPRESSION = PAGE);
            ALTER TABLE dbo.People NOCHECK CONSTRAINT CK_People_Age;
            ALTER TABLE dbo.People CHECK CONSTRAINT CK_People_Age;
            ALTER TABLE dbo.People NOCHECK CONSTRAINT FK_People_Parents;
            ALTER TABLE dbo.People WITH NOCHECK CHECK CONSTRAINT FK_People_Parents;
            ALTER TABLE dbo.Log REBUILD WITH (DATA_COMPRESSION = ROW);
            """);

        var result = await CompareAsync(source, target);

        Assert.Equal([new("Masking", null, "email()")], Find(result, DatabaseObjectType.Column, "Email").Properties);
        Assert.Equal([new("Sparse", "No", "Yes")], Find(result, DatabaseObjectType.Column, "Note").Properties);
        Assert.Equal([new("DataCompression", "NONE", "PAGE")], Find(result, DatabaseObjectType.PrimaryKey, "PK_People").Properties);
        Assert.Equal([new("NotTrusted", "No", "Yes")], Find(result, DatabaseObjectType.CheckConstraint, "CK_People_Age").Properties);
        Assert.Equal([new("NotTrusted", "No", "Yes")], Find(result, DatabaseObjectType.ForeignKey, "FK_People_Parents").Properties);
        Assert.Equal([new("HeapDataCompression", "NONE", "ROW")], Find(result, DatabaseObjectType.Table, "Log").Properties);
    }

    [Fact]
    public async Task Case_sensitive_database_is_detected()
    {
        server.RequireServer();
        var database = $"DataBridgeTest_{Guid.NewGuid():N}";
        await server.ExecuteAsync($"CREATE DATABASE [{database}] COLLATE Latin1_General_100_CS_AS;", Token);

        var schema = (await _reader.ReadSchemaAsync(server.Request(database), Token)).Value;

        Assert.True(schema.IdentifiersAreCaseSensitive);
    }

    [Fact]
    public async Task Reading_the_schema_does_not_modify_the_database()
    {
        server.RequireServer();
        var database = await CreateDatabaseAsync(Baseline);
        const string fingerprint = """
            SELECT COUNT(*), MAX(modify_date), CHECKSUM_AGG(CHECKSUM(name, type, modify_date)) FROM sys.objects;
            """;
        var before = await QueryRowAsync(database, fingerprint);

        await CompareAsync(database, database);

        Assert.Equal(before, await QueryRowAsync(database, fingerprint));
    }

    [Fact]
    public async Task Unknown_database_is_reported_as_unavailable()
    {
        server.RequireServer();

        var result = await _reader.ReadSchemaAsync(server.Request($"Missing_{Guid.NewGuid():N}"), Token);

        Assert.Equal(DataBridge.Application.Connections.ConnectionErrors.DatabaseUnavailable.Code, Assert.Single(result.Errors).Code);
    }

    [Fact]
    public async Task Reading_can_be_cancelled()
    {
        server.RequireServer();
        var database = await CreateDatabaseAsync(Baseline);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _reader.ReadSchemaAsync(server.Request(database), cancellation.Token));
    }

    private async Task<string> CreateDatabaseAsync(string script)
    {
        var database = await server.CreateDatabaseAsync(Token);
        await server.ExecuteInAsync(database, script, Token);
        return database;
    }

    private async Task<SchemaComparisonResult> CompareAsync(string source, string target)
    {
        var sourceSchema = await _reader.ReadSchemaAsync(server.Request(source), Token);
        var targetSchema = await _reader.ReadSchemaAsync(server.Request(target), Token);
        Assert.True(sourceSchema.IsSuccess, string.Join("; ", sourceSchema.Errors));
        Assert.True(targetSchema.IsSuccess, string.Join("; ", targetSchema.Errors));
        return new SchemaComparer(_reader.DefinitionNormalizer).Compare(sourceSchema.Value, targetSchema.Value, Token);
    }

    private async Task<string> QueryRowAsync(string database, string sql)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(
            Infrastructure.Providers.SqlServer.SqlServerConnectionFactory.BuildConnectionString(server.Request(database)).ConnectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(Token);
        Assert.True(await reader.ReadAsync(Token));
        return string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(ordinal => Convert.ToString(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture)));
    }

    private static SchemaObjectDifference Find(SchemaComparisonResult result, DatabaseObjectType type, string name) =>
        Assert.Single(result.Differences, difference => difference.ObjectId.ObjectType == type && difference.ObjectId.ObjectName == name);

    private static string Describe(SchemaComparisonResult result) => string.Join(
        Environment.NewLine,
        result.Differences
            .Where(difference => difference.Status != DifferenceStatus.Equal)
            .Select(difference => $"{difference.Status} {difference.ObjectId}: {string.Join("; ", difference.Properties)} {difference.Reason}"));
}
