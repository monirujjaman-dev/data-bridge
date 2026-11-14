using System.Text.Json;
using DataBridge.Application.Connections;
using DataBridge.Infrastructure.Logging;
using Microsoft.Extensions.Logging;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog.Formatting;

namespace DataBridge.Infrastructure.Tests.Logging;

/// <summary>
/// The full pipeline as the application uses it: Microsoft.Extensions.Logging in front, Serilog with
/// DataBridge redaction and enrichment behind, compact JSON out.
/// </summary>
public sealed class LoggingPipelineTests : IDisposable
{
    private const string Password = "hunter2-Very-Secret";

    private readonly StringWriter _output = new();
    private readonly SerilogLoggerFactory _factory;
    private readonly ILogger _logger;

    public LoggingPipelineTests()
    {
        var serilog = DataBridgeLogging
            .ApplyRedactionAndEnrichment(new Serilog.LoggerConfiguration(), LogEventLevel.Debug)
            .WriteTo.Sink(new FormattingSink(DataBridgeLogging.CreateFormatter(), _output))
            .CreateLogger();
        _factory = new SerilogLoggerFactory(serilog, dispose: true);
        _logger = _factory.CreateLogger("DataBridge.Tests");
    }

    public void Dispose()
    {
        _factory.Dispose();
        _output.Dispose();
    }

    [Fact]
    public void Writes_one_compact_json_object_per_event()
    {
        Log("Compared {TableCount} tables", 3);

        using var document = JsonDocument.Parse(Assert.Single(Lines()));
        Assert.Equal("Compared {TableCount} tables", document.RootElement.GetProperty("@mt").GetString());
        Assert.Equal(3, document.RootElement.GetProperty("TableCount").GetInt32());
        Assert.Equal("DataBridge", document.RootElement.GetProperty("Application").GetString());
    }

    [Fact]
    public void Property_named_like_a_secret_is_redacted()
    {
        Log("Signing in with {Password}", Password);

        AssertRedacted();
        Assert.Contains("\"Password\":\"[redacted]\"", Text(), StringComparison.Ordinal);
    }

    [Fact]
    public void Secret_value_is_redacted_however_it_is_logged()
    {
        var secret = new Secret(Password);

        Log("Value {Value}", secret);
        Log("Destructured {@Value}", secret);
        Log("Stringified {$Value}", secret);

        AssertRedacted();
        Assert.Equal(3, Lines().Length);
    }

    [Fact]
    public void Password_inside_a_connection_string_is_redacted_and_the_rest_kept()
    {
        Log("Connecting with {ConnectionString}", $"Server=db01;Database=Sales;User ID=app;Password={Password};Encrypt=True");

        AssertRedacted();
        Assert.Contains("Server=db01;Database=Sales", Text(), StringComparison.Ordinal);
    }

    [Fact]
    public void Secret_properties_of_destructured_objects_are_redacted()
    {
        Log("Options {@Options}", new { Server = "db01", Password, Nested = new { ApiKey = Password } });

        AssertRedacted();
        Assert.Contains("db01", Text(), StringComparison.Ordinal);
    }

    [Fact]
    public void Secret_values_in_dictionaries_and_sequences_are_redacted()
    {
        Log("Settings {Settings}", new Dictionary<string, string> { ["Server"] = "db01", ["Password"] = Password });
        Log("Strings {Strings}", new[] { "ok", $"Pwd={Password}" });

        AssertRedacted();
    }

    [Fact]
    public void Secret_in_exception_text_is_redacted_and_inner_exceptions_are_kept()
    {
        var inner = new InvalidOperationException("Inner failure detail");
        var exception = new InvalidOperationException($"Login failed for Server=db01;Password={Password}", inner);

        _logger.LogError(exception, "Connection test failed");

        AssertRedacted();
        Assert.Contains("Inner failure detail", Text(), StringComparison.Ordinal);
    }

    [Fact]
    public void Secret_in_literal_template_text_is_redacted()
    {
        // Deliberately misuses an interpolated template to prove the last line of defence.
        _logger.LogWarning($"Bad practice: Pwd={Password}");

        AssertRedacted();
    }

    [Fact]
    public void Scope_values_become_correlation_properties()
    {
        var sessionId = Guid.NewGuid();

        using (_logger.BeginScope(new Dictionary<string, object> { ["SessionId"] = sessionId }))
        {
            Log("Comparison started");
        }

        Log("Outside the scope");

        var lines = Lines();
        Assert.Contains(sessionId.ToString(), lines[0], StringComparison.Ordinal);
        Assert.DoesNotContain(sessionId.ToString(), lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Secret_in_scope_values_is_redacted()
    {
        using (_logger.BeginScope(new Dictionary<string, object> { ["Token"] = Password }))
        {
            Log("Inside the scope");
        }

        AssertRedacted();
    }

    [Fact]
    public void Microsoft_and_System_noise_below_warning_is_dropped()
    {
        _factory.CreateLogger("Microsoft.Extensions.Hosting").LogInformation("Hosting started");
        _factory.CreateLogger("System.Net.Http").LogInformation("Request sent");
        _factory.CreateLogger("Microsoft.Extensions.Hosting").LogWarning("Hosting warning");

        Assert.Single(Lines());
    }

    private void Log(string template, params object?[] values)
    {
        _logger.LogInformation(template, values);
    }

    private string Text() => _output.ToString();

    private string[] Lines() => Text().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private void AssertRedacted()
    {
        Assert.NotEmpty(Lines());
        Assert.DoesNotContain(Password, Text(), StringComparison.Ordinal);
    }

    /// <summary>Writes each event, formatted, to a <see cref="TextWriter"/>.</summary>
    private sealed class FormattingSink(ITextFormatter formatter, TextWriter output) : ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            lock (output)
            {
                formatter.Format(logEvent, output);
            }
        }
    }
}
