using System.Net.Sockets;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using static Qdrant.Client.Grpc.Conditions;

namespace SonnetDB.Parity.Adapters.Qdrant;

/// <summary>
/// Qdrant 竞品适配器，使用官方 <c>Qdrant.Client</c> 实现向量检索场景。
/// </summary>
public sealed class QdrantAdapter : IDataPlane, IVectorOps
{
    private readonly QdrantClient _client;

    /// <summary>使用 <c>PARITY_QDRANT_*</c> 环境变量创建 Qdrant 连接。</summary>
    public QdrantAdapter()
    {
        _client = new QdrantClient(
            host: Env("PARITY_QDRANT_HOST", "127.0.0.1"),
            port: int.Parse(Env("PARITY_QDRANT_PORT", "26334")));
    }

    /// <inheritdoc />
    public string BackendName => "qdrant";

    /// <inheritdoc />
    public Capability Capabilities => Capability.Vector | Capability.HnswFiltered;

    /// <inheritdoc />
    public IRelationalOps Relational => throw new NotSupportedException("Qdrant 适配器不支持关系型操作。");

    /// <inheritdoc />
    public ITimeSeriesOps TimeSeries => UnsupportedTimeSeriesOps.Instance;

    /// <inheritdoc />
    public IKvOps Kv => UnsupportedKvOps.Instance;

    /// <inheritdoc />
    public IObjectOps Objects => UnsupportedObjectOps.Instance;

    /// <inheritdoc />
    public IVectorOps Vector => this;

    /// <inheritdoc />
    public IMqOps Mq => UnsupportedMqOps.Instance;

    /// <inheritdoc />
    public IFullTextOps FullText => UnsupportedFullTextOps.Instance;

    /// <inheritdoc />
    public IAnalyticalOps Analytics => UnsupportedAnalyticalOps.Instance;

    /// <summary>探测 Qdrant 是否可达。</summary>
    public static async Task<bool> TryConnectAsync(CancellationToken ct)
    {
        try
        {
            var client = new QdrantClient(
                host: Env("PARITY_QDRANT_HOST", "127.0.0.1"),
                port: int.Parse(Env("PARITY_QDRANT_PORT", "26334")));
            await client.ListCollectionsAsync(cancellationToken: ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException or OperationCanceledException or Grpc.Core.RpcException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task ResetCollectionAsync(string collection, int dimension, CancellationToken ct)
    {
        if (await _client.CollectionExistsAsync(collection, cancellationToken: ct).ConfigureAwait(false))
            await _client.DeleteCollectionAsync(collection, cancellationToken: ct).ConfigureAwait(false);

        await _client.CreateCollectionAsync(
            collectionName: collection,
            vectorsConfig: new VectorParams { Size = (ulong)dimension, Distance = Distance.Cosine },
            hnswConfig: new HnswConfigDiff { M = 8, EfConstruct = 64 },
            cancellationToken: ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UpsertAsync(string collection, IReadOnlyList<VectorRecord> records, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(records);
        const int BatchSize = 512;
        for (var offset = 0; offset < records.Count; offset += BatchSize)
        {
            var batch = records.Skip(offset).Take(BatchSize).Select(static r => new PointStruct
            {
                Id = r.Id,
                Vectors = r.Vector,
                Payload = { ["category"] = r.Category },
            }).ToArray();
            await _client.UpsertAsync(collection, batch, cancellationToken: ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<VectorHit>> SearchAsync(
        string collection,
        float[] query,
        int topK,
        string? categoryFilter,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        Filter? filter = null;
        if (!string.IsNullOrWhiteSpace(categoryFilter))
            filter = MatchKeyword("category", categoryFilter);

        var hits = await _client.SearchAsync(
            collectionName: collection,
            vector: query,
            filter: filter,
            limit: (ulong)topK,
            payloadSelector: true,
            cancellationToken: ct).ConfigureAwait(false);

        return hits.Select(static hit => new VectorHit(
            hit.Id.Num,
            1d - hit.Score,
            hit.Payload.TryGetValue("category", out var value) ? value.StringValue : null)).ToArray();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static string Env(string key, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
