using System.Data;
using System.Text.Json;
using SonnetDB.Documents;
using SonnetDB.Documents.Vector;
using SonnetDB.Engine;
using SonnetDB.Generations;
using SonnetDB.Query;
using SonnetDB.SemanticContent;

namespace SonnetDB.Data.VectorData;

/// <summary>VectorData store 的嵌入式生命周期诊断；复用文档 catalog 和持久 RAG profile，不创建第二套向量目录。</summary>
public static class SonnetDBVectorLifecycleExtensions
{
    private const int MaxDimensions = 65_536;
    private static readonly TimeSpan PreflightTimeout = TimeSpan.FromSeconds(30);

    /// <summary>按真实 catalog 核验向量维度、有限数值与距离度量；普通集合明确返回 profile_unbound。</summary>
    /// <param name="store">现有 VectorData store。</param>
    /// <param name="collection">文档集合名。</param>
    /// <param name="index">向量索引名。</param>
    /// <param name="vector">待查询的向量，最多 65536 维。</param>
    /// <param name="metric">查询所用的距离度量。</param>
    /// <param name="cancellationToken">取消令牌；预检另有三十秒协作超时。</param>
    /// <returns>索引兼容结果；未持久绑定模型的集合不会得到 profile 验证成功声明。</returns>
    public static async Task<SndbVectorPreflightResult> PreflightAsync(
        this SonnetDBVectorStore store,
        string collection,
        string index,
        ReadOnlyMemory<float> vector,
        KnnMetric metric = KnnMetric.Cosine,
        CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(PreflightTimeout);
        Tsdb database = await GetEmbeddedAsync(store, deadline.Token).ConfigureAwait(false);
        DocumentVectorIndex definition = GetDefinition(database, collection, index);
        return Validate(definition, collection, vector, metric, null, null, null, deadline.Token);
    }

    /// <summary>按已发布 RAG generation 的完整持久 profile 和实际向量索引核验查询；模型、版本及外发策略均须完全一致。</summary>
    /// <param name="store">现有 VectorData store。</param>
    /// <param name="stream">已有持久 RAG stream。</param>
    /// <param name="queryProfile">实际生成查询向量的完整模型合同。</param>
    /// <param name="vector">待查询的向量，最多 65536 维。</param>
    /// <param name="cancellationToken">取消令牌；预检另有三十秒协作超时。</param>
    /// <returns>同一个持久 generation 的 catalog/profile 预检结果。</returns>
    public static async Task<SndbVectorPreflightResult> PreflightRagAsync(
        this SonnetDBVectorStore store,
        string stream,
        EmbeddingProfile queryProfile,
        ReadOnlyMemory<float> vector,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queryProfile);
        ArgumentException.ThrowIfNullOrWhiteSpace(stream);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(PreflightTimeout);
        CancellationToken token = deadline.Token;
        Tsdb database = await GetEmbeddedAsync(store, token).ConfigureAwait(false);
        RagIngestionStatus status = await new RagIngestionManager(database, stream).GetStatusAsync(token).ConfigureAwait(false);
        RagGenerationStatus active = status.Active
            ?? throw new InvalidOperationException("RAG stream 尚无已发布 generation。");
        using DatabaseGenerationQueryLease lease = database.Generations.AcquireActive(stream);
        EnsureMatchingGeneration(active, lease);
        string collection = lease.GetRequiredResource(
            RagIngestionWriter.ChunksResourceRole, DatabaseGenerationResourceKind.DocumentCollection).Name;
        DocumentVectorIndex definition = GetDefinition(database, collection, RagIngestionWriter.VectorIndexName);
        return Validate(definition, collection, vector, ToDocumentMetric(queryProfile.Metric),
            active.Profile, queryProfile, active.GenerationId, token);
    }

    /// <summary>读取 catalog 与已加载向量图的轻量状态，不加载集合、扫描主数据或重建索引。</summary>
    /// <param name="store">现有 VectorData store；连接必须已经打开。</param>
    /// <param name="collection">文档集合名。</param>
    /// <param name="index">向量索引名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>loaded 或 not_loaded 状态；不代表主数据一致性、索引新鲜度或召回质量。</returns>
    public static Task<DocumentVectorIndexHealth> GetIndexHealthAsync(
        this SonnetDBVectorStore store,
        string collection,
        string index,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SndbConnection connection = GetConnection(store);
        if (connection.State != ConnectionState.Open)
            throw new InvalidOperationException("健康读取要求连接已经打开，以免隐式执行数据库恢复。");
        Tsdb database = RequireEmbedded(connection);
        DocumentVectorIndexHealth health = database.Documents.GetVectorIndexHealth(collection, index);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(health);
    }

    private static SndbVectorPreflightResult Validate(
        DocumentVectorIndex index, string collection, ReadOnlyMemory<float> vector, KnnMetric metric,
        EmbeddingProfile? storedProfile, EmbeddingProfile? queryProfile, string? generationId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (vector.Length > MaxDimensions)
            throw new ArgumentOutOfRangeException(nameof(vector), "预检向量最多为 65536 维。");
        var issues = new List<SndbVectorPreflightIssue>();
        if (vector.Length != index.Dimensions)
            issues.Add(new("vector_dimension_mismatch", $"查询维度 {vector.Length} 与索引维度 {index.Dimensions} 不一致。"));
        if (!Enum.IsDefined(metric) || metric != index.Metric)
            issues.Add(new("vector_metric_mismatch", "查询距离度量与索引声明不一致。"));
        double squaredNorm = 0;
        bool finite = true;
        for (int i = 0; i < vector.Length; i++)
        {
            if ((i & 255) == 0) token.ThrowIfCancellationRequested();
            if (!float.IsFinite(vector.Span[i]))
            {
                issues.Add(new("vector_non_finite", "查询向量包含 NaN 或无限数值。"));
                finite = false;
                break;
            }
            squaredNorm += (double)vector.Span[i] * vector.Span[i];
        }
        if (finite && metric == KnnMetric.Cosine && squaredNorm == 0)
            issues.Add(new("vector_zero_norm", "余弦查询向量必须具有非零范数。"));
        if (finite && queryProfile?.Normalization == EmbeddingNormalization.L2 && Math.Abs(squaredNorm - 1) > 0.001)
            issues.Add(new("vector_normalization_mismatch", "查询向量不满足 profile 的 L2 单位范数合同。"));
        bool indexCompatible = issues.Count == 0;
        bool? profileCompatible = null;
        if (storedProfile is not null && queryProfile is not null)
        {
            bool valid = IsValidProfile(storedProfile) && IsValidProfile(queryProfile);
            profileCompatible = valid
                && storedProfile.Dimensions == index.Dimensions
                && ToDocumentMetric(storedProfile.Metric) == index.Metric
                && JsonSerializer.Serialize(storedProfile, SndbVectorLifecycleJsonContext.Default.EmbeddingProfile)
                    == JsonSerializer.Serialize(queryProfile, SndbVectorLifecycleJsonContext.Default.EmbeddingProfile);
            if (profileCompatible != true)
                issues.Add(new("vector_profile_mismatch", "查询完整模型合同与持久 profile 或索引声明不一致。"));
        }
        token.ThrowIfCancellationRequested();
        return new(collection, index.Name, index.Dimensions, index.Metric, indexCompatible, profileCompatible,
            profileCompatible is null ? "profile_unbound" : profileCompatible.Value ? "verified" : "mismatch",
            generationId, storedProfile?.Id, issues.AsReadOnly());
    }

    private static bool IsValidProfile(EmbeddingProfile profile)
        => profile.SupportedModalities is not null
            && profile.SupportedModalities.Count <= 7
            && profile.DataEgressPolicy is not null
            && SemanticContentValidator.ValidateProfile(profile).Count == 0;

    private static KnnMetric ToDocumentMetric(SonnetDB.Vector.Primitives.KnnMetric metric)
        => metric switch
        {
            SonnetDB.Vector.Primitives.KnnMetric.Cosine => KnnMetric.Cosine,
            SonnetDB.Vector.Primitives.KnnMetric.L2 => KnnMetric.L2,
            SonnetDB.Vector.Primitives.KnnMetric.InnerProduct => KnnMetric.InnerProduct,
            _ => (KnnMetric)(-1),
        };

    private static DocumentVectorIndex GetDefinition(Tsdb database, string collection, string index)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        ArgumentException.ThrowIfNullOrWhiteSpace(index);
        return database.Documents.GetVectorIndexHealth(collection, index).Definition;
    }

    internal static void EnsureMatchingGeneration(RagGenerationStatus active, DatabaseGenerationQueryLease lease)
    {
        if (lease.Generation.GenerationId != active.GenerationId || lease.Generation.Revision != active.Revision)
            throw new InvalidOperationException("RAG active generation 在预检期间发生变化，请重新预检。");
    }

    private static async Task<Tsdb> GetEmbeddedAsync(SonnetDBVectorStore store, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        SndbConnection connection = GetConnection(store);
        if (connection.ProviderMode != SndbProviderMode.Embedded)
            throw new NotSupportedException("向量生命周期诊断尚未接通远程传输；请在嵌入式连接调用。");
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return RequireEmbedded(connection);
    }

    private static SndbConnection GetConnection(SonnetDBVectorStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return (SndbConnection)store.GetService(typeof(SndbConnection))!;
    }

    private static Tsdb RequireEmbedded(SndbConnection connection)
        => connection.UnderlyingTsdb
            ?? throw new NotSupportedException("向量生命周期诊断尚未接通远程传输；请在嵌入式连接调用。");
}
