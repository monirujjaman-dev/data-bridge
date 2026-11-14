using System.Numerics;

namespace DataBridge.Core.Schema;

/// <summary>A table and its children. Columns are in ordinal order.</summary>
public sealed record TableDefinition(
    DatabaseObjectId Id,
    IReadOnlyList<ColumnDefinition> Columns,
    PrimaryKeyDefinition? PrimaryKey,
    IReadOnlyList<UniqueConstraintDefinition> UniqueConstraints,
    IReadOnlyList<ForeignKeyDefinition> ForeignKeys,
    IReadOnlyList<CheckConstraintDefinition> CheckConstraints,
    IReadOnlyList<DefaultConstraintDefinition> DefaultConstraints,
    IReadOnlyList<IndexDefinition> Indexes);

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
    int OrdinalPosition);

/// <summary>An identity column's seed and increment: integers of up to 38 digits, so not <see cref="decimal"/>.</summary>
public sealed record IdentitySpecification(BigInteger Seed, BigInteger Increment);

/// <summary>A computed column's expression as the engine stores it.</summary>
public sealed record ComputedSpecification(string Expression, bool IsPersisted);

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
