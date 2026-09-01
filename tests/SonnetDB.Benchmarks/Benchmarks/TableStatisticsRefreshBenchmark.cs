using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using SonnetDB.Engine;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;

namespace SonnetDB.Benchmarks.Benchmarks;

/// <summary>
/// 关系规划器统计刷新基准：量化多索引表执行 ANALYZE 时的编码、分配与扫描成本。
/// </summary>
[Config(typeof(TableStatisticsRefreshBenchmarkConfig))]
[BenchmarkCategory("Planner", "Statistics", "M41FollowUp")]
// BenchmarkDotNet 会为 benchmark 生成派生类型，因此该类不能密封。
public class TableStatisticsRefreshBenchmark
{
    private const int RowCount = 10_000;
    private const int BatchSize = 500;
    private string _root = string.Empty;
    private Tsdb? _database;
    private TableStore? _store;
    private TableStatisticsRefreshOptions? _options;

    /// <summary>创建固定行数和四个二级索引，初始化成本不计入正式测量。</summary>
    [GlobalSetup]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"sonnetdb-statistics-bench-{Guid.NewGuid():N}");
        _database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(_database, """
            CREATE TABLE planner_events (
                id INT,
                external_id STRING NOT NULL,
                tenant STRING NOT NULL,
                status STRING NOT NULL,
                occurred_at INT NOT NULL,
                correlation_id STRING NOT NULL,
                payload STRING NOT NULL,
                PRIMARY KEY (id)
            )
            """);
        SqlExecutor.Execute(
            _database,
            "CREATE UNIQUE INDEX ux_planner_events_external ON planner_events (external_id)");
        SqlExecutor.Execute(
            _database,
            "CREATE INDEX ix_planner_events_tenant_status ON planner_events (tenant, status)");
        SqlExecutor.Execute(
            _database,
            "CREATE INDEX ix_planner_events_occurred ON planner_events (occurred_at)");
        SqlExecutor.Execute(
            _database,
            "CREATE INDEX ix_planner_events_correlation ON planner_events (correlation_id)");

        _store = _database.Tables.Open("planner_events");
        InsertRows(_store);
        _options = new TableStatisticsRefreshOptions
        {
            MaxSampleRows = RowCount,
            PageSize = 512,
            MaxPageBytes = 4 * 1024 * 1024,
        };

        // 预先验证统计合同，并把首次 JIT 与文件创建成本排除在正式样本之外。
        TableStatistics statistics = _store.RefreshStatistics(_options);
        if (statistics.RowCount != RowCount || statistics.Indexes.Count != 4)
            throw new InvalidOperationException("统计刷新基准初始化结果不符合固定数据合同。");
    }

    /// <summary>扫描固定快照并刷新列统计、直方图和全部索引宽度估算。</summary>
    /// <returns>统计快照中的行数，用于阻止结果被消除。</returns>
    [Benchmark]
    public long RefreshStatistics()
        => _store!.RefreshStatistics(_options!).RowCount;

    /// <summary>关闭数据库并删除本基准独占的临时目录。</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _database?.Dispose();
        _database = null;
        _store = null;
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    /// <summary>按固定批次写入具有可重复分布的关系行。</summary>
    private static void InsertRows(TableStore store)
    {
        for (int start = 1; start <= RowCount; start += BatchSize)
        {
            int count = Math.Min(BatchSize, RowCount - start + 1);
            var rows = new IReadOnlyList<object?>[count];
            for (int offset = 0; offset < count; offset++)
            {
                int id = start + offset;
                rows[offset] = new object?[]
                {
                    (long)id,
                    $"event-{id:D8}",
                    $"tenant-{id % 128:D3}",
                    $"status-{id % 4}",
                    1_700_000_000_000L + id,
                    $"correlation-{id % 2_048:D4}",
                    $"payload-{id:D8}-abcdefghijklmnopqrstuvwxyz",
                };
            }

            store.InsertMany(rows);
        }
    }
}

/// <summary>统计刷新基准的固定短作业与内存诊断配置。</summary>
internal sealed class TableStatisticsRefreshBenchmarkConfig : ManualConfig
{
    /// <summary>使用有限预热和正式迭代，输出中位数、P90 与托管分配。</summary>
    public TableStatisticsRefreshBenchmarkConfig()
    {
        BuildTimeout = TimeSpan.FromMinutes(5);
        AddJob(Job.Default
            .WithWarmupCount(2)
            .WithIterationCount(5));
        AddColumn(StatisticColumn.Median, StatisticColumn.P90);
        AddDiagnoser(MemoryDiagnoser.Default);
    }
}
