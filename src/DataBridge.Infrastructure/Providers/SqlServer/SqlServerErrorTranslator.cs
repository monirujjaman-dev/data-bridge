using System.Security.Authentication;
using DataBridge.Application.Connections;
using DataBridge.Core.Connections;
using DataBridge.Shared.Errors;
using Microsoft.Data.SqlClient;

namespace DataBridge.Infrastructure.Providers.SqlServer;

/// <summary>
/// Turns SqlClient connection failures into actionable <see cref="ConnectionErrors"/>. SqlClient reports
/// network and TLS failures differently per platform (native SNI on Windows, managed SNI elsewhere), so both
/// error numbers and the exception chain are inspected.
/// </summary>
internal static class SqlServerErrorTranslator
{
    private const int LoginFailed = 18456;
    private const int UntrustedDomain = 18452;
    private const int PasswordExpired = 18487;
    private const int PasswordMustChange = 18488;
    private const int CannotOpenDatabase = 4060;
    private const int Timeout = -2;

    /// <summary>Windows SChannel and certificate HRESULTs reported as SqlException numbers.</summary>
    private static readonly HashSet<int> _certificateErrors =
    [
        unchecked((int)0x80090325), // SEC_E_UNTRUSTED_ROOT
        unchecked((int)0x800B0109), // CERT_E_UNTRUSTEDROOT
        unchecked((int)0x80090322), // SEC_E_WRONG_PRINCIPAL (name mismatch)
        unchecked((int)0x800B010F), // CERT_E_CN_NO_MATCH
        unchecked((int)0x80090328), // SEC_E_CERT_EXPIRED
    ];

    /// <summary>
    /// Network-level errors: named-pipes/TCP provider failures, refused or unreachable hosts, unknown host
    /// names and unknown instances.
    /// </summary>
    private static readonly HashSet<int> _unreachableErrors = [-1, 2, 5, 26, 35, 40, 53, 1225, 10049, 10051, 10053, 10054, 10060, 10061, 10065, 11001, 11004];

    /// <summary>Returns the error for a failed connection attempt, or <see langword="null"/> when it is not recognized.</summary>
    public static Error? Translate(Exception exception, DatabaseConnection profile)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(profile);

        if (Chain(exception).OfType<AuthenticationException>().FirstOrDefault() is { } tls)
        {
            return Mentions(tls, "certificate") ? ConnectionErrors.CertificateNotTrusted : ConnectionErrors.EncryptionFailed;
        }

        if (exception is not SqlException sql)
        {
            return null;
        }

        var numbers = sql.Errors.Cast<SqlError>().Select(error => error.Number).Append(sql.Number).ToHashSet();

        // Checked before login failures: a missing database is reported as 4060 followed by 18456.
        if (numbers.Contains(CannotOpenDatabase))
        {
            return ConnectionErrors.DatabaseUnavailable;
        }

        if (numbers.Contains(PasswordExpired) || numbers.Contains(PasswordMustChange))
        {
            return ConnectionErrors.PasswordExpired;
        }

        if (numbers.Contains(UntrustedDomain) || IsIntegratedFailure(sql, profile))
        {
            return ConnectionErrors.IntegratedAuthenticationFailed;
        }

        if (numbers.Contains(LoginFailed))
        {
            return profile.AuthenticationType == AuthenticationType.Integrated
                ? ConnectionErrors.IntegratedAuthenticationFailed
                : ConnectionErrors.LoginFailed;
        }

        if (numbers.Overlaps(_certificateErrors) || Mentions(sql, "certificate"))
        {
            return ConnectionErrors.CertificateNotTrusted;
        }

        if (numbers.Contains(Timeout))
        {
            return ConnectionErrors.Timeout(profile.Settings.ConnectTimeoutSeconds);
        }

        if (numbers.Overlaps(_unreachableErrors) || Chain(exception).Any(inner => inner is System.Net.Sockets.SocketException))
        {
            return ConnectionErrors.ServerUnreachable;
        }

        // A connection-level failure (severity 20 and above) with no better match is a network problem.
        if (sql.Class >= 20)
        {
            return ConnectionErrors.ServerUnreachable;
        }

        return ConnectionErrors.ServerError(sql.Number, sql.Message);
    }

    private static bool IsIntegratedFailure(SqlException sql, DatabaseConnection profile) =>
        profile.AuthenticationType == AuthenticationType.Integrated
        && (Mentions(sql, "SSPI") || Mentions(sql, "GSSAPI") || Mentions(sql, "Kerberos"));

    private static bool Mentions(Exception exception, string text) =>
        exception.Message.Contains(text, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<Exception> Chain(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            yield return current;
        }
    }
}
