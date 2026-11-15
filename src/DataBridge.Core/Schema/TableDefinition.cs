using System.Numerics;

namespace DataBridge.Core.Schema;

/// <summary>
/// A table and its children. Columns are in ordinal order. <c>HeapDataCompression</c> is the compression of a
/// table without a clustered index, or <see langword="null"/> when it has one (the clustered index carries it).
/// <c>ExpressionReferences</c> are the functions, sequences and other objects its computed columns, checks and
/// defaults use, for synchronization order only; not compared.
/// </summary>
public sealed record TableDefinition(
    DatabaseObjectId Id,
    IReadOnlyList<ColumnDefinition> Columns,
    PrimaryKeyDefinition? PrimaryKey,
    IReadOnlyList<UniqueConstraintDefinition> UniqueConstraints,
    IReadOnlyList<ForeignKeyDefinition> ForeignKeys,
    IReadOnlyList<CheckConstraintDefinition> CheckConstraints,
    IReadOnlyList<DefaultConstraintDefinition> DefaultConstraints,
    IReadOnlyList<IndexDefinition> Indexes,
    string? HeapDataCompression = null,
    IReadOnlyList<ModuleReference>? ExpressionReferences = null)
{
    public IReadOnlyList<ModuleReference> EffectiveExpressionReferences => ExpressionReferences ?? [];
}

/// <summary>
/// A column. Type facets that do not apply to the type are <see langword="null"/>. Defaults are modeled as
/// <see cref="DefaultConstraintDefinition"/>, not here.
/// </summary>
/// <param name="Name">The column name.</param>
/// <param name="SqlTypeName">The type name; user-defined alias types are schema-qualified.</param>
/// <param name="MaxLength">
/// In characters for character types, bytes for binary types; <see langword="null"/> for MAX or when the type has
/// no length.
/// </param>
/// <param name="IsMaxLength">Whether the length is MAX.</param>
/// <param name="Precision">For exact numeric types only.</param>
/// <param name="Scale">For exact numeric and fractional-second temporal types only.</param>
/// <param name="IsNullable">Whether the column allows NULL.</param>
/// <param name="Identity">The identity specification, or <see langword="null"/> when the column is not an identity.</param>
/// <param name="Computed">The computed specification, or <see langword="null"/> when the column is not computed.</param>
/// <param name="CollationName">For character types only.</param>
/// <param name="OrdinalPosition">The 1-based position among the table's columns (gaps from dropped columns removed).</param>
/// <param name="Options">Storage, security and engine-managed attributes; <see langword="null"/> means none.</param>
/// <param name="ProviderBaseTypeName">For a user-defined alias type, the system type it is based on; not compared.</param>
public sealed record ColumnDefinition(
    string Name,
    string SqlTypeName,
    int? MaxLength,
    bool IsMaxLength,
    byte? Precision,
    byte? Scale,
    bool IsNullable,
    IdentitySpecification? Identity,
    ComputedSpecification? Computed,
    string? CollationName,
    int OrdinalPosition,
    ColumnOptions? Options = null,
    string? ProviderBaseTypeName = null)
{
    public ColumnOptions EffectiveOptions => Options ?? ColumnOptions.None;
}

/// <summary>
/// Column attributes beyond type and nullability that change storage, security or engine behavior. Text values
/// are as the engine names them; <see langword="null"/> means not applicable.
/// </summary>
public sealed record ColumnOptions(
    bool IsSparse,
    bool IsColumnSet,
    bool IsRowGuidColumn,
    bool IsFileStream,
    string? XmlSchemaCollection,
    string? MaskingFunction,
    string? EncryptionType,
    string? GeneratedAlwaysType,
    bool IsHidden)
{
    public static readonly ColumnOptions None = new(false, false, false, false, null, null, null, null, false);
}

/// <summary>An identity column's seed and increment: integers of up to 38 digits, so not <see cref="decimal"/>.</summary>
public sealed record IdentitySpecification(BigInteger Seed, BigInteger Increment, bool IsNotForReplication = false);

/// <summary>A computed column. <c>Expression</c> is <see langword="null"/> when the login cannot see it.</summary>
public sealed record ComputedSpecification(string? Expression, bool IsPersisted);

/// <summary>
/// A sequence. Values are integers of up to 38 digits. <c>CacheSize</c> is the explicit cache size, or
/// <see langword="null"/> for the engine default or no cache.
/// </summary>
public sealed record SequenceDefinition(
    DatabaseObjectId Id,
    string SqlTypeName,
    BigInteger StartValue,
    BigInteger Increment,
    BigInteger MinimumValue,
    BigInteger MaximumValue,
    bool IsCycling,
    bool IsCached,
    int? CacheSize);
