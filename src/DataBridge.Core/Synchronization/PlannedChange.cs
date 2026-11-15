using DataBridge.Core.Schema;

namespace DataBridge.Core.Synchronization;

/// <summary>
/// What a planned change does, in enough detail for a provider to generate its SQL. Names are as they must appear in
/// the write target. A definition copied from the other side keeps that side's names; a system-generated constraint
/// name is never copied (the engine names it).
/// </summary>
public abstract record SchemaChangeDetail;

public sealed record CreateSchemaDetail(string Name) : SchemaChangeDetail;

public sealed record DropSchemaDetail(string Name) : SchemaChangeDetail;

/// <summary>Creates a table with its columns, primary key, unique, check and default constraints; foreign keys and indexes are separate changes.</summary>
public sealed record CreateTableDetail(TableDefinition Table) : SchemaChangeDetail;

public sealed record DropTableDetail(DatabaseObjectId Table) : SchemaChangeDetail;

/// <summary>Adds a column, with its default constraint when it has one.</summary>
public sealed record AddColumnDetail(DatabaseObjectId Table, ColumnDefinition Column, DefaultConstraintDefinition? Default) : SchemaChangeDetail;

/// <summary>Changes a column's type, length, precision, scale, collation or nullability in place.</summary>
public sealed record AlterColumnDetail(DatabaseObjectId Table, ColumnDefinition Column, ColumnDefinition Existing) : SchemaChangeDetail;

public sealed record DropColumnDetail(DatabaseObjectId Table, string ColumnName) : SchemaChangeDetail;

/// <summary>
/// Adds a constraint to an existing table: a <see cref="PrimaryKeyDefinition"/>, <see cref="UniqueConstraintDefinition"/>,
/// <see cref="ForeignKeyDefinition"/>, <see cref="CheckConstraintDefinition"/> or <see cref="DefaultConstraintDefinition"/>.
/// </summary>
/// <param name="Table">The table.</param>
/// <param name="Constraint">The constraint definition.</param>
/// <param name="WithNoCheck">For foreign keys and checks: add without checking existing rows, as the copied constraint is untrusted.</param>
public sealed record AddConstraintDetail(DatabaseObjectId Table, object Constraint, bool WithNoCheck) : SchemaChangeDetail;

public sealed record DropConstraintDetail(DatabaseObjectId Table, string Name) : SchemaChangeDetail;

/// <summary>Creates an index on a table or view.</summary>
public sealed record CreateIndexDetail(DatabaseObjectId Parent, IndexDefinition Index) : SchemaChangeDetail;

public sealed record DropIndexDetail(DatabaseObjectId Parent, string Name) : SchemaChangeDetail;

/// <summary>Creates a view, procedure, function or trigger from its definition, or alters it in place (<see cref="IsAlter"/>).</summary>
public sealed record CreateModuleDetail(DatabaseObjectId Id, ModuleDefinition Module, bool IsAlter) : SchemaChangeDetail;

public sealed record DropModuleDetail(DatabaseObjectId Id) : SchemaChangeDetail;

/// <summary>Creates a sequence, or alters an existing one's increment, limits, cycling and cache (<see cref="IsAlter"/>).</summary>
public sealed record CreateSequenceDetail(SequenceDefinition Sequence, bool IsAlter) : SchemaChangeDetail;

public sealed record DropSequenceDetail(DatabaseObjectId Id) : SchemaChangeDetail;

/// <summary>A data operation on one write-target table.</summary>
/// <param name="Table">The write-target table.</param>
/// <param name="Rows">The rows to change.</param>
/// <param name="UsesIdentityInsert">Whether inserted rows carry identity values, so <c>IDENTITY_INSERT</c> is turned on for this table only.</param>
public sealed record RowsDetail(TableDefinition Table, RowChangeSet Rows, bool UsesIdentityInsert) : SchemaChangeDetail;

/// <summary>One change before ordering and SQL generation.</summary>
/// <param name="ObjectId">The object it changes, as named in the write target.</param>
/// <param name="Type">What it does.</param>
/// <param name="Risk">How much harm it can do.</param>
/// <param name="IsDestructive">Whether it can remove objects, columns or rows, or lose data.</param>
/// <param name="Description">What it does, in words.</param>
/// <param name="Warnings">Side effects or likely failures the user should know about.</param>
/// <param name="Rank">A tie-breaker for ordering; lower runs first when dependencies allow.</param>
/// <param name="Detail">What a provider needs to generate the SQL.</param>
/// <param name="EstimatedAffectedRows">For data changes, the rows found by comparison.</param>
public sealed record PlannedChange(
    DatabaseObjectId ObjectId,
    SynchronizationOperationType Type,
    OperationRisk Risk,
    bool IsDestructive,
    string Description,
    IReadOnlyList<string> Warnings,
    int Rank,
    SchemaChangeDetail Detail,
    long? EstimatedAffectedRows = null);

/// <summary>
/// The changes and issues collected while planning, with dependencies between changes by index. Filled by the schema
/// and data planners, then turned into a plan by <see cref="PlanAssembler"/>.
/// </summary>
public sealed class PlanDraft
{
    private readonly List<PlannedChange> _changes = [];
    private readonly List<HashSet<int>> _dependencies = [];
    private readonly List<HashSet<int>> _partners = [];
    private readonly List<PlanIssue> _issues = [];

    public IReadOnlyList<PlannedChange> Changes => _changes;

    public IReadOnlyList<PlanIssue> Issues => _issues;

    public int Add(PlannedChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        _changes.Add(change);
        _dependencies.Add([]);
        _partners.Add([]);
        return _changes.Count - 1;
    }

    /// <summary>Records that <paramref name="change"/> must run after <paramref name="dependency"/>.</summary>
    public void DependOn(int change, int dependency)
    {
        if (change != dependency)
        {
            _dependencies[change].Add(dependency);
        }
    }

    public IReadOnlyCollection<int> DependenciesOf(int change) => _dependencies[change];

    /// <summary>
    /// Records that two changes stand or fall together: if either is left out, so is the other. Used for a
    /// constraint's drop and recreate, and for a drop added because a selected drop needs it, so a plan never drops
    /// something it promised to recreate, or removes a prerequisite for a drop it does not make.
    /// </summary>
    public void Bind(int change, int partner)
    {
        if (change == partner)
        {
            return;
        }

        _partners[change].Add(partner);
        _partners[partner].Add(change);
    }

    public IReadOnlyCollection<int> PartnersOf(int change) => _partners[change];

    public void Report(DatabaseObjectId? objectId, string message) => _issues.Add(new PlanIssue(objectId, message));
}
