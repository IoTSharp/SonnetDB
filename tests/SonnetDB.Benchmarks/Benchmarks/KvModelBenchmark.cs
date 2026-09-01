using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using SonnetDB.Engine;
using SonnetDB.Kv;

namespace SonnetDB.Benchmarks.Benchmarks;

/// <summary>
/// KV / 缓存模型基准：在固定的 10k 落盘键集上度量单键读取延迟与批量读取吞吐。
/// </summary>
[Config(typeof(KvModelBenchmarkConfig))]
[MemoryDiagnoser]
[BenchmarkCategory("Model", "KV")]
public class KvModelBenchmark
{
    private const int EntryCount = 10_000;
    private const int SeedBatchSize = 500;
    private const int BatchReadSize = 256;
    private const int ValueSize = 256;
    private const string KeyspaceName = "cache";
    private string _rootDirectory = string.Empty;
    private Tsdb? _database;
    private KvKeyspace? _keyspace;
    private string _pointReadKey = string.Empty;
    private string[] _batchReadKeys = [];

    /// <summary>创建固定键集并压缩为磁盘 state，避免把数据准备计入测量。</summary>
    [GlobalSetup]
    public void Setup()
    {
        _rootDirectory = Path.Combine(
            Path.GetTempPath(),
            "SonnetDB.Benchmarks",
            $"kv-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_rootDirectory);

        try
        {
            _database = Tsdb.Open(CreateOptions(_rootDirectory));
            _keyspace = _database.Keyspaces.Open(KeyspaceName);

            byte[] payload = new byte[ValueSize];
            new Random(42).NextBytes(payload);
            for (int start = 0; start < EntryCount; start += SeedBatchSize)
            {
                int count = Math.Min(SeedBatchSize, EntryCount - start);
                var entries = new KeyValuePair<string, byte[]>[count];
                for (int offset = 0; offset < count; offset++)
                {
                    int ordinal = start + offset;
                    entries[offset] = new KeyValuePair<string, byte[]>(CreateKey(ordinal), payload);
                }

                IReadOnlyDictionary<string, long> versions = _keyspace.PutMany(entries);
                if (versions.Count != count)
                    throw new InvalidDataException("KV benchmark fixture 未完整写入固定批次。");
            }

            _keyspace.Compact();
            _pointReadKey = CreateKey(EntryCount / 2);
            _batchReadKeys = new string[BatchReadSize];
            for (int index = 0; index < _batchReadKeys.Length; index++)
                _batchReadKeys[index] = CreateKey((index * 37) % EntryCount);

            if (_keyspace.Get(_pointReadKey)?.Length != ValueSize
                || _keyspace.GetMany(_batchReadKeys).Count != BatchReadSize)
            {
                throw new InvalidDataException("KV benchmark fixture 校验失败。");
            }
        }
        catch
        {
            Cleanup();
            throw;
        }
    }

    /// <summary>度量磁盘 state 热缓存下的单键读取延迟与托管分配。</summary>
    /// <returns>读取值的轻量校验和。</returns>
    [Benchmark(Baseline = true, Description = "KV point read latency (10k keys, 256 B value)")]
    public int PointReadLatency() => ReadPointOnce();

    /// <summary>执行一次单键读取，供 BenchmarkDotNet 与请求级尾延迟 runner 共用。</summary>
    internal int ReadPointOnce()
    {
        byte[] value = RequireKeyspace().Get(_pointReadKey)
            ?? throw new InvalidDataException("KV benchmark 点读未命中固定键。");
        return value.Length + value[0];
    }

    /// <summary>度量一次原生 GetMany 读取 256 个分散键时的归一化吞吐。</summary>
    /// <returns>成功返回的键数量。</returns>
    [Benchmark(OperationsPerInvoke = BatchReadSize, Description = "KV GetMany throughput (256 dispersed keys)")]
    public int GetManyThroughput() => ReadBatchOnce();

    /// <summary>执行一次包含 256 个键的 GetMany 请求，保留批请求边界。</summary>
    internal int ReadBatchOnce()
    {
        IReadOnlyDictionary<string, byte[]?> values = RequireKeyspace().GetMany(_batchReadKeys);
        return values.Count;
    }

    /// <summary>关闭数据库并删除当前基准独占的临时目录。</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _keyspace = null;
        _database?.Dispose();
        _database = null;
        DeleteFixtureDirectory();
    }

    /// <summary>创建关闭后台维护且不逐写 fsync 的固定读基准选项。</summary>
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

    /// <summary>按固定宽度生成可排序的缓存键。</summary>
    private static string CreateKey(int ordinal) => $"session:{ordinal:D8}";

    /// <summary>返回已完成 setup 的 keyspace。</summary>
    private KvKeyspace RequireKeyspace()
        => _keyspace ?? throw new InvalidOperationException("请先调用 Setup 创建 KV benchmark fixture。");

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

/// <summary>为 KV 模型报告 BenchmarkDotNet 迭代统计、吞吐与分配。</summary>
internal sealed class KvModelBenchmarkConfig : ManualConfig
{
    /// <summary>配置固定预热与测量轮次；请求级分位数由独立 evidence runner 采集。</summary>
    public KvModelBenchmarkConfig()
    {
        BuildTimeout = TimeSpan.FromMinutes(5);
        AddJob(Job.Default.WithWarmupCount(2).WithIterationCount(8));
        AddColumn(
            StatisticColumn.Median,
            StatisticColumn.P95,
            StatisticColumn.OperationsPerSecond);
    }
}
