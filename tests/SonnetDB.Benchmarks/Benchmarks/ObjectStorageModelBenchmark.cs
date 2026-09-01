using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Jobs;
using SonnetDB.Engine;
using SonnetDB.Kv;
using SonnetDB.ObjectStorage;

namespace SonnetDB.Benchmarks.Benchmarks;

/// <summary>
/// 对象存储模型基准：在固定的 256 个 64 KiB 对象上度量完整读取延迟与 Range 读取吞吐。
/// </summary>
[Config(typeof(ObjectStorageModelBenchmarkConfig))]
[MemoryDiagnoser]
[BenchmarkCategory("Model", "ObjectStorage")]
public class ObjectStorageModelBenchmark
{
    private const int ObjectCount = 256;
    private const int ObjectSize = 64 * 1024;
    private const int FullReadOperations = 16;
    private const int RangeReadOperations = 32;
    private const int RangeReadSize = 4 * 1024;
    private const string BucketName = "benchmark-objects";
    private string _rootDirectory = string.Empty;
    private Tsdb? _database;
    private SndbObjectStore? _store;
    private string[] _objectKeys = [];
    private byte[] _fullReadBuffer = [];
    private byte[] _rangeReadBuffer = [];

    /// <summary>创建固定对象集合；正文与元数据准备不计入读取测量。</summary>
    [GlobalSetup]
    public async Task Setup()
    {
        _rootDirectory = Path.Combine(
            Path.GetTempPath(),
            "SonnetDB.Benchmarks",
            $"object-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_rootDirectory);

        try
        {
            _database = Tsdb.Open(CreateOptions(_rootDirectory));
            _store = new SndbObjectStore(_database);
            _store.CreateBucket(BucketName, "performance-benchmark");
            _objectKeys = new string[ObjectCount];
            _fullReadBuffer = new byte[ObjectSize];
            _rangeReadBuffer = new byte[RangeReadSize];

            byte[] payload = new byte[ObjectSize];
            new Random(42).NextBytes(payload);
            for (int index = 0; index < ObjectCount; index++)
            {
                string key = $"payloads/object-{index:D4}.bin";
                _objectKeys[index] = key;
                using var content = new MemoryStream(payload, writable: false);
                await _store.PutObjectAsync(
                    BucketName,
                    key,
                    content,
                    contentType: "application/octet-stream").ConfigureAwait(false);
            }

            _database.Keyspaces.Open("__object_storage").Compact();
            SndbObjectInfo? validation = _store.HeadObject(BucketName, _objectKeys[ObjectCount / 2]);
            if (validation?.SizeBytes != ObjectSize)
                throw new InvalidDataException("Object Storage benchmark fixture 校验失败。");
        }
        catch
        {
            Cleanup();
            throw;
        }
    }

    /// <summary>度量 16 次完整 64 KiB 对象读取的归一化延迟与分配。</summary>
    /// <returns>读取内容与长度的轻量校验和。</returns>
    [Benchmark(
        Baseline = true,
        OperationsPerInvoke = FullReadOperations,
        Description = "Object full-read latency (16 x 64 KiB)")]
    public async Task<long> FullObjectReadLatency()
    {
        long checksum = 0;
        for (int index = 0; index < FullReadOperations; index++)
            checksum += await ReadFullObjectOnceAsync(index).ConfigureAwait(false);

        return checksum;
    }

    /// <summary>执行一次 64 KiB 完整对象读取，并支持 runner 取消。</summary>
    internal async ValueTask<long> ReadFullObjectOnceAsync(
        int operationOrdinal,
        CancellationToken cancellationToken = default)
    {
        string key = _objectKeys[Math.Abs((operationOrdinal * 17) % ObjectCount)];
        SndbObjectReadResult result = RequireStore().OpenRead(BucketName, key)
            ?? throw new InvalidDataException("Object Storage benchmark 完整读取未命中固定对象。");
        await using Stream content = result.Content;
        await content.ReadExactlyAsync(_fullReadBuffer, cancellationToken).ConfigureAwait(false);
        return result.Length + _fullReadBuffer[0] + _fullReadBuffer[^1];
    }

    /// <summary>度量 32 次 4 KiB Range 读取的归一化吞吐与分配。</summary>
    /// <returns>读取内容与偏移的轻量校验和。</returns>
    [Benchmark(
        OperationsPerInvoke = RangeReadOperations,
        Description = "Object range-read throughput (32 x 4 KiB)")]
    public async Task<long> RangeReadThroughput()
    {
        long checksum = 0;
        for (int index = 0; index < RangeReadOperations; index++)
            checksum += await ReadRangeOnceAsync(index).ConfigureAwait(false);

        return checksum;
    }

    /// <summary>执行一次 4 KiB Range 请求，并支持 runner 取消。</summary>
    internal async ValueTask<long> ReadRangeOnceAsync(
        int operationOrdinal,
        CancellationToken cancellationToken = default)
    {
        string key = _objectKeys[Math.Abs((operationOrdinal * 29) % ObjectCount)];
        long offset = Math.Abs((operationOrdinal * 997L) % (ObjectSize - RangeReadSize));
        SndbObjectReadResult result = RequireStore().OpenRead(
            BucketName,
            key,
            new SndbObjectRange(offset, RangeReadSize))
            ?? throw new InvalidDataException("Object Storage benchmark Range 读取未命中固定对象。");
        await using Stream content = result.Content;
        await content.ReadExactlyAsync(_rangeReadBuffer, cancellationToken).ConfigureAwait(false);
        return result.Offset + result.Length + _rangeReadBuffer[0] + _rangeReadBuffer[^1];
    }

    /// <summary>关闭数据库并删除当前基准独占的对象正文与元数据目录。</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _store = null;
        _database?.Dispose();
        _database = null;
        DeleteFixtureDirectory();
    }

    /// <summary>创建关闭后台 KV 维护且不逐写 fsync 的固定读基准选项。</summary>
    private static TsdbOptions CreateOptions(string rootDirectory)
        => new()
        {
            RootDirectory = rootDirectory,
            Kv = KvOptions.Default with
            {
                AutoCheckpointEnabled = false,
                SyncWalOnEveryWrite = false,
                ExpirerEnabled = false,
                CleanupEnabled = false,
            },
        };

    /// <summary>返回已完成 setup 的对象存储实例。</summary>
    private SndbObjectStore RequireStore()
        => _store ?? throw new InvalidOperationException("请先调用 Setup 创建 Object Storage benchmark fixture。");

    /// <summary>仅删除本类创建且位于专用临时父目录下的 fixture。</summary>
    private void DeleteFixtureDirectory()
    {
        if (string.IsNullOrWhiteSpace(_rootDirectory) || !Directory.Exists(_rootDirectory))
            return;

        string allowedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "SonnetDB.Benchmarks"));
        string fixtureRoot = Path.GetFullPath(_rootDirectory);
        string relative = Path.GetRelativePath(allowedRoot, fixtureRoot);
        if (relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            throw new InvalidOperationException("拒绝删除基准专用临时目录之外的路径。");
        }

        Directory.Delete(fixtureRoot, recursive: true);
        _rootDirectory = string.Empty;
    }
}

/// <summary>为对象存储模型报告 BenchmarkDotNet 迭代统计、吞吐与分配，并限制审计写入数量。</summary>
internal sealed class ObjectStorageModelBenchmarkConfig : ManualConfig
{
    /// <summary>使用固定调用数控制审计增长；请求级分位数由独立 evidence runner 采集。</summary>
    public ObjectStorageModelBenchmarkConfig()
    {
        BuildTimeout = TimeSpan.FromMinutes(5);
        AddJob(Job.Default
            .WithStrategy(RunStrategy.Monitoring)
            .WithWarmupCount(2)
            .WithIterationCount(8)
            .WithInvocationCount(32)
            .WithUnrollFactor(1));
        AddColumn(
            StatisticColumn.Median,
            StatisticColumn.P95,
            StatisticColumn.OperationsPerSecond);
    }
}
