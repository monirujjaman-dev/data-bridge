using System.Diagnostics.CodeAnalysis;
using DataBridge.Application.Connections;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;

namespace DataBridge.Infrastructure.Logging;

/// <summary>Renders a <see cref="Secret"/> as <see cref="Secret.RedactedText"/>, however it is logged.</summary>
internal sealed class SecretDestructuringPolicy : IDestructuringPolicy
{
    public bool TryDestructure(
        object value,
        ILogEventPropertyValueFactory propertyValueFactory,
        [NotNullWhen(true)] out LogEventPropertyValue? result)
    {
        if (value is Secret)
        {
            result = new ScalarValue(Secret.RedactedText);
            return true;
        }

        result = null;
        return false;
    }
}

/// <summary>
/// Replaces the value of any property whose name denotes a secret (for example <c>Password</c> or
/// <c>AccessToken</c>), and redacts secrets inside string properties such as connection strings.
/// </summary>
internal sealed class SensitivePropertyEnricher : ILogEventEnricher
{
    private static readonly ScalarValue _redacted = new(LogRedactor.Replacement);

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        foreach (var (name, value) in logEvent.Properties.ToArray())
        {
            var replacement = LogRedactor.IsSensitiveName(name) ? _redacted : Redact(value);
            if (!ReferenceEquals(replacement, value))
            {
                logEvent.AddOrUpdateProperty(new LogEventProperty(name, replacement));
            }
        }
    }

    private static LogEventPropertyValue Redact(LogEventPropertyValue value) =>
        value switch
        {
            ScalarValue { Value: string text } when LogRedactor.Redact(text) is var redacted && redacted != text =>
                new ScalarValue(redacted),
            StructureValue structure => RedactStructure(structure),
            DictionaryValue dictionary => RedactDictionary(dictionary),
            SequenceValue sequence => RedactSequence(sequence),
            _ => value,
        };

    private static StructureValue RedactStructure(StructureValue structure)
    {
        var changed = false;
        var properties = new List<LogEventProperty>(structure.Properties.Count);
        foreach (var property in structure.Properties)
        {
            var value = LogRedactor.IsSensitiveName(property.Name) ? _redacted : Redact(property.Value);
            changed |= !ReferenceEquals(value, property.Value);
            properties.Add(new LogEventProperty(property.Name, value));
        }

        return changed ? new StructureValue(properties, structure.TypeTag) : structure;
    }

    private static DictionaryValue RedactDictionary(DictionaryValue dictionary)
    {
        var changed = false;
        var elements = new List<KeyValuePair<ScalarValue, LogEventPropertyValue>>(dictionary.Elements.Count);
        foreach (var (key, element) in dictionary.Elements)
        {
            var value = key.Value is string name && LogRedactor.IsSensitiveName(name) ? _redacted : Redact(element);
            changed |= !ReferenceEquals(value, element);
            elements.Add(new(key, value));
        }

        return changed ? new DictionaryValue(elements) : dictionary;
    }

    private static SequenceValue RedactSequence(SequenceValue sequence)
    {
        var elements = sequence.Elements.Select(Redact).ToArray();
        return elements.Where((element, index) => !ReferenceEquals(element, sequence.Elements[index])).Any()
            ? new SequenceValue(elements)
            : sequence;
    }
}

/// <summary>
/// Formats with an inner formatter, then passes the whole line through <see cref="LogRedactor"/>. This is
/// the last line of defence: it also covers exception text and literal message-template text.
/// </summary>
internal sealed class RedactingTextFormatter(ITextFormatter inner) : ITextFormatter
{
    public void Format(LogEvent logEvent, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);

        using var buffer = new StringWriter(output.FormatProvider);
        inner.Format(logEvent, buffer);
        output.Write(LogRedactor.Redact(buffer.ToString()));
    }
}
