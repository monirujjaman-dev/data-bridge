using CommunityToolkit.Mvvm.ComponentModel;
using DataBridge.Application.Abstractions;
using DataBridge.Core.Comparison;
using DataBridge.Core.Schema;
using DataBridge.Core.Synchronization;

namespace DataBridge.Desktop.ViewModels;

/// <summary>A direction choice: which side the plan makes match the other.</summary>
public sealed record DirectionOption(SynchronizationDirection Direction, string Text)
{
    public static IReadOnlyList<DirectionOption> All { get; } =
    [
        new(SynchronizationDirection.SourceToTarget, "Source → Target (writes to the target)"),
        new(SynchronizationDirection.TargetToSource, "Target → Source (writes to the source)"),
    ];

    public override string ToString() => Text;
}

/// <summary>A difference offered for synchronization: a schema object or a table's rows.</summary>
public sealed partial class SyncDifferenceItem : ObservableObject
{
    private SyncDifferenceItem(DatabaseObjectId id, bool isData, string statusText, string detail, bool canSelect, DifferenceStatus? status)
    {
        Id = id;
        IsData = isData;
        StatusText = statusText;
        Detail = detail;
        CanSelect = canSelect;
        Status = status;
    }

    public DatabaseObjectId Id { get; }

    /// <summary>Whether this is a table's rows rather than a schema object.</summary>
    public bool IsData { get; }

    public DifferenceStatus? Status { get; }

    public string Name => IsData ? $"{Id.DisplayName} (rows)" : Id.DisplayName;

    public string KindText => IsData ? "Rows" : Id.ObjectType.ToString();

    public string StatusText { get; }

    public string Detail { get; }

    public bool CanSelect { get; }

    public bool IsAdd => Status == DifferenceStatus.SourceOnly;

    public bool IsRemove => Status == DifferenceStatus.TargetOnly;

    public bool IsChange => Status == DifferenceStatus.Modified;

    public bool IsBlocked => !CanSelect;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public static SyncDifferenceItem ForSchema(SchemaObjectDifference difference, int differingChildren)
    {
        var canSelect = difference.Status is DifferenceStatus.SourceOnly or DifferenceStatus.TargetOnly or DifferenceStatus.Modified;
        var status = difference.Status switch
        {
            DifferenceStatus.SourceOnly => "Only in source",
            DifferenceStatus.TargetOnly => "Only in target",
            DifferenceStatus.Modified => "Different",
            DifferenceStatus.Incompatible => "Incompatible",
            _ => "Unknown",
        };
        var detail = difference.Reason
            ?? (difference.Status == DifferenceStatus.Modified
                ? string.Join(", ", difference.Properties.Select(property => property.PropertyName)
                    .Concat(differingChildren > 0 ? [$"{differingChildren} differing columns, keys, constraints or indexes"] : []))
                : "");
        return new SyncDifferenceItem(difference.ObjectId, false, status, detail, canSelect, canSelect ? difference.Status : null);
    }

    public static SyncDifferenceItem ForRows(TableDataComparisonSummary summary)
    {
        var compared = summary.Outcome == TableDataComparisonOutcome.Compared;
        var detail = compared
            ? $"{summary.SourceOnlyRowCount:N0} only in source · {summary.TargetOnlyRowCount:N0} only in target · {summary.ModifiedRowCount:N0} different"
            : summary.Reason ?? "Not compared.";
        return new SyncDifferenceItem(
            summary.TableId, true, compared ? "Rows differ" : "Not compared", detail, compared, compared ? DifferenceStatus.Modified : null);
    }
}

/// <summary>One operation of the plan, with its individual approval when it needs one.</summary>
public sealed partial class PlanOperationItem(SynchronizationOperation operation) : ObservableObject
{
    public SynchronizationOperation Operation { get; } = operation;

    public string OrderText => (Operation.Order + 1).ToString(System.Globalization.CultureInfo.CurrentCulture);

    public string Description => Operation.Description;

    public string RiskText => Operation.IsDestructive ? $"{Operation.Risk} · destructive" : Operation.Risk.ToString();

    public bool IsLowRisk => Operation.Risk == OperationRisk.Low && !Operation.IsDestructive;

    public bool IsMediumRisk => Operation.Risk == OperationRisk.Medium && !Operation.IsDestructive;

    public bool IsHighRisk => Operation.Risk == OperationRisk.High && !Operation.IsDestructive;

    public bool IsDangerous => Operation.Risk == OperationRisk.Critical || Operation.IsDestructive;

    public bool NeedsApproval => Operation.RequiresExplicitApproval;

    public string ApprovalText => Operation.IsDestructive ? "I approve this destructive operation" : "I approve this high-risk operation";

    public bool HasWarnings => Operation.Warnings.Count > 0;

    public string WarningsText => string.Join(Environment.NewLine, Operation.Warnings.Select(warning => "• " + warning));

    /// <summary>The operation's SQL, batches separated by GO; for data operations, the per-row template.</summary>
    public string SqlText => string.Join(Environment.NewLine + "GO" + Environment.NewLine, Operation.SqlBatches);

    [ObservableProperty]
    public partial bool IsApproved { get; set; }
}

/// <summary>A validation finding, shown with its operation's position.</summary>
public sealed record FindingItem(ValidationFinding Finding, string Text)
{
    public bool IsBlocking => Finding.Severity == ValidationSeverity.Blocking;
}

/// <summary>Saves text the user chose to export, through the platform's save dialog.</summary>
public interface IFileSaver
{
    /// <returns>Whether the file was saved; <see langword="false"/> when the user cancelled.</returns>
    Task<bool> SaveTextAsync(string suggestedFileName, string text, CancellationToken cancellationToken);
}
