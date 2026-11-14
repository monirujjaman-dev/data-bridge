using DataBridge.Infrastructure.Logging;
using Microsoft.Extensions.Logging;
using Serilog.Events;

namespace DataBridge.Infrastructure.Tests.Logging;

public sealed class FileLoggingTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "DataBridge.Tests", Guid.NewGuid().ToString("N"), "logs");

    public void Dispose()
    {
        var root = Path.GetDirectoryName(_directory)!;
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Writes_redacted_json_lines_to_a_dated_file()
    {
        using (var factory = LoggerFactory.Create(builder => builder.AddDataBridgeLogging(new LoggingOptions(_directory))))
        {
            factory.CreateLogger("DataBridge.Tests").LogInformation("Signing in with {Password}", "hunter2");
        }

        var file = Assert.Single(Directory.GetFiles(_directory));
        Assert.Matches(@"databridge-\d{8}\.log$", file);
        var text = File.ReadAllText(file);
        Assert.Contains("[redacted]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Events_below_the_minimum_level_are_not_written()
    {
        var options = new LoggingOptions(_directory, MinimumLevel: LogEventLevel.Warning);
        using (var factory = LoggerFactory.Create(builder => builder.AddDataBridgeLogging(options)))
        {
            var logger = factory.CreateLogger("DataBridge.Tests");
            logger.LogInformation("Not written");
            logger.LogWarning("Written");
        }

        var text = File.ReadAllText(Assert.Single(Directory.GetFiles(_directory)));
        Assert.DoesNotContain("Not written", text, StringComparison.Ordinal);
        Assert.Contains("Written", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 1024 * 1024)]
    [InlineData(14, 10)]
    public void Invalid_options_are_rejected(int retainedFileCount, long fileSizeLimitBytes) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DataBridgeLogging.CreateConfiguration(new LoggingOptions(_directory, RetainedFileCount: retainedFileCount, FileSizeLimitBytes: fileSizeLimitBytes)));

    [Fact]
    public void Default_directory_is_under_local_application_data() =>
        Assert.EndsWith(Path.Combine("DataBridge", "logs"), LoggingOptions.DefaultDirectory(), StringComparison.Ordinal);
}
