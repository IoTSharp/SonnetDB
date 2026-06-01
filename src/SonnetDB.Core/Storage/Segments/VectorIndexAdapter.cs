using SonnetDB.Catalog;
using SonnetDB.Model;
using SonnetDB.Query;

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

internal sealed class LegacyHnswVectorIndexBuilder : IVectorIndexBuilder
{
    public VectorIndexBuildResult Build(VectorIndexBuildInput input)
    {
        ArgumentNullException.ThrowIfNull(input.Definition);

        if (input.Definition.Kind != VectorIndexKind.Hnsw)
            throw new NotSupportedException($"不支持的向量索引类型：{input.Definition.Kind}。");

        var index = HnswVectorBlockIndex.Build(
            input.BlockIndex,
            input.Points.Span,
            input.Definition.Hnsw);

        return new VectorIndexBuildResult(new LegacyHnswVectorIndexReader(index));
    }
}

internal sealed class LegacyHnswVectorIndexReader : IVectorIndexReader
{
    private readonly HnswVectorBlockIndex _index;

    public LegacyHnswVectorIndexReader(HnswVectorBlockIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        _index = index;
    }

    public int BlockIndex => _index.BlockIndex;

    public int Count => _index.Count;

    public int Dimension => _index.Dimension;

    public int Ef => _index.Ef;

    public long EstimatedBytes => _index.EstimatedBytes;

    internal HnswVectorBlockIndex InnerIndex => _index;

    public IReadOnlyList<VectorSearchResult> Search(
        ReadOnlySpan<float> queryVector,
        ReadOnlySpan<byte> valuePayload,
        ReadOnlySpan<long> timestamps,
        int resultLimit,
        KnnMetric metric)
    {
        var hits = _index.Search(
            queryVector,
            valuePayload,
            timestamps,
            resultLimit,
            metric);

        if (hits.Count == 0)
            return [];

        var results = new VectorSearchResult[hits.Count];
        for (int i = 0; i < hits.Count; i++)
        {
            var hit = hits[i];
            results[i] = new VectorSearchResult(hit.PointIndex, hit.Timestamp, hit.Distance);
        }

        return results;
    }
}
