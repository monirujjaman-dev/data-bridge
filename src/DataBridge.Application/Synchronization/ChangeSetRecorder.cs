using System.Collections.Concurrent;
using DataBridge.Application.Abstractions;
using DataBridge.Core.Comparison;
using DataBridge.Core.Schema;
using DataBridge.Core.Synchronization;

namespace DataBridge.Application.Synchronization;

/// <summary>
/// Records every differing row's key and fingerprints while data is compared, one change set per table, so a plan can
/// reference exactly the rows it will change without holding them (ADR-0016). Thread-safe: tables are compared in
/// parallel, and each table's rows arrive from one merge at a time.
/// </summary>
/// <remarks>
/// A discarded table (duplicate keys, order mismatch, read failure) keeps no change set. Disposing without
/// <see cref="CompleteAsync"/> deletes everything recorded, so a cancelled comparison leaves no files behind.
/// </remarks>
public sealed class ChangeSetRecorder(IChangeSetStore store) : IRowDifferenceSink, IAsyncDisposable
{
    private readonly ConcurrentDictionary<DatabaseObjectId, Entry> _tables = new();

    public void Add(RowDifference difference)
    {
        ArgumentNullException.ThrowIfNull(difference);
        var entry = _tables.GetOrAdd(difference.TableId, _ => new Entry());
        lock (entry)
        {
            if (entry.Discarded)
            {
                return;
            }

            entry.Writer ??= store.CreateWriter(difference.TableId);
            entry.Writer.Append(new StoredRowChange(
                difference.Status, difference.KeyValues, difference.SourceFingerprint, difference.TargetFingerprint));
        }
    }

    public void Discard(DatabaseObjectId tableId)
    {
        var entry = _tables.GetOrAdd(tableId, _ => new Entry());
        lock (entry)
        {
            entry.Discarded = true;
        }
    }

    /// <summary>Finishes every table's change set. Tables with no differing rows have none.</summary>
    public async Task<IReadOnlyDictionary<DatabaseObjectId, RecordedChangeSet>> CompleteAsync(CancellationToken cancellationToken)
    {
        var result = new Dictionary<DatabaseObjectId, RecordedChangeSet>();
        foreach (var (table, entry) in _tables)
        {
            if (entry.Writer is not { } writer)
            {
                continue;
            }

            if (!entry.Discarded)
            {
                result[table] = await writer.CompleteAsync(cancellationToken);
            }

            // Disposing an incomplete writer deletes its file; a completed one is kept.
            await writer.DisposeAsync();
            entry.Writer = null;
        }

        return result;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var entry in _tables.Values)
        {
            if (entry.Writer is { } writer)
            {
                await writer.DisposeAsync();
                entry.Writer = null;
            }
        }
    }

    private sealed class Entry
    {
        public IChangeSetWriter? Writer { get; set; }

        public bool Discarded { get; set; }
    }
}

/// <summary>Passes each differing row to several sinks, for example the display and a <see cref="ChangeSetRecorder"/>.</summary>
public sealed class FanOutDifferenceSink(params IRowDifferenceSink[] sinks) : IRowDifferenceSink
{
    public void Add(RowDifference difference)
    {
        foreach (var sink in sinks)
        {
            sink.Add(difference);
        }
    }

    public void Discard(DatabaseObjectId tableId)
    {
        foreach (var sink in sinks)
        {
            sink.Discard(tableId);
        }
    }
}
