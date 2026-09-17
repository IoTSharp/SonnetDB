using System.Text.Json;
using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.FullText;
using SonnetDB.Generations;
using SonnetDB.Query;

namespace SonnetDB.SemanticContent;

/// <summary>在同一持久 RAG generation 租约内执行有界精确向量、全文融合与可选重排。</summary>
/// <remarks>适用于预算内的嵌入式语料；不调用模型、不使用无预算的 ANN 或全量 SQL 物化路径。</remarks>
public sealed class RagGenerationSearch
{
    private readonly Tsdb _database;
    private readonly string _stream;
    private readonly EmbeddingProfile _profile;

    /// <summary>绑定 RAG stream 和查询向量的不可变 profile。</summary>
    /// <param name="database">调用方已获访问权限的数据库。</param>
    /// <param name="stream">由 RagIngestionWriter 发布的 stream。</param>
    /// <param name="profile">查询向量的 profile；标识、维度和度量必须与存储一致。</param>
    public RagGenerationSearch(Tsdb database, string stream, EmbeddingProfile profile)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(stream);
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.SupportedModalities is null || profile.SupportedModalities.Count > 16 || profile.DataEgressPolicy is null)
            throw new ArgumentException("查询 profile 的模态或外发策略无效。", nameof(profile));
        var modalities = new SemanticContentModality[profile.SupportedModalities.Count];
        for (int i = 0; i < modalities.Length; i++)
            modalities[i] = profile.SupportedModalities[i];
        profile = profile with { SupportedModalities = Array.AsReadOnly(modalities) };
        if (SemanticContentValidator.ValidateProfile(profile).Count != 0 || profile.Dimensions > 65_536)
            throw new ArgumentException("查询必须使用有效且不超过 65536 维的 profile。", nameof(profile));
        _database = database;
        _stream = stream;
        _profile = profile;
    }

    /// <summary>读取 active generation，预算耗尽、profile 不匹配或重排失败时不返回部分结果。</summary>
    /// <param name="query">非空查询文本，最多八千一百九十二字符。</param>
    /// <param name="embedding">已经生成且属于构造时 profile 的有限查询向量。</param>
    /// <param name="options">候选、扫描、posting、字符与时间预算。</param>
    /// <param name="reranker">可选重排扩展，须由宿主落实外发策略。</param>
    /// <param name="allowedDocumentIds">可选的已授权分块文档 ID 集；null 表示整个 stream 已授权。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>稳定排序且来源于同一 generation 的授权命中。</returns>
    public async ValueTask<IReadOnlyList<SemanticSearchHit>> SearchAsync(
        string query, ReadOnlyMemory<float> embedding, SemanticSearchFusionOptions? options = null,
        ISemanticSearchReranker? reranker = null, IReadOnlySet<string>? allowedDocumentIds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(query.Length, 8192);
        options ??= new();
        options.Validate();
        if (embedding.Length != _profile.Dimensions)
            throw new ArgumentException("查询向量维度与 profile 不一致。", nameof(embedding));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.MaxDuration);
        CancellationToken token = deadline.Token;
        token.ThrowIfCancellationRequested();
        float[] vector = embedding.ToArray();
        ValidateVector(vector, nameof(embedding));
        using DatabaseGenerationQueryLease lease = _database.Generations.AcquireActive(_stream);
        ValidatePublishedProfile(lease, options, token);
        token.ThrowIfCancellationRequested();
        string name = lease.GetRequiredResource(RagIngestionWriter.ChunksResourceRole, DatabaseGenerationResourceKind.DocumentCollection).Name;
        DocumentCollectionStore collection = _database.Documents.Open(name);
        var vectorIndex = collection.Schema.TryGetVectorIndex(RagIngestionWriter.VectorIndexName)
            ?? throw new InvalidDataException("RAG generation 缺少向量索引声明。");
        var fullTextIndex = collection.Schema.TryGetFullTextIndex(RagIngestionWriter.FullTextIndexName)
            ?? throw new InvalidDataException("RAG generation 缺少全文索引声明。");
        KnnMetric metric = _profile.Metric switch
        {
            Vector.Primitives.KnnMetric.Cosine => KnnMetric.Cosine,
            Vector.Primitives.KnnMetric.L2 => KnnMetric.L2,
            Vector.Primitives.KnnMetric.InnerProduct => KnnMetric.InnerProduct,
            _ => throw new InvalidDataException("不支持的向量度量。"),
        };
        if (vectorIndex.Dimensions != _profile.Dimensions || vectorIndex.Metric != metric)
            throw new InvalidOperationException("查询 profile 与 generation 向量索引不兼容。");
        var candidates = new Dictionary<string, SemanticSearchCandidate>(StringComparer.Ordinal);
        long scannedCharacters = 0;
        long textCharacters = query.Length;
        string? afterId = null;
        // 每页一条，最多 MaxScannedDocuments + 1 次；最后一条只用于检测预算溢出。
        for (int visited = 0; visited <= options.MaxScannedDocuments; visited++)
        {
            token.ThrowIfCancellationRequested();
            IReadOnlyList<DocumentRow> page = collection.ScanAfter(afterId, 1);
            token.ThrowIfCancellationRequested();
            if (page.Count == 0)
                break;
            if (visited == options.MaxScannedDocuments)
                throw new InvalidOperationException("RAG 检索超过文档扫描预算。");
            DocumentRow row = page[0];
            afterId = row.Id;
            scannedCharacters += row.Json.Length;
            if (scannedCharacters > options.MaxScannedJsonCharacters)
                throw new InvalidOperationException("RAG 检索超过 JSON 扫描字符预算。");
            if (allowedDocumentIds is not null && !allowedDocumentIds.Contains(row.Id))
                continue;
            RagIngestionChunkDocument document = JsonSerializer.Deserialize(row.Json, RagIngestionWriterJsonContext.Default.RagIngestionChunkDocument)
                ?? throw new InvalidDataException("RAG 分块文档为空。");
            if (document.ProfileId != _profile.Id || document.Embedding is null || document.Embedding.Length != vector.Length)
                throw new InvalidOperationException("查询 profile 与 generation 分块不兼容。");
            ValidateVector(document.Embedding, "storedEmbedding");
            if (string.IsNullOrWhiteSpace(document.ContentId) || string.IsNullOrWhiteSpace(document.ChunkId) || document.Text is null)
                throw new InvalidDataException("RAG 分块身份或正文无效。");
            textCharacters += document.Text.Length + (long)document.ContentId.Length + row.Id.Length
                + (document.Source?.Length ?? 0) + (document.Section?.Length ?? 0);
            if (textCharacters > options.MaxTextCharacters)
                throw new InvalidOperationException("RAG 检索超过正文字符预算。");
            double distance = VectorDistance.Compute(metric, vector, document.Embedding);
            if (!double.IsFinite(distance))
                throw new InvalidDataException("RAG 向量距离不是有限值。");
            candidates.Add(row.Id, new(row.Id, document.ContentId, document.Text,
                -distance, document.Source, document.Section));
        }
        token.ThrowIfCancellationRequested();
        var allowed = candidates.Keys.ToHashSet(StringComparer.Ordinal);
        DocumentFullTextFilteredSearchResult lexical = collection.SearchFullTextFiltered(
            fullTextIndex, "$.text", query, options.MaxCandidatesPerSource, allowed, options.MaxPostingVisits, token);
        if (lexical.PostingBudgetExceeded)
            throw new InvalidOperationException("RAG 检索超过全文 posting 检查预算。");
        token.ThrowIfCancellationRequested();
        SemanticSearchCandidate[] vectorHits = candidates.Values.OrderByDescending(static hit => hit.Score)
            .ThenBy(static hit => hit.Id, StringComparer.Ordinal).Take(options.MaxCandidatesPerSource).ToArray();
        SemanticSearchCandidate[] textHits = lexical.Hits.Select(hit => candidates[hit.DocumentId] with { Score = hit.Score }).ToArray();
        return await SemanticSearchFusion.FuseAsync(query, [new(vectorHits), new(textHits)], options, reranker, token).ConfigureAwait(false);
    }

    private void ValidateVector(float[] values, string name)
    {
        double squaredNorm = 0;
        foreach (float value in values)
        {
            if (!float.IsFinite(value))
                throw new ArgumentException("向量元素必须为有限值。", name);
            squaredNorm += (double)value * value;
        }
        if ((_profile.Normalization == EmbeddingNormalization.L2 && Math.Abs(squaredNorm - 1) > 0.001)
            || (_profile.Metric == Vector.Primitives.KnnMetric.Cosine && squaredNorm == 0))
            throw new ArgumentException("向量不满足 profile 归一化或余弦非零约束。", name);
    }

    private void ValidatePublishedProfile(DatabaseGenerationQueryLease lease, SemanticSearchFusionOptions options, CancellationToken token)
    {
        string name = lease.GetRequiredResource(RagIngestionWriter.SnapshotResourceRole, DatabaseGenerationResourceKind.KvKeyspace).Name;
        byte[] bytes = _database.Keyspaces.Open(name).Get("snapshot")
            ?? throw new InvalidDataException("RAG generation 缺少已发布快照。");
        if (bytes.Length > options.MaxSnapshotBytes)
            throw new InvalidOperationException("RAG 快照超过字节预算。");
        token.ThrowIfCancellationRequested();
        using JsonDocument snapshot = JsonDocument.Parse(bytes);
        token.ThrowIfCancellationRequested();
        JsonElement root = snapshot.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("schemaVersion", out var schema) || !schema.TryGetInt32(out int version) || version != 1
            || !root.TryGetProperty("stream", out var stream) || stream.GetString() != _stream
            || !root.TryGetProperty("generationId", out var generation) || generation.GetString() != lease.Generation.GenerationId
            || !root.TryGetProperty("expectedRevision", out var revision) || !revision.TryGetInt64(out long expected) || expected != lease.Generation.Revision - 1
            || !root.TryGetProperty("profile", out var profile))
            throw new InvalidDataException("RAG 快照与 generation 身份不一致。");
        EmbeddingProfile stored = profile.Deserialize(RagIngestionWriterJsonContext.Default.EmbeddingProfile)
            ?? throw new InvalidDataException("RAG 快照缺少 profile。");
        if (JsonSerializer.Serialize(stored, RagIngestionWriterJsonContext.Default.EmbeddingProfile)
            != JsonSerializer.Serialize(_profile, RagIngestionWriterJsonContext.Default.EmbeddingProfile))
            throw new InvalidOperationException("查询 profile 与已发布 profile 不完全一致。");
    }
}
