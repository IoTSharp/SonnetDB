using System.Data;
using System.Data.Common;
using SonnetDB.Data.Embedded;
using SonnetDB.Data.Internal;
using SonnetDB.Data.Remote;

namespace SonnetDB.Data;

/// <summary>
/// SonnetDB 的 ADO.NET 连接对象。同一类型同时承载嵌入式与远程两种实现，
/// 由 <see cref="SndbConnectionStringBuilder.ResolveMode"/> 推断分发。
/// </summary>
/// <remarks>
/// <para>
/// 嵌入式：连接字符串形如 <c>Data Source=./data</c> 或 <c>Data Source=sonnetdb://./data</c>，
/// 内部直接打开 <see cref="SonnetDB.Engine.Tsdb"/>，并通过引用计数共享同一进程内的同目录实例。
/// </para>
/// <para>
/// 远程：连接字符串形如 <c>Data Source=sonnetdb+http://host:port/dbname;Token=xxx</c>，
/// 内部使用 <see cref="System.Net.Http.HttpClient"/> 调用 <c>POST /v1/db/{db}/sql</c>，
/// 结果以 ndjson 流式反序列化。
/// </para>
/// <para>轻事务第一阶段支持单个关系表内的小批量 DML。</para>
/// </remarks>
public sealed class SndbConnection : DbConnection
{
    private string _connectionString = string.Empty;
    private SndbConnectionStringBuilder _builder = new();
    private IConnectionImpl? _impl;
    private SndbTransaction? _currentTransaction;
    private bool _disposed;

    /// <summary>使用空连接字符串构造，必须随后赋值 <see cref="ConnectionString"/> 再 <see cref="Open"/>。</summary>
    public SndbConnection() { }

    /// <summary>使用指定的连接字符串构造。</summary>
    public SndbConnection(string? connectionString)
    {
        if (!string.IsNullOrWhiteSpace(connectionString))
            ConnectionString = connectionString;
    }

    /// <inheritdoc />
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string ConnectionString
    {
        get => _connectionString;
        set
        {
            if (State != ConnectionState.Closed)
                throw new InvalidOperationException("不能在连接打开状态下修改 ConnectionString。");
            _connectionString = value ?? string.Empty;
            _builder = new SndbConnectionStringBuilder(_connectionString);
        }
    }

    /// <inheritdoc />
    public override string Database => _impl?.Database ?? _builder.Database ?? _builder.DataSource;

    /// <inheritdoc />
    public override string DataSource => _impl?.DataSource ?? _builder.DataSource;

    /// <inheritdoc />
    public override string ServerVersion
        => _impl?.ServerVersion ?? typeof(SndbConnection).Assembly.GetName().Version?.ToString() ?? "1.0.0";

    /// <inheritdoc />
    public override ConnectionState State => _impl?.State ?? ConnectionState.Closed;

    /// <summary>当前连接所采用的运行模式。</summary>
    public SndbProviderMode ProviderMode => _builder.ResolveMode();

    /// <summary>
    /// 仅嵌入式模式可用：返回底层 <see cref="SonnetDB.Engine.Tsdb"/> 引擎实例；远程模式或未打开时为 null。
    /// </summary>
    public SonnetDB.Engine.Tsdb? UnderlyingTsdb
        => _impl is EmbeddedConnectionImpl emb ? emb.Tsdb : null;

    /// <inheritdoc />
    /// <remarks>
    /// 远程模式下相当于执行 <c>USE &lt;databaseName&gt;</c>：仅修改当前连接的目标库路由，
    /// 后续 <see cref="SndbCommand"/> 会发往 <c>POST /v1/db/{databaseName}/sql</c>；
    /// 不会做服务端校验，若库不存在或无权限会在下一条 SQL 上自然报错。
    /// 嵌入式模式下因 Data Source 等价于物理路径，此方法仍抛出 <see cref="NotSupportedException"/>。
    /// </remarks>
    public override void ChangeDatabase(string databaseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        if (_impl is null || State != ConnectionState.Open)
            throw new InvalidOperationException("连接未打开，无法切换数据库。");

        using var cmd = new SndbCommand($"USE `{databaseName.Replace("`", "``")}`", this);
        cmd.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public override void Open()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (State == ConnectionState.Open)
            return;

        _impl = _builder.ResolveMode() switch
        {
            SndbProviderMode.Embedded => new EmbeddedConnectionImpl(_builder),
            SndbProviderMode.Remote => new RemoteConnectionImpl(_builder),
            _ => throw new InvalidOperationException("未知的 SndbProviderMode。"),
        };
        try
        {
            _impl.Open();
        }
        catch
        {
            _impl.Dispose();
            _impl = null;
            throw;
        }
    }

    /// <inheritdoc />
    public override async Task OpenAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (State == ConnectionState.Open)
            return;

        _impl = _builder.ResolveMode() switch
        {
            SndbProviderMode.Embedded => new EmbeddedConnectionImpl(_builder),
            SndbProviderMode.Remote => new RemoteConnectionImpl(_builder),
            _ => throw new InvalidOperationException("未知的 SndbProviderMode。"),
        };
        try
        {
            await _impl.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _impl.Dispose();
            _impl = null;
            throw;
        }
    }

    /// <inheritdoc />
    public override void Close()
    {
        if (_currentTransaction is { IsCompleted: false } tx)
        {
            try { RollbackTransaction(tx); }
            catch { /* Close best-effort rollback. */ }
        }

        var impl = _impl;
        _impl = null;
        _currentTransaction = null;
        impl?.Close();
        impl?.Dispose();
    }

    /// <inheritdoc />
    protected override DbCommand CreateDbCommand() => new SndbCommand { Connection = this };

    /// <inheritdoc />
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
    {
        var impl = GetOpenImpl();
        if (_currentTransaction is { IsCompleted: false })
            throw new InvalidOperationException("当前连接已有活动轻事务。");

        var state = impl.BeginTransaction(isolationLevel);
        var effectiveIsolation = isolationLevel == IsolationLevel.Unspecified
            ? IsolationLevel.ReadCommitted
            : isolationLevel;
        _currentTransaction = new SndbTransaction(this, effectiveIsolation, state);
        return _currentTransaction;
    }

    /// <summary>开始一段 SonnetDB 轻事务。</summary>
    public new SndbTransaction BeginTransaction()
        => BeginTransaction(IsolationLevel.Unspecified);

    /// <summary>开始一段 SonnetDB 轻事务。</summary>
    public new SndbTransaction BeginTransaction(IsolationLevel isolationLevel)
        => (SndbTransaction)BeginDbTransaction(isolationLevel);

    /// <summary>异步开始一段 SonnetDB 轻事务。</summary>
    public new ValueTask<SndbTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
        => BeginTransactionAsync(IsolationLevel.Unspecified, cancellationToken);

    /// <summary>异步开始一段 SonnetDB 轻事务。</summary>
    public new ValueTask<SndbTransaction> BeginTransactionAsync(
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(BeginTransaction(isolationLevel));
    }

    /// <summary>使用强类型返回 <see cref="SndbCommand"/>。</summary>
    public new SndbCommand CreateCommand() => new() { Connection = this };

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing) Close();
        _disposed = true;
        base.Dispose(disposing);
    }

    internal IConnectionImpl GetOpenImpl()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_impl is null || _impl.State != ConnectionState.Open)
            throw new InvalidOperationException("连接未打开。");
        return _impl;
    }

    internal object? GetTransactionStateForCommand(SndbTransaction? transaction)
    {
        if (transaction is null)
            return null;
        if (!ReferenceEquals(transaction.Connection, this))
            throw new InvalidOperationException("Command.Transaction 不属于当前连接。");
        if (!ReferenceEquals(transaction, _currentTransaction) || transaction.IsCompleted)
            throw new InvalidOperationException("Command.Transaction 不是当前连接的活动轻事务。");

        return transaction.TransactionState;
    }

    internal void CommitTransaction(SndbTransaction transaction)
    {
        var impl = GetOpenImpl();
        EnsureCurrentTransaction(transaction);
        try
        {
            impl.CommitTransaction(transaction.TransactionState);
            transaction.MarkCompletedFromConnection();
            _currentTransaction = null;
        }
        catch
        {
            _currentTransaction = null;
            transaction.MarkCompletedFromConnection();
            throw;
        }
    }

    internal async Task CommitTransactionAsync(SndbTransaction transaction, CancellationToken cancellationToken)
    {
        var impl = GetOpenImpl();
        EnsureCurrentTransaction(transaction);
        try
        {
            await impl.CommitTransactionAsync(transaction.TransactionState, cancellationToken).ConfigureAwait(false);
            transaction.MarkCompletedFromConnection();
            _currentTransaction = null;
        }
        catch
        {
            _currentTransaction = null;
            transaction.MarkCompletedFromConnection();
            throw;
        }
    }

    internal void RollbackTransaction(SndbTransaction transaction)
    {
        var impl = GetOpenImpl();
        EnsureCurrentTransaction(transaction);
        try
        {
            impl.RollbackTransaction(transaction.TransactionState);
        }
        finally
        {
            transaction.MarkCompletedFromConnection();
            _currentTransaction = null;
        }
    }

    internal async Task RollbackTransactionAsync(SndbTransaction transaction, CancellationToken cancellationToken)
    {
        var impl = GetOpenImpl();
        EnsureCurrentTransaction(transaction);
        try
        {
            await impl.RollbackTransactionAsync(transaction.TransactionState, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            transaction.MarkCompletedFromConnection();
            _currentTransaction = null;
        }
    }

    private void EnsureCurrentTransaction(SndbTransaction transaction)
    {
        if (!ReferenceEquals(transaction, _currentTransaction))
            throw new InvalidOperationException("事务不是当前连接的活动轻事务。");
    }
}
