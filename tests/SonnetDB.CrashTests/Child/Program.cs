using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Graphs;
using SonnetDB.Graphs.Storage;
using SonnetDB.Kv;
using SonnetDB.Memory;
using SonnetDB.Model;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using SonnetDB.Storage.Segments;

if (args.Length < 3)
{
    Console.Error.WriteLine("Usage: SonnetDB.CrashTests.Child <scenario> <root> <ready-file>");
    return 2;
}

string scenario = args[0];
string root = args[1];
string readyFile = args[2];

Directory.CreateDirectory(root);

switch (scenario)
{
    case "crash_kill9_during_fsync":
        RunKillDuringFsync(root, readyFile);
        return 0;
    case "crash_kill9_mid_compaction":
        RunKillMidCompaction(root, readyFile);
        return 0;
    case "crash_kill9_after_delete":
        RunKillAfterDelete(root, readyFile);
        return 0;
    case "crash_kill9_os_flushed_writes":
        RunKillOsFlushedWrites(root, readyFile);
        return 0;
    case "crash_kill9_between_trigger_table_commits":
        RunKillBetweenTriggerTableCommits(root, readyFile);
        return 0;
    case "crash_kill9_graph_batch_during_fsync":
        RunKillGraphBatchDuringFsync(root, readyFile);
        return 0;
    case "hold_graph_manager_lifecycle_lease":
        RunHoldGraphManagerLifecycleLease(root, readyFile);
        return 0;
    default:
        Console.Error.WriteLine($"Unknown scenario '{scenario}'.");
        return 3;
}

static void RunKillDuringFsync(string root, string readyFile)
{
    using var db = Tsdb.Open(new TsdbOptions
    {
        RootDirectory = root,
        SyncWalOnEveryWrite = true,
        FlushPolicy = new MemTableFlushPolicy
        {
            MaxPoints = long.MaxValue,
            MaxBytes = long.MaxValue,
            HardCapBytes = 0,
            MaxAge = TimeSpan.MaxValue,
        },
        SegmentWriterOptions = new SegmentWriterOptions { FsyncOnCommit = false },
        BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
        Compaction = new CompactionPolicy { Enabled = false },
    });

    File.WriteAllText(readyFile, "ready");

    long timestamp = 1_000;
    while (true)
    {
        db.Write(MakePoint("fsync", timestamp, "kill9", timestamp));
        timestamp++;
    }
}

static void RunKillMidCompaction(string root, string readyFile)
{
    using var db = Tsdb.Open(new TsdbOptions
    {
        RootDirectory = root,
        SyncWalOnEveryWrite = true,
        FlushPolicy = new MemTableFlushPolicy
        {
            MaxPoints = long.MaxValue,
            MaxBytes = long.MaxValue,
            HardCapBytes = 0,
            MaxAge = TimeSpan.MaxValue,
        },
        SegmentWriterOptions = new SegmentWriterOptions { FsyncOnCommit = false },
        BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
        Compaction = new CompactionPolicy
        {
            Enabled = true,
            MinTierSize = 2,
            FirstTierMaxBytes = 1024 * 1024,
            PollInterval = TimeSpan.FromMilliseconds(10),
            ShutdownTimeout = TimeSpan.FromSeconds(5),
        },
    });

    for (int segment = 0; segment < 4; segment++)
    {
        for (int i = 0; i < 32; i++)
            db.Write(MakePoint("compact", segment * 1_000L + i, $"h{segment}", i));

        db.FlushNow();
    }

    File.WriteAllText(readyFile, "ready");
    Thread.Sleep(Timeout.Infinite);
}

// #194：验证 Delete 强制同步 WAL。写入不同步（SyncWalOnEveryWrite=false），落段后删除一段区间，
// 随后被 kill -9。Delete 已在返回前 fsync 了 WAL Delete 记录，故重开后删除必须存活（数据不复活）。
static void RunKillAfterDelete(string root, string readyFile)
{
    using var db = Tsdb.Open(new TsdbOptions
    {
        RootDirectory = root,
        SyncWalOnEveryWrite = false, // 写入路径不同步：仅 Delete 强制同步，凸显 #194
        FlushPolicy = new MemTableFlushPolicy
        {
            MaxPoints = long.MaxValue,
            MaxBytes = long.MaxValue,
            HardCapBytes = 0,
            MaxAge = TimeSpan.MaxValue,
        },
        SegmentWriterOptions = new SegmentWriterOptions { FsyncOnCommit = true },
        BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
        Compaction = new CompactionPolicy { Enabled = false },
        // 单条删除不会触发周期 manifest checkpoint（默认 1024 条 / 30s），逼迫恢复依赖 WAL Delete 记录。
        TombstoneCheckpoint = new TombstoneCheckpointOptions { Enabled = true, MaxDeletesSinceCheckpoint = 1024, MaxInterval = TimeSpan.FromDays(1) },
    });

    // 写 200 个点并 flush 成段（段落盘持久）。
    for (long ts = 0; ts < 200; ts++)
        db.Write(MakePoint("del", ts, "h", ts));
    db.FlushNow();

    ulong seriesId = db.Catalog.Snapshot().First().Id;

    // 删除 [50,100] 区间——Delete 强制 fsync WAL Delete 记录。
    db.Delete(seriesId, "v", 50L, 100L);

    // 就绪后等待被 kill；此时 manifest 尚未 checkpoint，恢复只能靠已同步的 WAL Delete 记录。
    File.WriteAllText(readyFile, "ready");
    Thread.Sleep(Timeout.Infinite);
}

// #196：默认 FlushWalToOsOnWrite=true 时，写入不做 fsync（SyncWalOnEveryWrite=false），也不 flush 成段，
// 但每写把 WAL 交给 OS。随后被 kill -9（进程崩溃，非掉电）。OS page cache 在进程崩溃下存活，
// 故重开 WAL replay 应恢复全部已确认写——证明进程崩溃不丢已确认写。
static void RunKillOsFlushedWrites(string root, string readyFile)
{
    using var db = Tsdb.Open(new TsdbOptions
    {
        RootDirectory = root,
        SyncWalOnEveryWrite = false,      // 不 fsync
        FlushWalToOsOnWrite = true,       // 默认：每写 flush 到 OS（不 fsync）
        FlushPolicy = new MemTableFlushPolicy
        {
            MaxPoints = long.MaxValue,
            MaxBytes = long.MaxValue,
            HardCapBytes = 0,
            MaxAge = TimeSpan.MaxValue,
        },
        SegmentWriterOptions = new SegmentWriterOptions { FsyncOnCommit = false },
        BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
        Compaction = new CompactionPolicy { Enabled = false },
    });

    // 写 300 个点，全部停留在 MemTable + WAL（不 FlushNow，不落段）。
    for (long ts = 0; ts < 300; ts++)
        db.Write(MakePoint("osflush", ts, "h", ts));

    // 就绪后等待被 kill；已确认写只在 WAL（已 flush 到 OS）与进程内 MemTable 中。
    File.WriteAllText(readyFile, "ready");
    Thread.Sleep(Timeout.Infinite);
}

// #333：在独立关系表 keyspace 的提交间隔注入真进程终止，记录 V1 AFTER ROW
// 触发器的跨 WAL 边界。该场景用于证据报告，不改变生产提交合同。
static void RunKillBetweenTriggerTableCommits(string root, string readyFile)
{
    using var db = Tsdb.Open(new TsdbOptions
    {
        RootDirectory = root,
        Kv = KvOptions.Default with
        {
            SyncWalOnEveryWrite = true,
            ExpirerEnabled = false,
            CleanupEnabled = false,
        },
        BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
        Compaction = new CompactionPolicy { Enabled = false },
    });

    SqlExecutor.Execute(db,
        "CREATE TABLE orders (id INT, status STRING, PRIMARY KEY (id))");
    SqlExecutor.Execute(db,
        "CREATE TABLE audit_outbox (event_id INT, order_id INT, PRIMARY KEY (event_id))");
    SqlExecutor.Execute(db, """
        CREATE TRIGGER orders_audit AFTER INSERT ON orders FOR EACH ROW
        LANGUAGE SQL AS BEGIN
            INSERT INTO audit_outbox (event_id, order_id) VALUES (NEW.id, NEW.id);
        END
        """);

    db.Tables.ApplyTransactionAfterTableTestHook = tableName =>
    {
        if (!string.Equals(tableName, "orders", StringComparison.Ordinal))
            return;

        // The parent waits for this marker and then terminates this process.
        // Keep the callback blocked so the next table cannot be applied.
        File.WriteAllText(readyFile, "source-table-applied");
        Thread.Sleep(Timeout.Infinite);
    };
    SqlExecutor.Execute(db, "INSERT INTO orders (id, status) VALUES (1, 'new')");
}

static void RunKillGraphBatchDuringFsync(string root, string readyFile)
{
    using var db = Tsdb.Open(new TsdbOptions
    {
        RootDirectory = root,
        Kv = KvOptions.Default with
        {
            SyncWalOnEveryWrite = true,
            AutoCheckpointEnabled = false,
            ExpirerEnabled = false,
            CleanupEnabled = false,
        },
        BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
        Compaction = new CompactionPolicy { Enabled = false },
    });

    GraphStore store = db.Graphs.Create("crash-graph");
    store.Keyspace.WalSyncTestHook = () =>
    {
        File.WriteAllText(readyFile, "graph-batch-appended-before-fsync");
        Thread.Sleep(Timeout.Infinite);
    };

    GraphPropertyEntry[] sourceProperties = BuildGraphProperties("source");
    GraphPropertyEntry[] targetProperties = BuildGraphProperties("target");
    GraphPropertyEntry[] edgeProperties = BuildGraphProperties("edge");
    GraphTransaction transaction = store.BeginTransaction(Guid.Parse("34600000-0000-0000-0000-000000000001"));
    transaction.UpsertVertex(
        new GraphElementId(1),
        expectedElementVersion: 0,
        [new LabelId(1)],
        sourceProperties);
    transaction.UpsertVertex(
        new GraphElementId(2),
        expectedElementVersion: 0,
        [new LabelId(2)],
        targetProperties);
    transaction.UpsertEdge(
        new GraphElementId(10),
        expectedElementVersion: 0,
        new GraphElementId(1),
        new GraphElementId(2),
        new LabelId(3),
        edgeProperties);
    _ = transaction.Commit();
}

static void RunHoldGraphManagerLifecycleLease(string root, string readyFile)
{
    using var manager = new GraphManager(root, KvOptions.Default);
    string temporaryReadyFile = readyFile + $".{Environment.ProcessId}.tmp";
    File.WriteAllText(temporaryReadyFile, "graph-manager-lease-acquired");
    File.Move(temporaryReadyFile, readyFile);
    Thread.Sleep(Timeout.Infinite);
}

static GraphPropertyEntry[] BuildGraphProperties(string prefix)
{
    var properties = new GraphPropertyEntry[512];
    for (int index = 0; index < properties.Length; index++)
    {
        properties[index] = new GraphPropertyEntry(
            index + 1,
            GraphPropertyValue.FromString(prefix + ":" + index + ":" + new string('x', 256)));
    }
    return properties;
}

static Point MakePoint(string measurement, long timestamp, string host, double value)
    => Point.Create(
        measurement,
        timestamp,
        new Dictionary<string, string> { ["host"] = host },
        new Dictionary<string, FieldValue> { ["v"] = FieldValue.FromDouble(value) });
