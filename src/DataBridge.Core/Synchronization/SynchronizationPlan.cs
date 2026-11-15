using System.Collections.ObjectModel;
using DataBridge.Core.Connections;
using DataBridge.Core.Schema;

namespace DataBridge.Core.Synchronization;

/// <summary>
/// An ordered, dependency-aware set of operations that would make the write target match the other side
/// (domain-model §9). Immutable: there is no way to change a plan after <see cref="Create"/>; any change means a new
/// plan with a new <see cref="Id"/> and <see cref="ContentHash"/>, and an approval applies only to the content hash it
/// was given for (FR-PLN-011).
/// </summary>
/// <remarks>
/// Creating a plan never touches a database. <see cref="ContentHash"/> covers every field except itself, including
/// each operation's SQL, risk, dependencies and row change set, so two plans with the same hash describe the same
/// work.
/// </remarks>
public sealed class SynchronizationPlan
{
    private SynchronizationPlan(
        Guid id,
        Guid comparisonSessionId,
        SynchronizationDirection direction,
        Guid writeTargetConnectionId,
        string writeTargetDatabaseName,
        string writeTargetServerName,
        Guid? writeTargetDatabaseGuid,
        string? copySideIdentityFingerprint,
        EnvironmentLabel writeTargetEnvironment,
        string writeTargetIdentityFingerprint,
        string writeTargetSchemaFingerprint,
        SynchronizationPlanOptions options,
        IReadOnlyList<SynchronizationOperation> operations,
        IReadOnlyList<PlanIssue> notIncluded,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        ComparisonSessionId = comparisonSessionId;
        Direction = direction;
        WriteTargetConnectionId = writeTargetConnectionId;
        WriteTargetDatabaseName = writeTargetDatabaseName;
        WriteTargetServerName = writeTargetServerName;
        WriteTargetDatabaseGuid = writeTargetDatabaseGuid;
        CopySideIdentityFingerprint = copySideIdentityFingerprint;
        WriteTargetEnvironment = writeTargetEnvironment;
        WriteTargetIdentityFingerprint = writeTargetIdentityFingerprint;
        WriteTargetSchemaFingerprint = writeTargetSchemaFingerprint;
        Options = options;
        Operations = operations;
        NotIncluded = notIncluded;
        CreatedAtUtc = createdAtUtc;
        ContentHash = ComputeContentHash();
    }

    public Guid Id { get; }

    /// <summary>The comparison the plan was built from.</summary>
    public Guid ComparisonSessionId { get; }

    public SynchronizationDirection Direction { get; }

    /// <summary>The profile of the database the plan writes to.</summary>
    public Guid WriteTargetConnectionId { get; }

    /// <summary>The write target's database name as the server reported it.</summary>
    public string WriteTargetDatabaseName { get; }

    /// <summary>The write target's server name as the server reported it.</summary>
    public string WriteTargetServerName { get; }

    /// <summary>The write target's database GUID, when the server reports one.</summary>
    public Guid? WriteTargetDatabaseGuid { get; }

    /// <summary>
    /// The identity fingerprint of the database copied from, as compared, or <see langword="null"/> when the plan was
    /// built without it. Informational: it is covered by the content hash and shown for review, but not revalidated
    /// before execution, since nothing is written to that database.
    /// </summary>
    public string? CopySideIdentityFingerprint { get; }

    /// <summary>The write target profile's environment label when the plan was created.</summary>
    public EnvironmentLabel WriteTargetEnvironment { get; }

    /// <summary>The write target's <see cref="ResolvedDatabaseIdentity.IdentityFingerprint"/> when compared.</summary>
    public string WriteTargetIdentityFingerprint { get; }

    /// <summary>The write target's <see cref="SchemaFingerprint"/> when compared; a different one invalidates the plan.</summary>
    public string WriteTargetSchemaFingerprint { get; }

    public SynchronizationPlanOptions Options { get; }

    /// <summary>The operations in execution order.</summary>
    public IReadOnlyList<SynchronizationOperation> Operations { get; }

    /// <summary>Selected differences that are not in the plan, with the reason.</summary>
    public IReadOnlyList<PlanIssue> NotIncluded { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    /// <summary>SHA-256 over all other fields and every operation.</summary>
    public string ContentHash { get; }

    public bool HasDestructiveOperations => Operations.Any(operation => operation.IsDestructive);

    /// <summary>The operations that need individual approval: High, Critical and destructive ones.</summary>
    public IReadOnlyList<SynchronizationOperation> OperationsRequiringApproval =>
        [.. Operations.Where(operation => operation.RequiresExplicitApproval)];

    /// <summary>Creates a plan, checking the invariants that keep execution order and approvals meaningful.</summary>
    /// <exception cref="ArgumentException">
    /// Orders are not 0..n-1, an id repeats, an operation depends on a later or unknown one, or a destructive operation
    /// is present without <see cref="SynchronizationPlanOptions.AllowDestructiveOperations"/>.
    /// </exception>
    public static SynchronizationPlan Create(
        Guid id,
        Guid comparisonSessionId,
        SynchronizationDirection direction,
        Guid writeTargetConnectionId,
        ResolvedDatabaseIdentity writeTarget,
        EnvironmentLabel writeTargetEnvironment,
        string writeTargetSchemaFingerprint,
        SynchronizationPlanOptions options,
        IEnumerable<SynchronizationOperation> operations,
        IEnumerable<PlanIssue> notIncluded,
        DateTimeOffset createdAtUtc,
        ResolvedDatabaseIdentity? copySide = null)
    {
        ArgumentNullException.ThrowIfNull(writeTarget);
        ArgumentException.ThrowIfNullOrWhiteSpace(writeTargetSchemaFingerprint);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(notIncluded);
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A plan needs an id.", nameof(id));
        }

        if (createdAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamps must be UTC.", nameof(createdAtUtc));
        }

        var ordered = operations.Select(Freeze).OrderBy(operation => operation.Order).ToArray();
        CheckOrder(ordered, options);

        return new SynchronizationPlan(
            id,
            comparisonSessionId,
            direction,
            writeTargetConnectionId,
            writeTarget.DatabaseName,
            writeTarget.ServerName,
            writeTarget.DatabaseGuid,
            copySide?.IdentityFingerprint,
            writeTargetEnvironment,
            writeTarget.IdentityFingerprint,
            writeTargetSchemaFingerprint,
            options,
            new ReadOnlyCollection<SynchronizationOperation>(ordered),
            new ReadOnlyCollection<PlanIssue>([.. notIncluded]),
            createdAtUtc);
    }

    /// <summary>Recomputes the hash from the content; equals <see cref="ContentHash"/> for every plan.</summary>
    public string ComputeContentHash()
    {
        using var hash = new CanonicalHash();
        hash.Add("plan", Id)
            .Add("session", ComparisonSessionId)
            .Add("direction", Direction)
            .Add("writeTargetConnection", WriteTargetConnectionId)
            .Add("writeTargetDatabase", WriteTargetDatabaseName)
            .Add("writeTargetServer", WriteTargetServerName)
            .Add("writeTargetGuid", WriteTargetDatabaseGuid?.ToString("D"))
            .Add("copySideIdentity", CopySideIdentityFingerprint)
            .Add("writeTargetEnvironment", WriteTargetEnvironment)
            .Add("writeTargetIdentity", WriteTargetIdentityFingerprint)
            .Add("writeTargetSchema", WriteTargetSchemaFingerprint)
            .Add("allowDestructive", Options.AllowDestructiveOperations)
            .Add("batchSize", Options.BatchSize)
            .Add("largeRows", Options.LargeRowCountThreshold)
            .Add("created", CreatedAtUtc);

        hash.List("operations", Operations.Count);
        foreach (var operation in Operations)
        {
            AddOperation(hash, operation);
        }

        hash.List("notIncluded", NotIncluded.Count);
        foreach (var issue in NotIncluded)
        {
            AddObject(hash, "issueObject", issue.ObjectId);
            hash.Add("issue", issue.Message);
        }

        return hash.Finish();
    }

    private static void AddOperation(CanonicalHash hash, SynchronizationOperation operation)
    {
        hash.Add("id", operation.Id).Add("order", operation.Order);
        AddObject(hash, "object", operation.ObjectId);
        hash.Add("type", operation.Type)
            .Add("risk", operation.Risk)
            .Add("destructive", operation.IsDestructive)
            .Add("description", operation.Description)
            .Add("affectedRows", operation.EstimatedAffectedRows);

        hash.List("dependsOn", operation.DependsOn.Count);
        foreach (var dependency in operation.DependsOn)
        {
            hash.Add("dependency", dependency);
        }

        hash.List("sql", operation.SqlBatches.Count);
        foreach (var batch in operation.SqlBatches)
        {
            hash.Add("batch", batch);
        }

        hash.List("warnings", operation.Warnings.Count);
        foreach (var warning in operation.Warnings)
        {
            hash.Add("warning", warning);
        }

        if (operation.Rows is not { } rows)
        {
            hash.Add("rows", (string?)null);
            return;
        }

        hash.Add("rowsStore", rows.StoreId)
            .Add("rowsStatus", rows.Status)
            .Add("rowsCount", rows.RowCount)
            .Add("rowsHash", rows.ContentHash);
        hash.List("rowsKey", rows.KeyColumns.Count);
        foreach (var column in rows.KeyColumns)
        {
            hash.Add("key", column);
        }

        hash.List("rowsColumns", rows.Columns.Count);
        foreach (var column in rows.Columns)
        {
            hash.Add("column", column);
        }
    }

    private static void AddObject(CanonicalHash hash, string name, DatabaseObjectId? id)
    {
        if (id is not { } value)
        {
            hash.Add(name, (string?)null);
            return;
        }

        hash.Add(name + ".schema", value.SchemaName)
            .Add(name + ".name", value.ObjectName)
            .Add(name + ".type", value.ObjectType)
            .Add(name + ".parent", value.ParentObjectName);
    }

    /// <summary>Copies an operation's lists so a caller's later changes to them cannot reach the plan.</summary>
    private static SynchronizationOperation Freeze(SynchronizationOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return operation with
        {
            DependsOn = new ReadOnlyCollection<Guid>([.. operation.DependsOn]),
            SqlBatches = new ReadOnlyCollection<string>([.. operation.SqlBatches]),
            Warnings = new ReadOnlyCollection<string>([.. operation.Warnings]),
            Rows = operation.Rows is { } rows
                ? rows with
                {
                    KeyColumns = new ReadOnlyCollection<string>([.. rows.KeyColumns]),
                    Columns = new ReadOnlyCollection<string>([.. rows.Columns]),
                }
                : null,
        };
    }

    private static void CheckOrder(SynchronizationOperation[] operations, SynchronizationPlanOptions options)
    {
        var positions = new Dictionary<Guid, int>();
        for (var index = 0; index < operations.Length; index++)
        {
            var operation = operations[index];
            if (operation.Order != index)
            {
                throw new ArgumentException("Operation orders must run from 0 without gaps.", nameof(operations));
            }

            if (operation.Id == Guid.Empty || !positions.TryAdd(operation.Id, index))
            {
                throw new ArgumentException("Operation ids must be unique and not empty.", nameof(operations));
            }

            if (operation.DependsOn.Any(dependency => !positions.TryGetValue(dependency, out var position) || position >= index))
            {
                throw new ArgumentException("An operation may depend only on operations that come before it.", nameof(operations));
            }

            if (operation.IsDestructive && !options.AllowDestructiveOperations)
            {
                throw new ArgumentException("Destructive operations need AllowDestructiveOperations.", nameof(operations));
            }
        }
    }
}
