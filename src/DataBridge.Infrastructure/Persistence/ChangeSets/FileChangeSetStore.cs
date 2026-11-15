using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using DataBridge.Application.Abstractions;
using DataBridge.Core.Comparison;
using DataBridge.Core.Data;
using DataBridge.Core.Schema;
using DataBridge.Core.Synchronization;

namespace DataBridge.Infrastructure.Persistence.ChangeSets;

/// <summary>
/// Stores each compared table's differing rows (key values and both fingerprints) in one file under the user's local
/// application data (ADR-0016). The plan carries a SHA-256 per status over the stored records; reading verifies it, so
/// a changed or truncated file is detected rather than executed.
/// </summary>
/// <remarks>
/// Keys are row values, so files are never logged, are deleted with their plan, and files older than
/// <see cref="MaxAge"/> are deleted when the store starts. The file format is private to this class: a header, then
/// records of status, key values (<see cref="DatabaseValueCodec"/>) and fingerprints.
/// </remarks>
internal sealed class FileChangeSetStore : IChangeSetStore
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    private const string Extension = ".changeset";
    private static readonly byte[] _magic = "DBCS1"u8.ToArray();

    private readonly string _directory;

    public FileChangeSetStore(string directory, TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
        Purge(timeProvider.GetUtcNow() - MaxAge);
    }

    public IChangeSetWriter CreateWriter(DatabaseObjectId table)
    {
        var storeId = Guid.NewGuid().ToString("N");
        return new Writer(storeId, PathOf(storeId));
    }

    public async IAsyncEnumerable<StoredRowChange> ReadAsync(
        string storeId,
        DifferenceStatus status,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var path = PathOf(storeId);
        if (!File.Exists(path))
        {
            throw new InvalidDataException("The stored rows are missing.");
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (!reader.ReadBytes(_magic.Length).AsSpan().SequenceEqual(_magic))
        {
            throw new InvalidDataException("The stored rows are not a DataBridge change set.");
        }

        while (stream.Position < stream.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (row, _) = ReadRecord(reader);
            if (row.Status == status)
            {
                yield return row;
            }
        }

        await Task.CompletedTask;
    }

    public Task<bool> VerifyAsync(RowChangeSet rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var path = PathOf(rows.StoreId);
            return Task.FromResult(File.Exists(path) && string.Equals(HashOf(path, rows.Status, out var count), rows.ContentHash, StringComparison.Ordinal) && count == rows.RowCount);
        }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException or IOException)
        {
            return Task.FromResult(false);
        }
    }

    /// <summary>Recomputes a status's hash and row count from the file.</summary>
    private static string HashOf(string path, DifferenceStatus status, out long count)
    {
        count = 0;
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        reader.ReadBytes(_magic.Length);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        while (stream.Position < stream.Length)
        {
            var (row, bytes) = ReadRecord(reader);
            if (row.Status == status)
            {
                AppendRecord(hash, bytes);
                count++;
            }
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public Task DeleteAsync(string storeId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(PathOf(storeId));
        return Task.CompletedTask;
    }

    internal string PathOf(string storeId)
    {
        if (storeId.Length != 32 || !storeId.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Not a change set id.", nameof(storeId));
        }

        return Path.Combine(_directory, storeId + Extension);
    }

    private void Purge(DateTimeOffset olderThan)
    {
        foreach (var file in Directory.EnumerateFiles(_directory, "*" + Extension))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < olderThan.UtcDateTime)
                {
                    File.Delete(file);
                }
            }
            catch (IOException)
            {
                // In use or already gone; the next start tries again.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static byte[] Encode(StoredRowChange row)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((byte)row.Status);
            writer.Write(row.KeyValues.Count);
            foreach (var value in row.KeyValues)
            {
                DatabaseValueCodec.Write(writer, value);
            }

            WriteNullable(writer, row.SourceFingerprint);
            WriteNullable(writer, row.TargetFingerprint);
        }

        return buffer.ToArray();
    }

    private static (StoredRowChange Row, byte[] Bytes) ReadRecord(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        if (length <= 0 || length > reader.BaseStream.Length - reader.BaseStream.Position)
        {
            throw new InvalidDataException("The stored rows are truncated.");
        }

        var bytes = reader.ReadBytes(length);
        using var record = new BinaryReader(new MemoryStream(bytes), Encoding.UTF8);
        var status = (DifferenceStatus)record.ReadByte();
        var count = record.ReadInt32();
        if (count is < 1 or > 1024)
        {
            throw new InvalidDataException("The stored rows are damaged.");
        }

        var key = new DatabaseValue[count];
        for (var index = 0; index < count; index++)
        {
            key[index] = DatabaseValueCodec.Read(record);
        }

        return (new StoredRowChange(status, key, ReadNullable(record), ReadNullable(record)), bytes);
    }

    /// <summary>Hashes a record with its length, so record boundaries are part of the hash.</summary>
    private static void AppendRecord(IncrementalHash hash, byte[] bytes)
    {
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
    }

    private static void WriteNullable(BinaryWriter writer, string? value)
    {
        writer.Write(value is not null);
        if (value is not null)
        {
            writer.Write(value);
        }
    }

    private static string? ReadNullable(BinaryReader reader) => reader.ReadBoolean() ? reader.ReadString() : null;

    private sealed class Writer : IChangeSetWriter
    {
        private readonly string _storeId;
        private readonly string _path;
        private readonly FileStream _stream;
        private readonly BinaryWriter _writer;
        private readonly Dictionary<DifferenceStatus, (long Count, IncrementalHash Hash)> _statuses = [];
        private bool _completed;

        public Writer(string storeId, string path)
        {
            _storeId = storeId;
            _path = path;
            _stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024);
            _writer = new BinaryWriter(_stream, Encoding.UTF8, leaveOpen: true);
            _writer.Write(_magic);
        }

        public void Append(StoredRowChange row)
        {
            ArgumentNullException.ThrowIfNull(row);
            ObjectDisposedException.ThrowIf(_completed, this);

            var bytes = Encode(row);
            _writer.Write(bytes.Length);
            _writer.Write(bytes);
            if (!_statuses.TryGetValue(row.Status, out var entry))
            {
                entry = (0, IncrementalHash.CreateHash(HashAlgorithmName.SHA256));
            }

            AppendRecord(entry.Hash, bytes);
            _statuses[row.Status] = (entry.Count + 1, entry.Hash);
        }

        public async Task<RecordedChangeSet> CompleteAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_completed, this);
            _completed = true;
            _writer.Flush();
            await _stream.FlushAsync(cancellationToken);
            var rows = _statuses.ToDictionary(
                pair => pair.Key,
                pair => new RecordedRows(pair.Value.Count, Convert.ToHexStringLower(pair.Value.Hash.GetHashAndReset())));
            return new RecordedChangeSet(_storeId, rows);
        }

        public async ValueTask DisposeAsync()
        {
            var completed = _completed;
            _completed = true;
            await _writer.DisposeAsync();
            await _stream.DisposeAsync();
            foreach (var (_, hash) in _statuses.Values)
            {
                hash.Dispose();
            }

            if (!completed)
            {
                File.Delete(_path);
            }
        }
    }
}
