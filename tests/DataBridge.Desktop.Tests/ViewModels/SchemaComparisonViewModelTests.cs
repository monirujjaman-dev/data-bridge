using DataBridge.Application.Connections;
using DataBridge.Core.Comparison;
using DataBridge.Core.Connections;
using DataBridge.Core.Schema;
using DataBridge.Desktop.ViewModels;
using DataBridge.Shared.Results;
using Microsoft.Extensions.DependencyInjection;

namespace DataBridge.Desktop.Tests.ViewModels;

/// <summary>The Schema comparison screen over the real composition, with fake connections and schema reads.</summary>
public sealed class SchemaComparisonViewModelTests : IDisposable
{
    private readonly DesktopFixture _fixture = new();

    public SchemaComparisonViewModelTests() =>
        _fixture.DatabaseProvider.Resolve = request => Result.Success(ResolvedDatabaseIdentity.Create(
            DatabaseProviderType.SqlServer, "DB01", request.Profile.DatabaseName, request.Profile.DatabaseName.Length, "SQL Server 2022", "Latin1_General_CI_AS"));

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Navigation_lists_the_schema_comparison_screen()
    {
        await _fixture.StartAsync();

        Assert.Equal(["Connections", "Schema comparison", "Data comparison"], _fixture.MainWindow.NavigationItems.Select(item => item.Title));
    }

    [Fact]
    public async Task Comparing_two_profiles_shows_the_identities_and_differences()
    {
        var page = await StartWithProfilesAsync();
        _fixture.SchemaReader.Schemas["Sales"] = Schema(Table("Orders"), Table("Customers"));
        _fixture.SchemaReader.Schemas["SalesCopy"] = Schema(Table("Orders"));

        await page.CompareCommand.ExecuteAsync(null);

        Assert.Null(page.ComparisonError);
        Assert.Equal("Sales", page.SourceIdentity!.DatabaseName);
        Assert.Equal("SalesCopy", page.TargetIdentity!.DatabaseName);
        Assert.False(page.IsSameDatabase);
        var item = Assert.Single(page.VisibleDifferences);
        Assert.Equal("dbo.Customers", item.Name);
        Assert.True(item.IsAdd);
        Assert.Equal("Only in source", item.StatusText);
        Assert.Equal("1 only in source · 3 equal", page.SummaryText);
        Assert.Equal("Comparison finished.", page.StatusText);

        page.ShowEqual = true;
        Assert.Equal(4, page.VisibleDifferences.Count);
    }

    [Fact]
    public async Task Selecting_a_difference_shows_its_properties()
    {
        var page = await StartWithProfilesAsync();
        _fixture.SchemaReader.Schemas["Sales"] = Schema(Table("Orders", nullable: false));
        _fixture.SchemaReader.Schemas["SalesCopy"] = Schema(Table("Orders", nullable: true));

        await page.CompareCommand.ExecuteAsync(null);
        page.SelectedDifference = page.VisibleDifferences.Single(item => item.Difference.ObjectId.ObjectType == DatabaseObjectType.Column);

        Assert.Equal([new PropertyDifference("Nullable", "No", "Yes")], page.SelectedProperties);
        Assert.Equal("Nullable", page.SelectedDifference.Detail);
    }

    [Fact]
    public async Task Same_profile_cannot_be_compared_with_itself()
    {
        var page = await StartWithProfilesAsync();
        page.SelectedTarget = page.SelectedSource;

        Assert.False(page.CompareCommand.CanExecute(null));
        Assert.Equal("Choose a different profile for the target.", page.SelectionHint);
    }

    [Fact]
    public async Task Two_profiles_for_the_same_database_raise_the_alert()
    {
        var page = await StartWithProfilesAsync(targetDatabase: "Sales");

        await page.CompareCommand.ExecuteAsync(null);

        Assert.True(page.IsSameDatabase);
    }

    [Fact]
    public async Task Connection_failure_is_shown_with_its_side_and_no_stale_result()
    {
        var page = await StartWithProfilesAsync();
        await page.CompareCommand.ExecuteAsync(null);
        Assert.True(page.HasReport);

        _fixture.DatabaseProvider.Resolve = request => request.Profile.DatabaseName == "SalesCopy"
            ? Result.Failure<ResolvedDatabaseIdentity>(ConnectionErrors.ServerUnreachable)
            : Result.Success(ResolvedDatabaseIdentity.Create(DatabaseProviderType.SqlServer, "DB01", "Sales", 5, "SQL Server 2022", null));
        await page.CompareCommand.ExecuteAsync(null);

        Assert.Equal($"Target: {ConnectionErrors.ServerUnreachable.Message}", page.ComparisonError);
        Assert.False(page.HasReport);
        Assert.Empty(page.VisibleDifferences);
    }

    [Fact]
    public async Task Comparison_can_be_cancelled()
    {
        var page = await StartWithProfilesAsync();
        _fixture.SchemaReader.ReadGate = new TaskCompletionSource();

        var running = page.CompareCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => page.StatusText == "Reading both schemas…");
        Assert.True(page.IsBusy);
        page.CancelBusyCommand.Execute(null);
        await running;

        Assert.False(page.IsBusy);
        Assert.False(page.HasReport);
        Assert.Equal("Cancelled. Nothing was changed on either database.", page.StatusText);
    }

    private async Task<SchemaComparisonViewModel> StartWithProfilesAsync(string targetDatabase = "SalesCopy")
    {
        await _fixture.StartAsync();
        var create = _fixture.Services.GetRequiredService<CreateConnectionProfile>();
        foreach (var (name, database) in new[] { ("Production", "Sales"), ("Staging", targetDatabase) })
        {
            var input = new ConnectionProfileInput(name, "db01", database, AuthenticationType.SqlAuthentication, "app", new Secret("pw"), ConnectionSettings.Default);
            Assert.True((await create.ExecuteAsync(input, TestContext.Current.CancellationToken)).IsSuccess);
        }

        var page = _fixture.SchemaComparison;
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

    private static TableDefinition Table(string name, bool nullable = false) => new(
        new DatabaseObjectId("dbo", name, DatabaseObjectType.Table),
        [new ColumnDefinition("Id", "int", null, false, null, null, nullable, null, null, null, 1)],
        null, [], [], [], [], []);

    private static DatabaseSchema Schema(params TableDefinition[] tables) =>
        new("Database", [new SchemaDefinition("dbo", tables, [], [], [])], [], false, true, DateTimeOffset.UnixEpoch);
}
