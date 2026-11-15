using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataBridge.Core.Schema;

/// <summary>
/// A SHA-256 over a schema's content, used to notice that a write target changed after it was compared
/// (FR-PLN-008, FR-PLN-012). Ignores when the schema was read and the order the catalog returned objects in; every
/// definition, option and unsupported object counts. Equal fingerprints make an unchanged schema very likely, not
/// certain (domain-model §11).
/// </summary>
public static class SchemaFingerprint
{
    private static readonly JsonSerializerOptions _options = new()
    {
        Converters = { new BigIntegerConverter(), new JsonStringEnumConverter() },
        WriteIndented = false,
    };

    public static string Compute(DatabaseSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        var canonical = new
        {
            schema.DatabaseName,
            schema.IdentifiersAreCaseSensitive,
            schema.HasCompleteMetadata,
            Schemas = schema.Schemas.OrderBy(item => item.Name, StringComparer.Ordinal).Select(Canonical).ToArray(),
            Unsupported = schema.UnsupportedObjects
                .OrderBy(item => item.SchemaName, StringComparer.Ordinal)
                .ThenBy(item => item.ObjectName, StringComparer.Ordinal)
                .ThenBy(item => item.ObjectTypeDescription, StringComparer.Ordinal)
                .ToArray(),
        };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(canonical, _options);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static object Canonical(SchemaDefinition schema) => new
    {
        schema.Name,
        Tables = schema.Tables.OrderBy(table => table.Id.ObjectName, StringComparer.Ordinal).Select(Canonical).ToArray(),
        Views = schema.Views.OrderBy(view => view.Id.ObjectName, StringComparer.Ordinal)
            .Select(view => view with { Module = Canonical(view.Module), Indexes = ByName(view.Indexes, index => index.Name) })
            .ToArray(),
        Modules = schema.ProgrammableObjects
            .OrderBy(module => module.Id.ObjectName, StringComparer.Ordinal)
            .ThenBy(module => module.Id.ParentObjectName, StringComparer.Ordinal)
            .Select(module => module with { Module = Canonical(module.Module) })
            .ToArray(),
        Sequences = ByName(schema.Sequences, sequence => sequence.Id.ObjectName),
    };

    private static TableDefinition Canonical(TableDefinition table) => table with
    {
        Columns = [.. table.Columns.OrderBy(column => column.OrdinalPosition)],
        UniqueConstraints = ByName(table.UniqueConstraints, constraint => constraint.Name),
        ForeignKeys = ByName(table.ForeignKeys, constraint => constraint.Name),
        CheckConstraints = ByName(table.CheckConstraints, constraint => constraint.Name),
        DefaultConstraints = ByName(table.DefaultConstraints, constraint => constraint.Name),
        Indexes = ByName(table.Indexes, index => index.Name),
    };

    private static ModuleDefinition Canonical(ModuleDefinition module) => module with
    {
        References = [.. module.EffectiveReferences
            .OrderBy(reference => reference.SchemaName, StringComparer.Ordinal)
            .ThenBy(reference => reference.ObjectName, StringComparer.Ordinal)],
    };

    private static T[] ByName<T>(IEnumerable<T> items, Func<T, string> name) =>
        [.. items.OrderBy(name, StringComparer.Ordinal)];

    private sealed class BigIntegerConverter : JsonConverter<BigInteger>
    {
        public override BigInteger Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            BigInteger.Parse(reader.GetString()!, CultureInfo.InvariantCulture);

        public override void Write(Utf8JsonWriter writer, BigInteger value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
    }
}
