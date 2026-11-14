using Microsoft.Extensions.Logging;

namespace DataBridge.Application.Tests.Connections;

/// <summary>A log entry as a use case wrote it: the rendered message and the structured values.</summary>
internal sealed record LogEntry(
    LogLevel Level,
    EventId EventId,
    string Message,
    IReadOnlyList<KeyValuePair<string, object?>> Values,
    Exception? Exception)
{
    /// <summary>Everything a log sink could write for this entry.</summary>
    public string AllText =>
        string.Join(
            " | ",
            [Message, .. Values.Select(value => $"{value.Key}={value.Value}"), Exception?.ToString() ?? ""]);
}

/// <summary>Collects log entries from every logger it creates.</summary>
internal sealed class RecordingLog
{
    private readonly List<LogEntry> _entries = [];

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_entries)
            {
                return [.. _entries];
            }
        }
    }

    public ILogger<T> For<T>() => new Logger<T>(this);

    private void Add(LogEntry entry)
    {
        lock (_entries)
        {
            _entries.Add(entry);
        }
    }

    private sealed class Logger<T>(RecordingLog log) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var values = state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
            log.Add(new LogEntry(logLevel, eventId, formatter(state, exception), values, exception));
        }
    }
}
