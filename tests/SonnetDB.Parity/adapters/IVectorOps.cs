namespace SonnetDB.Parity.Adapters;

/// <summary>
/// 向量检索支柱的语义操作集合。
/// </summary>
public interface IVectorOps
{
    /// <summary>重建场景集合。</summary>
    Task ResetCollectionAsync(string collection, int dimension, CancellationToken ct);

    /// <summary>批量 upsert 向量记录。</summary>
    Task UpsertAsync(string collection, IReadOnlyList<VectorRecord> records, CancellationToken ct);

    /// <summary>查询最近邻。</summary>
    Task<IReadOnlyList<VectorHit>> SearchAsync(
        string collection,
        float[] query,
        int topK,
        string? categoryFilter,
        CancellationToken ct);
}

/// <summary>
/// 不支持向量检索能力的空操作对象。
/// </summary>
public sealed class UnsupportedVectorOps : IVectorOps
{
    /// <summary>共享实例。</summary>
    public static UnsupportedVectorOps Instance { get; } = new();

    private UnsupportedVectorOps() { }

    /// <inheritdoc />
    public Task ResetCollectionAsync(string collection, int dimension, CancellationToken ct) => Unsupported();

    /// <inheritdoc />
    public Task UpsertAsync(string collection, IReadOnlyList<VectorRecord> records, CancellationToken ct) => Unsupported();

    /// <inheritdoc />
    public Task<IReadOnlyList<VectorHit>> SearchAsync(string collection, float[] query, int topK, string? categoryFilter, CancellationToken ct) => Unsupported<IReadOnlyList<VectorHit>>();

    private static Task Unsupported()
        => throw new NotSupportedException("当前后端不支持向量检索操作。");

    private static Task<T> Unsupported<T>()
        => throw new NotSupportedException("当前后端不支持向量检索操作。");
}

/// <summary>
/// 规范化向量记录。
/// </summary>
public sealed record VectorRecord(ulong Id, float[] Vector, string Category);

/// <summary>
/// 规范化向量命中。
/// </summary>
public sealed record VectorHit(ulong Id, double Distance, string? Category);
