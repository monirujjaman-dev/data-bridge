using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DataBridge.Application.Connections;
using DataBridge.Application.DataComparison;
using DataBridge.Core.Comparison;
using DataBridge.Core.Connections;
using Microsoft.Extensions.Logging;

namespace DataBridge.Desktop.ViewModels;

/// <summary>
/// The Data comparison screen (FR-DAT-001 to FR-DAT-016): pick two profiles, load their tables with how each would be
/// matched, select tables (and keys where none exists), compare off the UI thread with progress and cancellation, and
/// browse per-table results and the first differing rows of each table. Read-only on both databases.
/// </summary>
public sealed partial class DataComparisonViewModel : ViewModelBase
{
    /// <summary>Differing rows kept per table for display; the rest are counted, not held (NFR-PER-002).</summary>
    public const int MaxRowsShownPerTable = 1000;

    private readonly ListConnectionProfiles _list;
    private readonly ListDataComparisonTables _tables;
    private readonly CompareData _compare;
    private readonly ILogger<DataComparisonViewModel> _logger;
    private BoundedDifferenceSink _sink = new(MaxRowsShownPerTable);

    public DataComparisonViewModel(
        ListConnectionProfiles list,
        ListDataComparisonTables tables,
        CompareData compare,
        ILogger<DataComparisonViewModel> logger)
    {
        _list = list;
        _tables = tables;
        _compare = compare;
        _logger = logger;
    }

    public ObservableCollection<ProfileOption> Profiles { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadTablesCommand), nameof(CompareCommand))]
    [NotifyPropertyChangedFor(nameof(SelectionHint))]
    public partial ProfileOption? SelectedSource { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadTablesCommand), nameof(CompareCommand))]
    [NotifyPropertyChangedFor(nameof(SelectionHint))]
    public partial ProfileOption? SelectedTarget { get; set; }

    public string? SelectionHint => SelectedSource is not null && SelectedSource.Id == SelectedTarget?.Id
        ? "Choose a different profile for the target."
        : null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadCommand), nameof(LoadTablesCommand), nameof(CompareCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsBusy { get; private set; }

    public bool IsIdle => !IsBusy;

    [ObservableProperty]
    public partial string? StatusText { get; private set; }

    [ObservableProperty]
    public partial string? ComparisonError { get; private set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    /// <summary>The identities from the last table load or comparison, for the route sign.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIdentities), nameof(IsSameDatabase), nameof(SourceIdentity), nameof(TargetIdentity))]
    public partial (ResolvedDatabaseIdentity Source, ResolvedDatabaseIdentity Target)? Identities { get; private set; }

    public bool HasIdentities => Identities is not null;

    public ResolvedDatabaseIdentity? SourceIdentity => Identities?.Source;

    public ResolvedDatabaseIdentity? TargetIdentity => Identities?.Target;

    public bool IsSameDatabase => Identities is { } pair && pair.Source.IsSameDatabaseAs(pair.Target);

    public ObservableCollection<DataTableItem> Tables { get; } = [];

    public bool HasTables => Tables.Count > 0;

    // Options (FR-DAT-011)

    [ObservableProperty]
    public partial decimal BatchSize { get; set; } = DataComparisonOptions.Default.BatchSize;

    [ObservableProperty]
    public partial decimal Parallelism { get; set; } = DataComparisonOptions.Default.MaxDegreeOfParallelism;

    [ObservableProperty]
    public partial bool IncludeBinaryColumns { get; set; } = true;

    // Results

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReport), nameof(HasNonSnapshotReads))]
    public partial DataComparisonReport? Report { get; private set; }

    public bool HasReport => Report is not null;

    public bool HasNonSnapshotReads => Report?.HasNonSnapshotReads == true;

    [ObservableProperty]
    public partial IReadOnlyList<TableResultItem> Results { get; private set; } = [];

    [ObservableProperty]
    public partial string? SummaryText { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedResult))]
    public partial TableResultItem? SelectedResult { get; set; }

    public bool HasSelectedResult => SelectedResult is not null;

    [ObservableProperty]
    public partial IReadOnlyList<RowDifferenceItem> Rows { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedRow))]
    public partial RowDifferenceItem? SelectedRow { get; set; }

    public bool HasSelectedRow => SelectedRow is not null;

    private bool CanUseProfiles() =>
        IsIdle && SelectedSource is not null && SelectedTarget is not null && SelectedSource.Id != SelectedTarget.Id;

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
            StatusText = Profiles.Count < 2 ? "Save two connection profiles to compare their data." : null;
        }, cancellationToken);

    [RelayCommand(CanExecute = nameof(CanUseProfiles), IncludeCancelCommand = true)]
    private Task LoadTablesAsync(CancellationToken cancellationToken) =>
        RunAsync("load tables", "Reading both schemas…", async token =>
        {
            ClearResults();
            Tables.Clear();
            OnPropertyChanged(nameof(HasTables));
            var (sourceId, targetId) = (SelectedSource!.Id, SelectedTarget!.Id);
            var result = await RunInBackgroundAsync(innerToken => _tables.ExecuteAsync(sourceId, targetId, innerToken), token);
            if (result.IsFailure)
            {
                ComparisonError = string.Join(Environment.NewLine, result.Errors.Select(error => error.Message));
                StatusText = "The tables could not be loaded.";
                return;
            }

            Identities = (result.Value.Source, result.Value.Target);
            foreach (var table in result.Value.Tables)
            {
                var item = new DataTableItem(table);
                item.PropertyChanged += (_, change) =>
                {
                    if (change.PropertyName == nameof(DataTableItem.IsSelected))
                    {
                        CompareCommand.NotifyCanExecuteChanged();
                    }
                };
                Tables.Add(item);
            }

            OnPropertyChanged(nameof(HasTables));
            CompareCommand.NotifyCanExecuteChanged();
            StatusText = $"{Tables.Count} tables. Select the tables to compare.";
        }, cancellationToken);

    private bool CanCompare() => CanUseProfiles() && Tables.Any(table => table.IsSelected);

    [RelayCommand(CanExecute = nameof(CanCompare), IncludeCancelCommand = true)]
    private Task CompareAsync(CancellationToken cancellationToken) =>
        RunAsync("compare data", "Connecting…", async token =>
        {
            ClearResults();
            var request = BuildRequest();
            var sink = new BoundedDifferenceSink(MaxRowsShownPerTable);
            var progress = new Progress<DataComparisonProgress>(update => StatusText = Describe(update));
            var result = await RunInBackgroundAsync(innerToken => _compare.ExecuteAsync(request, sink, progress, innerToken), token);
            if (result.IsFailure)
            {
                ComparisonError = string.Join(Environment.NewLine, result.Errors.Select(error => error.Message));
                StatusText = "The comparison did not run.";
                return;
            }

            _sink = sink;
            ShowReport(result.Value);
        }, cancellationToken);

    [RelayCommand]
    private void CancelBusy()
    {
        CompareCommand.Cancel();
        LoadTablesCommand.Cancel();
        LoadCommand.Cancel();
    }

    partial void OnSelectedResultChanged(TableResultItem? value)
    {
        Rows = value is null ? [] : [.. _sink.RowsOf(value.Summary.TableId).Select(row => new RowDifferenceItem(row))];
        SelectedRow = null;
    }

    partial void OnSelectedSourceChanged(ProfileOption? value) => ClearTables();

    partial void OnSelectedTargetChanged(ProfileOption? value) => ClearTables();

    private DataComparisonRequest BuildRequest()
    {
        var selected = Tables.Where(table => table.IsSelected).ToArray();
        var keys = selected
            .Where(table => table.UserKeyColumns.Count > 0)
            .ToDictionary(table => table.Table.TableId, table => table.UserKeyColumns);
        var options = new DataComparisonOptions(
            BatchSize: (int)BatchSize,
            MaxDegreeOfParallelism: (int)Parallelism,
            IncludeBinaryColumns: IncludeBinaryColumns);
        return new DataComparisonRequest(
            SelectedSource!.Id,
            SelectedTarget!.Id,
            [.. selected.Select(table => table.Table.TableId)],
            keys,
            options);
    }

    private void ShowReport(DataComparisonReport report)
    {
        Identities = (report.Source, report.Target);
        Report = report;
        Results = [.. report.Tables
            .OrderByDescending(table => table.DifferenceCount > 0 || table.Outcome != TableDataComparisonOutcome.Compared)
            .ThenBy(table => table.TableId.DisplayName, StringComparer.Ordinal)
            .Select(table => new TableResultItem(table, _sink.RowsOf(table.TableId).Count))];
        var compared = report.Tables.Count(table => table.Outcome == TableDataComparisonOutcome.Compared);
        var differing = report.Tables.Count(table => table.Outcome == TableDataComparisonOutcome.Compared && table.DifferenceCount > 0);
        var notCompared = report.Tables.Count - compared;
        SummaryText = $"{compared} compared · {differing} with differences · {notCompared} not compared";
        StatusText = "Comparison finished.";
        SelectedResult = Results.Count > 0 ? Results[0] : null;
    }

    private void ClearResults()
    {
        ComparisonError = null;
        Report = null;
        Results = [];
        SummaryText = null;
        SelectedResult = null;
        _sink = new BoundedDifferenceSink(MaxRowsShownPerTable);
    }

    private void ClearTables()
    {
        if (IsBusy)
        {
            return;
        }

        ClearResults();
        Tables.Clear();
        Identities = null;
        OnPropertyChanged(nameof(HasTables));
        CompareCommand.NotifyCanExecuteChanged();
    }

    private static string Describe(DataComparisonProgress progress) => progress switch
    {
        { Status: ComparisonStatus.Connecting } => "Connecting and reading both schemas…",
        { Table: { } table } when progress.SourceRowsRead > 0 || progress.TargetRowsRead > 0 =>
            string.Create(CultureInfo.CurrentCulture, $"Comparing {table.DisplayName}: {progress.SourceRowsRead:N0} source and {progress.TargetRowsRead:N0} target rows read ({progress.TablesCompleted} of {progress.TablesTotal} tables done)"),
        _ => $"Comparing… ({progress.TablesCompleted} of {progress.TablesTotal} tables done)",
    };

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
