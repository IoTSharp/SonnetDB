using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using SonnetDB.Vector.Compute;

namespace SonnetDB.Benchmarks.Benchmarks;

/// <summary>
/// M42 P1 向量批量余弦基准：比较每行重算 query norm 与预计算一次的路径。
/// </summary>
[Config(typeof(VectorCosineBatchBenchmarkConfig))]
[MemoryDiagnoser]
[BenchmarkCategory("Vector", "M42", "Cosine")]
public class VectorCosineBatchBenchmark
{
    private float[] _query = [];
    private float[] _dataset = [];
    private float _queryNormSquared;

    /// <summary>向量维度，覆盖常见小向量和 embedding 维度。</summary>
    [Params(64, 384)]
    public int Dimension { get; set; }

    /// <summary>每次批量扫描的候选行数。</summary>
    [Params(1_000, 10_000)]
    public int CandidateCount { get; set; }

    /// <summary>创建固定种子的查询和候选数据。</summary>
    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(0x4D343250);
        _query = CreateValues(random, Dimension);
        _dataset = new float[checked(Dimension * CandidateCount)];
        FillValues(_dataset, random);
        _queryNormSquared = Distance.NormSquared(_query);
    }

    /// <summary>基线：每个候选都通过常规 API 重算 query norm。</summary>
    [Benchmark(Baseline = true, Description = "Cosine recomputes query norm")]
    public double RecomputeQueryNorm()
    {
        double checksum = 0d;
        for (int row = 0; row < CandidateCount; row++)
        {
            checksum += Distance.Cosine(
                _query,
                _dataset.AsSpan(row * Dimension, Dimension));
        }

        return checksum;
    }

    /// <summary>优化路径：批量扫描只计算一次 query norm。</summary>
    [Benchmark(Description = "Cosine reuses query norm")]
    public double ReuseQueryNorm()
    {
        double checksum = 0d;
        for (int row = 0; row < CandidateCount; row++)
        {
            checksum += Distance.CosineWithQueryNormSquared(
                _query,
                _queryNormSquared,
                _dataset.AsSpan(row * Dimension, Dimension));
        }

        return checksum;
    }

    private static float[] CreateValues(Random random, int count)
    {
        var values = new float[count];
        FillValues(values, random);
        return values;
    }

    private static void FillValues(Span<float> values, Random random)
    {
        for (int i = 0; i < values.Length; i++)
            values[i] = (float)(random.NextDouble() * 2d - 1d);
    }
}

/// <summary>向量 query norm 基准的短作业配置。</summary>
internal sealed class VectorCosineBatchBenchmarkConfig : ManualConfig
{
    /// <summary>固定预热和测量迭代，保留托管分配诊断。</summary>
    public VectorCosineBatchBenchmarkConfig()
    {
        BuildTimeout = TimeSpan.FromMinutes(5);
        AddJob(Job.Default.WithWarmupCount(2).WithIterationCount(5));
        AddColumn(StatisticColumn.Median, StatisticColumn.P90);
    }
}
