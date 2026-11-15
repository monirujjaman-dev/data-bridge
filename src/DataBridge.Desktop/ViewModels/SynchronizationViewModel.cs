using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataBridge.Application.Abstractions;
using DataBridge.Application.Connections;
using DataBridge.Application.DataComparison;
using DataBridge.Application.SchemaComparison;
using DataBridge.Application.Synchronization;
using DataBridge.Core.Comparison;
using DataBridge.Core.Connections;
using DataBridge.Core.Schema;
using DataBridge.Core.Synchronization;
using Microsoft.Extensions.Logging;

namespace DataBridge.Desktop.ViewModels;

/// <summary>
/// The Synchronization screen (FR-PLN-001 to FR-PLN-014): compare two profiles, choose the direction and the differences
/// to include, build a plan, validate it against the write target, review its operations, risk, warnings and SQL,
/// export the script, and approve it. Nothing here writes to a database; execution arrives in Phase 6.
/// </summary>
public sealed partial class SynchronizationViewModel : ViewModelBase
{
    private readonly ListConnectionProfiles _list;
    private readonly CompareSchemas _compareSchemas;
    private readonly ListDataComparisonTables _tables;
    private readonly CompareData _compareData;
    private readonly CreateSynchronizationPlan _create;
    private readonly ValidateSynchronizationPlan _validate;
    private readonly ApproveSynchronizationPlan _approve;
    private readonly SynchronizationScripts _scripts;
    private readonly IChangeSetStore _changeSetStore;
    private readonly SynchronizationPlanStore _plans;
    private readonly IFileSaver _fileSaver;
    private readonly ILogger<SynchronizationViewModel> _logger;

    private SchemaComparisonReport? _schemaReport;
    private DataComparisonReport? _dataReport;
    private IReadOnlyDictionary<DatabaseObjectId, RecordedChangeSet> _changeSets = new Dictionary<DatabaseObjectId, RecordedChangeSet>();

    public SynchronizationViewModel(
        ListConnectionProfiles list,
        CompareSchemas compareSchemas,
        ListDataComparisonTables tables,
        CompareData compareData,
        CreateSynchronizationPlan create,
        ValidateSynchronizationPlan validate,
        ApproveSynchronizationPlan approve,
        SynchronizationScripts scripts,
        IChangeSetStore changeSetStore,
        SynchronizationPlanStore plans,
        IFileSaver fileSaver,
        ILogger<SynchronizationViewModel> logger)
    {
        _list = list;
        _compareSchemas = compareSchemas;
        _tables = tables;
        _compareData = compareData;
        _create = create;
        _validate = validate;
        _approve = approve;
        _scripts = scripts;
        _changeSetStore = changeSetStore;
        _plans = plans;
        _fileSaver = fileSaver;
        _logger = logger;
    }

    public ObservableCollection<ProfileOption> Profiles { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompareCommand))]
    [NotifyPropertyChangedFor(nameof(SelectionHint))]
    public partial ProfileOption? SelectedSource { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompareCommand))]
    [NotifyPropertyChangedFor(nameof(SelectionHint))]
    public partial ProfileOption? SelectedTarget { get; set; }

    public string? SelectionHint => SelectedSource is not null && SelectedSource.Id == SelectedTarget?.Id
        ? "Choose a different profile for the target."
        : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WritesToTarget), nameof(CopySideLabel), nameof(WriteSideLabel), nameof(CopyIdentity), nameof(WriteIdentity), nameof(WriteEnvironmentText), nameof(WritesToProduction))]
    public partial DirectionOption Direction { get; set; } = DirectionOption.All[0];

    public bool WritesToTarget => Direction.Direction == SynchronizationDirection.SourceToTarget;

    [ObservableProperty]
    public partial bool IncludeSchema { get; set; } = true;

    [ObservableProperty]
    public partial bool IncludeData { get; set; } = true;

    /// <summary>Off by default: DROP, DELETE and lossy changes are not even planned unless this is on (FR-PLN-010).</summary>
    [ObservableProperty]
    public partial bool AllowDestructiveOperations { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadCommand), nameof(CompareCommand), nameof(CreatePlanCommand), nameof(ValidateCommand), nameof(ApproveCommand), nameof(ExportScriptCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsBusy { get; private set; }

    public bool IsIdle => !IsBusy;

    [ObservableProperty]
    public partial string? StatusText { get; private set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    [ObservableProperty]
    public partial string? OperationError { get; private set; }

    // Route sign

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIdentities), nameof(CopyIdentity), nameof(WriteIdentity), nameof(IsSameDatabase))]
    public partial (ResolvedDatabaseIdentity Source, ResolvedDatabaseIdentity Target)? Identities { get; private set; }

    public bool HasIdentities => Identities is not null;

    public bool IsSameDatabase => Identities is { } pair && pair.Source.IsSameDatabaseAs(pair.Target);

    public ResolvedDatabaseIdentity? CopyIdentity => WritesToTarget ? Identities?.Source : Identities?.Target;

    public ResolvedDatabaseIdentity? WriteIdentity => WritesToTarget ? Identities?.Target : Identities?.Source;

    public string CopySideLabel => WritesToTarget ? "SOURCE · COPIED FROM · READ ONLY" : "TARGET · COPIED FROM · READ ONLY";

    public string WriteSideLabel => WritesToTarget ? "TARGET · WRITE TARGET" : "SOURCE · WRITE TARGET";

    private ProfileOption? WriteProfile => WritesToTarget ? SelectedTarget : SelectedSource;

    public string WriteEnvironmentText => WriteProfile?.Environment is { } label and not EnvironmentLabel.None ? label.ToString().ToUpperInvariant() : "";

    public bool WritesToProduction => WriteProfile?.Environment == EnvironmentLabel.Production;

    // Differences

    public ObservableCollection<SyncDifferenceItem> Differences { get; } = [];

    public bool HasDifferences => Differences.Count > 0;

    // Plan

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlan), nameof(PlanStatusText), nameof(IsValidated), nameof(IsApproved), nameof(RequiresTypedName), nameof(PlanSummary), nameof(StatusReason))]
    [NotifyCanExecuteChangedFor(nameof(ValidateCommand), nameof(ApproveCommand), nameof(ExportScriptCommand))]
    public partial PlanRecord? Plan { get; private set; }

    public bool HasPlan => Plan is not null;

    public bool IsValidated => Plan?.Status == PlanStatus.Validated;

    public bool IsApproved => Plan?.Status == PlanStatus.Approved;

    public bool RequiresTypedName => Plan is { } record && PlanApprovalPolicy.RequiresTypedDatabaseName(record.Plan);

    public string? StatusReason => Plan?.StatusReason;

    public string PlanStatusText => Plan?.Status switch
    {
        null => "",
        PlanStatus.Draft => "Draft: validate it against the write target next.",
        PlanStatus.Validated => "Validated: review it and approve.",
        PlanStatus.ValidationFailed => "Validation found problems that block this plan.",
        PlanStatus.Invalidated => "Invalidated: the write target changed. Compare again and create a new plan.",
        PlanStatus.Approved => "Approved. Execution arrives in a later version of DataBridge; nothing has been written.",
        var status => status.ToString()!,
    };

    public string PlanSummary => Plan is not { } record
        ? ""
        : string.Create(
            CultureInfo.CurrentCulture,
            $"{record.Plan.Operations.Count} operations · {record.Plan.Operations.Count(operation => operation.IsDestructive)} destructive · {record.Plan.OperationsRequiringApproval.Count} need approval · {record.Plan.NotIncluded.Count} not included");

    public ObservableCollection<PlanOperationItem> Operations { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedOperation))]
    public partial PlanOperationItem? SelectedOperation { get; set; }

    public bool HasSelectedOperation => SelectedOperation is not null;

    [ObservableProperty]
    public partial IReadOnlyList<string> NotIncluded { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<FindingItem> Findings { get; private set; } = [];

    [ObservableProperty]
    public partial string? ScriptText { get; private set; }

    // Approval

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApproveCommand))]
    public partial bool ConfirmWriteTarget { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApproveCommand))]
    public partial string? TypedDatabaseName { get; set; }

    private bool CanCompare() => IsIdle && SelectedSource is not null && SelectedTarget is not null && SelectedSource.Id != SelectedTarget.Id;

    [RelayCommand(CanExecute = nameof(IsIdle), IncludeCancelCommand = true)]
    private Task LoadAsync(CancellationToken cancellationToken) =>
        RunAsync("load profiles", "Loading profiles…", async token =>
        {
            var profiles = await RunInBackgroundAsync(_list.ExecuteAsync, token);
            var (sourceId, targetId) = (SelectedSource?.Id, SelectedTarget?.Id);
            Profiles.Clear();
            foreach (var profile in profiles)
            {
                Profiles.Add(new ProfileOption(profile.Id, profile.Name, profile.Environment));
            }

            SelectedSource = Profiles.FirstOrDefault(option => option.Id == sourceId);
            SelectedTarget = Profiles.FirstOrDefault(option => option.Id == targetId);
            StatusText = Profiles.Count < 2 ? "Save two connection profiles to synchronize them." : null;
        }, cancellationToken);

    /// <summary>Compares schema and/or data, recording differing rows so they can be planned. Read-only on both databases.</summary>
    [RelayCommand(CanExecute = nameof(CanCompare), IncludeCancelCommand = true)]
    private Task CompareAsync(CancellationToken cancellationToken) =>
        RunAsync("compare the databases", "Comparing schemas…", async token =>
        {
            await DiscardPlanAsync(token);
            await ForgetComparisonAsync(token);
            var (sourceId, targetId) = (SelectedSource!.Id, SelectedTarget!.Id);

            if (IncludeSchema)
            {
                var schema = await RunInBackgroundAsync(inner => _compareSchemas.ExecuteAsync(sourceId, targetId, null, inner), token);
                if (schema.IsFailure)
                {
                    OperationError = Join(schema.Errors);
                    StatusText = "The schemas could not be compared.";
                    return;
                }

                _schemaReport = schema.Value;
                Identities = (schema.Value.Source, schema.Value.Target);
            }

            if (IncludeData && !await CompareDataAsync(sourceId, targetId, token))
            {
                return;
            }

            ShowDifferences();
        }, cancellationToken);

    private async Task<bool> CompareDataAsync(Guid sourceId, Guid targetId, CancellationToken token)
    {
        StatusText = "Listing tables…";
        var tables = await RunInBackgroundAsync(inner => _tables.ExecuteAsync(sourceId, targetId, inner), token);
        if (tables.IsFailure)
        {
            OperationError = Join(tables.Errors);
            StatusText = "The tables could not be listed.";
            return false;
        }

        Identities = (tables.Value.Source, tables.Value.Target);
        var comparable = tables.Value.Tables.Where(table => table.Problem is null).Select(table => table.TableId).ToArray();
        if (comparable.Length == 0)
        {
            return true;
        }

        var request = new DataComparisonRequest(sourceId, targetId, comparable, new Dictionary<DatabaseObjectId, IReadOnlyList<string>>(), DataComparisonOptions.Default);
        var progress = new Progress<DataComparisonProgress>(update => StatusText =
            $"Comparing rows… ({update.TablesCompleted} of {update.TablesTotal} tables done)");
        await using var recorder = new ChangeSetRecorder(_changeSetStore);
        var data = await RunInBackgroundAsync(inner => _compareData.ExecuteAsync(request, recorder, progress, inner), token);
        if (data.IsFailure)
        {
            OperationError = Join(data.Errors);
            StatusText = "The data could not be compared.";
            return false;
        }

        _dataReport = data.Value;
        _changeSets = await recorder.CompleteAsync(token);
        return true;
    }

    private bool CanCreatePlan() => IsIdle && Differences.Any(item => item.IsSelected);

    [RelayCommand(CanExecute = nameof(CanCreatePlan), IncludeCancelCommand = true)]
    private Task CreatePlanAsync(CancellationToken cancellationToken) =>
        RunAsync("create the plan", "Building the plan…", async token =>
        {
            var selected = Differences.Where(item => item.IsSelected).ToArray();
            var request = new SynchronizationPlanRequest(
                SelectedSource!.Id,
                SelectedTarget!.Id,
                Direction.Direction,
                _schemaReport,
                [.. selected.Where(item => !item.IsData).Select(item => item.Id)],
                _dataReport,
                [.. selected.Where(item => item.IsData).Select(item => item.Id)],
                _changeSets,
                new SynchronizationPlanOptions(AllowDestructiveOperations));
            var previous = Plan;
            var created = await RunInBackgroundAsync(inner => _create.ExecuteAsync(request, inner), token);
            if (created.IsFailure)
            {
                OperationError = Join(created.Errors);
                StatusText = "The plan could not be created.";
                return;
            }

            if (previous is not null)
            {
                await _scripts.DiscardAsync(previous.Plan.Id, token);
            }

            ShowPlan(created.Value);
            var script = await _scripts.ScriptAsync(created.Value.Plan.Id, token);
            ScriptText = script.IsSuccess ? script.Value : null;
            StatusText = created.Value.Plan.Operations.Count == 0
                ? "Nothing in the selection can be synchronized. See what was not included."
                : "Plan created. Nothing has been written. Validate it next.";
        }, cancellationToken);

    private bool CanValidate() => IsIdle && Plan is { Status: PlanStatus.Draft or PlanStatus.Validated or PlanStatus.ValidationFailed } record && record.Plan.Operations.Count > 0;

    [RelayCommand(CanExecute = nameof(CanValidate), IncludeCancelCommand = true)]
    private Task ValidateAsync(CancellationToken cancellationToken) =>
        RunAsync("validate the plan", "Checking the write target…", async token =>
        {
            var planId = Plan!.Plan.Id;
            var validated = await RunInBackgroundAsync(inner => _validate.ExecuteAsync(planId, inner), token);
            if (validated.IsFailure)
            {
                OperationError = Join(validated.Errors);
                StatusText = "The plan could not be validated.";
                return;
            }

            ShowPlan(validated.Value);
            StatusText = PlanStatusText;
        }, cancellationToken);

    private bool CanApprove() =>
        IsIdle
        && Plan is { Status: PlanStatus.Validated } record
        && ConfirmWriteTarget
        && Operations.Where(item => item.NeedsApproval).All(item => item.IsApproved)
        && (!RequiresTypedName || string.Equals(TypedDatabaseName, record.Plan.WriteTargetDatabaseName, StringComparison.Ordinal));

    [RelayCommand(CanExecute = nameof(CanApprove))]
    private void Approve()
    {
        var record = Plan!;
        var request = new ApprovalRequest(
            record.Plan.WriteTargetIdentityFingerprint,
            [.. Operations.Where(item => item.IsApproved).Select(item => item.Operation.Id)],
            TypedDatabaseName);
        var approved = _approve.Execute(record.Plan.Id, request);
        if (approved.IsFailure)
        {
            OperationError = Join(approved.Errors);
            return;
        }

        OperationError = null;
        ShowPlan(approved.Value);
        StatusText = PlanStatusText;
    }

    private bool CanExport() => IsIdle && ScriptText is not null;

    [RelayCommand(CanExecute = nameof(CanExport), IncludeCancelCommand = true)]
    private Task ExportScriptAsync(CancellationToken cancellationToken) =>
        RunAsync("export the script", "Saving the script…", async token =>
        {
            var name = $"DataBridge-plan-{Plan!.Plan.CreatedAtUtc:yyyyMMdd-HHmmss}.sql";
            var saved = await _fileSaver.SaveTextAsync(name, ScriptText!, token);
            StatusText = saved ? "Script saved. It is for review: running it yourself skips DataBridge's checks." : "The script was not saved.";
        }, cancellationToken);

    [RelayCommand]
    private void CancelBusy()
    {
        CompareCommand.Cancel();
        CreatePlanCommand.Cancel();
        ValidateCommand.Cancel();
        ExportScriptCommand.Cancel();
        LoadCommand.Cancel();
    }

    partial void OnSelectedSourceChanged(ProfileOption? value) => ResetComparison();

    partial void OnSelectedTargetChanged(ProfileOption? value) => ResetComparison();

    partial void OnDirectionChanged(DirectionOption value)
    {
        ResetPlan();
        if (!IsBusy)
        {
            // What the write target lacks depends on the direction, so the default selection does too.
            ShowDifferences();
        }
    }

    partial void OnAllowDestructiveOperationsChanged(bool value) => ResetPlan();

    partial void OnIncludeSchemaChanged(bool value) => ResetComparison();

    partial void OnIncludeDataChanged(bool value) => ResetComparison();

    private void ShowDifferences()
    {
        Differences.Clear();
        if (_schemaReport is { } schema)
        {
            var differences = schema.Result.Differences;
            foreach (var difference in differences.Where(item => item.Status != DifferenceStatus.Equal && !item.ObjectId.IsChild))
            {
                var children = differences.Count(child => child.ObjectId.IsChild
                    && child.Status != DifferenceStatus.Equal
                    && child.ObjectId.SchemaName == difference.ObjectId.SchemaName
                    && child.ObjectId.ParentObjectName == difference.ObjectId.ObjectName);
                Add(SyncDifferenceItem.ForSchema(difference, children));
            }
        }

        if (_dataReport is { } data)
        {
            foreach (var table in data.Tables.Where(table => table.Outcome != TableDataComparisonOutcome.Compared || table.DifferenceCount > 0))
            {
                Add(SyncDifferenceItem.ForRows(table));
            }
        }

        OnPropertyChanged(nameof(HasDifferences));
        CreatePlanCommand.NotifyCanExecuteChanged();
        StatusText = Differences.Count == 0
            ? "No differences: the databases already match."
            : $"{Differences.Count} differences. Select what to synchronize, then create the plan.";

        void Add(SyncDifferenceItem item)
        {
            // Copy what the write target lacks or has differently; dropping is opt-in.
            item.IsSelected = item.CanSelect && item.Status != Direction.Direction.OnlyInWriteTarget();
            item.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName == nameof(SyncDifferenceItem.IsSelected))
                {
                    CreatePlanCommand.NotifyCanExecuteChanged();
                }
            };
            Differences.Add(item);
        }
    }

    private void ShowPlan(PlanRecord record)
    {
        var approvals = Operations.Where(item => item.IsApproved).Select(item => item.Operation.Id).ToHashSet();
        var samePlan = Plan?.Plan.Id == record.Plan.Id;
        Plan = record;
        Operations.Clear();
        foreach (var operation in record.Plan.Operations)
        {
            var item = new PlanOperationItem(operation) { IsApproved = samePlan && approvals.Contains(operation.Id) };
            item.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName == nameof(PlanOperationItem.IsApproved))
                {
                    ApproveCommand.NotifyCanExecuteChanged();
                }
            };
            Operations.Add(item);
        }

        if (!samePlan)
        {
            ConfirmWriteTarget = false;
            TypedDatabaseName = null;
        }

        NotIncluded = [.. record.Plan.NotIncluded.Select(issue => issue.Message)];
        Findings = record.Validation is { } validation
            ? [.. validation.Findings.Select(finding => new FindingItem(finding, Describe(record, finding)))]
            : [];
        SelectedOperation = Operations.FirstOrDefault();
        OperationError = null;
        ApproveCommand.NotifyCanExecuteChanged();
        ValidateCommand.NotifyCanExecuteChanged();
    }

    private static string Describe(PlanRecord record, Application.Abstractions.ValidationFinding finding)
    {
        var operation = record.Plan.Operations.FirstOrDefault(item => item.Id == finding.OperationId);
        return operation is null ? finding.Message : $"{operation.Order + 1}. {finding.Message}";
    }

    private void ResetPlan()
    {
        if (IsBusy || Plan is null)
        {
            return;
        }

        var planId = Plan.Plan.Id;
        ClearPlan();
        _ = _scripts.DiscardAsync(planId, CancellationToken.None);
    }

    private void ResetComparison()
    {
        if (IsBusy)
        {
            return;
        }

        if (Plan is { } record)
        {
            ClearPlan();
            _ = _scripts.DiscardAsync(record.Plan.Id, CancellationToken.None);
        }

        _ = ForgetComparisonAsync(CancellationToken.None);
        Differences.Clear();
        Identities = null;
        OnPropertyChanged(nameof(HasDifferences));
        OnPropertyChanged(nameof(WriteEnvironmentText));
        OnPropertyChanged(nameof(WritesToProduction));
        CreatePlanCommand.NotifyCanExecuteChanged();
    }

    private void ClearPlan()
    {
        Plan = null;
        Operations.Clear();
        NotIncluded = [];
        Findings = [];
        ScriptText = null;
        SelectedOperation = null;
        ConfirmWriteTarget = false;
        TypedDatabaseName = null;
    }

    private async Task DiscardPlanAsync(CancellationToken cancellationToken)
    {
        if (Plan is { } record)
        {
            ClearPlan();
            await _scripts.DiscardAsync(record.Plan.Id, cancellationToken);
        }
    }

    /// <summary>Deletes the previous comparison's recorded rows that no plan uses.</summary>
    private async Task ForgetComparisonAsync(CancellationToken cancellationToken)
    {
        var stores = _changeSets.Values.Select(changeSet => changeSet.StoreId).ToArray();
        _schemaReport = null;
        _dataReport = null;
        _changeSets = new Dictionary<DatabaseObjectId, RecordedChangeSet>();
        foreach (var storeId in stores.Where(storeId => !_plans.IsChangeSetInUse(storeId)))
        {
            await _changeSetStore.DeleteAsync(storeId, cancellationToken);
        }
    }

    private static string Join(IEnumerable<DataBridge.Shared.Errors.Error> errors) =>
        string.Join(Environment.NewLine, errors.Select(error => error.Message));

    private async Task RunAsync(string operation, string progressText, Func<CancellationToken, Task> work, CancellationToken cancellationToken)
    {
        IsBusy = true;
        ErrorMessage = null;
        OperationError = null;
        StatusText = progressText;
        try
        {
            await work(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            DesktopLog.OperationCancelled(_logger, operation);
            StatusText = "Cancelled. Nothing was changed on either database.";
        }
#pragma warning disable CA1031 // The UI must survive any failure; it is logged with full detail.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            DesktopLog.OperationFailed(_logger, exception, operation);
            ErrorMessage = $"Could not {operation}. The details are in the log file.";
            StatusText = null;
        }
        finally
        {
            IsBusy = false;
            CreatePlanCommand.NotifyCanExecuteChanged();
            ValidateCommand.NotifyCanExecuteChanged();
            ApproveCommand.NotifyCanExecuteChanged();
            ExportScriptCommand.NotifyCanExecuteChanged();
        }
    }
}
