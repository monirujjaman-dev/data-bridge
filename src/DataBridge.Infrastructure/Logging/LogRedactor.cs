using System.Text.RegularExpressions;
using DataBridge.Application.Connections;

namespace DataBridge.Infrastructure.Logging;

/// <summary>
/// Removes secrets from log text (NFR-SEC-003). Applied to every formatted log line, so it also covers
/// exception messages, stack traces and literal message-template text that property-level redaction cannot see.
/// </summary>
/// <remarks>
/// Recognizes <c>key=value</c> pairs as in connection strings (<c>Password=…;</c>, <c>Pwd=…</c>) and JSON
/// properties (<c>"password":"…"</c>) whose key names a secret. It is a safety net, not a licence to log
/// secrets: code must still never pass a secret to a logger.
/// </remarks>
public static partial class LogRedactor
{
    /// <summary>Replaces each secret value; equal to <see cref="Secret.RedactedText"/>.</summary>
    public const string Replacement = Secret.RedactedText;

    /// <summary>Whether a property or key name denotes a secret, ignoring case, spaces, underscores and hyphens.</summary>
    public static bool IsSensitiveName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var normalized = name.Replace(" ", "", StringComparison.Ordinal)
            .Replace("_", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal);
        return SensitiveNameRegex().IsMatch(normalized);
    }

    /// <summary>Returns <paramref name="text"/> with every recognized secret value replaced.</summary>
    public static string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var redacted = KeyValueRegex().Replace(text, "${key}" + Replacement);
        return JsonPropertyRegex().Replace(redacted, "${key}\"" + Replacement + "\"");
    }

    // password, passwd, pwd, secret(s), client secret, token(s), access/refresh token, api key, private key,
    // and longer names containing them (UserPassword, DbPwd, ClientSecretValue).
    [GeneratedRegex(
        "(password|passwd|pwd|secret|token|apikey|privatekey|accesskey)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveNameRegex();

    // key = value in connection-string style. The value runs to ';', a line break or end of text; quoted
    // values ("…" or '…', with doubled quotes as escapes) may contain ';'. Inside JSON output, line breaks
    // and quotes arrive escaped (\n, \"), so the value also stops at an escaped line break, tab or quote,
    // and a JSON-escaped quoted value (\"…\") is matched whole. Other backslashes are part of the value.
    [GeneratedRegex(
        """(?<key>\b[\w ]*(?:password|passwd|pwd|secret|token|api ?key|private ?key|access ?key)[\w]*\s*=\s*)(?<value>"(?:[^"]|"")*"|'(?:[^']|'')*'|\\"(?:(?!\\")[^\r\n])*\\"|(?:[^;\r\n"\\]|\\\\|\\(?![nrt"]))*)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KeyValueRegex();

    // "key": "value" in JSON, for structured output and serialized objects.
    [GeneratedRegex(
        """(?<key>"[\w\-]*(?:password|passwd|pwd|secret|token|apikey|api_key|privatekey|accesskey)[\w\-]*"\s*:\s*)"(?:[^"\\]|\\.)*"(?=\s*[,}\]])""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex JsonPropertyRegex();
}
