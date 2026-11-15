using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataBridge.Application.Connections;
using DataBridge.Application.SchemaComparison;
using DataBridge.Core.Comparison;
using DataBridge.Core.Connections;
using DataBridge.Core.Schema;
using Microsoft.Extensions.Logging;

namespace DataBridge.Desktop.ViewModels;

/// <summary>A saved profile offered as a source or target.</summary>
public sealed record ProfileOption(Guid Id, string Name, DataBridge.Core.Connections.EnvironmentLabel Environment = DataBridge.Core.Connections.EnvironmentLabel.None)
{
    public override string ToString() => Name;
}

/// <summary>A row in the comparison results: one object and how it differs.</summary>
public sealed record SchemaDifferenceItem(SchemaObjectDifference Difference)
{
    public string Name => Difference.ObjectId.DisplayName;

    public string TypeText => Difference.ObjectId.ObjectType switch
    {
        DatabaseObjectType.StoredProcedure => "Stored procedure",
        DatabaseObjectType.ScalarFunction => "Scalar function",
        DatabaseObjectType.TableValuedFunction => "Table-valued function",
        DatabaseObjectType.PrimaryKey => "Primary key",
        DatabaseObjectType.UniqueConstraint => "Unique constraint",
        DatabaseObjectType.ForeignKey => "Foreign key",
        DatabaseObjectType.CheckConstraint => "Check constraint",
        DatabaseObjectType.DefaultConstraint => "Default constraint",
        var type => type.ToString(),
    };

    public string StatusText => Difference.Status switch
    {
        DifferenceStatus.Equal => "Equal",
        DifferenceStatus.SourceOnly => "Only in source",
        DifferenceStatus.TargetOnly => "Only in target",
        DifferenceStatus.Modified => "Different",
        DifferenceStatus.Incompatible => "Incompatible",
        _ => "Cannot compare",
    };

    /// <summary>The second line: why it cannot be compared, or which properties differ.</summary>
    public string Detail => Difference.Reason
        ?? (Difference.Properties.Count > 0
            ? string.Join(", ", Difference.Properties.Select(property => property.PropertyName))
            : TypeText);

    public bool IsChild => Difference.ObjectId.IsChild;

    // Signal variants, chosen by what a sync would do to the target (DESIGN.md).
    public bool IsAdd => Difference.Status == DifferenceStatus.SourceOnly;

    public bool IsRemove => Difference.Status == DifferenceStatus.TargetOnly;

    public bool IsChange => Difference.Status == DifferenceStatus.Modified;

    public bool IsCleared => Difference.Status == DifferenceStatus.Equal;

    public bool IsBlocked => Difference.Status == DifferenceStatus.Incompatible;

    public bool IsWarning => Difference.Status == DifferenceStatus.Unknown;
}

/// <summary>An object one side has but the comparison does not cover, with the reason.</summary>
public sealed record UnsupportedItem(ComparisonSide Side, UnsupportedObject Item)
{
    public string Name => Item.DisplayName;

    public string Detail => $"{Side} · {Item.ObjectTypeDescription} · {Item.Reason}";
}

/// <summary>
/// The Schema comparison screen (FR-SCH-001 to FR-SCH-012): pick two saved profiles, compare their schemas off the
/// UI thread with progress and cancellation, and browse the differences. Read-only on both databases.
/// </summary>
public sealed partial class SchemaComparisonViewModel : ViewModelBase
{
    private readonly ListConnectionProfiles _list;
    private readonly CompareSchemas _compare;
    private readonly ILogger<SchemaComparisonViewModel> _logger;
    private IReadOnlyList<SchemaDifferenceItem> _allDifferences = [];

    public SchemaComparisonViewModel(ListConnectionProfiles list, CompareSchemas compare, ILogger<SchemaComparisonViewModel> logger)
    {
        _list = list;
        _compare = compare;
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

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompareCommand), nameof(LoadCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsBusy { get; private set; }

    public bool IsIdle => !IsBusy;

    [ObservableProperty]
    public partial string? StatusText { get; private set; }

    /// <summary>Why the comparison could not run, in words the user can act on.</summary>
    [ObservableProperty]
    public partial string? ComparisonError { get; private set; }

    /// <summary>An operation failed unexpectedly. Details are in the log.</summary>
    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReport), nameof(SourceIdentity), nameof(TargetIdentity), nameof(IsSameDatabase), nameof(HasPartialMetadata))]
    public partial SchemaComparisonReport? Report { get; private set; }

    public bool HasReport => Report is not null;

    public ResolvedDatabaseIdentity? SourceIdentity => Report?.Source;

    public ResolvedDatabaseIdentity? TargetIdentity => Report?.Target;

    /// <summary>Both profiles resolve to the same database: the route sign turns to its alert state.</summary>
    public bool IsSameDatabase => Report?.IsSameDatabase == true;

    public bool HasPartialMetadata => Report is { } report && (!report.SourceHasCompleteMetadata || !report.TargetHasCompleteMetadata);

    /// <summary>Whether equal objects are listed too; by default only differences are.</summary>
    [ObservableProperty]
    public partial bool ShowEqual { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoVisibleDifferences))]
    public partial IReadOnlyList<SchemaDifferenceItem> VisibleDifferences { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedProperties), nameof(HasSelection))]
    public partial SchemaDifferenceItem? SelectedDifference { get; set; }

    public bool HasSelection => SelectedDifference is not null;

    public bool HasNoVisibleDifferences => VisibleDifferences.Count == 0;

    public bool HasUnsupportedObjects => UnsupportedObjects.Count > 0;

    public IReadOnlyList<PropertyDifference> SelectedProperties => SelectedDifference?.Difference.Properties ?? [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnsupportedObjects))]
    public partial IReadOnlyList<UnsupportedItem> UnsupportedObjects { get; private set; } = [];

    [ObservableProperty]
    public partial string? SummaryText { get; private set; }

    public string? SelectionHint => SelectedSource is not null && SelectedSource.Id == SelectedTarget?.Id
        ? "Choose a different profile for the target."
        : null;

    [RelayCommand(CanExecute = nameof(IsIdle), IncludeCancelCommand = true)]
    private Task LoadAsync(CancellationToken cancellationToken) =>
        RunAsync("load profiles", "Loading profiles…", async token =>
        {
            var profiles = await RunInBackgroundAsync(_list.ExecuteAsync, token);
            var (sourceId, targetId) = (SelectedSource?.Id, SelectedTarget?.Id);
            Profiles.Clear();
            foreach (var profile in profiles)
            {
                Profiles.Add(new ProfileOption(profile.Id, profile.Name));
            }

            SelectedSource = Profiles.FirstOrDefault(option => option.Id == sourceId);
            SelectedTarget = Profiles.FirstOrDefault(option => option.Id == targetId);
            StatusText = Profiles.Count < 2 ? "Save two connection profiles to compare their schemas." : null;
        }, cancellationToken);

    private bool CanCompare() =>
        IsIdle && SelectedSource is not null && SelectedTarget is not null && SelectedSource.Id != SelectedTarget.Id;

    [RelayCommand(CanExecute = nameof(CanCompare), IncludeCancelCommand = true)]
    private Task CompareAsync(CancellationToken cancellationToken) =>
        RunAsync("compare schemas", "Connecting…", async token =>
        {
            // A previous result describes other databases or an older state; never show it next to a new attempt.
            ComparisonError = null;
            ShowReport(null, []);
            var (sourceId, targetId) = (SelectedSource!.Id, SelectedTarget!.Id);
            var progress = new Progress<ComparisonStatus>(status => StatusText = Describe(status));

            var outcome = await RunInBackgroundAsync(
                async innerToken =>
                {
                    var result = await _compare.ExecuteAsync(sourceId, targetId, progress, innerToken);
                    return (Result: result, Items: result.IsSuccess ? BuildItems(result.Value.Result) : []);
                },
                token);

            if (outcome.Result.IsFailure)
            {
                ComparisonError = string.Join(Environment.NewLine, outcome.Result.Errors.Select(error => error.Message));
                StatusText = "The comparison did not run.";
                return;
            }

            ShowReport(outcome.Result.Value, outcome.Items);
        }, cancellationToken);

    [RelayCommand]
    private void CancelBusy()
    {
        CompareCommand.Cancel();
        LoadCommand.Cancel();
    }

    partial void OnShowEqualChanged(bool value) => ApplyFilter();

    private void ShowReport(SchemaComparisonReport? report, IReadOnlyList<SchemaDifferenceItem> items)
    {
        _allDifferences = items;
        Report = report;
        if (report is null)
        {
            UnsupportedObjects = [];
            SummaryText = null;
            ApplyFilter();
            return;
        }

        UnsupportedObjects =
        [
            .. report.Result.SourceUnsupportedObjects.Select(item => new UnsupportedItem(ComparisonSide.Source, item)),
            .. report.Result.TargetUnsupportedObjects.Select(item => new UnsupportedItem(ComparisonSide.Target, item)),
        ];
        SummaryText = Summarize(report.Result);
        ApplyFilter();
        StatusText = report.Result.HasDifferences ? "Comparison finished." : "Comparison finished: the schemas match.";
    }

    private void ApplyFilter()
    {
        var selected = SelectedDifference?.Difference.ObjectId;
        VisibleDifferences = ShowEqual
            ? _allDifferences
            : _allDifferences.Where(item => item.Difference.Status != DifferenceStatus.Equal).ToArray();
        SelectedDifference = VisibleDifferences.FirstOrDefault(item => item.Difference.ObjectId == selected);
    }

    private static SchemaDifferenceItem[] BuildItems(SchemaComparisonResult result) =>
        result.Differences.Select(difference => new SchemaDifferenceItem(difference)).ToArray();

    private static string Summarize(SchemaComparisonResult result)
    {
        var parts = new (DifferenceStatus Status, string Text)[]
        {
            (DifferenceStatus.Modified, "different"),
            (DifferenceStatus.SourceOnly, "only in source"),
            (DifferenceStatus.TargetOnly, "only in target"),
            (DifferenceStatus.Incompatible, "incompatible"),
            (DifferenceStatus.Unknown, "cannot compare"),
            (DifferenceStatus.Equal, "equal"),
        };
        var summary = parts
            .Select(part => (Count: result.Count(part.Status), part.Text))
            .Where(part => part.Count > 0)
            .Select(part => $"{part.Count} {part.Text}");
        var unsupported = result.SourceUnsupportedObjects.Count + result.TargetUnsupportedObjects.Count;
        return string.Join(" · ", unsupported > 0 ? summary.Append($"{unsupported} not compared") : summary);
    }

    private static string Describe(ComparisonStatus status) => status switch
    {
        ComparisonStatus.Connecting => "Connecting…",
        ComparisonStatus.ReadingMetadata => "Reading both schemas…",
        ComparisonStatus.ComparingSchema => "Comparing…",
        _ => status.ToString(),
    };

    /// <summary>Runs one operation with busy state, status text, cancellation and error reporting.</summary>
    private async Task RunAsync(string operation, string progressText, Func<CancellationToken, Task> work, CancellationToken cancellationToken)
    {
        IsBusy = true;
        ErrorMessage = null;
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
        }
    }
}
