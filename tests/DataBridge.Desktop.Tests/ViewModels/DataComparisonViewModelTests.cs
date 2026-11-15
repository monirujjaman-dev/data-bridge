using DataBridge.Application.Connections;
using DataBridge.Core.Comparison;
using DataBridge.Core.Connections;
using DataBridge.Core.Data;
using DataBridge.Core.Schema;
using DataBridge.Desktop.ViewModels;
using DataBridge.Shared.Results;
using Microsoft.Extensions.DependencyInjection;

namespace DataBridge.Desktop.Tests.ViewModels;

/// <summary>The Data comparison screen over the real composition, with fake connections, schemas and rows.</summary>
public sealed class DataComparisonViewModelTests : IDisposable
{
    private readonly DesktopFixture _fixture = new();

    public DataComparisonViewModelTests()
    {
        _fixture.DatabaseProvider.Resolve = request => Result.Success(ResolvedDatabaseIdentity.Create(
            DatabaseProviderType.SqlServer, "DB01", request.Profile.DatabaseName, request.Profile.DatabaseName.Length, "SQL Server 2022", null));
        foreach (var database in new[] { "Sales", "SalesCopy" })
        {
            _fixture.SchemaReader.Schemas[database] = new DatabaseSchema(
                database, [new SchemaDefinition("dbo", [Table("Orders", keyed: true), Table("Notes", keyed: false)], [], [], [])], [], false, true, DateTimeOffset.UnixEpoch);
        }
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Navigation_lists_the_data_comparison_screen()
    {
        await _fixture.StartAsync();

        Assert.Contains(_fixture.MainWindow.NavigationItems, item => item.Title == "Data comparison");
    }

    [Fact]
    public async Task Loading_tables_shows_how_each_is_matched_and_selects_the_comparable_ones()
    {
        var page = await StartWithProfilesAsync();

        await page.LoadTablesCommand.ExecuteAsync(null);

        Assert.True(page.HasIdentities);
        var orders = page.Tables.Single(table => table.Name == "dbo.Orders");
        Assert.True(orders.IsSelected);
        Assert.Equal("Matched on Id (primary key)", orders.Detail);
        var notes = page.Tables.Single(table => table.Name == "dbo.Notes");
        Assert.False(notes.IsSelected);
        Assert.True(notes.NeedsKey);
        Assert.Equal("Columns that can form a key: Id", notes.CandidateKeyText);
    }

    [Fact]
    public async Task Comparing_shows_table_results_and_the_differing_rows()
    {
        var page = await StartWithProfilesAsync();
        Rows("Sales", "Orders", (1, "a"), (2, "b"));
        Rows("SalesCopy", "Orders", (1, "a"), (2, "B"), (3, "c"));
        await page.LoadTablesCommand.ExecuteAsync(null);

        await page.CompareCommand.ExecuteAsync(null);

        Assert.Null(page.ComparisonError);
        var result = Assert.Single(page.Results);
        Assert.Equal("Different", result.OutcomeText);
        Assert.Equal(result, page.SelectedResult);
        Assert.Equal(["2 Different", "3 Only in target"], page.Rows.Select(row => $"{row.KeyText} {row.StatusText}"));
        page.SelectedRow = page.Rows[0];
        Assert.Equal([new ColumnValueItem("Name", "b", "B")], page.SelectedRow.Columns);
        Assert.True(page.HasNonSnapshotReads);
        Assert.Equal("1 compared · 1 with differences · 0 not compared", page.SummaryText);
    }

    [Fact]
    public async Task A_user_key_makes_a_keyless_table_comparable()
    {
        var page = await StartWithProfilesAsync();
        Rows("Sales", "Notes", (1, "a"));
        Rows("SalesCopy", "Notes", (1, "a"));
        await page.LoadTablesCommand.ExecuteAsync(null);
        var notes = page.Tables.Single(table => table.Name == "dbo.Notes");
        page.Tables.Single(table => table.Name == "dbo.Orders").IsSelected = false;
        notes.IsSelected = true;
        notes.UserKey = " Id ";

        await page.CompareCommand.ExecuteAsync(null);

        var summary = Assert.Single(page.Results).Summary;
        Assert.Equal((TableDataComparisonOutcome.Compared, MatchingKeySource.UserSelected), (summary.Outcome, summary.Key!.Source));
    }

    [Fact]
    public async Task Only_the_first_rows_of_a_table_are_kept_for_display()
    {
        var page = await StartWithProfilesAsync();
        var extra = DataComparisonViewModel.MaxRowsShownPerTable + 500;
        Rows("Sales", "Orders", Enumerable.Range(1, extra).Select(id => ((long)id, "x")).ToArray());
        Rows("SalesCopy", "Orders");
        await page.LoadTablesCommand.ExecuteAsync(null);

        await page.CompareCommand.ExecuteAsync(null);

        Assert.Equal(DataComparisonViewModel.MaxRowsShownPerTable, page.Rows.Count);
        Assert.Equal(extra, page.SelectedResult!.Summary.SourceOnlyRowCount);
        Assert.Contains("Showing the first", page.SelectedResult.ShownText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Comparison_can_be_cancelled()
    {
        var page = await StartWithProfilesAsync();
        Rows("Sales", "Orders", (1, "a"));
        Rows("SalesCopy", "Orders", (1, "a"));
        await page.LoadTablesCommand.ExecuteAsync(null);
        _fixture.DataReader.BatchGate = new TaskCompletionSource();

        var running = page.CompareCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => _fixture.DataReader.ReadsStarted > 0);
        page.CancelBusyCommand.Execute(null);
        await running;

        Assert.False(page.IsBusy);
        Assert.False(page.HasReport);
        Assert.Equal("Cancelled. Nothing was changed on either database.", page.StatusText);
    }

    [Fact]
    public async Task Changing_a_profile_clears_loaded_tables()
    {
        var page = await StartWithProfilesAsync();
        await page.LoadTablesCommand.ExecuteAsync(null);

        page.SelectedTarget = page.SelectedSource;

        Assert.False(page.HasTables);
        Assert.False(page.CompareCommand.CanExecute(null));
        Assert.Equal("Choose a different profile for the target.", page.SelectionHint);
    }

    private void Rows(string database, string table, params (long Id, string Name)[] rows)
    {
        if (!_fixture.DataReader.Rows.TryGetValue(database, out var tables))
        {
            _fixture.DataReader.Rows[database] = tables = [];
        }

        tables[table] = [.. rows.Select(row => new DataRow([DatabaseValue.FromInt64(row.Id, "int")], [DatabaseValue.FromText(row.Name, "nvarchar")]))];
    }

    private async Task<DataComparisonViewModel> StartWithProfilesAsync()
    {
        await _fixture.StartAsync();
        var create = _fixture.Services.GetRequiredService<CreateConnectionProfile>();
        foreach (var (name, database) in new[] { ("Production", "Sales"), ("Staging", "SalesCopy") })
        {
            var input = new ConnectionProfileInput(name, "db01", database, AuthenticationType.SqlAuthentication, "app", new Secret("pw"), ConnectionSettings.Default);
            Assert.True((await create.ExecuteAsync(input, TestContext.Current.CancellationToken)).IsSuccess);
        }

        var page = _fixture.DataComparison;
        await page.LoadCommand.ExecuteAsync(null);
        page.SelectedSource = page.Profiles.Single(option => option.Name == "Production");
        page.SelectedTarget = page.Profiles.Single(option => option.Name == "Staging");
        return page;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private static TableDefinition Table(string name, bool keyed) => new(
        new DatabaseObjectId("dbo", name, DatabaseObjectType.Table),
        [
            new ColumnDefinition("Id", "int", null, false, null, null, false, null, null, null, 1),
            new ColumnDefinition("Name", "nvarchar", 50, false, null, null, true, null, null, null, 2),
        ],
        keyed ? new PrimaryKeyDefinition("PK", false, "CLUSTERED", [new IndexColumnDefinition("Id", 1, false, false)], new IndexOptions(false, 100, false, true, true, false, "NONE")) : null,
        [], [], [], [], []);
}
