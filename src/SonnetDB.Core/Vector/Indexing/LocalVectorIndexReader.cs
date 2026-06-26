using SonnetDB.Vector.Core;
using SonnetDB.Vector.Primitives;

namespace SonnetDB.Vector.Indexing;

internal sealed class LocalVectorIndexReader : IVectorIndexReader
{
    private readonly IIndex<int> _index;
    private bool _disposed;

    public LocalVectorIndexReader(IIndex<int> index, VectorIndexAlgorithm algorithm, KnnMetric metric)
    {
        _index = index;
        Algorithm = algorithm;
        Metric = metric;
    }

    public VectorIndexAlgorithm Algorithm { get; }

    public KnnMetric Metric { get; }

    public int Dimension => _index.Dimensions;

    public int Count => checked((int)_index.Count);

    internal IIndex<int> Index => _index;

    public IReadOnlyList<VectorSearchResult> Search(VectorSearchRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.TopK);
        if (request.Metric != Metric)
        {
            throw new ArgumentException(
                $"搜索度量必须与索引度量一致：index={Metric}, request={request.Metric}。",
                nameof(request));
        }
        if (request.Query.Length != Dimension)
        {
            throw new ArgumentException(
                $"查询向量维度不匹配：期望 {Dimension}，实际 {request.Query.Length}。",
                nameof(request));
        }
        if (Count == 0)
            return [];

        int limit = Math.Min(request.TopK, Count);
        var buffer = new (int Key, float Score)[limit];
        int written = _index.Search(request.Query.Span, limit, buffer);
        var results = new VectorSearchResult[written];
        for (int i = 0; i < written; i++)
        {
            float distance = VectorDistance.ToLowerIsBetterScore(Metric, buffer[i].Score);
            results[i] = new VectorSearchResult(buffer[i].Key, distance);
        }

        Array.Sort(results, static (left, right) => left.Distance.CompareTo(right.Distance));
        return results;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_index is IDisposable disposable)
            disposable.Dispose();
    }
}
