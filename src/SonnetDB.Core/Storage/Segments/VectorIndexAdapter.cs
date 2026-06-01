using SonnetDB.Catalog;
using SonnetDB.Model;
using SonnetDB.Query;
using DotVectorIndexAlgorithm = DotVector.Indexing.VectorIndexAlgorithm;
using DotVectorIndexBuildInput = DotVector.Indexing.VectorIndexBuildInput;
using DotVectorIndexHnswOptions = DotVector.Indexing.VectorIndexHnswOptions;
using DotVectorIndexReader = DotVector.Indexing.IVectorIndexReader;
using DotVectorLocalIndexBuilder = DotVector.Indexing.LocalVectorIndexBuilder;
using DotVectorKnnMetric = DotVector.Primitives.KnnMetric;
using DotVectorSearchRequest = DotVector.Indexing.VectorSearchRequest;

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

internal sealed class DotVectorHnswVectorIndexBuilder : IVectorIndexBuilder
{
    public VectorIndexBuildResult Build(VectorIndexBuildInput input)
    {
        ArgumentNullException.ThrowIfNull(input.Definition);

        if (input.Definition.Kind != VectorIndexKind.Hnsw)
            throw new NotSupportedException($"不支持的向量索引类型：{input.Definition.Kind}。");

        if (input.Points.IsEmpty)
            throw new ArgumentException("HNSW 图至少需要 1 个向量点。", nameof(input));

        int dimension = input.Points.Span[0].Value.VectorDimension;
        var vectors = CopyVectors(input.Points.Span, dimension);
        var reader = DotVectorLocalIndexBuilder.Instance.Build(new DotVectorIndexBuildInput(
            DotVectorIndexAlgorithm.Hnsw,
            DotVectorKnnMetric.Cosine,
            vectors,
            input.Points.Length,
            dimension,
            new DotVectorIndexHnswOptions(
                M: input.Definition.Hnsw.M,
                EfConstruction: input.Definition.Hnsw.Ef,
                EfSearch: input.Definition.Hnsw.Ef,
                Seed: ComputeSeed(input.BlockIndex, input.Points.Length, dimension, input.Definition.Hnsw.M, input.Definition.Hnsw.Ef))));

        return new VectorIndexBuildResult(new DotVectorHnswVectorIndexReader(
            input.BlockIndex,
            input.Definition.Hnsw.Ef,
            reader));
    }

    internal static VectorIndexBuildResult BuildFromPayload(
        int blockIndex,
        ReadOnlySpan<byte> valuePayload,
        int count,
        int dimension,
        HnswVectorIndexOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(blockIndex);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimension);

        var vectors = CopyVectors(valuePayload, count, dimension);
        var reader = DotVectorLocalIndexBuilder.Instance.Build(new DotVectorIndexBuildInput(
            DotVectorIndexAlgorithm.Hnsw,
            DotVectorKnnMetric.Cosine,
            vectors,
            count,
            dimension,
            new DotVectorIndexHnswOptions(
                M: options.M,
                EfConstruction: options.Ef,
                EfSearch: options.Ef,
                Seed: ComputeSeed(blockIndex, count, dimension, options.M, options.Ef))));

        return new VectorIndexBuildResult(new DotVectorHnswVectorIndexReader(blockIndex, options.Ef, reader));
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

    private static int ComputeSeed(int blockIndex, int count, int dimension, int m, int ef)
    {
        var hash = new HashCode();
        hash.Add(blockIndex);
        hash.Add(count);
        hash.Add(dimension);
        hash.Add(m);
        hash.Add(ef);
        int seed = hash.ToHashCode();
        return seed == 0 ? 1 : seed;
    }
}

internal sealed class DotVectorHnswVectorIndexReader : IVectorIndexReader
{
    private readonly DotVectorIndexReader _reader;

    public DotVectorHnswVectorIndexReader(int blockIndex, int ef, DotVectorIndexReader reader)
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

    public long EstimatedBytes => Math.Max(1, checked((long)Count * Dimension * sizeof(float) * 2));

    public IReadOnlyList<VectorSearchResult> Search(
        ReadOnlySpan<float> queryVector,
        ReadOnlySpan<byte> valuePayload,
        ReadOnlySpan<long> timestamps,
        int resultLimit,
        KnnMetric metric)
    {
        if (resultLimit <= 0 || Count == 0 || metric != KnnMetric.Cosine || queryVector.Length != Dimension)
            return [];

        var hits = _reader.Search(new DotVectorSearchRequest(
            queryVector.ToArray(),
            Math.Min(resultLimit, Count),
            DotVectorKnnMetric.Cosine));

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
