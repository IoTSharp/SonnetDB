using System.Data;
using System.Data.Common;
using System.Reflection;
using SonnetDB.Data;
using SonnetDB.Data.Internal;
using SonnetDB.Data.Remote;
using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;

namespace SonnetDB.Core.Tests.Ado;

/// <summary>
/// 验证 SndbCommand 的命令超时、调用方取消优先级以及执行资源释放语义。
/// </summary>
public sealed class SndbCommandTimeoutTests
{
    private static readonly FieldInfo ConnectionImplField = typeof(SndbConnection).GetField(
        "_impl",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("未找到 SndbConnection._impl 测试注入点。");

    /// <summary>六条公开执行路径都应由同一个 CommandTimeout 令牌终止。</summary>
    [Theory]
    [InlineData(ExecutionPath.NonQuerySync)]
    [InlineData(ExecutionPath.NonQueryAsync)]
    [InlineData(ExecutionPath.ScalarSync)]
    [InlineData(ExecutionPath.ScalarAsync)]
    [InlineData(ExecutionPath.ReaderSync)]
    [InlineData(ExecutionPath.ReaderAsync)]
    public async Task CommandTimeout_AllExecutionPaths_ThrowsTimeoutException(ExecutionPath path)
    {
        bool delayExecution = path is ExecutionPath.ReaderSync or ExecutionPath.ReaderAsync;
        var implementation = new ControllableConnectionImpl(delayExecution);
        using var connection = CreateOpenConnection(implementation);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM timeout_probe";
        command.CommandTimeout = 1;

        Exception? exception;
        try
        {
            exception = await Record.ExceptionAsync(() => InvokeAsync(command, path))
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            // 实现出错时也释放测试门闩，避免遗留永久等待的后台任务。
            implementation.Release();
        }

        var timeout = Assert.IsType<TimeoutException>(exception);
        Assert.Contains("CommandTimeout=1", timeout.Message, StringComparison.Ordinal);
        Assert.Equal(1, implementation.ExecutionCount);
        if (!delayExecution)
            Assert.True(implementation.ResultDisposed);
    }

    /// <summary>CommandTimeout 为零或负值时不启动计时器，保持无限等待的既有兼容语义。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task CommandTimeout_NonPositive_DoesNotStartTimer(int commandTimeout)
    {
        var implementation = new ControllableConnectionImpl(delayExecution: true);
        using var connection = CreateOpenConnection(implementation);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM timeout_probe";
        command.CommandTimeout = commandTimeout;

        var execution = command.ExecuteNonQueryAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        Assert.False(execution.IsCompleted);

        implementation.Release();
        Assert.Equal(-1, await execution.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, implementation.ExecutionCount);
        Assert.True(implementation.ResultDisposed);
    }

    /// <summary>调用方主动取消时保留原始令牌，不得转换成命令超时。</summary>
    [Fact]
    public async Task CallerCancellation_TakesPrecedenceAndPreservesToken()
    {
        var implementation = new ControllableConnectionImpl(delayExecution: true);
        using var connection = CreateOpenConnection(implementation);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM timeout_probe";
        command.CommandTimeout = 30;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(
            () => command.ExecuteNonQueryAsync(cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(1, implementation.ExecutionCount);
    }

    /// <summary>执行阶段与结果消费阶段共享同一时间预算，不能在读取结果时重新计时。</summary>
    [Fact]
    public async Task CommandTimeout_ExecutionAndResultConsumption_ShareSingleBudget()
    {
        var stageDelay = TimeSpan.FromMilliseconds(650);
        var implementation = new ControllableConnectionImpl(
            delayExecution: false,
            executionDelay: stageDelay,
            resultDelay: stageDelay);
        using var connection = CreateOpenConnection(implementation);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM timeout_probe";
        command.CommandTimeout = 1;

        var exception = await Assert.ThrowsAsync<TimeoutException>(() => command.ExecuteScalarAsync())
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("CommandTimeout=1", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, implementation.ExecutionCount);
        Assert.True(implementation.ResultDisposed);
    }

    /// <summary>Reader 返回后，同步和异步行读取都必须对正文阻塞应用 CommandTimeout。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReaderRead_CommandTimeout_AppliesToSyncAndAsyncReads(bool useAsyncRead)
    {
        var implementation = new ControllableConnectionImpl(
            delayExecution: false,
            resultDelay: Timeout.InfiniteTimeSpan);
        using var connection = CreateOpenConnection(implementation);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM timeout_probe";
        command.CommandTimeout = 1;

        Exception? exception;
        await using (var reader = await command.ExecuteReaderAsync())
        {
            exception = await Record.ExceptionAsync(async () =>
            {
                if (useAsyncRead)
                    _ = await reader.ReadAsync().ConfigureAwait(false);
                else
                    _ = await Task.Run(reader.Read).ConfigureAwait(false);
            }).WaitAsync(TimeSpan.FromSeconds(5));
        }

        var timeout = Assert.IsType<TimeoutException>(exception);
        Assert.Contains("CommandTimeout=1", timeout.Message, StringComparison.Ordinal);
        Assert.True(implementation.ResultDisposed);
    }

    /// <summary>每次 Read 都重新获得完整窗口，用户处理上一行的空闲时间不计入下一次读取。</summary>
    [Fact]
    public async Task ReaderRead_EachCallUsesIndependentTimeoutWindow()
    {
        var implementation = new ControllableConnectionImpl(
            delayExecution: false,
            resultDelay: TimeSpan.FromMilliseconds(650),
            resultRowCount: 2);
        using var connection = CreateOpenConnection(implementation);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM timeout_probe";
        command.CommandTimeout = 1;

        await using var reader = await command.ExecuteReaderAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(1_100));

        Assert.True(await reader.ReadAsync().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(await reader.ReadAsync().WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(await reader.ReadAsync().WaitAsync(TimeSpan.FromSeconds(2)));
    }

    /// <summary>
    /// 成功读取应复用同一个 Reader 超时源，且上次读取的调用方令牌必须及时解除注册。
    /// </summary>
    [Fact]
    public async Task ReaderRead_SuccessfulCalls_ReuseTimeoutTokenAndDetachCallerToken()
    {
        const int RowCount = 128;
        var implementation = new ControllableConnectionImpl(
            delayExecution: false,
            resultDelay: TimeSpan.Zero,
            resultRowCount: RowCount);
        using var connection = CreateOpenConnection(implementation);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM timeout_probe";
        command.CommandTimeout = 1;

        await using var reader = await command.ExecuteReaderAsync();
        using var firstCaller = new CancellationTokenSource();
        using var secondCaller = new CancellationTokenSource();

        Assert.True(await reader.ReadAsync(firstCaller.Token));
        firstCaller.Cancel();
        for (int row = 1; row < RowCount; row++)
            Assert.True(await reader.ReadAsync(secondCaller.Token));
        Assert.False(await reader.ReadAsync(secondCaller.Token));

        Assert.Equal(RowCount + 1, implementation.ResultReadTokens.Count);
        var timeoutToken = implementation.ResultReadTokens[0];
        Assert.All(
            implementation.ResultReadTokens,
            token => Assert.Equal(timeoutToken, token));
    }

    /// <summary>ReadAsync 的调用方取消优先于读取超时，并保留调用方令牌。</summary>
    [Fact]
    public async Task ReaderRead_CallerCancellation_PreservesCallerToken()
    {
        var implementation = new ControllableConnectionImpl(
            delayExecution: false,
            resultDelay: Timeout.InfiniteTimeSpan);
        using var connection = CreateOpenConnection(implementation);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM timeout_probe";
        command.CommandTimeout = 30;
        await using var reader = await command.ExecuteReaderAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(
            () => reader.ReadAsync(cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    /// <summary>连接自身先产生取消异常时，不得误报为 CommandTimeout。</summary>
    [Fact]
    public async Task ConnectionTimeout_First_PreservesUnderlyingCancellation()
    {
        var implementation = new ControllableConnectionImpl(
            delayExecution: false,
            executionException: new TaskCanceledException("模拟连接超时。"));
        using var connection = CreateOpenConnection(implementation);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM timeout_probe";
        command.CommandTimeout = 30;

        var exception = await Assert.ThrowsAsync<TaskCanceledException>(
            () => command.ExecuteNonQueryAsync());

        Assert.Equal("模拟连接超时。", exception.Message);
    }

    /// <summary>命令超时与调用方取消在同一传播窗口发生时，调用方取消按既定策略优先。</summary>
    [Fact]
    public async Task CallerCancellation_RacingCommandTimeout_PreservesCallerToken()
    {
        using var cancellation = new CancellationTokenSource();
        var implementation = new ControllableConnectionImpl(
            delayExecution: true,
            onExecutionCanceled: cancellation.Cancel);
        using var connection = CreateOpenConnection(implementation);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM timeout_probe";
        command.CommandTimeout = 1;

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(
            () => command.ExecuteNonQueryAsync(cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    /// <summary>同步和异步执行中的 Cancel 都必须终止当前命令租约。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancel_DuringExecution_ThrowsOperationCanceledException(bool useSynchronousApi)
    {
        var implementation = new ControllableConnectionImpl(delayExecution: true);
        using var connection = CreateOpenConnection(implementation);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM timeout_probe";

        Task execution = useSynchronousApi
            ? Task.Run(command.ExecuteNonQuery)
            : command.ExecuteNonQueryAsync();
        await WaitForExecutionStartAsync(implementation);
        command.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => execution);
    }

    /// <summary>TableDirect 必须把调用方取消令牌传给 provider 的批量执行入口。</summary>
    [Fact]
    public async Task TableDirect_CallerCancellation_ReachesBulkProvider()
    {
        var implementation = new ControllableConnectionImpl(delayExecution: true);
        using var connection = CreateOpenConnection(implementation);
        using var command = connection.CreateCommand();
        command.CommandType = CommandType.TableDirect;
        command.CommandText = "cpu,host=test value=1 1";
        using var cancellation = new CancellationTokenSource();

        Task<int> execution = command.ExecuteNonQueryAsync(cancellation.Token);
        await WaitForExecutionStartAsync(implementation);
        cancellation.Cancel();

        OperationCanceledException error = await Assert.ThrowsAsync<OperationCanceledException>(() => execution);
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    /// <summary>TableDirect 必须把 CommandTimeout 令牌传给 provider 的批量执行入口。</summary>
    [Fact]
    public async Task TableDirect_CommandTimeout_ReachesBulkProvider()
    {
        var implementation = new ControllableConnectionImpl(delayExecution: true);
        using var connection = CreateOpenConnection(implementation);
        using var command = connection.CreateCommand();
        command.CommandType = CommandType.TableDirect;
        command.CommandText = "cpu,host=test value=1 1";
        command.CommandTimeout = 1;

        await Assert.ThrowsAsync<TimeoutException>(() => command.ExecuteNonQueryAsync())
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, implementation.ExecutionCount);
    }

    /// <summary>Reader 返回后，Cancel 仍必须中断正在等待正文行的同步和异步读取。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancel_DuringReaderRead_ThrowsOperationCanceledException(bool useSynchronousRead)
    {
        var implementation = new ControllableConnectionImpl(
            delayExecution: false,
            resultDelay: Timeout.InfiniteTimeSpan);
        using var connection = CreateOpenConnection(implementation);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM timeout_probe";
        await using var reader = await command.ExecuteReaderAsync();

        Task read = useSynchronousRead
            ? Task.Run(reader.Read)
            : reader.ReadAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        command.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => read);
        reader.Close();
        await using var nextReader = await command.ExecuteReaderAsync();
    }

    /// <summary>Reader 结果释放和 CloseConnection 同时失败时必须保留最先发生的结果释放异常。</summary>
    [Fact]
    public async Task ReaderClose_ResultDisposeFailure_PrecedesConnectionCloseFailure()
    {
        var resultFailure = new InvalidOperationException("result dispose failed");
        var closeFailure = new IOException("connection close failed");
        var implementation = new ControllableConnectionImpl(
            delayExecution: false,
            resultDisposeException: resultFailure,
            closeException: closeFailure);
        using var connection = CreateOpenConnection(implementation);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM timeout_probe";
        var reader = await command.ExecuteReaderAsync(CommandBehavior.CloseConnection);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(reader.Close);

        Assert.Same(resultFailure, error);
        Assert.True(implementation.ResultDisposed);
        Assert.True(implementation.CloseAttempted);
    }

    /// <summary>例程取消保留在 Core 中的错误合同，ADO 边界按超时来源转换为 TimeoutException。</summary>
    [Fact]
    public async Task CommandTimeout_RoutineCancellation_ThrowsTimeoutException()
    {
        var implementation = new ControllableConnectionImpl(
            delayExecution: true,
            cancellationExceptionFactory: static () => new RoutineExecutionException(
                RoutineErrorCodes.Cancelled,
                "例程调用已取消。"));
        using var connection = CreateOpenConnection(implementation);
        using var command = connection.CreateCommand();
        command.CommandText = "CALL slow_routine()";
        command.CommandTimeout = 1;

        await Assert.ThrowsAsync<TimeoutException>(() => command.ExecuteNonQueryAsync())
            .WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>远程响应已返回响应头但首个 meta 行迟迟不到时，读取必须响应取消并释放流。</summary>
    [Fact]
    public async Task RemoteExecutionResultCreateAsync_CanceledBeforeMeta_DisposesStream()
    {
        var stream = new BlockingReadStream();
        using var response = new HttpResponseMessage
        {
            Content = new StreamContent(stream),
        };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RemoteExecutionResult.CreateAsync(response, stream, cancellation.Token));

        Assert.True(stream.IsDisposed);
    }

    /// <summary>Core 执行入口必须保留例程取消错误合同，供 ADO.NET 边界转换。</summary>
    [Fact]
    public void SqlExecutor_CanceledExecutionOptions_ThrowsRoutineCancellationException()
    {
        string root = Path.Combine(Path.GetTempPath(), "sndb-command-timeout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var database = Tsdb.Open(new TsdbOptions { RootDirectory = root });
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            var exception = Assert.Throws<RoutineExecutionException>(() => SqlExecutor.ExecuteStatement(
                database,
                databaseName: null,
                SqlParser.Parse("SELECT 1"),
                controlPlane: null,
                transaction: null,
                new SqlExecutionOptions { CancellationToken = cancellation.Token }));
            Assert.Equal(RoutineErrorCodes.Cancelled, exception.Code);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* 临时目录清理失败不掩盖断言结果。 */ }
        }
    }

    /// <summary>文档 SQL 删除在候选扫描中取消时不得提交已匹配的前置文档。</summary>
    [Fact]
    public void EmbeddedDocumentDelete_MidCandidateCancellation_LeavesAllDocumentsUntouched()
    {
        string root = Path.Combine(Path.GetTempPath(), "sndb-document-delete-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var database = Tsdb.Open(new TsdbOptions { RootDirectory = root });
            SqlExecutor.Execute(database, "CREATE DOCUMENT COLLECTION cancellation_docs");
            var store = database.Documents.Open("cancellation_docs");
            Assert.False(store.InsertMany(
            [
                new DocumentWriteRequest("doc-1", "{\"state\":\"pending\"}"),
                new DocumentWriteRequest("doc-2", "{\"state\":\"pending\"}"),
                new DocumentWriteRequest("doc-3", "{\"state\":\"pending\"}"),
            ]).HasErrors);

            using var cancellation = new CancellationTokenSource();
            int predicateCalls = 0;
            database.Functions.RegisterScalar(
                "cancel_after_first_delete_candidate",
                _ =>
                {
                    if (Interlocked.Increment(ref predicateCalls) == 1)
                        cancellation.Cancel();
                    return true;
                },
                minArgumentCount: 1,
                maxArgumentCount: 1);

            var exception = Assert.Throws<RoutineExecutionException>(() => SqlExecutor.ExecuteStatement(
                database,
                databaseName: null,
                SqlParser.Parse("DELETE FROM cancellation_docs WHERE cancel_after_first_delete_candidate(id)"),
                controlPlane: null,
                transaction: null,
                new SqlExecutionOptions { CancellationToken = cancellation.Token }));

            Assert.Equal(RoutineErrorCodes.Cancelled, exception.Code);
            Assert.Equal(1, predicateCalls);
            Assert.Equal(["doc-1", "doc-2", "doc-3"], store.Scan().Select(static row => row.Id).ToArray());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* 临时目录清理失败不掩盖断言结果。 */ }
        }
    }

    /// <summary>文档 SQL 更新在候选扫描中取消时不得替换已匹配的前置文档。</summary>
    [Fact]
    public void EmbeddedDocumentUpdate_MidCandidateCancellation_LeavesAllDocumentsUntouched()
    {
        string root = Path.Combine(Path.GetTempPath(), "sndb-document-update-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var database = Tsdb.Open(new TsdbOptions { RootDirectory = root });
            SqlExecutor.Execute(database, "CREATE DOCUMENT COLLECTION cancellation_docs");
            var store = database.Documents.Open("cancellation_docs");
            Assert.False(store.InsertMany(
            [
                new DocumentWriteRequest("doc-1", "{\"state\":\"pending\"}"),
                new DocumentWriteRequest("doc-2", "{\"state\":\"pending\"}"),
                new DocumentWriteRequest("doc-3", "{\"state\":\"pending\"}"),
            ]).HasErrors);

            using var cancellation = new CancellationTokenSource();
            int predicateCalls = 0;
            database.Functions.RegisterScalar(
                "cancel_after_first_update_candidate",
                _ =>
                {
                    if (Interlocked.Increment(ref predicateCalls) == 1)
                        cancellation.Cancel();
                    return true;
                },
                minArgumentCount: 1,
                maxArgumentCount: 1);

            var exception = Assert.Throws<RoutineExecutionException>(() => SqlExecutor.ExecuteStatement(
                database,
                databaseName: null,
                SqlParser.Parse("UPDATE cancellation_docs SET document = '{\"state\":\"updated\"}' WHERE cancel_after_first_update_candidate(id)"),
                controlPlane: null,
                transaction: null,
                new SqlExecutionOptions { CancellationToken = cancellation.Token }));

            Assert.Equal(RoutineErrorCodes.Cancelled, exception.Code);
            Assert.Equal(1, predicateCalls);
            Assert.All(store.Scan(), row => Assert.Equal("{\"state\":\"pending\"}", row.Json));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* 临时目录清理失败不掩盖断言结果。 */ }
        }
    }

    /// <summary>创建一个已打开状态的连接门面，并注入可控的内部实现。</summary>
    private static SndbConnection CreateOpenConnection(IConnectionImpl implementation)
    {
        var connection = new SndbConnection();
        ConnectionImplField.SetValue(connection, implementation);
        return connection;
    }

    /// <summary>按测试枚举调用同步或异步 ADO.NET 执行入口。</summary>
    private static async Task InvokeAsync(SndbCommand command, ExecutionPath path)
    {
        switch (path)
        {
            case ExecutionPath.NonQuerySync:
                _ = await Task.Run(command.ExecuteNonQuery).ConfigureAwait(false);
                break;
            case ExecutionPath.NonQueryAsync:
                _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
                break;
            case ExecutionPath.ScalarSync:
                _ = await Task.Run(command.ExecuteScalar).ConfigureAwait(false);
                break;
            case ExecutionPath.ScalarAsync:
                _ = await command.ExecuteScalarAsync().ConfigureAwait(false);
                break;
            case ExecutionPath.ReaderSync:
                await Task.Run(() =>
                {
                    using var reader = command.ExecuteReader();
                }).ConfigureAwait(false);
                break;
            case ExecutionPath.ReaderAsync:
                await using (var reader = await command.ExecuteReaderAsync().ConfigureAwait(false))
                {
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(path), path, "未知的命令执行路径。");
        }
    }

    /// <summary>等待可控连接已进入执行，以避免 Cancel 与命令启动竞态导致无效测试。</summary>
    private static async Task WaitForExecutionStartAsync(ControllableConnectionImpl implementation)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (implementation.ExecutionCount != 0)
                return;
            await Task.Delay(TimeSpan.FromMilliseconds(10)).ConfigureAwait(false);
        }

        throw new TimeoutException("测试命令未在预期时间内开始执行。");
    }

    public enum ExecutionPath
    {
        NonQuerySync,
        NonQueryAsync,
        ScalarSync,
        ScalarAsync,
        ReaderSync,
        ReaderAsync,
    }

    /// <summary>
    /// 可控连接实现：读取器路径阻塞命令创建，其余路径返回一个只允许异步消费的结果。
    /// </summary>
    private sealed class ControllableConnectionImpl(
        bool delayExecution,
        TimeSpan? executionDelay = null,
        TimeSpan? resultDelay = null,
        int resultRowCount = 1,
        Exception? executionException = null,
        Action? onExecutionCanceled = null,
        Func<Exception>? cancellationExceptionFactory = null,
        Exception? resultDisposeException = null,
        Exception? closeException = null) : IConnectionImpl
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _executionCount;
        private bool _disposed;

        public string DataSource => "timeout-test";

        public string Database => "timeout-test";

        public string ServerVersion => "test";

        public ConnectionState State => _disposed ? ConnectionState.Closed : ConnectionState.Open;

        public int ExecutionCount => Volatile.Read(ref _executionCount);

        public bool ResultDisposed { get; private set; }

        public bool CloseAttempted { get; private set; }

        public List<CancellationToken> ResultReadTokens { get; } = [];

        /// <summary>同步执行不是 SndbCommand 当前路径的一部分，调用即表示测试回归。</summary>
        public IExecutionResult Execute(
            string sql,
            SndbParameterCollection parameters,
            CommandBehavior behavior,
            object? transactionState)
            => throw new InvalidOperationException("SndbCommand 不应绕过可取消的异步执行入口。");

        /// <summary>等待命令令牌或测试门闩，再返回可控结果。</summary>
        public async Task<IExecutionResult> ExecuteAsync(
            string sql,
            SndbParameterCollection parameters,
            CommandBehavior behavior,
            object? transactionState,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _executionCount);
            if (executionException is not null)
                throw executionException;

            try
            {
                if (executionDelay is { } finiteDelay)
                    await Task.Delay(finiteDelay, cancellationToken).ConfigureAwait(false);
                else if (delayExecution)
                    await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                onExecutionCanceled?.Invoke();
                if (cancellationExceptionFactory is not null)
                    throw cancellationExceptionFactory();
                throw;
            }

            return new BlockingExecutionResult(
                _release.Task,
                () => ResultDisposed = true,
                resultDelay,
                resultRowCount,
                ResultReadTokens,
                resultDisposeException);
        }

        /// <summary>批量同步入口不应由当前命令实现调用。</summary>
        public IExecutionResult ExecuteBulk(
            string commandText,
            SndbParameterCollection parameters,
            object? transactionState)
            => throw new NotSupportedException();

        /// <summary>复用可控异步执行门闩，验证 TableDirect 的取消令牌传递。</summary>
        public Task<IExecutionResult> ExecuteBulkAsync(
            string commandText,
            SndbParameterCollection parameters,
            object? transactionState,
            CancellationToken cancellationToken)
            => ExecuteAsync(
                commandText,
                parameters,
                CommandBehavior.Default,
                transactionState,
                cancellationToken);

        /// <summary>测试不覆盖事务。</summary>
        public object BeginTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();

        /// <summary>测试不覆盖事务。</summary>
        public void CommitTransaction(object transactionState) => throw new NotSupportedException();

        /// <summary>测试不覆盖事务。</summary>
        public Task CommitTransactionAsync(object transactionState, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        /// <summary>测试不覆盖元数据快照。</summary>
        public IReadOnlyList<TableSchema> SnapshotTables() => throw new NotSupportedException();

        /// <summary>测试不覆盖事务。</summary>
        public void RollbackTransaction(object transactionState) => throw new NotSupportedException();

        /// <summary>测试不覆盖事务。</summary>
        public Task RollbackTransactionAsync(object transactionState, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        /// <summary>连接已由测试直接置为打开状态。</summary>
        public void Open() { }

        /// <summary>连接已由测试直接置为打开状态。</summary>
        public ValueTask OpenAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        /// <summary>关闭测试实现并释放所有等待者。</summary>
        public void Close()
        {
            CloseAttempted = true;
            _disposed = true;
            Release();
            if (closeException is not null)
                throw closeException;
        }

        /// <summary>释放测试实现。</summary>
        public void Dispose() => Close();

        /// <summary>释放可控等待点，供成功路径和失败清理使用。</summary>
        public void Release() => _release.TrySetResult();
    }

    /// <summary>只允许带令牌异步消费的测试结果，用于确认超时覆盖结果读取阶段。</summary>
    private sealed class BlockingExecutionResult(
        Task release,
        Action onDispose,
        TimeSpan? resultDelay,
        int resultRowCount,
        List<CancellationToken> readTokens,
        Exception? disposeException) : IExecutionResult
    {
        private int _rowsRead;

        public int RecordsAffected => -1;

        public IReadOnlyList<string> Columns { get; } = ["value"];

        /// <summary>同步消费会绕过超时令牌，因此测试直接判定为回归。</summary>
        public bool ReadNextRow()
            => throw new InvalidOperationException("结果消费必须使用可取消的异步读取。");

        /// <summary>等待命令超时令牌或测试释放门闩。</summary>
        public async ValueTask<bool> ReadNextRowAsync(CancellationToken cancellationToken)
        {
            readTokens.Add(cancellationToken);
            if (resultDelay is { } finiteDelay)
            {
                if (_rowsRead >= resultRowCount)
                    return false;

                await Task.Delay(finiteDelay, cancellationToken).ConfigureAwait(false);
                _rowsRead++;
                return true;
            }

            await release.WaitAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        /// <summary>分阶段结果定位成功后返回固定测试值。</summary>
        public object? GetValue(int ordinal)
            => _rowsRead > 0 ? 1L : throw new InvalidOperationException("测试结果没有当前行。");

        /// <summary>返回测试列的声明类型。</summary>
        public Type GetFieldType(int ordinal) => typeof(object);

        /// <summary>记录结果释放，并按需注入清理异常。</summary>
        public void Dispose()
        {
            onDispose();
            if (disposeException is not null)
                throw disposeException;
        }
    }

    /// <summary>直到取消前不返回任何字节的流，用于模拟远程首包卡住。</summary>
    private sealed class BlockingReadStream : Stream
    {
        public bool IsDisposed { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        /// <summary>同步读取不用于异步远程路径。</summary>
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <summary>异步读取保持等待，直到命令令牌取消。</summary>
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        /// <summary>测试流不支持定位。</summary>
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        /// <summary>测试流不支持设置长度。</summary>
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <summary>测试流不支持写入。</summary>
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <summary>测试流没有待刷新的内容。</summary>
        public override void Flush() { }

        /// <summary>记录远程结果创建失败后是否释放底层流。</summary>
        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
