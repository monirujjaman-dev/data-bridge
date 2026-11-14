using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Compact;

namespace DataBridge.Infrastructure.Logging;

/// <summary>Where and how much DataBridge logs.</summary>
/// <param name="LogDirectory">Folder for the rolling log files.</param>
/// <param name="MinimumLevel">The lowest level written. Defaults to <see cref="LogEventLevel.Information"/>.</param>
/// <param name="RetainedFileCount">How many log files to keep. Defaults to 14.</param>
/// <param name="FileSizeLimitBytes">Size at which a new file is started. Defaults to 10 MB.</param>
public sealed record LoggingOptions(
    string LogDirectory,
    LogEventLevel MinimumLevel = LogEventLevel.Information,
    int RetainedFileCount = 14,
    long FileSizeLimitBytes = 10 * 1024 * 1024)
{
    /// <summary>The log file name pattern; the file sink inserts the date before the extension.</summary>
    public const string FileNamePattern = "databridge-.log";

    /// <summary>The default folder: <c>DataBridge/logs</c> under the user's local application data folder.</summary>
    public static string DefaultDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
            "DataBridge",
            "logs");
}

/// <summary>
/// Configures logging: Microsoft.Extensions.Logging in code, Serilog underneath, writing compact JSON to a
/// rolling file. Every event is redacted (NFR-SEC-003): <see cref="Application.Connections.Secret"/> values,
/// properties with secret-like names and secret-like text in the formatted output. Logging scopes are
/// captured, so correlation ids such as <c>SessionId</c>, <c>PlanId</c> and <c>OperationId</c> added with
/// <c>ILogger.BeginScope</c> appear on every event inside the scope.
/// </summary>
public static class DataBridgeLogging
{
    /// <summary>Adds the DataBridge Serilog pipeline as the logging provider. The provider owns and disposes it.</summary>
    public static ILoggingBuilder AddDataBridgeLogging(this ILoggingBuilder builder, LoggingOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var logger = CreateConfiguration(options).CreateLogger();
        builder.AddSerilog(logger, dispose: true);
        builder.SetMinimumLevel(ToMicrosoftLevel(options.MinimumLevel));
        return builder;
    }

    /// <summary>The Serilog configuration: redaction, enrichment and the rolling JSON file.</summary>
    public static LoggerConfiguration CreateConfiguration(LoggingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.LogDirectory);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.RetainedFileCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.FileSizeLimitBytes, 1024);

        return ApplyRedactionAndEnrichment(new LoggerConfiguration(), options.MinimumLevel)
            .WriteTo.File(
                CreateFormatter(),
                Path.Combine(options.LogDirectory, LoggingOptions.FileNamePattern),
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                fileSizeLimitBytes: options.FileSizeLimitBytes,
                retainedFileCountLimit: options.RetainedFileCount,
                shared: true);
    }

    /// <summary>Redaction and enrichment shared by every sink; exposed so tests can attach their own sink.</summary>
    internal static LoggerConfiguration ApplyRedactionAndEnrichment(LoggerConfiguration configuration, LogEventLevel minimumLevel) =>
        configuration
            .MinimumLevel.Is(minimumLevel)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .Destructure.With<SecretDestructuringPolicy>()
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "DataBridge")

            // Last, so it also sees properties added by the enrichers above.
            .Enrich.With<SensitivePropertyEnricher>();

    /// <summary>Compact JSON, then whole-line redaction.</summary>
    internal static ITextFormatter CreateFormatter() => new RedactingTextFormatter(new CompactJsonFormatter());

    private static LogLevel ToMicrosoftLevel(LogEventLevel level) =>
        level switch
        {
            LogEventLevel.Verbose => LogLevel.Trace,
            LogEventLevel.Debug => LogLevel.Debug,
            LogEventLevel.Information => LogLevel.Information,
            LogEventLevel.Warning => LogLevel.Warning,
            LogEventLevel.Error => LogLevel.Error,
            _ => LogLevel.Critical,
        };
}
