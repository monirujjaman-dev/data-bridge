using DataBridge.Core.Comparison;
using DataBridge.Core.Connections;
using DataBridge.Core.Data;
using DataBridge.Core.Schema;
using DataBridge.Shared.Results;

namespace DataBridge.Application.Abstractions;

/// <summary>A column to read, as defined on the side being read, with its comparison family.</summary>
public sealed record ReadColumn(ColumnDefinition Column, ValueKind Kind);

/// <summary>What to read from one table on one side.</summary>
/// <param name="TableId">The table as named on this side.</param>
/// <param name="KeyColumns">The matching key, in key order; rows are returned in this key's order.</param>
/// <param name="ValueColumns">The compared columns, in plan order.</param>
/// <param name="BatchSize">The most rows a batch holds.</param>
public sealed record TableReadRequest(
    DatabaseObjectId TableId,
    IReadOnlyList<ReadColumn> KeyColumns,
    IReadOnlyList<ReadColumn> ValueColumns,
    int BatchSize);

/// <summary>
/// Typed, ordered row streaming (architecture §6). Read-only: implementations only run <c>SELECT</c>s and never change
/// database settings, such as to enable snapshot isolation.
/// </summary>
public interface IDataReaderProvider
{
    DatabaseProviderType ProviderType { get; }

    /// <summary>How the provider's server orders key values in the rows it returns (FR-DAT-016).</summary>
    IKeyOrdering KeyOrdering { get; }

    /// <summary>Maps the provider's column types to comparison families.</summary>
    IValueKindClassifier Classifier { get; }

    /// <summary>Connects and starts a read with the preferred consistency, or the best the database allows.</summary>
    /// <returns>A reader, or a failure with an actionable <c>ConnectionErrors</c> error. Unexpected failures throw.</returns>
    Task<Result<ITableRowReader>> OpenAsync(
        DatabaseConnectionRequest request,
        ReadConsistencyMode preferredConsistency,
        CancellationToken cancellationToken);
}

/// <summary>An open read session on one database. Dispose it to end the read and release the connection.</summary>
public interface ITableRowReader : IAsyncDisposable
{
    /// <summary>The isolation the session actually got.</summary>
    ReadConsistencyMode AchievedConsistency { get; }

    /// <summary>Streams the table's rows in strictly ascending key order, in batches of at most the requested size.</summary>
    /// <exception cref="DataReadException">The server failed the read; the message holds no row values.</exception>
    IAsyncEnumerable<IReadOnlyList<DataRow>> ReadRowsAsync(TableReadRequest request, CancellationToken cancellationToken);
}

/// <summary>A read failed on the server. The message is safe to show and log: it never contains row values.</summary>
public sealed class DataReadException : Exception
{
    public DataReadException()
    {
    }

    public DataReadException(string message)
        : base(message)
    {
    }

    public DataReadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
