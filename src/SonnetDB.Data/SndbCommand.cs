using System.Data;
using System.Data.Common;
using SonnetDB.Data.Internal;
using SonnetDB.Exceptions;

namespace SonnetDB.Data;

/// <summary>
/// SonnetDB ADO.NET 命令对象。把 SQL 与参数交给当前连接的内部实现执行（嵌入式或远程）。
/// </summary>
/// <remarks>
/// <para>
/// 参数支持位置 <c>?</c> 与命名 <c>@name</c> / <c>:name</c> 占位符（#213）。嵌入式模式下参数值
/// 直接绑定进已解析的 AST（值绑定而非字符串拼接，从根上防注入，并可复用解析缓存）；远程模式因
/// 线协议只接受 SQL 字符串，仍在客户端把命名占位符按安全字面量替换后发送，保留既有类型序列化保真度。
/// </para>
/// <para>
/// <see cref="ExecuteNonQuery"/> 返回值约定：INSERT、关系表 UPDATE/DELETE 返回实际影响行数；
/// measurement DELETE 返回新增墓碑数；CREATE MEASUREMENT 返回 0；SELECT 返回 -1
/// （与 <see cref="DbCommand"/> 标准一致）。
/// </para>
/// </remarks>
public sealed class SndbCommand : DbCommand
{
    private SndbConnection? _connection;
    private SndbTransaction? _transaction;
    private string _commandText = string.Empty;
    private CommandType _commandType = CommandType.Text;
    private readonly SndbParameterCollection _parameters = new();
    private readonly object _executionSync = new();
    private ExecutionLease? _activeExecution;

    /// <summary>构造一个未关联连接的命令。</summary>
    public SndbCommand() { }

    /// <summary>用 SQL 文本与连接构造命令。</summary>
    public SndbCommand(string commandText, SndbConnection? connection = null)
    {
        _commandText = commandText ?? string.Empty;
        _connection = connection;
    }

    /// <inheritdoc />
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string CommandText
    {
        get => _commandText;
        set => _commandText = value ?? string.Empty;
    }

    /// <inheritdoc />
    public override int CommandTimeout { get; set; }

    /// <inheritdoc />
    public override CommandType CommandType
    {
        get => _commandType;
        set
        {
            if (value != CommandType.Text && value != CommandType.TableDirect)
                throw new NotSupportedException(
                    "SonnetDB 仅支持 CommandType.Text（普通 SQL）与 CommandType.TableDirect（批量入库快路径）。");
            _commandType = value;
        }
    }

    /// <inheritdoc />
    public override bool DesignTimeVisible { get; set; }

    /// <inheritdoc />
    public override UpdateRowSource UpdatedRowSource { get; set; } = UpdateRowSource.None;

    /// <inheritdoc />
    protected override DbConnection? DbConnection
    {
        get => _connection;
        set => _connection = value as SndbConnection
            ?? (value is null ? null : throw new InvalidCastException("Connection 必须是 SndbConnection。"));
    }

    /// <inheritdoc />
    protected override DbParameterCollection DbParameterCollection => _parameters;

    /// <inheritdoc />
    protected override DbTransaction? DbTransaction
    {
        get => _transaction;
        set
        {
            _transaction = value switch
            {
                null => null,
                SndbTransaction transaction => transaction,
                _ => throw new InvalidCastException("Transaction 必须是 SndbTransaction。"),
            };
        }
    }

    /// <summary>强类型参数集合。</summary>
    public new SndbParameterCollection Parameters => _parameters;

    /// <summary>强类型连接。</summary>
    public new SndbConnection? Connection
    {
        get => _connection;
        set => _connection = value;
    }

    /// <summary>强类型事务。</summary>
    public new SndbTransaction? Transaction
    {
        get => _transaction;
        set => _transaction = value;
    }

    /// <inheritdoc />
    public override void Cancel()
    {
        // 取消当前租约；无活动执行时按 DbCommand 约定静默返回。
        ExecutionLease? lease;
        lock (_executionSync)
            lease = _activeExecution;
        lease?.Cancel();
    }

    /// <inheritdoc />
    public override void Prepare() { /* no-op */ }

    /// <inheritdoc />
    protected override DbParameter CreateDbParameter() => new SndbParameter();

    /// <inheritdoc />
    public override int ExecuteNonQuery()
        => ExecuteWithTimeoutAsync(CancellationToken.None, (cancellationToken, _) => ExecuteNonQueryCoreAsync(cancellationToken))
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();

    /// <inheritdoc />
    public override object? ExecuteScalar()
        => ExecuteWithTimeoutAsync(CancellationToken.None, (cancellationToken, _) => ExecuteScalarCoreAsync(cancellationToken))
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();

    /// <inheritdoc />
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        => ExecuteWithTimeoutAsync(
                CancellationToken.None,
                (cancellationToken, lease) => ExecuteDbDataReaderCoreAsync(behavior, cancellationToken, lease))
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();

    /// <inheritdoc />
    public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
        => ExecuteWithTimeoutAsync(cancellationToken, (executionToken, _) => ExecuteNonQueryCoreAsync(executionToken));

    /// <inheritdoc />
    public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
        => ExecuteWithTimeoutAsync(cancellationToken, (executionToken, _) => ExecuteScalarCoreAsync(executionToken));

    /// <inheritdoc />
    protected override Task<DbDataReader> ExecuteDbDataReaderAsync(
        CommandBehavior behavior,
        CancellationToken cancellationToken)
        => ExecuteWithTimeoutAsync(
            cancellationToken,
            (executionToken, lease) => ExecuteDbDataReaderCoreAsync(behavior, executionToken, lease));

    /// <summary>
    /// 在同一个超时范围内执行命令并消费非查询结果，确保远程流式响应不会绕过 CommandTimeout。
    /// </summary>
    private async Task<int> ExecuteNonQueryCoreAsync(CancellationToken cancellationToken)
    {
        using var result = await ExecuteCoreAsync(CommandBehavior.Default, cancellationToken).ConfigureAwait(false);
        if (result.RecordsAffected == -1)
        {
            // SELECT 被误用作 NonQuery 时仍按旧约定消费完整结果，但读取过程也必须响应超时。
            while (await result.ReadNextRowAsync(cancellationToken).ConfigureAwait(false)) { }
        }

        return result.RecordsAffected;
    }

    /// <summary>
    /// 在同一个超时范围内执行命令并读取标量，远程后续数据行也使用本次执行令牌。
    /// </summary>
    private async Task<object?> ExecuteScalarCoreAsync(CancellationToken cancellationToken)
    {
        using var result = await ExecuteCoreAsync(CommandBehavior.Default, cancellationToken).ConfigureAwait(false);
        if (result.Columns.Count == 0)
            return null;
        if (!await result.ReadNextRowAsync(cancellationToken).ConfigureAwait(false))
            return null;

        var value = result.GetValue(0);
        while (await result.ReadNextRowAsync(cancellationToken).ConfigureAwait(false)) { }
        return value;
    }

    /// <summary>
    /// 执行读取器命令并把结果所有权移交给 SndbDataReader；本窗口覆盖创建阶段，
    /// 返回后的每次 Read/ReadAsync 会按相同 CommandTimeout 建立独立窗口。
    /// </summary>
    private async Task<DbDataReader> ExecuteDbDataReaderCoreAsync(
        CommandBehavior behavior,
        CancellationToken cancellationToken,
        ExecutionLease lease)
    {
        var result = await ExecuteCoreAsync(behavior, cancellationToken).ConfigureAwait(false);
        return new SndbDataReader(
            result,
            behavior,
            _connection,
            CommandTimeout,
            lease.CancellationToken,
            lease.Release);
    }

    /// <summary>
    /// 为正 CommandTimeout 的公开调用建立唯一 linked CTS；无限超时走零计时器快路径，
    /// 两条路径都明确区分调用方取消与命令超时。
    /// </summary>
    private async Task<TResult> ExecuteWithTimeoutAsync<TResult>(
        CancellationToken callerCancellationToken,
        Func<CancellationToken, ExecutionLease, Task<TResult>> operation)
    {
        var lease = AcquireExecutionLease();
        try
        {
            if (CommandTimeout <= 0 && !callerCancellationToken.CanBeCanceled)
                return await ExecuteWithCancellationAsync(lease.CancellationToken, lease, CancellationToken.None).ConfigureAwait(false);

            using var timeoutCancellation = CommandTimeout > 0 ? new CancellationTokenSource() : null;
            timeoutCancellation?.CancelAfter(TimeSpan.FromSeconds(CommandTimeout));
            using var executionCancellation = timeoutCancellation is null
                ? CancellationTokenSource.CreateLinkedTokenSource(callerCancellationToken, lease.CancellationToken)
                : CancellationTokenSource.CreateLinkedTokenSource(
                    callerCancellationToken,
                    lease.CancellationToken,
                    timeoutCancellation.Token);

            return await ExecuteWithCancellationAsync(executionCancellation.Token, lease, timeoutCancellation?.Token ?? CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            // Reader 接管租约后由其 Close/Dispose 释放；其它执行路径在此立即回收。
            if (!lease.IsOwnedByReader)
                lease.Release();
        }

        async Task<TResult> ExecuteWithCancellationAsync(
            CancellationToken executionToken,
            ExecutionLease currentLease,
            CancellationToken timeoutToken)
        {
            try
            {
                var result = await operation(executionToken, currentLease).ConfigureAwait(false);
                if (result is SndbDataReader)
                    currentLease.TransferToReader();
                return result;
            }
            catch (OperationCanceledException exception) when (callerCancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(exception.Message, exception, callerCancellationToken);
            }
            catch (OperationCanceledException exception) when (lease.IsCancellationRequested)
            {
                throw new OperationCanceledException(exception.Message, exception, lease.CancellationToken);
            }
            catch (OperationCanceledException exception) when (timeoutToken.IsCancellationRequested)
            {
                throw new TimeoutException($"SonnetDB 命令执行超过 CommandTimeout={CommandTimeout} 秒。", exception);
            }
            catch (RoutineExecutionException exception) when (exception.Code == RoutineErrorCodes.Cancelled)
            {
                if (callerCancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException(exception.Message, exception, callerCancellationToken);
                if (lease.IsCancellationRequested)
                    throw new OperationCanceledException(exception.Message, exception, lease.CancellationToken);
                if (timeoutToken.IsCancellationRequested)
                    throw new TimeoutException($"SonnetDB 命令执行超过 CommandTimeout={CommandTimeout} 秒。", exception);
                throw;
            }
        }
    }

    /// <summary>创建唯一活动执行租约，避免并发执行覆盖 Cancel 的目标。</summary>
    private ExecutionLease AcquireExecutionLease()
    {
        lock (_executionSync)
        {
            if (_activeExecution is not null)
                throw new InvalidOperationException("同一个 SndbCommand 不能并发执行或在活动 Reader 未关闭时再次执行。");

            var lease = new ExecutionLease(this);
            _activeExecution = lease;
            return lease;
        }
    }

    /// <summary>仅当租约仍是当前活动实例时移除，避免旧 Reader 干扰后续执行。</summary>
    private void ReleaseExecutionLease(ExecutionLease lease)
    {
        lock (_executionSync)
        {
            if (ReferenceEquals(_activeExecution, lease))
                _activeExecution = null;
            lease.DisposeCancellation();
        }
    }

    /// <summary>命令执行与 Reader 生命周期共享的手动取消资源。</summary>
    private sealed class ExecutionLease(SndbCommand owner)
    {
        private readonly CancellationTokenSource _cancellation = new();
        private int _released;

        public CancellationToken CancellationToken => _cancellation.Token;

        public bool IsCancellationRequested => _cancellation.IsCancellationRequested;

        public bool IsOwnedByReader { get; private set; }

        /// <summary>取消当前执行；执行刚完成并已释放租约时静默处理竞争。</summary>
        public void Cancel()
        {
            try
            {
                _cancellation.Cancel();
            }
            catch (ObjectDisposedException) when (Volatile.Read(ref _released) != 0)
            {
                // DbCommand.Cancel 与执行完成并发时应保持幂等，不能暴露内部 CTS 释放竞态。
            }
        }

        public void TransferToReader() => IsOwnedByReader = true;

        public void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                owner.ReleaseExecutionLease(this);
        }

        public void DisposeCancellation() => _cancellation.Dispose();
    }

    /// <summary>校验命令状态并分发到嵌入式或远程连接实现。</summary>
    private async Task<IExecutionResult> ExecuteCoreAsync(CommandBehavior behavior, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_connection is null)
            throw new InvalidOperationException("Command 没有关联 Connection。");
        if (string.IsNullOrWhiteSpace(_commandText))
            throw new InvalidOperationException("CommandText 为空。");

        var impl = _connection.GetOpenImpl();
        var transactionState = _connection.GetTransactionStateForCommand(_transaction);
        if (_commandType == CommandType.TableDirect)
        {
            // 批量入库快路径：CommandText 即 payload（含可选首行 measurement 前缀），
            // 不做 ParameterBinder 的 SQL 字面量替换。
            return await impl.ExecuteBulkAsync(_commandText, _parameters, transactionState, cancellationToken)
                .ConfigureAwait(false);
        }

        // #213：不再在此处做字符串字面量替换。原始 SQL + 参数下沉给具体连接实现：
        // 嵌入式走 Core AST 值绑定（防注入 + 复用解析缓存）；远程仍在其 impl 内按需绑定。
        if (IsSqlTransactionControl(_commandText))
            throw new InvalidOperationException("请通过 SndbConnection.BeginTransaction()/SndbTransaction 控制事务。");
        return await impl.ExecuteAsync(_commandText, _parameters, behavior, transactionState, cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool IsSqlTransactionControl(string sql)
    {
        var text = sql.Trim();
        while (text.EndsWith(';'))
            text = text[..^1].TrimEnd();

        return text.Equals("BEGIN", StringComparison.OrdinalIgnoreCase)
            || text.Equals("BEGIN TRANSACTION", StringComparison.OrdinalIgnoreCase)
            || text.Equals("COMMIT", StringComparison.OrdinalIgnoreCase)
            || text.Equals("COMMIT TRANSACTION", StringComparison.OrdinalIgnoreCase)
            || text.Equals("ROLLBACK", StringComparison.OrdinalIgnoreCase)
            || text.Equals("ROLLBACK TRANSACTION", StringComparison.OrdinalIgnoreCase);
    }
}
