using System.Data;
using System.Data.SqlTypes;
using System.Numerics;
using System.Runtime.CompilerServices;
using DataBridge.Application.Abstractions;
using DataBridge.Core.Comparison;
using DataBridge.Core.Connections;
using DataBridge.Core.Data;
using DataBridge.Shared.Results;
using Microsoft.Data.SqlClient;
using DataRow = DataBridge.Core.Comparison.DataRow;

namespace DataBridge.Infrastructure.Providers.SqlServer.Data;

/// <summary>
/// Streams SQL Server rows in key order (architecture §7, ADR-0014). A session holds one connection; with snapshot
/// isolation already allowed on the database it reads inside a snapshot transaction, otherwise read committed. It never
/// changes database settings and never uses <c>NOLOCK</c>.
/// </summary>
internal sealed class SqlServerDataReaderProvider : IDataReaderProvider
{
    public DatabaseProviderType ProviderType => DatabaseProviderType.SqlServer;

    public IKeyOrdering KeyOrdering => SqlServerKeyOrdering.Instance;

    public IValueKindClassifier Classifier => SqlServerValueKinds.Instance;

    public async Task<Result<ITableRowReader>> OpenAsync(
        DatabaseConnectionRequest request,
        ReadConsistencyMode preferredConsistency,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var connection = SqlServerConnectionFactory.Create(request);
        try
        {
            await connection.OpenAsync(cancellationToken);
            var snapshot = preferredConsistency == ReadConsistencyMode.Snapshot && await SnapshotAllowedAsync(connection, cancellationToken);
            var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
                snapshot ? IsolationLevel.Snapshot : IsolationLevel.ReadCommitted, cancellationToken);
            return Result.Success<ITableRowReader>(new SqlServerTableRowReader(
                connection, transaction, snapshot ? ReadConsistencyMode.Snapshot : ReadConsistencyMode.ReadCommitted));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await connection.DisposeAsync();
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("Opening the read was cancelled.", exception, cancellationToken);
            }

            return SqlServerErrorTranslator.Translate(exception, request.Profile) is { } error
                ? Result.Failure<ITableRowReader>(error)
                : throw new DataReadException("The read session could not be started.", exception);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>Whether <c>ALLOW_SNAPSHOT_ISOLATION</c> is already on; comparison never turns it on.</summary>
    private static async Task<bool> SnapshotAllowedAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT snapshot_isolation_state FROM sys.databases WHERE database_id = DB_ID();";
        var state = await command.ExecuteScalarAsync(cancellationToken);
        return state is byte value && value == 1;
    }
}

/// <summary>One read session: a connection and its transaction. Reads one table at a time.</summary>
internal sealed class SqlServerTableRowReader(SqlConnection connection, SqlTransaction transaction, ReadConsistencyMode consistency)
    : ITableRowReader
{
    public ReadConsistencyMode AchievedConsistency => consistency;

    public async IAsyncEnumerable<IReadOnlyList<DataRow>> ReadRowsAsync(
        TableReadRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.BatchSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.BatchSize, "The batch size must be at least 1.");
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SqlServerRowQuery.Build(request);

        // A stream of a large table legitimately takes long; cancellation, not a timeout, bounds it.
        command.CommandTimeout = 0;

        await using var reader = await Execute(() => command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken));
        var columns = request.KeyColumns.Concat(request.ValueColumns).ToArray();
        var keyCount = request.KeyColumns.Count;
        var batch = new List<DataRow>(request.BatchSize);
        while (await Execute(() => reader.ReadAsync(cancellationToken)))
        {
            var values = new DatabaseValue[columns.Length];
            for (var ordinal = 0; ordinal < columns.Length; ordinal++)
            {
                values[ordinal] = await Execute(() => SqlServerValueReader.ReadAsync(reader, ordinal, columns[ordinal], cancellationToken));
            }

            batch.Add(new DataRow(values[..keyCount], values[keyCount..]));
            if (batch.Count == request.BatchSize)
            {
                yield return batch;
                batch = new List<DataRow>(request.BatchSize);
            }
        }

        if (batch.Count > 0)
        {
            yield return batch;
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Read-only: ending the transaction either way changes nothing; rollback releases the snapshot promptly.
        try
        {
            await transaction.RollbackAsync();
        }
        catch (Exception exception) when (exception is InvalidOperationException or SqlException)
        {
            // The connection already broke or the transaction already ended.
        }

        await transaction.DisposeAsync();
        await connection.DisposeAsync();
    }

    /// <summary>Server failures become <see cref="DataReadException"/> with a message that holds no row values.</summary>
    private static async Task<T> Execute<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (SqlException exception) when (exception.Number != 0)
        {
            throw new DataReadException($"The server returned error {exception.Number} while reading the table.", exception);
        }
        catch (SqlException exception)
        {
            throw new DataReadException("The connection failed while reading the table.", exception);
        }
    }
}

/// <summary>Reads one column of the current row into a typed value, following the column's comparison family.</summary>
internal static class SqlServerValueReader
{
    public static async Task<DatabaseValue> ReadAsync(SqlDataReader reader, int ordinal, ReadColumn column, CancellationToken cancellationToken)
    {
        var type = column.Column.SqlTypeName;
        if (await reader.IsDBNullAsync(ordinal, cancellationToken))
        {
            return DatabaseValue.Null(column.Kind, type);
        }

        var baseType = SqlServerRowQuery.TypeOf(column.Column);
        return column.Kind switch
        {
            ValueKind.WholeNumber => DatabaseValue.FromInt64(Convert.ToInt64(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture), type),
            ValueKind.ExactNumeric => DatabaseValue.FromDecimal(
                baseType is "money" or "smallmoney" ? FromDecimal(reader.GetDecimal(ordinal)) : FromSqlDecimal(reader.GetSqlDecimal(ordinal)),
                type),
            ValueKind.ApproximateNumeric => DatabaseValue.FromDouble(baseType == "real" ? reader.GetFloat(ordinal) : reader.GetDouble(ordinal), type),
            ValueKind.Boolean => DatabaseValue.FromBoolean(reader.GetBoolean(ordinal), type),
            ValueKind.Text => DatabaseValue.FromText(reader.GetString(ordinal), type),
            ValueKind.Xml => DatabaseValue.FromXml(reader.GetString(ordinal), type),
            ValueKind.Binary => DatabaseValue.FromBinary(await reader.GetFieldValueAsync<byte[]>(ordinal, cancellationToken), type),
            ValueKind.UniqueIdentifier => DatabaseValue.FromGuid(reader.GetGuid(ordinal), type),
            ValueKind.Date => DatabaseValue.FromDate(DateOnly.FromDateTime(reader.GetDateTime(ordinal)), type),
            ValueKind.Time => DatabaseValue.FromTime(reader.GetTimeSpan(ordinal), type),
            ValueKind.DateTime => DatabaseValue.FromDateTime(reader.GetDateTime(ordinal), type),
            ValueKind.DateTimeOffset => DatabaseValue.FromDateTimeOffset(reader.GetDateTimeOffset(ordinal), type),
            _ => throw new InvalidOperationException($"Columns of kind {column.Kind} are not read."),
        };
    }

    /// <summary>Exact conversion: <see cref="SqlDecimal"/> holds up to 38 digits as a 128-bit magnitude and a sign.</summary>
    public static DecimalValue FromSqlDecimal(SqlDecimal value)
    {
        var data = value.Data;
        var magnitude = new BigInteger((uint)data[0])
            | (new BigInteger((uint)data[1]) << 32)
            | (new BigInteger((uint)data[2]) << 64)
            | (new BigInteger((uint)data[3]) << 96);
        return new DecimalValue(value.IsPositive ? magnitude : -magnitude, value.Scale);
    }

    public static DecimalValue FromDecimal(decimal value)
    {
        var bits = decimal.GetBits(value);
        var magnitude = new BigInteger((uint)bits[0]) | (new BigInteger((uint)bits[1]) << 32) | (new BigInteger((uint)bits[2]) << 64);
        var scale = (bits[3] >> 16) & 0xFF;
        return new DecimalValue(bits[3] < 0 ? -magnitude : magnitude, scale);
    }
}
