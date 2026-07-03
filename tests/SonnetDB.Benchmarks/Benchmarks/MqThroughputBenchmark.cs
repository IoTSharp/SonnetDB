using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Jobs;
using SonnetMQ;

namespace SonnetDB.Benchmarks.Benchmarks;

/// <summary>
/// SonnetMQ 吞吐基准（P5a #230 基线）：度量 publish / pull+ack 在单/多 topic、
/// 批量 vs 单条、不同 payload 尺寸下的吞吐，为 #231~#234 的优化提供对照基线。
/// </summary>
[Config(typeof(MqThroughputConfig))]
[MemoryDiagnoser]
[BenchmarkCategory("MqThroughput")]
public class MqThroughputBenchmark
{
    private const string ConsumerGroup = "bench";
    private string _rootDir = string.Empty;
    private SonnetMqStore? _store;
    private byte[] _payload = [];
    private string[] _topics = [];
    private SonnetMqPublishEntry[] _batch = [];

    /// <summary>每次迭代发布的消息总数。</summary>
    [Params(200_000)]
    public int MessageCount { get; set; }

    /// <summary>单条消息 payload 字节数。</summary>
    [Params(64, 1024, 16 * 1024)]
    public int PayloadBytes { get; set; }

    /// <summary>topic 数量，用于暴露全局锁下的多 topic 争用。</summary>
    [Params(1, 8)]
    public int TopicCount { get; set; }

    /// <summary>每次迭代前重建空队列与预生成负载。</summary>
    [IterationSetup]
    public void IterationSetup()
    {
        _payload = new byte[PayloadBytes];
        for (int i = 0; i < _payload.Length; i++)
            _payload[i] = (byte)(i * 31);

        _topics = new string[TopicCount];
        for (int i = 0; i < TopicCount; i++)
            _topics[i] = $"bench.topic.{i}";

        int batchSize = Math.Max(1, MessageCount / TopicCount);
        _batch = new SonnetMqPublishEntry[batchSize];
        for (int i = 0; i < batchSize; i++)
            _batch[i] = new SonnetMqPublishEntry(_payload);

        _rootDir = Path.Combine(Path.GetTempPath(), $"sonnetmq_bench_{Guid.NewGuid():N}");
        _store = SonnetMqStore.Open(new SonnetMqOptions
        {
            Path = _rootDir,
            FlushOnPublish = true,
            SyncOnPublish = false,
            RetentionInterval = TimeSpan.Zero,
        });
    }

    /// <summary>每次迭代后关闭并删除队列目录。</summary>
    [IterationCleanup]
    public void IterationCleanup()
    {
        _store?.Dispose();
        _store = null;
        if (Directory.Exists(_rootDir))
        {
            try { Directory.Delete(_rootDir, recursive: true); }
            catch (IOException) { /* best effort */ }
        }
    }

    /// <summary>逐条 <see cref="SonnetMqStore.Publish"/>，跨 topic 轮转。</summary>
    [Benchmark(Baseline = true, Description = "publish single")]
    public void PublishSingle()
    {
        var store = _store!;
        for (int i = 0; i < MessageCount; i++)
            store.Publish(_topics[i % TopicCount], _payload);
    }

    /// <summary>每 topic 一次 <see cref="SonnetMqStore.PublishMany"/> 批量发布。</summary>
    [Benchmark(Description = "publish batched")]
    public void PublishBatched()
    {
        var store = _store!;
        foreach (string topic in _topics)
            store.PublishMany(topic, _batch);
    }

    /// <summary>批量发布后按 topic 做 pull+ack 回环，度量消费侧吞吐。</summary>
    [Benchmark(Description = "pull + ack roundtrip")]
    public long PullAckRoundtrip()
    {
        var store = _store!;
        foreach (string topic in _topics)
            store.PublishMany(topic, _batch);

        long consumed = 0;
        foreach (string topic in _topics)
        {
            while (true)
            {
                var messages = store.Pull(topic, ConsumerGroup, 512);
                if (messages.Count == 0)
                    break;

                consumed += messages.Count;
                store.Ack(topic, ConsumerGroup, messages[^1].Offset);
            }
        }

        return consumed;
    }

    private sealed class MqThroughputConfig : ManualConfig
    {
        public MqThroughputConfig()
        {
            AddJob(Job.Default
                .WithStrategy(RunStrategy.Monitoring)
                .WithWarmupCount(1)
                .WithIterationCount(5));
        }
    }
}
