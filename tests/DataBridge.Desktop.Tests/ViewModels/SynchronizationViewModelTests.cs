using DataBridge.Application.Abstractions;
using DataBridge.Application.Connections;
using DataBridge.Core.Connections;
using DataBridge.Core.Schema;
using DataBridge.Desktop.ViewModels;
using DataBridge.Shared.Results;
using Microsoft.Extensions.DependencyInjection;

namespace DataBridge.Desktop.Tests.ViewModels;

/// <summary>The Synchronization screen over the real composition, with fake connections, schema reads and validation.</summary>
public sealed class SynchronizationViewModelTests : IDisposable
{
    private readonly DesktopFixture _fixture = new();

    public SynchronizationViewModelTests() =>
        _fixture.DatabaseProvider.Resolve = request => Result.Success(ResolvedDatabaseIdentity.Create(
            DatabaseProviderType.SqlServer, "DB01", request.Profile.DatabaseName, request.Profile.DatabaseName.Length, "SQL Server 2022", "Latin1_General_CI_AS"));

    public void Dispose() => _fixture.Dispose();

    private static TableDefinition Table(string name) => new(
        new DatabaseObjectId("dbo", name, DatabaseObjectType.Table),
        [new ColumnDefinition("Id", "int", null, false, null, null, false, null, null, null, 1)],
        null, [], [], [], [], []);

    private static DatabaseSchema Schema(params TableDefinition[] tables) =>
        new("Database", [new SchemaDefinition("dbo", tables, [], [], [])], [], false, true, DateTimeOffset.UnixEpoch);

    private async Task<SynchronizationViewModel> StartAsync(EnvironmentLabel targetEnvironment = EnvironmentLabel.None)
    {
        await _fixture.StartAsync();
        var create = _fixture.Services.GetRequiredService<CreateConnectionProfile>();
        foreach (var (name, database, environment) in new[] { ("Live", "Sales", EnvironmentLabel.None), ("Copy", "SalesCopy", targetEnvironment) })
        {
            var input = new ConnectionProfileInput(name, "db01", database, AuthenticationType.SqlAuthentication, "app", new Secret("pw"), ConnectionSettings.Default, environment);
            Assert.True((await create.ExecuteAsync(input, TestContext.Current.CancellationToken)).IsSuccess);
        }

        _fixture.SchemaReader.Schemas["Sales"] = Schema(Table("Orders"), Table("Customers"));
        _fixture.SchemaReader.Schemas["SalesCopy"] = Schema(Table("Orders"), Table("Legacy"));

        var page = _fixture.Synchronization;
        await page.LoadCommand.ExecuteAsync(null);
        page.SelectedSource = page.Profiles.Single(option => option.Name == "Live");
        page.SelectedTarget = page.Profiles.Single(option => option.Name == "Copy");
        page.IncludeData = false;
        return page;
    }

    [Fact]
    public async Task Comparing_lists_differences_and_selects_what_the_write_target_lacks()
    {
        var page = await StartAsync();

        await page.CompareCommand.ExecuteAsync(null);

        Assert.Null(page.OperationError);
        Assert.Equal("SalesCopy", page.WriteIdentity!.DatabaseName);
        Assert.Equal("Sales", page.CopyIdentity!.DatabaseName);
        Assert.True(page.Differences.Single(item => item.Id.ObjectName == "Customers").IsSelected);
        Assert.False(page.Differences.Single(item => item.Id.ObjectName == "Legacy").IsSelected);
        Assert.True(page.CreatePlanCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_plan_is_created_validated_approved_and_exported_without_writing()
    {
        var page = await StartAsync();
        await page.CompareCommand.ExecuteAsync(null);

        await page.CreatePlanCommand.ExecuteAsync(null);
        Assert.Equal("Create table dbo.Customers", Assert.Single(page.Operations).Description);
        Assert.Contains("CREATE TABLE [dbo].[Customers]", page.ScriptText, StringComparison.Ordinal);
        Assert.False(page.ApproveCommand.CanExecute(null));

        await page.ValidateCommand.ExecuteAsync(null);
        Assert.True(page.IsValidated, page.PlanStatusText);
        Assert.False(page.ApproveCommand.CanExecute(null));

        page.ConfirmWriteTarget = true;
        Assert.True(page.ApproveCommand.CanExecute(null));
        page.ApproveCommand.Execute(null);
        Assert.True(page.IsApproved);
        Assert.Contains("nothing has been written", page.PlanStatusText, StringComparison.Ordinal);

        await page.ExportScriptCommand.ExecuteAsync(null);
        var (name, text) = Assert.Single(_fixture.FileSaver.Saved);
        Assert.EndsWith(".sql", name, StringComparison.Ordinal);
        Assert.Contains("IF DB_NAME() <> N'SalesCopy'", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Drops_need_the_destructive_opt_in_and_their_own_approval()
    {
        var page = await StartAsync();
        await page.CompareCommand.ExecuteAsync(null);
        foreach (var item in page.Differences)
        {
            item.IsSelected = item.Id.ObjectName == "Legacy";
        }

        await page.CreatePlanCommand.ExecuteAsync(null);
        Assert.Empty(page.Operations);
        Assert.Contains(page.NotIncluded, message => message.Contains("destructive operations are not allowed", StringComparison.Ordinal));

        page.AllowDestructiveOperations = true;
        Assert.False(page.HasPlan);
        await page.CreatePlanCommand.ExecuteAsync(null);
        var drop = Assert.Single(page.Operations);
        Assert.True(drop.NeedsApproval);
        Assert.True(drop.IsDangerous);

        await page.ValidateCommand.ExecuteAsync(null);
        page.ConfirmWriteTarget = true;
        Assert.False(page.ApproveCommand.CanExecute(null));
        Assert.Single(page.Operations).IsApproved = true;
        Assert.True(page.ApproveCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_production_write_target_needs_the_database_name_typed()
    {
        var page = await StartAsync(EnvironmentLabel.Production);
        Assert.True(page.WritesToProduction);
        Assert.Equal("PRODUCTION", page.WriteEnvironmentText);
        await page.CompareCommand.ExecuteAsync(null);
        await page.CreatePlanCommand.ExecuteAsync(null);
        await page.ValidateCommand.ExecuteAsync(null);
        page.ConfirmWriteTarget = true;

        Assert.True(page.RequiresTypedName);
        Assert.False(page.ApproveCommand.CanExecute(null));
        page.TypedDatabaseName = "salescopy";
        Assert.False(page.ApproveCommand.CanExecute(null));
        page.TypedDatabaseName = "SalesCopy";
        Assert.True(page.ApproveCommand.CanExecute(null));
    }

    [Fact]
    public async Task Blocking_findings_keep_the_plan_from_being_approved_and_changing_direction_drops_the_plan()
    {
        var page = await StartAsync();
        await page.CompareCommand.ExecuteAsync(null);
        await page.CreatePlanCommand.ExecuteAsync(null);
        _fixture.TargetInspector.Findings.Add(new ValidationFinding(page.Operations[0].Operation.Id, ValidationSeverity.Blocking, "The login lacks CREATE TABLE in the database."));

        await page.ValidateCommand.ExecuteAsync(null);
        page.ConfirmWriteTarget = true;

        Assert.Equal("1. The login lacks CREATE TABLE in the database.", Assert.Single(page.Findings).Text);
        Assert.False(page.ApproveCommand.CanExecute(null));

        page.Direction = DirectionOption.All[1];
        Assert.False(page.HasPlan);
        Assert.Equal("Sales", page.WriteIdentity!.DatabaseName);
        Assert.True(page.Differences.Single(item => item.Id.ObjectName == "Legacy").IsSelected);
        Assert.False(page.Differences.Single(item => item.Id.ObjectName == "Customers").IsSelected);
    }
}
