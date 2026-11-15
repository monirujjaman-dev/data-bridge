using DataBridge.Core.Comparison;
using DataBridge.Core.Connections;
using DataBridge.Core.Data;
using DataBridge.Core.Schema;
using DataBridge.Core.Synchronization;
using DataBridge.Shared.Errors;
using DataBridge.Shared.Results;

namespace DataBridge.Application.Abstractions;

/// <summary>
/// Generates a provider's SQL for planned changes (architecture §6). Pure: it never connects to a database. DML is
/// always a parameterized template that contains no row values, and identifiers are quoted with the provider's
/// rules (FR-EXE-003).
/// </summary>
public interface ISqlGenerator
{
    DatabaseProviderType ProviderType { get; }

    /// <summary>The batches that perform <paramref name="change"/>, or a failure saying why it cannot be generated.</summary>
    Result<IReadOnlyList<string>> Generate(PlannedChange change);

    /// <summary>
    /// The plan as a script for review and export (FR-PLN-009, FR-PLN-013): a header, a guard that stops it in the wrong
    /// database, and each operation in order. Data operations appear as their templates, commented out, because only
    /// DataBridge can run them with their row values and drift checks.
    /// </summary>
    string BuildScript(SynchronizationPlan plan);
}

/// <summary>How serious a validation finding is.</summary>
public enum ValidationSeverity
{
    /// <summary>Execution would fail or do harm; the plan cannot be approved.</summary>
    Blocking,

    /// <summary>Worth knowing; the plan can still be approved.</summary>
    Warning,
}

/// <summary>Something validation found about the write target.</summary>
/// <param name="OperationId">The operation it concerns, when it concerns one.</param>
/// <param name="Severity">Whether it blocks approval.</param>
/// <param name="Message">What was found; never contains row values.</param>
public sealed record ValidationFinding(Guid? OperationId, ValidationSeverity Severity, string Message);

/// <summary>
/// Checks the write target's current state against a plan (FR-PLN-008): permissions for each operation, and whether
/// existing rows would make an operation fail (NULLs where a column becomes NOT NULL, duplicates where a key or unique
/// index is added, rows a new foreign key would reject). Read-only: it only runs permission functions and
/// <c>SELECT</c>s.
/// </summary>
public interface ISynchronizationTargetInspector
{
    DatabaseProviderType ProviderType { get; }

    /// <returns>The findings, or a failure if the write target could not be reached.</returns>
    Task<Result<IReadOnlyList<ValidationFinding>>> InspectAsync(
        DatabaseConnectionRequest writeTarget,
        AssembledPlan plan,
        CancellationToken cancellationToken);
}

/// <summary>One stored differing row: its key and the fingerprints both sides had when compared.</summary>
public sealed record StoredRowChange(
    DifferenceStatus Status,
    IReadOnlyList<DatabaseValue> KeyValues,
    string? SourceFingerprint,
    string? TargetFingerprint);

/// <summary>Records one table's differing rows while it is compared. Not thread-safe; one writer per table.</summary>
public interface IChangeSetWriter : IAsyncDisposable
{
    void Append(StoredRowChange row);

    /// <summary>Finishes the file and returns the counts and hashes per status.</summary>
    Task<RecordedChangeSet> CompleteAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Local storage for the keys and fingerprints of the rows a plan changes (ADR-0016). Keys are row values, so the
/// files live only in the user's local application data, are deleted with their plans, and are never logged.
/// </summary>
public interface IChangeSetStore
{
    IChangeSetWriter CreateWriter(DatabaseObjectId table);

    /// <summary>Whether the stored rows still match the plan's count and hash; call before reading them for execution.</summary>
    Task<bool> VerifyAsync(RowChangeSet rows, CancellationToken cancellationToken);

    /// <summary>Streams the stored rows of one status in key order.</summary>
    IAsyncEnumerable<StoredRowChange> ReadAsync(string storeId, DifferenceStatus status, CancellationToken cancellationToken);

    Task DeleteAsync(string storeId, CancellationToken cancellationToken);
}

/// <summary>Synchronization use-case failures.</summary>
public static class SynchronizationUseCaseErrors
{
    public static readonly Error PlanNotFound =
        new("Synchronization.PlanNotFound", "The plan no longer exists. Create a new plan.");

    public static readonly Error NoComparison =
        new("Synchronization.NoComparison", "Compare the databases before planning.");

    public static readonly Error ComparisonsDisagree =
        new("Synchronization.ComparisonsDisagree", "The schema and data comparisons were made against different databases or schemas. Compare again.");

    public static readonly Error SameDatabase =
        new("Synchronization.SameDatabase", "Both profiles resolve to the same database, so there is nothing to synchronize.");

    public static readonly Error NotValidated =
        new("Synchronization.NotValidated", "Validate the plan before approving it.");

    public static readonly Error ValidationFailed =
        new("Synchronization.ValidationFailed", "Validation found problems that block this plan.");

    public static readonly Error Invalidated =
        new("Synchronization.Invalidated", "The write target changed since it was compared. Compare again and create a new plan.");

    public static readonly Error ChangeSetCorrupt =
        new("Synchronization.ChangeSetCorrupt", "The stored rows of this plan are missing or damaged. Compare again and create a new plan.");

    public static Error Unsupported(string message) => new("Synchronization.Unsupported", message);
}
