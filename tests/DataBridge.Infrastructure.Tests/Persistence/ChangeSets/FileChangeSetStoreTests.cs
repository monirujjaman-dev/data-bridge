using DataBridge.Application.Abstractions;
using DataBridge.Core.Comparison;
using DataBridge.Core.Data;
using DataBridge.Core.Schema;
using DataBridge.Core.Synchronization;
using DataBridge.Infrastructure.Persistence.ChangeSets;

namespace DataBridge.Infrastructure.Tests.Persistence.ChangeSets;

public sealed class FileChangeSetStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "DataBridgeTests", Guid.NewGuid().ToString("N"));
    private static readonly DatabaseObjectId _table = new("dbo", "T", DatabaseObjectType.Table);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static StoredRowChange Row(DifferenceStatus status, long key, string? source = "s", string? target = "t") =>
        new(status, [DatabaseValue.FromInt64(key, "bigint"), DatabaseValue.FromText("k", "nvarchar")], source, target);

    private static RowChangeSet For(RecordedChangeSet recorded, DifferenceStatus status) =>
        new(recorded.StoreId, status, ["A", "B"], ["A", "B"], recorded.Rows[status].RowCount, recorded.Rows[status].ContentHash);

    [Fact]
    public async Task Rows_round_trip_by_status_with_counts_and_a_verifiable_hash()
    {
        var store = new FileChangeSetStore(_directory, TimeProvider.System);
        RecordedChangeSet recorded;
        await using (var writer = store.CreateWriter(_table))
        {
            writer.Append(Row(DifferenceStatus.SourceOnly, 1, target: null));
            writer.Append(Row(DifferenceStatus.Modified, 2));
            writer.Append(Row(DifferenceStatus.SourceOnly, 3, target: null));
            recorded = await writer.CompleteAsync(Token);
        }

        Assert.Equal(2, recorded.Rows[DifferenceStatus.SourceOnly].RowCount);
        Assert.Equal(1, recorded.Rows[DifferenceStatus.Modified].RowCount);
        Assert.True(await store.VerifyAsync(For(recorded, DifferenceStatus.SourceOnly), Token));

        var keys = new List<long>();
        await foreach (var row in store.ReadAsync(recorded.StoreId, DifferenceStatus.SourceOnly, Token))
        {
            keys.Add(row.KeyValues[0].AsInt64());
            Assert.Equal("k", row.KeyValues[1].AsString());
            Assert.Equal("s", row.SourceFingerprint);
            Assert.Null(row.TargetFingerprint);
        }

        Assert.Equal([1L, 3L], keys);
    }

    [Fact]
    public async Task A_changed_file_or_count_fails_verification()
    {
        var store = new FileChangeSetStore(_directory, TimeProvider.System);
        RecordedChangeSet recorded;
        await using (var writer = store.CreateWriter(_table))
        {
            writer.Append(Row(DifferenceStatus.Modified, 1));
            writer.Append(Row(DifferenceStatus.Modified, 2));
            recorded = await writer.CompleteAsync(Token);
        }

        var expected = For(recorded, DifferenceStatus.Modified);
        Assert.False(await store.VerifyAsync(expected with { RowCount = 3 }, Token));

        var path = store.PathOf(recorded.StoreId);
        var bytes = await File.ReadAllBytesAsync(path, Token);
        bytes[^3] ^= 0xFF;
        await File.WriteAllBytesAsync(path, bytes, Token);
        Assert.False(await store.VerifyAsync(expected, Token));

        await store.DeleteAsync(recorded.StoreId, Token);
        Assert.False(await store.VerifyAsync(expected, Token));
    }

    [Fact]
    public async Task An_unfinished_writer_leaves_no_file_and_old_files_are_purged_on_start()
    {
        var store = new FileChangeSetStore(_directory, TimeProvider.System);
        await using (var writer = store.CreateWriter(_table))
        {
            writer.Append(Row(DifferenceStatus.TargetOnly, 1));
        }

        Assert.Empty(Directory.EnumerateFiles(_directory));

        var old = Path.Combine(_directory, new string('a', 32) + ".changeset");
        await File.WriteAllTextAsync(old, "x", Token);
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow - FileChangeSetStore.MaxAge - TimeSpan.FromHours(1));
        _ = new FileChangeSetStore(_directory, TimeProvider.System);

        Assert.False(File.Exists(old));
        Assert.Throws<ArgumentException>(() => store.PathOf("../../etc/passwd"));
    }
}
