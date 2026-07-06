using SonnetDB.Catalog;
using SonnetDB.Model;
using SonnetDB.Query;
using CoreVectorIndexAlgorithm = SonnetDB.Vector.Indexing.VectorIndexAlgorithm;
using CoreVectorIndexBuildInput = SonnetDB.Vector.Indexing.VectorIndexBuildInput;
using CoreVectorIndexHnswOptions = SonnetDB.Vector.Indexing.VectorIndexHnswOptions;
using CoreVectorIndexIvfOptions = SonnetDB.Vector.Indexing.VectorIndexIvfOptions;
using CoreVectorIndexIvfPqOptions = SonnetDB.Vector.Indexing.VectorIndexIvfPqOptions;
using CoreVectorIndexReader = SonnetDB.Vector.Indexing.IVectorIndexReader;
using CoreVectorIndexVamanaOptions = SonnetDB.Vector.Indexing.VectorIndexVamanaOptions;
using CoreVectorKnnMetric = SonnetDB.Vector.Primitives.KnnMetric;
using CoreVectorLocalIndexBlob = SonnetDB.Vector.Indexing.LocalVectorIndexBlob;
using CoreVectorLocalIndexBuilder = SonnetDB.Vector.Indexing.LocalVectorIndexBuilder;
using CoreVectorSearchRequest = SonnetDB.Vector.Indexing.VectorSearchRequest;

namespace SonnetDB.Storage.Segments;

internal interface IVectorIndexBuilder
{
    VectorIndexBuildResult Build(VectorIndexBuildInput input);
}

internal interface IVectorIndexReader
{
    int BlockIndex { get; }

    int Count { get; }

    int Dimension { get; }

    int Ef { get; }

    /// <summary>该索引建图时使用的距离度量；检索时只有查询度量与之一致才走 ANN。</summary>
    KnnMetric Metric { get; }

    long EstimatedBytes { get; }

    IReadOnlyList<VectorSearchResult> Search(
        ReadOnlySpan<float> queryVector,
        ReadOnlySpan<byte> valuePayload,
        ReadOnlySpan<long> timestamps,
        int resultLimit,
        KnnMetric metric);
}

internal sealed record VectorIndexBuildInput(
    int BlockIndex,
    ReadOnlyMemory<DataPoint> Points,
    VectorIndexDefinition Definition);

internal sealed record VectorIndexBuildResult(IVectorIndexReader Reader);

internal readonly record struct VectorSearchResult(int PointIndex, long Timestamp, double Distance);

internal sealed class LocalVectorIndexBuilderAdapter : IVectorIndexBuilder
{
    public VectorIndexBuildResult Build(VectorIndexBuildInput input)
    {
        ArgumentNullException.ThrowIfNull(input.Definition);

        if (input.Points.IsEmpty)
            throw new ArgumentException("向量索引至少需要 1 个向量点。", nameof(input));

        int dimension = input.Points.Span[0].Value.VectorDimension;
        var vectors = CopyVectors(input.Points.Span, dimension);
        var reader = BuildLocalVectorReader(input.BlockIndex, input.Definition, vectors, input.Points.Length, dimension);

        return new VectorIndexBuildResult(new LocalVectorIndexReaderAdapter(
            input.BlockIndex,
            input.Definition.SearchEf(),
            reader));
    }

    internal static VectorIndexBuildResult BuildFromPayload(
        int blockIndex,
        ReadOnlySpan<byte> valuePayload,
        int count,
        int dimension,
        VectorIndexDefinition definition)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(blockIndex);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimension);

        ArgumentNullException.ThrowIfNull(definition);

        var vectors = CopyVectors(valuePayload, count, dimension);
        var reader = BuildLocalVectorReader(blockIndex, definition, vectors, count, dimension);

        return new VectorIndexBuildResult(new LocalVectorIndexReaderAdapter(blockIndex, definition.SearchEf(), reader));
    }

    internal static VectorIndexBuildResult BuildFromBlob(
        int blockIndex,
        Stream stream,
        int length,
        uint expectedCrc32,
        int expectedCount,
        int expectedDimension,
        int expectedEf)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(blockIndex);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedDimension);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedEf);

        var reader = CoreVectorLocalIndexBlob.Read(stream, length, expectedCrc32);
        if (reader.Count != expectedCount || reader.Dimension != expectedDimension)
        {
            reader.Dispose();
            throw new InvalidDataException(
                $"SonnetDB vector index blob metadata mismatch: expected count={expectedCount}, dim={expectedDimension}, actual count={reader.Count}, dim={reader.Dimension}.");
        }

        return new VectorIndexBuildResult(new LocalVectorIndexReaderAdapter(blockIndex, expectedEf, reader));
    }

    internal static uint WriteBlob(Stream stream, IVectorIndexReader reader)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(reader);
        if (reader is not LocalVectorIndexReaderAdapter localReader)
            throw new NotSupportedException("Only SonnetDB local vector index readers can be serialized.");

        return CoreVectorLocalIndexBlob.Write(stream, localReader.InnerReader);
    }

    private static CoreVectorIndexReader BuildLocalVectorReader(
        int blockIndex,
        VectorIndexDefinition definition,
        ReadOnlyMemory<float> vectors,
        int count,
        int dimension)
        => CoreVectorLocalIndexBuilder.Instance.Build(ToLocalVectorInput(blockIndex, definition, vectors, count, dimension));

    private static CoreVectorIndexBuildInput ToLocalVectorInput(
        int blockIndex,
        VectorIndexDefinition definition,
        ReadOnlyMemory<float> vectors,
        int count,
        int dimension)
    {
        int seed = ComputeSeed(blockIndex, count, dimension, definition);
        var metric = ToCoreMetric(definition.Metric);
        return definition.Kind switch
        {
            VectorIndexKind.Hnsw => new CoreVectorIndexBuildInput(
                CoreVectorIndexAlgorithm.Hnsw,
                metric,
                vectors,
                count,
                dimension,
                Hnsw: ToHnswOptions(definition, seed)),

            VectorIndexKind.IvfFlat => new CoreVectorIndexBuildInput(
                CoreVectorIndexAlgorithm.IvfFlat,
                metric,
                vectors,
                count,
                dimension,
                Ivf: ToIvfOptions(definition, seed)),

            VectorIndexKind.IvfPq => new CoreVectorIndexBuildInput(
                CoreVectorIndexAlgorithm.IvfPq,
                metric,
                vectors,
                count,
                dimension,
                IvfPq: ToIvfPqOptions(definition, seed)),

            VectorIndexKind.Vamana => new CoreVectorIndexBuildInput(
                CoreVectorIndexAlgorithm.Vamana,
                metric,
                vectors,
                count,
                dimension,
                Vamana: ToVamanaOptions(definition, seed)),

            _ => throw new NotSupportedException($"不支持的向量索引类型：{definition.Kind}。"),
        };
    }

    internal static CoreVectorKnnMetric ToCoreMetric(KnnMetric metric)
        => metric switch
        {
            KnnMetric.L2 => CoreVectorKnnMetric.L2,
            KnnMetric.InnerProduct => CoreVectorKnnMetric.InnerProduct,
            _ => CoreVectorKnnMetric.Cosine,
        };

    private static CoreVectorIndexHnswOptions ToHnswOptions(VectorIndexDefinition definition, int seed)
    {
        var hnsw = definition.Hnsw ?? throw new InvalidOperationException("HNSW 向量索引参数缺失。");
        return new CoreVectorIndexHnswOptions(hnsw.M, hnsw.EfConstruction, hnsw.Ef, seed);
    }

    private static CoreVectorIndexIvfOptions ToIvfOptions(VectorIndexDefinition definition, int seed)
    {
        var ivf = definition.Ivf ?? throw new InvalidOperationException("IVF 向量索引参数缺失。");
        return new CoreVectorIndexIvfOptions(ivf.NList, ivf.NProbe, ivf.MaxIterations, seed);
    }

    private static CoreVectorIndexIvfPqOptions ToIvfPqOptions(VectorIndexDefinition definition, int seed)
    {
        var ivfPq = definition.IvfPq ?? throw new InvalidOperationException("IVF-PQ 向量索引参数缺失。");
        return new CoreVectorIndexIvfPqOptions(ivfPq.NList, ivfPq.NProbe, ivfPq.MaxIterations, ivfPq.M, ivfPq.NBits, seed);
    }

    private static CoreVectorIndexVamanaOptions ToVamanaOptions(VectorIndexDefinition definition, int seed)
    {
        var vamana = definition.Vamana ?? throw new InvalidOperationException("Vamana 向量索引参数缺失。");
        return new CoreVectorIndexVamanaOptions(vamana.MaxDegree, vamana.SearchListSize, vamana.Alpha, vamana.BeamWidth, seed);
    }

    private static float[] CopyVectors(ReadOnlySpan<DataPoint> points, int dimension)
    {
        var vectors = new float[checked(points.Length * dimension)];
        for (int row = 0; row < points.Length; row++)
        {
            int currentDimension = points[row].Value.VectorDimension;
            if (currentDimension != dimension)
            {
                throw new InvalidOperationException(
                    $"HNSW 构建要求 block 内向量维度一致：首点 dim={dimension}，节点 {row} dim={currentDimension}。");
            }

            points[row].Value.AsVector().Span.CopyTo(vectors.AsSpan(row * dimension, dimension));
        }

        return vectors;
    }

    private static float[] CopyVectors(ReadOnlySpan<byte> valuePayload, int count, int dimension)
    {
        int expectedBytes = checked(count * dimension * sizeof(float));
        if (valuePayload.Length != expectedBytes)
        {
            throw new ArgumentException(
                $"valuePayload 长度必须等于 count × dimension × 4（期望 {expectedBytes}，实际 {valuePayload.Length}）。",
                nameof(valuePayload));
        }

        var vectors = new float[checked(count * dimension)];
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(valuePayload).CopyTo(vectors);
        return vectors;
    }

    private static int ComputeSeed(int blockIndex, int count, int dimension, VectorIndexDefinition definition)
    {
        var hash = new HashCode();
        hash.Add(blockIndex);
        hash.Add(count);
        hash.Add(dimension);
        hash.Add(definition.Kind);
        hash.Add(definition.Metric);
        switch (definition.Kind)
        {
            case VectorIndexKind.Hnsw:
                var hnsw = definition.Hnsw ?? throw new InvalidOperationException("HNSW 向量索引参数缺失。");
                hash.Add(hnsw.M);
                hash.Add(hnsw.Ef);
                hash.Add(hnsw.EfConstruction);
                break;

            case VectorIndexKind.IvfFlat:
                var ivf = definition.Ivf ?? throw new InvalidOperationException("IVF 向量索引参数缺失。");
                hash.Add(ivf.NList);
                hash.Add(ivf.NProbe);
                hash.Add(ivf.MaxIterations);
                break;

            case VectorIndexKind.IvfPq:
                var ivfPq = definition.IvfPq ?? throw new InvalidOperationException("IVF-PQ 向量索引参数缺失。");
                hash.Add(ivfPq.NList);
                hash.Add(ivfPq.NProbe);
                hash.Add(ivfPq.MaxIterations);
                hash.Add(ivfPq.M);
                hash.Add(ivfPq.NBits);
                break;

            case VectorIndexKind.Vamana:
                var vamana = definition.Vamana ?? throw new InvalidOperationException("Vamana 向量索引参数缺失。");
                hash.Add(vamana.MaxDegree);
                hash.Add(vamana.SearchListSize);
                hash.Add(vamana.Alpha);
                hash.Add(vamana.BeamWidth);
                break;
        }
        int seed = hash.ToHashCode();
        return seed == 0 ? 1 : seed;
    }
}

internal static class VectorIndexDefinitionExtensions
{
    public static int SearchEf(this VectorIndexDefinition definition)
        => definition.Kind switch
        {
            VectorIndexKind.Hnsw => definition.Hnsw?.Ef ?? throw new InvalidOperationException("HNSW 向量索引参数缺失。"),
            VectorIndexKind.IvfFlat => definition.Ivf?.NProbe ?? throw new InvalidOperationException("IVF 向量索引参数缺失。"),
            VectorIndexKind.IvfPq => definition.IvfPq?.NProbe ?? throw new InvalidOperationException("IVF-PQ 向量索引参数缺失。"),
            VectorIndexKind.Vamana => definition.Vamana?.SearchListSize ?? throw new InvalidOperationException("Vamana 向量索引参数缺失。"),
            _ => throw new NotSupportedException($"不支持的向量索引类型：{definition.Kind}。"),
        };
}

internal sealed class LocalVectorIndexReaderAdapter : IVectorIndexReader
{
    private readonly CoreVectorIndexReader _reader;

    public LocalVectorIndexReaderAdapter(int blockIndex, int ef, CoreVectorIndexReader reader)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(blockIndex);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ef);
        ArgumentNullException.ThrowIfNull(reader);

        BlockIndex = blockIndex;
        Ef = ef;
        _reader = reader;
    }

    public int BlockIndex { get; }

    public int Count => _reader.Count;

    public int Dimension => _reader.Dimension;

    public int Ef { get; }

    /// <summary>建图度量直接取自内部 reader（新建走 definition.Metric，加载走 blob 头里的 metric）。</summary>
    public KnnMetric Metric => ToQueryMetric(_reader.Metric);

    internal CoreVectorIndexReader InnerReader => _reader;

    public long EstimatedBytes => Math.Max(1, checked((long)Count * Dimension * sizeof(float) * 2));

    private static KnnMetric ToQueryMetric(CoreVectorKnnMetric metric)
        => metric switch
        {
            CoreVectorKnnMetric.L2 => KnnMetric.L2,
            CoreVectorKnnMetric.InnerProduct => KnnMetric.InnerProduct,
            _ => KnnMetric.Cosine,
        };

    public IReadOnlyList<VectorSearchResult> Search(
        ReadOnlySpan<float> queryVector,
        ReadOnlySpan<byte> valuePayload,
        ReadOnlySpan<long> timestamps,
        int resultLimit,
        KnnMetric metric)
    {
        // I7：只有查询度量与索引建图度量一致才走 ANN；不一致由调用方回退暴力扫描。
        if (resultLimit <= 0 || Count == 0 || metric != Metric || queryVector.Length != Dimension)
            return [];

        var hits = _reader.Search(new CoreVectorSearchRequest(
            queryVector.ToArray(),
            Math.Min(resultLimit, Count),
            LocalVectorIndexBuilderAdapter.ToCoreMetric(metric)));

        if (hits.Count == 0)
            return [];

        var results = new VectorSearchResult[hits.Count];
        for (int i = 0; i < hits.Count; i++)
        {
            var hit = hits[i];
            long timestamp = hit.PointIndex >= 0 && hit.PointIndex < timestamps.Length
                ? timestamps[hit.PointIndex]
                : 0L;
            results[i] = new VectorSearchResult(hit.PointIndex, timestamp, hit.Distance);
        }

        return results;
    }
}
