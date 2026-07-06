using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using SonnetDB.Catalog;
using SonnetDB.Query;
using SonnetDB.Storage.Segments;

namespace SonnetDB.Benchmarks.Benchmarks;

/// <summary>
/// PR #62：384 维向量检索基准。
/// 对比 brute-force 精确 Top-K 与 HNSW ANN 的查询延迟，并计算同批查询上的平均 Recall@10。
/// </summary>
[Config(typeof(VectorConfig))]
[MemoryDiagnoser]
[BenchmarkCategory("Vector")]
public class VectorRecallBenchmark
{
    private const int _dimension = 384;
    private const int _k = 10;
    private const int _queryCount = 10;
    private const int _seed = 20260422;

    private float[] _vectorData = [];
    private long[] _timestamps = [];
    private int[] _queryIndexes = [];
    private int[][] _exactTopKIds = [];
    private HnswVectorBlockIndex? _hnswIndex;

    /// <summary>
    /// 数据集规模。
    /// 默认包含 10k / 100k；如需启用 1M，请设置环境变量 <c>SONNETDB_VECTOR_BENCH_INCLUDE_1M=1</c>。
    /// </summary>
    [ParamsSource(nameof(GetVectorCounts))]
    public int VectorCount { get; set; }

    /// <summary>
    /// 提供基准使用的数据集规模列表。
    /// </summary>
    public IEnumerable<int> GetVectorCounts()
    {
        yield return 10_000;
        yield return 100_000;

        if (ShouldIncludeOneMillion())
            yield return 1_000_000;
    }

    /// <summary>
    /// 全局初始化：生成归一化向量、构建 HNSW 图，并预计算查询批次的精确 Top10。
    /// </summary>
    [GlobalSetup]
    public void GlobalSetup()
    {
        _vectorData = GenerateNormalizedVectorData(VectorCount, _dimension, _seed);
        _timestamps = new long[VectorCount];
        for (int i = 0; i < _timestamps.Length; i++)
            _timestamps[i] = i;

        _queryIndexes = SelectQueryIndexes(VectorCount, _queryCount, _seed + 17);
        _exactTopKIds = new int[_queryCount][];
        for (int i = 0; i < _queryCount; i++)
        {
            _exactTopKIds[i] = new int[_k];
            SearchExactTopK(
                GetQueryVector(i),
                _vectorData,
                VectorCount,
                _dimension,
                _exactTopKIds[i],
                new double[_k]);
        }

        _hnswIndex = HnswVectorBlockIndex.Build(
            blockIndex: 0,
            valPayload: MemoryMarshal.AsBytes(_vectorData.AsSpan()),
            count: VectorCount,
            dimension: _dimension,
            options: new HnswVectorIndexOptions(M: 16, Ef: 200, EfConstruction: 200));
    }

    /// <summary>
    /// brute-force 精确查询：同批 10 个 query 逐一扫描全量向量，取 Top10。
    /// </summary>
    /// <returns>用于防止 BenchmarkDotNet 消除调用的距离校验和。</returns>
    [Benchmark(Baseline = true, OperationsPerInvoke = _queryCount, Description = "Brute-force Top10 (384-d cosine)")]
    public double BruteForce_Top10()
    {
        double checksum = 0;
        var ids = new int[_k];
        var distances = new double[_k];
        for (int i = 0; i < _queryCount; i++)
        {
            SearchExactTopK(GetQueryVector(i), _vectorData, VectorCount, _dimension, ids, distances);
            for (int j = 0; j < _k; j++)
                checksum += distances[j];
        }

        return checksum;
    }

    /// <summary>
    /// HNSW ANN 查询：同批 10 个 query 逐一搜索 Top10。
    /// </summary>
    /// <returns>用于防止 BenchmarkDotNet 消除调用的距离校验和。</returns>
    [Benchmark(OperationsPerInvoke = _queryCount, Description = "HNSW Top10 (384-d cosine)")]
    public double Hnsw_Top10()
    {
        double checksum = 0;
        ReadOnlySpan<byte> payload = MemoryMarshal.AsBytes(_vectorData.AsSpan());
        for (int i = 0; i < _queryCount; i++)
        {
            var hits = _hnswIndex!.Search(GetQueryVector(i), payload, _timestamps, _k, KnnMetric.Cosine);
            for (int j = 0; j < hits.Count; j++)
                checksum += hits[j].Distance;
        }

        return checksum;
    }

    /// <summary>
    /// 计算同批 10 个 query 上的平均 Recall@10。
    /// 该方法会重新执行 HNSW 查询，并与预先缓存的精确 Top10 结果做集合交集。
    /// </summary>
    /// <returns>平均 Recall@10，取值区间 [0, 1]。</returns>
    [Benchmark(OperationsPerInvoke = _queryCount, Description = "HNSW Recall@10 (batch average)")]
    public double Hnsw_RecallAt10()
    {
        double recallSum = 0;
        ReadOnlySpan<byte> payload = MemoryMarshal.AsBytes(_vectorData.AsSpan());
        for (int i = 0; i < _queryCount; i++)
        {
            var hits = _hnswIndex!.Search(GetQueryVector(i), payload, _timestamps, _k, KnnMetric.Cosine);
            recallSum += ComputeRecallAt10(_exactTopKIds[i], hits);
        }

        return recallSum / _queryCount;
    }

    private ReadOnlySpan<float> GetQueryVector(int queryOrdinal)
    {
        int vectorIndex = _queryIndexes[queryOrdinal];
        return _vectorData.AsSpan(vectorIndex * _dimension, _dimension);
    }

    private static bool ShouldIncludeOneMillion()
    {
        string? value = Environment.GetEnvironmentVariable("SONNETDB_VECTOR_BENCH_INCLUDE_1M");
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static float[] GenerateNormalizedVectorData(int vectorCount, int dimension, int seed)
    {
        var random = new Random(seed);
        var data = new float[checked(vectorCount * dimension)];
        var temp = new float[dimension];

        for (int vectorIndex = 0; vectorIndex < vectorCount; vectorIndex++)
        {
            double norm2 = 0;
            for (int d = 0; d < dimension; d++)
            {
                float value = (float)(random.NextDouble() * 2.0 - 1.0);
                temp[d] = value;
                norm2 += value * value;
            }

            float invNorm = norm2 <= double.Epsilon ? 1f : (float)(1.0 / Math.Sqrt(norm2));
            int offset = vectorIndex * dimension;
            for (int d = 0; d < dimension; d++)
                data[offset + d] = temp[d] * invNorm;
        }

        return data;
    }

    private static int[] SelectQueryIndexes(int vectorCount, int queryCount, int seed)
    {
        var random = new Random(seed);
        var indexes = new int[queryCount];
        for (int i = 0; i < indexes.Length; i++)
            indexes[i] = random.Next(vectorCount);
        return indexes;
    }

    private static void SearchExactTopK(
        ReadOnlySpan<float> queryVector,
        ReadOnlySpan<float> vectorData,
        int vectorCount,
        int dimension,
        int[] topIds,
        double[] topDistances)
    {
        Array.Fill(topIds, -1);
        Array.Fill(topDistances, double.PositiveInfinity);

        int worstIndex = 0;
        double worstDistance = double.PositiveInfinity;

        for (int vectorIndex = 0; vectorIndex < vectorCount; vectorIndex++)
        {
            double distance = VectorDistance.ComputeCosine(
                queryVector,
                vectorData.Slice(vectorIndex * dimension, dimension));

            if (distance >= worstDistance)
                continue;

            topIds[worstIndex] = vectorIndex;
            topDistances[worstIndex] = distance;
            FindWorst(topDistances, out worstIndex, out worstDistance);
        }
    }

    private static void FindWorst(double[] distances, out int worstIndex, out double worstDistance)
    {
        worstIndex = 0;
        worstDistance = distances[0];
        for (int i = 1; i < distances.Length; i++)
        {
            if (distances[i] > worstDistance)
            {
                worstDistance = distances[i];
                worstIndex = i;
            }
        }
    }

    private static double ComputeRecallAt10(int[] expectedTopKIds, IReadOnlyList<HnswAnnSearchResult> actualHits)
    {
        int matched = 0;
        for (int i = 0; i < actualHits.Count; i++)
        {
            int actualId = actualHits[i].PointIndex;
            for (int j = 0; j < expectedTopKIds.Length; j++)
            {
                if (expectedTopKIds[j] == actualId)
                {
                    matched++;
                    break;
                }
            }
        }

        return (double)matched / expectedTopKIds.Length;
    }

    private sealed class VectorConfig : ManualConfig
    {
        public VectorConfig()
        {
            AddJob(Job.Default
                .WithWarmupCount(1)
                .WithIterationCount(3));
        }
    }
}
