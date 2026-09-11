using System.Diagnostics;
using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;

namespace SonnetDB.Routines;

/// <summary>显式调用的关系表 outbox 批处理选项；不会启动后台线程或自动轮询。</summary>
public sealed record SqlOutboxWorkerOptions
{
    /// <summary>工作器独占管理的关系表名称，仅允许 ASCII 字母、数字和下划线。</summary>
    public string TableName { get; init; } = "sql_outbox";
    /// <summary>一次调用最多检查和领取的事件数，范围 1～1000。</summary>
    public int MaxBatchSize { get; init; } = 32;
    /// <summary>一次批处理的取消期限，范围大于零且不超过两分钟。</summary>
    public TimeSpan BatchTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>持久化领取租约的有效期，过期后允许其他工作器重新投递。</summary>
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(1);
    /// <summary>单事件最多领取次数；达到上限的失败或过期租约进入 dead 状态。</summary>
    public int MaxAttempts { get; init; } = 5;
    /// <summary>首次失败后的重试间隔，后续按次数指数增加并受最大间隔限制。</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>最大重试间隔。</summary>
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromMinutes(5);
    /// <summary>允许交给处理器的单事件正文字符数上限。</summary>
    public int MaxPayloadCharacters { get; init; } = 1_048_576;

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(TableName);
        if (TableName.Length > 64 || !(char.IsAsciiLetter(TableName[0]) || TableName[0] == '_')
            || TableName.Any(static c => !(char.IsAsciiLetterOrDigit(c) || c == '_')))
            throw new ArgumentException("outbox 表名必须是长度不超过 64 的 ASCII SQL 标识符。", nameof(TableName));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxBatchSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxBatchSize, 1000);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(BatchTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(BatchTimeout, TimeSpan.FromMinutes(2));
        ArgumentOutOfRangeException.ThrowIfLessThan(LeaseDuration, TimeSpan.FromMilliseconds(1));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(LeaseDuration, TimeSpan.FromDays(1));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxAttempts, 1000);
        ArgumentOutOfRangeException.ThrowIfLessThan(RetryDelay, TimeSpan.FromMilliseconds(1));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxRetryDelay, RetryDelay);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxRetryDelay, TimeSpan.FromDays(1));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxPayloadCharacters, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxPayloadCharacters, 16_777_216);
    }
}

/// <summary>已经持久化领取的 outbox 事件；EventId 在重试和数据库重开后保持不变。</summary>
/// <param name="EventId">生产者指定的稳定幂等键。</param>
/// <param name="Topic">生产者指定的业务主题。</param>
/// <param name="Payload">原样保存的正文；工作器不执行 JSON 序列化。</param>
/// <param name="Attempt">当前领取次数，从 1 开始。</param>
public sealed record SqlOutboxMessage(string EventId, string Topic, string Payload, int Attempt);

/// <summary>一次有界 outbox 批处理的统计；被取消的批处理抛出取消异常。</summary>
/// <param name="Claimed">本次领取的事件数。</param>
/// <param name="Delivered">处理成功且租约确认成功的事件数。</param>
/// <param name="Retried">处理失败后已安排重试的事件数。</param>
/// <param name="DeadLettered">本次标记为 dead 的事件数。</param>
/// <param name="LeaseLost">处理结束时租约已过期或被替换的事件数。</param>
public sealed record SqlOutboxBatchResult(int Claimed, int Delivered, int Retried, int DeadLettered, int LeaseLost);

/// <summary>以关系表持久化租约的显式 outbox 工作器，提供至少一次投递语义。</summary>
/// <remarks>
/// <para>创建工作器会创建或校验专用关系表。生产者在原事务或 SQL 触发器中执行
/// <c>INSERT INTO sql_outbox (event_id, topic, payload) VALUES (...)</c>；省略的工作器列使用默认值。
/// 工作器列 state、attempts、available_at、lease_token、lease_until、last_error、completed_at
/// 由工作器管理；时间列使用 Unix 毫秒。状态为 pending、leased、done、dead。</para>
/// <para>处理器在领取事务提交后、数据库锁之外执行，必须使用 EventId 实现幂等并遵守取消令牌。
/// 投递成功但确认前终止进程会再次投递相同 EventId。租约过期允许并行重投，旧处理器不能确认新租约。
/// done/dead 行保留供审计，清理及重投策略由宿主负责；本类型不会启动定时器进行后台轮询。</para>
/// <para>领取、确认和重试状态返回前显式同步 outbox WAL。源事件的持久性仍遵循源事务配置。
/// 状态 WAL 同步失败会报告结果未知并禁止继续使用该表，必须关闭并重开数据库；不能把该异常视作已回滚。</para>
/// </remarks>
public sealed class SqlOutboxWorker
{
    private readonly Tsdb _database;
    private readonly SqlOutboxWorkerOptions _options;
    private readonly TimeProvider _time;
    internal Action? BeforeStateSyncTestHook { get; set; }
    private string Table => _options.TableName;
    private const string Eligible = "((state = 'pending' AND available_at <= @now) OR (state = 'leased' AND lease_until <= @now))";
    private static readonly (string Name, TableColumnType Type, string? Default)[] Columns =
    [
        ("event_id", TableColumnType.String, null), ("topic", TableColumnType.String, "''"),
        ("payload", TableColumnType.String, null), ("state", TableColumnType.String, "'pending'"),
        ("attempts", TableColumnType.Int64, "0"), ("available_at", TableColumnType.Int64, "0"),
        ("lease_token", TableColumnType.String, "''"), ("lease_until", TableColumnType.Int64, "0"),
        ("last_error", TableColumnType.String, "''"), ("completed_at", TableColumnType.Int64, "0"),
    ];

    /// <summary>创建工作器并原子创建或校验其专用表；不能从活动 SQL 执行或事务中重入。</summary>
    /// <param name="database">同一数据库实例；工作器不拥有其生命周期。</param>
    /// <param name="options">批次、租约与重试设置。</param>
    /// <param name="timeProvider">提供租约 UTC 时间；为空时使用系统时钟。</param>
    public SqlOutboxWorker(Tsdb database, SqlOutboxWorkerOptions? options = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
        _options = options ?? new();
        _options.Validate();
        _time = timeProvider ?? TimeProvider.System;
        RejectAmbientExecution();
        using var deadline = new CancellationTokenSource(_options.BatchTimeout);
        // CREATE 按既有 schema → manager 锁序执行，不在 manager 锁内进入 schema 锁。
        Execute($"""
                    CREATE TABLE IF NOT EXISTS {Table} (
                        event_id STRING NOT NULL, topic STRING NOT NULL DEFAULT '', payload STRING NOT NULL,
                        state STRING NOT NULL DEFAULT 'pending', attempts INT NOT NULL DEFAULT 0,
                        available_at INT NOT NULL DEFAULT 0, lease_token STRING NOT NULL DEFAULT '',
                        lease_until INT NOT NULL DEFAULT 0, last_error STRING NOT NULL DEFAULT '',
                        completed_at INT NOT NULL DEFAULT 0, PRIMARY KEY (event_id))
                    """, SqlParameters.Empty, deadline.Token);
        _database.Tables.ExecuteCommitLocked(() =>
        {
            ValidateSchema();
            return 0;
        }, deadline.Token.ThrowIfCancellationRequested);
    }

    /// <summary>按既定数量和期限处理一个批次；无可领取事件时立即返回，禁止从活动 SQL 执行或事务中重入。</summary>
    /// <param name="handler">数据库锁外的异步处理器；必须按 EventId 幂等并协作取消。</param>
    /// <param name="cancellationToken">取消后保留已领取但未确认的租约，等待过期恢复。</param>
    /// <returns>已确认投递、重试、死信及租约丢失数量。</returns>
    public async Task<SqlOutboxBatchResult> ProcessBatchAsync(
        Func<SqlOutboxMessage, CancellationToken, ValueTask> handler, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        RejectAmbientExecution();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.BatchTimeout);
        CancellationToken token = deadline.Token;
        token.ThrowIfCancellationRequested();
        var candidateTime = SampleClock();
        var candidates = _database.Tables.ExecuteCommitLocked(() =>
        {
            ValidateSchema();
            return Select($"SELECT event_id FROM {Table} WHERE {Eligible} ORDER BY available_at, event_id LIMIT @take",
                new SqlParameters().AddNamed("now", CurrentTime(candidateTime)).AddNamed("take", _options.MaxBatchSize), token).Rows;
        }, token.ThrowIfCancellationRequested);
        int claimed = 0, delivered = 0, retried = 0, dead = 0, lost = 0;
        for (int index = 0; index < candidates.Count && index < _options.MaxBatchSize; index++)
        {
            token.ThrowIfCancellationRequested();
            var claim = Claim((string)candidates[index][0]!, token);
            if (claim.Dead) { dead++; continue; }
            if (claim.Message is not { } message) continue;
            claimed++;
            Exception? deliveryFailure = null;
            try
            {
                await handler(message, token).AsTask().WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception) { deliveryFailure = exception; }
            token.ThrowIfCancellationRequested();
            string state = deliveryFailure is null ? "done" : message.Attempt >= _options.MaxAttempts ? "dead" : "pending";
            if (!Complete(message, claim.Token, state, deliveryFailure?.GetType().Name ?? "", token)) lost++;
            else if (state == "done") delivered++;
            else if (state == "dead") dead++;
            else retried++;
        }
        return new(claimed, delivered, retried, dead, lost);
    }

    private (SqlOutboxMessage? Message, string Token, bool Dead) Claim(string id, CancellationToken token)
    {
        // 可注入时钟是宿主代码，只在数据库锁外调用。
        var clock = SampleClock();
        return _database.Tables.ExecuteCommitLocked(() =>
        {
            ValidateSchema();
            long now = CurrentTime(clock);
            var row = _database.Tables.Open(Table).GetByPrimaryKey([id]);
            if (row is null || !IsEligible(row.Values, now)) return ((SqlOutboxMessage?)null, "", false);
            long previousAttempts = (long)row.Values[4]!;
            string payload = (string)row.Values[2]!;
            object?[] values = row.Values.ToArray();
            if (previousAttempts < 0 || previousAttempts >= _options.MaxAttempts || payload.Length > _options.MaxPayloadCharacters)
            {
                values[3] = "dead";
                values[6] = "";
                values[7] = 0L;
                values[8] = payload.Length > _options.MaxPayloadCharacters ? "payload_limit" : "attempt_limit";
                UpdateOwnedRow(id, row, values, token);
                return ((SqlOutboxMessage?)null, "", true);
            }
            string leaseToken = Guid.NewGuid().ToString("N");
            int attempt = checked((int)previousAttempts + 1);
            values[3] = "leased";
            values[4] = (long)attempt;
            values[6] = leaseToken;
            values[7] = checked(now + (long)_options.LeaseDuration.TotalMilliseconds);
            UpdateOwnedRow(id, row, values, token);
            return (new SqlOutboxMessage(id, (string)row.Values[1]!, payload, attempt), leaseToken, false);
        }, token.ThrowIfCancellationRequested);
    }

    private bool Complete(SqlOutboxMessage message, string lease, string state, string error, CancellationToken token)
    {
        var clock = SampleClock();
        return _database.Tables.ExecuteCommitLocked(() =>
        {
            ValidateSchema();
            long now = CurrentTime(clock);
            var row = _database.Tables.Open(Table).GetByPrimaryKey([message.EventId]);
            if (row is null || !Equals(row.Values[3], "leased") || !Equals(row.Values[6], lease)
                || (long)row.Values[7]! <= now) return false;
            long delay = (long)Math.Min(_options.MaxRetryDelay.TotalMilliseconds,
                _options.RetryDelay.TotalMilliseconds * Math.Pow(2, Math.Min(message.Attempt - 1, 30)));
            object?[] values = row.Values.ToArray();
            values[3] = state;
            values[5] = state == "pending" ? checked(now + delay) : 0L;
            values[6] = "";
            values[7] = 0L;
            values[8] = error;
            values[9] = state == "done" ? now : 0L;
            return UpdateOwnedRow(message.EventId, row, values, token) == 1;
        }, token.ThrowIfCancellationRequested);
    }

    private static bool IsEligible(IReadOnlyList<object?> values, long now)
        => Equals(values[3], "pending") && (long)values[5]! <= now
            || Equals(values[3], "leased") && (long)values[7]! <= now;

    private int UpdateOwnedRow(string id, TableRow previous, object?[] values, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        TableSchema schema = _database.Tables.Catalog.TryGet(Table)!;
        var mutation = new TableRowMutation([id], values)
        {
            ExpectedRowState = TableRowCodec.Encode(schema, previous.Values),
        };
        // 专用元数据 mutation 保留普通存储约束及 WAL 合同，不派发用户 SQL UPDATE 触发器。
        int affected = _database.Tables.ApplyTransaction(new Dictionary<string, IReadOnlyList<TableRowMutation>>(StringComparer.Ordinal)
        {
            [Table] = [mutation],
        });
        TableStore store = _database.Tables.Open(Table);
        try
        {
            BeforeStateSyncTestHook?.Invoke();
            store.SyncTransactionWal();
        }
        catch (Exception exception)
        {
            // 同步失败时变更可能已经落盘；不写相反补偿，不进入外部处理器。
            store.InvalidateTransaction(exception);
            throw new TableTransactionRecoveryException("outbox 状态同步结果未知，必须关闭并重新打开数据库。", exception);
        }
        return affected;
    }

    private (long Milliseconds, long Timestamp) SampleClock()
        => (_time.GetUtcNow().ToUnixTimeMilliseconds(), Stopwatch.GetTimestamp());

    private static long CurrentTime((long Milliseconds, long Timestamp) clock)
        => checked(clock.Milliseconds + (long)Stopwatch.GetElapsedTime(clock.Timestamp).TotalMilliseconds);

    private object? Execute(string sql, SqlParameters parameters, CancellationToken token)
    {
        try
        {
            return SqlExecutor.ExecuteStatement(_database, null,
                SqlParameterBinder.Bind(SqlParser.Parse(sql), parameters), null, null,
                new SqlExecutionOptions { CancellationToken = token, Caller = "sql-outbox", EnableParallelism = false });
        }
        catch (RoutineExecutionException exception) when (exception.Code == RoutineErrorCodes.Cancelled && token.IsCancellationRequested)
        {
            throw new OperationCanceledException("outbox 批处理已取消。", exception, token);
        }
    }

    private SelectExecutionResult Select(string sql, SqlParameters parameters, CancellationToken token)
        => (SelectExecutionResult)Execute(sql, parameters, token)!;

    private static void RejectAmbientExecution()
    {
        if (SqlTransactionContext.Current is not null || RoutineExecutionContext.Current is not null)
            throw new InvalidOperationException("outbox 工作器只能在源 SQL 执行和事务结束后调用，禁止从 SQL 函数或例程中重入。");
    }

    private void ValidateSchema()
    {
        var schema = _database.Tables.Catalog.TryGet(Table)
            ?? throw new InvalidOperationException("outbox 表已被删除。");
        if (schema.Columns.Count != Columns.Length || schema.PrimaryKey.Count != 1 || schema.PrimaryKey[0] != "event_id"
            || schema.ForeignKeys.Count != 0 || schema.CheckConstraints.Count != 0
            || _database.Routines.FindTriggersDependingOnObject(Table)
                .Any(trigger => trigger.TableName == Table && trigger.Event == SqlTriggerEvent.Update))
            throw new InvalidOperationException("outbox 专用表 schema 或工作器 UPDATE 触发器不符合合同。");
        for (int index = 0; index < Columns.Length; index++)
        {
            var column = schema.Columns[index];
            var expected = Columns[index];
            if (column.Name != expected.Name || column.DataType != expected.Type || column.IsNullable
                || column.IsRowVersion || column.IsAutoIncrement || column.DefaultExpressionSql != expected.Default)
                throw new InvalidOperationException($"outbox 列 '{expected.Name}' 不符合工作器合同。");
        }
    }
}
