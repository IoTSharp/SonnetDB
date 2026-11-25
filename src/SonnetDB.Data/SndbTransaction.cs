using System.Data;
using System.Data.Common;
using SonnetDB.Data.Remote;

namespace SonnetDB.Data;

/// <summary>
/// SonnetDB ADO.NET 轻事务对象，支持同一数据库内多个关系表的小批量 DML。
/// </summary>
public sealed class SndbTransaction : DbTransaction
{
    private readonly SndbConnection _connection;
    private readonly object _transactionState;
    private bool _completed;
    private bool _disposed;

    internal SndbTransaction(SndbConnection connection, IsolationLevel isolationLevel, object transactionState)
    {
        _connection = connection;
        IsolationLevel = isolationLevel;
        _transactionState = transactionState;
    }

    /// <inheritdoc />
    public override IsolationLevel IsolationLevel { get; }

    /// <inheritdoc />
    protected override DbConnection DbConnection => _connection;

    internal object TransactionState
    {
        get
        {
            ThrowIfCompletedOrDisposed();
            return _transactionState;
        }
    }

    internal bool IsCompleted => _completed || _disposed;

    internal bool SupportsDmlBatch => !IsCompleted
        && _transactionState is RemoteTransactionState { SupportsDmlBatch: true };

    internal Task<IReadOnlyList<SndbDmlBatchResult>?> ExecuteDmlBatchAsync(
        IReadOnlyList<SndbDmlBatchStatement> statements, CancellationToken cancellationToken,
        Func<ValueTask>? onSending = null)
    {
        var state = _connection.GetTransactionStateForCommand(this);
        if (!SupportsDmlBatch || _connection.GetOpenImpl() is not RemoteConnectionImpl remote)
            throw new NotSupportedException("当前事务未协商有序 DML 批处理。");

        return remote.ExecuteTransactionDmlBatchAsync((RemoteTransactionState)state!, statements, cancellationToken, onSending);
    }

    /// <inheritdoc />
    public override void Commit()
    {
        ThrowIfCompletedOrDisposed();
        _connection.CommitTransaction(this);
        _completed = true;
    }

    /// <inheritdoc />
    public override async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfCompletedOrDisposed();
        await _connection.CommitTransactionAsync(this, cancellationToken).ConfigureAwait(false);
        _completed = true;
    }

    /// <inheritdoc />
    public override void Rollback()
    {
        ThrowIfCompletedOrDisposed();
        _connection.RollbackTransaction(this);
        _completed = true;
    }

    /// <inheritdoc />
    public override async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfCompletedOrDisposed();
        await _connection.RollbackTransactionAsync(this, cancellationToken).ConfigureAwait(false);
        _completed = true;
    }

    internal void MarkCompletedFromConnection()
        => _completed = true;

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing && !_completed)
        {
            try
            {
                _connection.RollbackTransaction(this);
            }
            finally
            {
                _completed = true;
            }
        }

        _disposed = true;
        base.Dispose(disposing);
    }

    private void ThrowIfCompletedOrDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
            throw new InvalidOperationException("轻事务已结束。");
    }
}
