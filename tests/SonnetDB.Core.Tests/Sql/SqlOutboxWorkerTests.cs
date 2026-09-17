using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Kv;
using SonnetDB.Routines;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

public sealed class SqlOutboxWorkerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-outbox-" + Guid.NewGuid().ToString("N"));
    private readonly TestClock _clock = new();

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task ProcessBatch_SourceRollbackAndReopen_OnlyDeliversCommittedEvents()
    {
        using (var db = Open())
        {
            _ = Worker(db);
            Execute(db, "CREATE TABLE orders (id STRING, payload STRING, PRIMARY KEY (id))");
            Execute(db, """
                CREATE TRIGGER enqueue_order AFTER INSERT ON orders FOR EACH ROW LANGUAGE SQL AS BEGIN
                    INSERT INTO sql_outbox (event_id, topic, payload) VALUES (NEW.id, 'orders', NEW.payload);
                END
                """);
            SqlExecutor.ExecuteScript(db, "BEGIN; INSERT INTO orders (id, payload) VALUES ('cancelled', 'no-send'); ROLLBACK;");
            Assert.Empty(Select(db, "SELECT * FROM sql_outbox").Rows);
            Execute(db, "INSERT INTO orders (id, payload) VALUES ('committed', 'keep')");
        }
        using var reopened = Open();
        SqlOutboxMessage? delivered = null;
        var result = await Worker(reopened).ProcessBatchAsync((message, _) =>
        {
            delivered = message;
            return ValueTask.CompletedTask;
        });
        Assert.Equal(new SqlOutboxBatchResult(1, 1, 0, 0, 0), result);
        Assert.Equal(new SqlOutboxMessage("committed", "orders", "keep", 1), delivered);
        Assert.Equal(new object?[] { "done", 1L, "", 0L },
            Assert.Single(Select(reopened, "SELECT state, attempts, lease_token, lease_until FROM sql_outbox").Rows));
    }

    [Fact]
    public async Task ProcessBatch_HandlerFailure_PersistsBackoffThenRetriesStableId()
    {
        using var db = Open();
        var worker = Worker(db);
        Enqueue(db, "retry");
        var failed = await worker.ProcessBatchAsync((_, _) => throw new IOException("local delivery failed"));
        Assert.Equal(new SqlOutboxBatchResult(1, 0, 1, 0, 0), failed);
        Assert.Equal(new object?[] { "pending", 1L, "IOException" },
            Assert.Single(Select(db, "SELECT state, attempts, last_error FROM sql_outbox").Rows));
        int calls = 0;
        Assert.Equal(0, (await worker.ProcessBatchAsync((_, _) => { calls++; return ValueTask.CompletedTask; })).Claimed);
        Assert.Equal(0, calls);
        _clock.Advance(TimeSpan.FromSeconds(2));
        var retried = await worker.ProcessBatchAsync((message, _) =>
        {
            Assert.Equal("retry", message.EventId);
            Assert.Equal(2, message.Attempt);
            return ValueTask.CompletedTask;
        });
        Assert.Equal(1, retried.Delivered);
    }

    [Fact]
    public async Task ProcessBatch_DeliveryBeforeAcknowledgementAndReopen_RedeliversSameEvent()
    {
        string? firstId = null;
        using (var db = Open())
        {
            var worker = Worker(db);
            Enqueue(db, "stable-id");
            using var cancelled = new CancellationTokenSource();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.ProcessBatchAsync((message, _) =>
            {
                firstId = message.EventId;
                cancelled.Cancel();
                return ValueTask.CompletedTask;
            }, cancelled.Token));
            Assert.Equal("leased", Assert.Single(Select(db, "SELECT state FROM sql_outbox").Rows)[0]);
        }
        _clock.Advance(TimeSpan.FromSeconds(11));
        using var reopened = Open();
        var result = await Worker(reopened).ProcessBatchAsync((message, _) =>
        {
            Assert.Equal(firstId, message.EventId);
            Assert.Equal(2, message.Attempt);
            return ValueTask.CompletedTask;
        });
        Assert.Equal(1, result.Delivered);
    }

    [Fact]
    public async Task ProcessBatch_TwoWorkers_ActiveLeaseHasOnlyOneHandler()
    {
        using var db = Open();
        var first = Worker(db);
        var second = Worker(db);
        Enqueue(db, "shared");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<SqlOutboxBatchResult> running = first.ProcessBatchAsync(async (_, token) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var other = await second.ProcessBatchAsync((_, _) => throw new InvalidOperationException("lease duplicated"));
            Assert.Equal(0, other.Claimed);
        }
        finally
        {
            release.TrySetResult();
            await running.WaitAsync(TimeSpan.FromSeconds(2));
        }
        Assert.Equal(1, (await running).Delivered);
    }

    [Fact]
    public async Task ProcessBatch_ExpiredHandlerAcknowledgesReplacementLease_RejectsStaleAcknowledgement()
    {
        using var db = Open();
        var first = Worker(db);
        var second = Worker(db);
        Enqueue(db, "replace");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<SqlOutboxBatchResult>? replacement = null;
        try
        {
            var original = await first.ProcessBatchAsync((_, _) =>
            {
                _clock.Advance(TimeSpan.FromSeconds(11));
                replacement = second.ProcessBatchAsync(async (message, token) =>
                {
                    Assert.Equal(2, message.Attempt);
                    await release.Task.WaitAsync(token);
                });
                return ValueTask.CompletedTask;
            });
            Assert.Equal(1, original.LeaseLost);
            Assert.Equal("leased", Assert.Single(Select(db, "SELECT state FROM sql_outbox").Rows)[0]);
        }
        finally
        {
            release.TrySetResult();
            if (replacement is not null) await replacement.WaitAsync(TimeSpan.FromSeconds(2));
        }
        Assert.NotNull(replacement);
        Assert.Equal(1, (await replacement).Delivered);
    }

    [Fact]
    public async Task ProcessBatch_BatchDeadline_CancelsHandlerAndLeavesRecoverableLease()
    {
        using var db = Open();
        var worker = new SqlOutboxWorker(db, Options() with { BatchTimeout = TimeSpan.FromMilliseconds(100) }, _clock);
        Enqueue(db, "deadline");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.ProcessBatchAsync(async (_, token) =>
            await Task.Delay(TimeSpan.FromSeconds(3), token)));
        Assert.Equal("leased", Assert.Single(Select(db, "SELECT state FROM sql_outbox").Rows)[0]);
        _clock.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal(1, (await Worker(db).ProcessBatchAsync((_, _) => ValueTask.CompletedTask)).Delivered);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessBatch_AttemptLimit_MarksFailedOrExpiredEventDead(bool expires)
    {
        using var db = Open();
        var worker = new SqlOutboxWorker(db, Options() with { MaxAttempts = 1 }, _clock);
        Enqueue(db, "exhausted");
        if (expires)
        {
            using var cancelled = new CancellationTokenSource();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.ProcessBatchAsync((_, _) =>
            {
                cancelled.Cancel();
                return ValueTask.CompletedTask;
            }, cancelled.Token));
            _clock.Advance(TimeSpan.FromSeconds(11));
        }
        var result = await worker.ProcessBatchAsync((_, _) => throw new IOException("delivery failed"));
        Assert.Equal(1, result.DeadLettered);
        Assert.Equal("dead", Assert.Single(Select(db, "SELECT state FROM sql_outbox").Rows)[0]);
        Assert.Equal(0, (await worker.ProcessBatchAsync((_, _) => throw new IOException("must not deliver"))).Claimed);
    }

    [Fact]
    public async Task ProcessBatch_MaxItemsAndParameterizedPayload_BoundsBatchWithoutInterpretingData()
    {
        using var db = Open();
        var worker = new SqlOutboxWorker(db, Options() with { MaxBatchSize = 1 }, _clock);
        const string value = "'; DROP TABLE sql_outbox; --";
        Enqueue(db, "a", value);
        Enqueue(db, "b", "second");
        var result = await worker.ProcessBatchAsync((message, _) =>
        {
            Assert.Equal(value, message.Payload);
            return ValueTask.CompletedTask;
        });
        Assert.Equal(1, result.Delivered);
        Assert.Equal(2, Select(db, "SELECT * FROM sql_outbox").Rows.Count);
        Assert.Equal(1L, Assert.Single(Select(db, "SELECT COUNT(*) FROM sql_outbox WHERE state = 'pending'").Rows)[0]);
    }

    [Fact]
    public void Create_ConflictingSchemaOrUntrustedIdentifier_RejectsWithoutChangingUserTable()
    {
        using var db = Open();
        Execute(db, "CREATE TABLE sql_outbox (event_id STRING, PRIMARY KEY (event_id))");
        Assert.Throws<InvalidOperationException>(() => Worker(db));
        Assert.Throws<ArgumentException>(() => new SqlOutboxWorker(db, Options() with { TableName = "outbox; DROP TABLE sql_outbox" }));
        Assert.Single(db.Tables.Catalog.TryGet("sql_outbox")!.Columns);
    }

    /// <summary>无显式事务的标量函数也不能重入工作器或其构造器。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Worker_ScalarFunctionReentry_RejectsBeforeCreatingTableOrCallingHandler(bool construct)
    {
        using var db = Tsdb.Open(new TsdbOptions
        {
            RootDirectory = _root,
            AllowUserFunctions = true,
            BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var worker = Worker(db);
        Enqueue(db, "callback-reentry");
        Execute(db, "CREATE TABLE callback_rows (id INT, PRIMARY KEY (id))");
        Execute(db, "INSERT INTO callback_rows (id) VALUES (1)");
        int handlerCalls = 0;
        int callbackCalls = 0;
        db.Functions.RegisterScalar("reenter_outbox", arguments =>
        {
            callbackCalls++;
            Assert.Null(SqlTransactionContext.Current);
            Assert.NotNull(RoutineExecutionContext.Current);
            if (construct)
                _ = new SqlOutboxWorker(db, Options() with { TableName = "callback_outbox" }, _clock);
            else
                worker.ProcessBatchAsync((_, _) =>
                {
                    handlerCalls++;
                    return ValueTask.CompletedTask;
                }, deadline.Token).WaitAsync(deadline.Token).GetAwaiter().GetResult();
            return 1L;
        });

        Assert.Throws<InvalidOperationException>(() => SqlExecutor.ExecuteStatement(db, null,
            SqlParser.Parse("SELECT reenter_outbox(id) FROM callback_rows"), null, null,
            new SqlExecutionOptions { CancellationToken = deadline.Token }));

        Assert.Equal(1, callbackCalls);
        Assert.Equal(0, handlerCalls);
        Assert.Null(db.Tables.Catalog.TryGet("callback_outbox"));
        Assert.Equal("pending", Assert.Single(Select(db, "SELECT state FROM sql_outbox").Rows)[0]);
    }

    [Fact]
    public async Task ProcessBatch_UpdateTriggerAddedDuringDelivery_RejectsWithoutDispatchingUserAction()
    {
        using var db = Open();
        var worker = Worker(db);
        Enqueue(db, "schema-change");
        Execute(db, "CREATE TABLE unexpected_actions (id STRING, PRIMARY KEY (id))");
        await Assert.ThrowsAsync<InvalidOperationException>(() => worker.ProcessBatchAsync((_, _) =>
        {
            Execute(db, """
                CREATE TRIGGER unexpected_update AFTER UPDATE ON sql_outbox FOR EACH ROW LANGUAGE SQL AS BEGIN
                    INSERT INTO unexpected_actions (id) VALUES (NEW.event_id);
                END
                """);
            return ValueTask.CompletedTask;
        }));
        Assert.Empty(Select(db, "SELECT * FROM unexpected_actions").Rows);
        Assert.Equal("leased", Assert.Single(Select(db, "SELECT state FROM sql_outbox").Rows)[0]);
        Execute(db, "DROP TRIGGER unexpected_update");
        _clock.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal(1, (await worker.ProcessBatchAsync((_, _) => ValueTask.CompletedTask)).Delivered);
    }

    [Fact]
    public async Task Create_ConcurrentSchemaMutation_UsesSchemaThenManagerLockOrder()
    {
        using var db = Open();
        using var schemaEntered = new ManualResetEventSlim();
        using var constructorEntered = new ManualResetEventSlim();
        int schemaEntries = 0;
        db.SchemaMutationLockAcquiredTestHook = () =>
        {
            if (Interlocked.Increment(ref schemaEntries) != 1) return;
            schemaEntered.Set();
            Assert.True(constructorEntered.Wait(TimeSpan.FromSeconds(2)));
        };
        db.BeforeSchemaMutationLockTestHook = () =>
        {
            if (schemaEntered.IsSet) constructorEntered.Set();
        };
        Task ddl = Task.Run(() => Execute(db, "CREATE TABLE concurrent_schema (id INT, PRIMARY KEY (id))"));
        Task? createWorker = null;
        try
        {
            Assert.True(schemaEntered.Wait(TimeSpan.FromSeconds(2)));
            createWorker = Task.Run(() => Worker(db));
            await Task.WhenAll(ddl, createWorker).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.NotNull(db.Tables.Catalog.TryGet("sql_outbox"));
        }
        finally
        {
            constructorEntered.Set();
            db.BeforeSchemaMutationLockTestHook = null;
            db.SchemaMutationLockAcquiredTestHook = null;
            await ddl.WaitAsync(TimeSpan.FromSeconds(3));
            if (createWorker is not null) await createWorker.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessBatch_StateSyncFailure_ReportsUnknownAndRequiresReopen(bool acknowledgement)
    {
        int deliveries = 0;
        using (var db = Tsdb.Open(new TsdbOptions
        {
            RootDirectory = _root,
            Kv = KvOptions.Default with { SyncWalOnEveryWrite = false },
            BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
        }))
        {
            var worker = Worker(db);
            Enqueue(db, "sync-failure");
            int syncs = 0;
            worker.BeforeStateSyncTestHook = () =>
            {
                if (++syncs == (acknowledgement ? 2 : 1)) throw new IOException("injected fsync failure");
            };
            await Assert.ThrowsAsync<TableTransactionRecoveryException>(() => worker.ProcessBatchAsync((_, _) =>
            {
                deliveries++;
                return ValueTask.CompletedTask;
            }));
            Assert.Equal(acknowledgement ? 1 : 0, deliveries);
            Assert.Throws<TableTransactionRecoveryException>(() => Select(db, "SELECT * FROM sql_outbox"));
        }
        using var reopened = Open();
        Assert.Equal(acknowledgement ? "done" : "leased",
            Assert.Single(Select(reopened, "SELECT state FROM sql_outbox").Rows)[0]);
        // 该注入发生在实际 fsync 前且未模拟物理断电；重开结果不证明掉电后的状态。
    }

    private SqlOutboxWorker Worker(Tsdb db) => new(db, Options(), _clock);
    private static SqlOutboxWorkerOptions Options() => new()
    {
        MaxBatchSize = 4, LeaseDuration = TimeSpan.FromSeconds(10), BatchTimeout = TimeSpan.FromSeconds(5),
        RetryDelay = TimeSpan.FromSeconds(1), MaxRetryDelay = TimeSpan.FromSeconds(4),
    };
    private Tsdb Open() => Tsdb.Open(new TsdbOptions
    {
        RootDirectory = _root,
        BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
    });
    private static void Execute(Tsdb db, string sql) => SqlExecutor.Execute(db, sql);
    private static SelectExecutionResult Select(Tsdb db, string sql)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, sql));
    private static void Enqueue(Tsdb db, string id, string payload = "body")
        => SqlExecutor.ExecuteStatement(db, null,
            SqlParameterBinder.Bind(SqlParser.Parse("INSERT INTO sql_outbox (event_id, topic, payload) VALUES (@id, 'test', @body)"),
                new SqlParameters().AddNamed("id", id).AddNamed("body", payload)), null, null);

    private sealed class TestClock : TimeProvider
    {
        private long _milliseconds = 1_800_000_000_000;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Read(ref _milliseconds));
        public void Advance(TimeSpan duration) => Interlocked.Add(ref _milliseconds, (long)duration.TotalMilliseconds);
    }
}
