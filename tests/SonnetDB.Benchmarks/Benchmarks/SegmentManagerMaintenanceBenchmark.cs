using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using SonnetDB.Catalog;
using SonnetDB.Engine;
using SonnetDB.Memory;
using SonnetDB.Model;
using SonnetDB.Query;
using SonnetDB.Storage.Segments;

namespace SonnetDB.Benchmarks.Benchmarks;

/// <summary>
/// M19 #124 段维护发布基准：用 add / swap / drop 分别模拟 flush、compaction 与 retention，
/// 并在可选的真实查询压力下测量发布延迟和分配。
/// </summary>
[Config(typeof(SegmentManagerMaintenanceBenchmarkConfig))]
[BenchmarkCategory("M19", "SegmentMaintenance")]
public sealed class SegmentManagerMaintenanceBenchmark
{
    private const ulong SeriesId = 0x124UL;
    private const string FieldName = "value";

    private string _root = string.Empty;
    private string _databaseRoot = string.Empty;
    private string _addedSegmentPath = string.Empty;
    private SegmentManager? _manager;
    private QueryEngine? _queryEngine;
    private CancellationTokenSource? _queryCancellation;
    private Task[] _queryTasks = [];
    private long _completedQueries;

    /// <summary>发布前已经加载的段数量。</summary>
    [Params(16, 256, 1024)]
    public int LoadedSegments { get; set; }

    /// <summary>维护发布期间持续执行完整点查询的并发 worker 数量。</summary>
    [Params(0, 4)]
    public int QueryWorkers { get; set; }

    /// <summary>创建固定的小段集合和待发布段；文件构造不计入维护延迟。</summary>
    [GlobalSetup]
    public void GlobalSetup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"sonnetdb-segment-maintenance-{Guid.NewGuid():N}");
        _databaseRoot = Path.Combine(_root, "database");
        string stagingRoot = Path.Combine(_root, "staging");
        Directory.CreateDirectory(Path.Combine(_databaseRoot, TsdbPaths.SegmentsDirName));
        Directory.CreateDirectory(Path.Combine(stagingRoot, TsdbPaths.SegmentsDirName));

        var writer = new SegmentWriter(new SegmentWriterOptions { FsyncOnCommit = false });
        for (int segmentId = 1; segmentId <= LoadedSegments; segmentId++)
        {
            WriteSegment(
                writer,
                TsdbPaths.SegmentPath(_databaseRoot, segmentId),
                segmentId,
                timestamp: segmentId);
        }

        long addedId = LoadedSegments + 1L;
        _addedSegmentPath = TsdbPaths.SegmentPath(stagingRoot, addedId);
        WriteSegment(writer, _addedSegmentPath, addedId, timestamp: addedId);
    }

    /// <summary>为每次测量重新打开相同的基础段集合，并按参数启动查询压力。</summary>
    [IterationSetup]
    public void IterationSetup()
    {
        _manager = SegmentManager.Open(_databaseRoot);
        _queryEngine = new QueryEngine(new MemTable(), _manager, new SeriesCatalog());
        _completedQueries = 0;

        if (QueryWorkers == 0)
            return;

        _queryCancellation = new CancellationTokenSource();
        using var ready = new CountdownEvent(QueryWorkers);
        _queryTasks = new Task[QueryWorkers];
        for (int worker = 0; worker < QueryWorkers; worker++)
        {
            CancellationToken token = _queryCancellation.Token;
            _queryTasks[worker] = Task.Factory.StartNew(
                () => RunQueries(token, ready),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        ready.Wait();
        if (!SpinWait.SpinUntil(
                () => Volatile.Read(ref _completedQueries) >= QueryWorkers,
                TimeSpan.FromSeconds(30)))
        {
            throw new TimeoutException("并发查询 worker 未在 30 秒内完成预热查询。");
        }
    }

    /// <summary>
    /// 复现 #207 前的全量索引构建成本，作为当前增量发布的参考基线。
    /// </summary>
    /// <returns>参考快照包含的段数量。</returns>
    [Benchmark(Baseline = true, Description = "旧全量索引重建参考")]
    public int FullIndexRebuildReference()
    {
        var indices = new List<SegmentIndex>(LoadedSegments);
        foreach (var reader in _manager!.Readers)
        {
            if (reader.Header.SegmentId != 1L)
                indices.Add(SegmentIndex.Build(reader, reader.Header.SegmentId));
        }

        using var addedReader = SegmentReader.Open(_addedSegmentPath);
        indices.Add(SegmentIndex.Build(addedReader, addedReader.Header.SegmentId));
        return new MultiSegmentIndex(indices).SegmentCount;
    }

    /// <summary>测量 flush 完成后接入一个新段的增量发布成本。</summary>
    /// <returns>发布后的段数量。</returns>
    [Benchmark(Description = "AddSegment / flush 发布")]
    public int AddSegment()
    {
        _manager!.AddSegment(_addedSegmentPath);
        return _manager.SegmentCount;
    }

    /// <summary>测量 compaction 以一段替换一段的增量发布成本。</summary>
    /// <returns>发布后的段数量。</returns>
    [Benchmark(Description = "SwapSegments / compaction 发布")]
    public int SwapSegments()
    {
        _manager!.SwapSegments([1L], _addedSegmentPath);
        return _manager.SegmentCount;
    }

    /// <summary>测量 retention 淘汰一个整段的增量发布成本。</summary>
    /// <returns>发布后的段数量。</returns>
    [Benchmark(Description = "DropSegments / retention 发布")]
    public int DropSegments()
    {
        _manager!.DropSegments([1L]);
        return _manager.SegmentCount;
    }

    /// <summary>停止查询 worker，关闭本轮 manager，使下一轮从同一基础集合开始。</summary>
    [IterationCleanup]
    public void IterationCleanup()
    {
        _queryCancellation?.Cancel();
        if (_queryTasks.Length > 0)
            Task.WaitAll(_queryTasks);
        _queryTasks = [];
        _queryCancellation?.Dispose();
        _queryCancellation = null;
        _queryEngine = null;
        _manager?.Dispose();
        _manager = null;
    }

    /// <summary>删除基准生成的临时段文件。</summary>
    [GlobalCleanup]
    public void GlobalCleanup()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static void WriteSegment(
        SegmentWriter writer,
        string path,
        long segmentId,
        long timestamp)
    {
        var memTable = new MemTable();
        memTable.Append(SeriesId, timestamp, FieldName, FieldValue.FromDouble(timestamp), segmentId);
        writer.WriteFrom(memTable, segmentId, path);
    }

    private void RunQueries(CancellationToken cancellationToken, CountdownEvent ready)
    {
        var query = new PointQuery(SeriesId, FieldName, TimeRange.All);
        ready.Signal();
        while (!cancellationToken.IsCancellationRequested)
        {
            int points = 0;
            foreach (var _ in _queryEngine!.Execute(query))
                points++;
            if (points == 0)
                throw new InvalidDataException("维护并发基准未查询到预置数据。");
            Interlocked.Increment(ref _completedQueries);
        }
    }
}

internal sealed class SegmentManagerMaintenanceBenchmarkConfig : ManualConfig
{
    public SegmentManagerMaintenanceBenchmarkConfig()
    {
        AddJob(Job.Default
            .WithWarmupCount(1)
            .WithIterationCount(5)
            .WithInvocationCount(1)
            .WithUnrollFactor(1));
        AddColumn(StatisticColumn.Median, StatisticColumn.P90);
        AddDiagnoser(MemoryDiagnoser.Default);
    }
}
