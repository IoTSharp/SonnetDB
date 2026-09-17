using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.Generations;
using SonnetDB.Kv;

namespace SonnetDB.SemanticContent;

/// <summary>
/// 把完整文本快照持久化为具有 Document、FullText 和 Vector 索引的不可变 generation。
/// 失败或取消保留 staging；重开数据库后可续跑，只有完整版本才会原子发布。
/// </summary>
/// <remarks>
/// 使用数据库现有 KV WAL 与 generation 发布合同。调用方负责原始对象、provider 外发治理和审计，
/// 并保证同一 profile ID 的模型合同不可变。provider 必须遵守取消令牌；其调用可能因崩溃重放。
/// 删除从新 active 版本立即生效；旧版本物理清理由 generation CleanupRetired 在租约释放后执行。
/// </remarks>
public sealed class RagIngestionWriter
{
    /// <summary>generation 中分块 Document collection 的逻辑角色。</summary>
    public const string ChunksResourceRole = "rag.chunks";

    /// <summary>generation 中持久化完整快照的 KV 逻辑角色。</summary>
    public const string SnapshotResourceRole = "rag.snapshot";

    /// <summary>分块正文 FullText 索引名称。</summary>
    public const string FullTextIndexName = "rag_text";

    /// <summary>分块 embedding Vector 索引名称。</summary>
    public const string VectorIndexName = "rag_vector";

    private const string JobsKeyspaceName = "rag-ingestion-jobs";
    private static readonly ConditionalWeakTable<Tsdb, SemaphoreSlim> Gates = new();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] FullTextFields = ["$.text"];
    private readonly Tsdb _database;
    private readonly string _stream;
    private readonly EmbeddingProfile _profile;
    private readonly RagIngestionWriterOptions _options;
    private readonly SemaphoreSlim _gate;

    /// <summary>创建绑定数据库、独占 generation stream 和不可变 embedding profile 的 writer。</summary>
    /// <param name="database">生命周期由调用方管理的数据库。</param>
    /// <param name="stream">本 writer 独占的 generation stream。</param>
    /// <param name="profile">provider 必须遵守的不可变模型合同。</param>
    /// <param name="options">单次工作预算。</param>
    public RagIngestionWriter(
        Tsdb database,
        string stream,
        EmbeddingProfile profile,
        RagIngestionWriterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(stream);
        ArgumentNullException.ThrowIfNull(profile);
        if (StrictUtf8.GetByteCount(stream) > 4096)
            throw new ArgumentOutOfRangeException(nameof(stream));
        if (profile.SupportedModalities is null || profile.SupportedModalities.Count > 6
            || profile.DataEgressPolicy is null
            || SemanticContentValidator.ValidateProfile(profile).Count != 0
            || profile.Dimensions > 32_768)
            throw new ArgumentException("embedding profile 无效或维度超过 32768。", nameof(profile));
        _database = database;
        _stream = stream;
        _profile = profile with { SupportedModalities = profile.SupportedModalities.ToArray() };
        ValidateUtf8(profile.Id);
        ValidateUtf8(profile.Provider);
        ValidateUtf8(profile.Model);
        ValidateUtf8(profile.Revision);
        ValidateUtf8(profile.DataEgressPolicy.Target);
        _options = options ?? new RagIngestionWriterOptions();
        ValidateOptions(_options);
        _gate = Gates.GetValue(database, static _ => new SemaphoreSlim(1, 1));
    }

    /// <summary>
    /// 冻结并写入完整期望快照。省略旧内容表示删除；存在未完成任务时必须先续跑或显式放弃。
    /// 已完成且没有内容或 profile 变化的快照不创建新版本，也不调用 provider。
    /// </summary>
    /// <param name="snapshot">完整期望快照；本切片仅支持 Text/Document 文本分块。</param>
    /// <param name="embedAsync">生成指定文本分块向量的函数；必须遵守 profile、外发策略及取消令牌。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>完整发布后的版本与统计。</returns>
    public async ValueTask<RagIngestionWriteResult> WriteAsync(
        RagIngestionSnapshot snapshot,
        Func<SemanticContentChunk, CancellationToken, ValueTask<float[]>> embedAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(embedAsync);
        using var deadline = CreateDeadline(cancellationToken);
        CancellationToken token = deadline.Token;
        RagIngestionSnapshot frozen = Freeze(snapshot, token);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using DatabaseGenerationQueryLease? active = AcquireActive();
            RagIngestionWriterCheckpoint? pending = ReadPending(token);
            if (pending is not null && !IsPublished(pending, active))
                throw new InvalidOperationException("该 stream 有未完成任务，请先 ResumeAsync 或 DiscardPendingAsync。");

            RagIngestionWriterCheckpoint? previous = ReadPublished(active, token);
            RagIngestionPlan plan = RagIngestionPlanner.CreatePlan(
                previous?.Snapshot,
                frozen,
                PlanningOptions(),
                token);
            bool sameProfile = previous is not null && ProfilesEqual(previous.Profile, _profile);
            if (previous is not null && previous.Profile.Id == _profile.Id && !sameProfile)
                throw new ArgumentException("已有 profile ID 的模型合同不可更改；请使用新的 profile ID。", nameof(snapshot));
            if (active is not null && plan.IsEmpty && sameProfile)
                return new(active.Generation, 0, 0, 0, 0, CountChunks(frozen));

            var checkpoint = new RagIngestionWriterCheckpoint
            {
                Stream = _stream,
                GenerationId = Guid.NewGuid().ToString("N"),
                ExpectedRevision = active?.Generation.Revision ?? 0,
                Profile = _profile,
                Snapshot = frozen,
                AddedContents = plan.AddCount,
                UpdatedContents = plan.UpdateCount,
                DeletedContents = plan.DeleteCount,
            };
            byte[] bytes = EncodeCheckpoint(checkpoint);
            token.ThrowIfCancellationRequested();
            KvKeyspace jobs = Jobs;
            jobs.Put(JobKey, bytes);
            // 无论调用方是否关闭逐次 fsync，都先保证任务身份 durable，再创建 staging 资源。
            jobs.CreateSnapshot();
            return await ExecuteAsync(checkpoint, active, previous, embedAsync, token).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 从数据库持久化任务续跑。跳过已恢复的完整 chunk；发布后中断的任务直接返回已发布版本。
    /// </summary>
    /// <param name="embedAsync">遵守同一 profile 合同与取消令牌的 embedding 函数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>发布结果；该 stream 从未保存任务时为空。</returns>
    public async ValueTask<RagIngestionWriteResult?> ResumeAsync(
        Func<SemanticContentChunk, CancellationToken, ValueTask<float[]>> embedAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(embedAsync);
        using var deadline = CreateDeadline(cancellationToken);
        CancellationToken token = deadline.Token;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            RagIngestionWriterCheckpoint? checkpoint = ReadPending(token);
            if (checkpoint is null)
                return null;
            if (!ProfilesEqual(checkpoint.Profile, _profile))
                throw new InvalidOperationException("续跑必须使用任务原有的完整 embedding profile 合同。");
            checkpoint = checkpoint with { Snapshot = Freeze(checkpoint.Snapshot, token) };
            using DatabaseGenerationQueryLease? active = AcquireActive();
            if (IsPublished(checkpoint, active))
                return Result(checkpoint, active!.Generation, 0, CountChunks(checkpoint.Snapshot));
            if ((active?.Generation.Revision ?? 0) != checkpoint.ExpectedRevision)
                throw new InvalidOperationException("active revision 已变化，不能续跑旧 staging；请显式放弃旧任务。");
            return await ExecuteAsync(checkpoint, active, ReadPublished(active, token), embedAsync, token).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>删除本 writer 尚未发布的 staging 和任务记录；不删除任何已发布 generation。</summary>
    /// <param name="cancellationToken">开始清理前检查的取消令牌。</param>
    /// <returns>是否删除了一个未发布任务。</returns>
    public async ValueTask<bool> DiscardPendingAsync(CancellationToken cancellationToken = default)
    {
        using var deadline = CreateDeadline(cancellationToken);
        await _gate.WaitAsync(deadline.Token).ConfigureAwait(false);
        try
        {
            RagIngestionWriterCheckpoint? checkpoint = ReadPending(deadline.Token);
            if (checkpoint is null)
                return false;
            // 即使 stream 已由外部推进，也不能删除曾发布但已 retired 的资源。
            // 用 catalog 描述符判定曾发布身份；Acquire 会把资源缺失也映射为 RevisionUnavailable，
            // 不能据此把损坏的 retired generation 误当作可删除 staging。
            IReadOnlyList<DatabaseGeneration> generations = _database.Generations.List(_stream);
            for (int index = 0; index < generations.Count; index++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (generations[index].GenerationId == checkpoint.GenerationId)
                    return false;
            }
            deadline.Token.ThrowIfCancellationRequested();
            _database.Documents.Drop(ResourceName(checkpoint));
            _database.Keyspaces.Drop(ResourceName(checkpoint));
            Jobs.Delete(JobKey);
            Jobs.CreateSnapshot();
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<RagIngestionWriteResult> ExecuteAsync(
        RagIngestionWriterCheckpoint checkpoint,
        DatabaseGenerationQueryLease? active,
        RagIngestionWriterCheckpoint? previous,
        Func<SemanticContentChunk, CancellationToken, ValueTask<float[]>> embedAsync,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string name = ResourceName(checkpoint);
        if (_database.Documents.Catalog.TryGet(name) is null)
        {
            _database.Documents.Create(DocumentCollectionSchema.Create(
                name,
                fullTextIndexes: [new(FullTextIndexName, FullTextFields)],
                vectorIndexes: [new(VectorIndexName, "$.embedding", _profile.Dimensions, DocumentMetric())]));
        }
        DocumentCollectionStore staging = _database.Documents.Open(name);
        DocumentCollectionStore? old = active is not null && previous is not null
            && ProfilesEqual(previous.Profile, _profile)
            ? _database.Documents.Open(active.GetRequiredResource(
                ChunksResourceRole, DatabaseGenerationResourceKind.DocumentCollection).Name)
            : null;
        int embedded = 0;
        int reused = 0;
        foreach (SemanticContentManifest manifest in checkpoint.Snapshot.Manifests)
        {
            foreach (SemanticContentChunk chunk in manifest.Chunks)
            {
                token.ThrowIfCancellationRequested();
                string id = ChunkDocumentId(manifest.Id, chunk.Id);
                RagIngestionChunkDocument? recovered = ReadChunk(staging.Get(id));
                float[] embedding;
                if (recovered is not null)
                {
                    EnsureReusable(recovered, manifest.Id, chunk);
                    embedding = recovered.Embedding;
                    reused++;
                }
                else
                {
                    RagIngestionChunkDocument? existing = ReadChunk(old?.Get(id));
                    if (existing is not null && IsReusable(existing, manifest.Id, chunk))
                    {
                        ValidateVector(existing.Embedding);
                        embedding = existing.Embedding;
                        reused++;
                    }
                    else
                    {
                        embedding = await EmbedAsync(chunk, embedAsync, token).ConfigureAwait(false);
                        embedded++;
                    }
                }
                token.ThrowIfCancellationRequested();
                var document = new RagIngestionChunkDocument
                {
                    ContentId = manifest.Id,
                    ChunkId = chunk.Id,
                    ProfileId = _profile.Id,
                    Text = chunk.Text,
                    Source = manifest.Source,
                    Section = chunk.Section,
                    Ordinal = chunk.Ordinal,
                    Embedding = embedding,
                };
                // 所有元数据始终从冻结任务重建；Upsert 重放也修复主数据提交后的派生索引中断。
                staging.Upsert(id, JsonSerializer.Serialize(document, RagIngestionWriterJsonContext.Default.RagIngestionChunkDocument));
            }
        }
        token.ThrowIfCancellationRequested();
        _database.Keyspaces.Open(name).Put("snapshot", EncodeCheckpoint(checkpoint));
        DatabaseGeneration generation = _database.Generations.Publish(new DatabaseGenerationPublishRequest
        {
            Stream = _stream,
            GenerationId = checkpoint.GenerationId,
            ExpectedRevision = checkpoint.ExpectedRevision,
            Resources =
            [
                new(ChunksResourceRole, DatabaseGenerationResourceKind.DocumentCollection, name),
                new(SnapshotResourceRole, DatabaseGenerationResourceKind.KvKeyspace, name),
                new("rag.fulltext", DatabaseGenerationResourceKind.DocumentFullTextIndex, FullTextIndexName, name),
            ],
        }, token);
        return Result(checkpoint, generation, embedded, reused);
    }

    private async ValueTask<float[]> EmbedAsync(
        SemanticContentChunk chunk,
        Func<SemanticContentChunk, CancellationToken, ValueTask<float[]>> embedAsync,
        CancellationToken token)
    {
        for (int attempt = 1; attempt <= _options.MaxEmbeddingAttempts; attempt++)
        {
            token.ThrowIfCancellationRequested();
            float[] vector;
            try
            {
                vector = await embedAsync(chunk, token).ConfigureAwait(false);
            }
            catch (Exception exception) when (attempt < _options.MaxEmbeddingAttempts
                && exception is IOException or HttpRequestException or TimeoutException)
            {
                await Task.Delay(_options.RetryDelay, token).ConfigureAwait(false);
                continue;
            }
            token.ThrowIfCancellationRequested();
            ValidateVector(vector);
            return vector.ToArray();
        }
        throw new InvalidOperationException("embedding 尝试次数耗尽。");
    }

    private RagIngestionSnapshot Freeze(RagIngestionSnapshot snapshot, CancellationToken token)
    {
        if (snapshot.SchemaVersion != 1)
            throw new ArgumentException("writer 仅支持版本 1 的快照合同。", nameof(snapshot));
        SemanticContentManifest[] manifests = RagIngestionPreflight.FreezeSnapshot(
            snapshot, _options.MaxManifests,
            new(_options.MaxChunks, 0, 0, _options.MaxTextCharacters), new(), nameof(snapshot), token);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (SemanticContentManifest manifest in manifests)
        {
            token.ThrowIfCancellationRequested();
            if (manifest.SchemaVersion != 1)
                throw new ArgumentException("writer 仅支持版本 1 的清单合同。", nameof(snapshot));
            if (!ids.Add(manifest.Id) || manifest.Id.Length > 4096)
                throw new ArgumentException("内容 ID 重复或超过 4096 字符。", nameof(snapshot));
            if (manifest.Modality is not (SemanticContentModality.Text or SemanticContentModality.Document)
                || !_profile.SupportedModalities.Contains(manifest.Modality))
                throw new ArgumentException("writer 和 profile 必须支持清单的 Text/Document 模态。", nameof(snapshot));
            if (manifest.EmbeddingProfileId is not null && manifest.EmbeddingProfileId != _profile.Id)
                throw new ArgumentException("清单引用的 profile 与 writer 不一致。", nameof(snapshot));
            ValidateUtf8(manifest.Id);
            ValidateUtf8(manifest.ContentHash);
            ValidateUtf8(manifest.MimeType);
            ValidateUtf8(manifest.Source);
            ValidateUtf8(manifest.Text);
            ValidateUtf8(manifest.ObjectRef!.Bucket);
            ValidateUtf8(manifest.ObjectRef.Key);
            ValidateUtf8(manifest.ObjectRef.VersionId);
            ValidateUtf8(manifest.ObjectRef.ETag);
            foreach (SemanticContentChunk chunk in manifest.Chunks)
            {
                token.ThrowIfCancellationRequested();
                if (chunk.Id.Length > 4096)
                    throw new ArgumentException("chunk ID 超过 4096 字符。", nameof(snapshot));
                _ = ChunkDocumentId(manifest.Id, chunk.Id);
                ValidateUtf8(chunk.Text);
                ValidateUtf8(chunk.Section);
                ValidateUtf8(chunk.ContentHash);
            }
        }
        return new RagIngestionSnapshot(manifests) { SchemaVersion = snapshot.SchemaVersion };
    }

    private RagIngestionPlanningOptions PlanningOptions() => new()
    {
        MaxManifests = _options.MaxManifests,
        MaxActions = checked(_options.MaxManifests * 2),
        MaxTotalChunks = checked(_options.MaxChunks * 2),
        MaxTotalTextCharacters = (long)_options.MaxTextCharacters * 2,
    };

    private KvKeyspace Jobs => _database.Keyspaces.Open(JobsKeyspaceName);
    private string JobKey => Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(_stream)));
    private static string ResourceName(RagIngestionWriterCheckpoint checkpoint) => "rag_" + checkpoint.GenerationId;
    private static string ChunkDocumentId(string contentId, string chunkId)
        => Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(FormattableString.Invariant($"{contentId.Length}:{contentId}{chunkId}"))));

    private static void ValidateUtf8(string? text)
    {
        if (text is not null)
            _ = StrictUtf8.GetByteCount(text);
    }

    private Query.KnnMetric DocumentMetric() => _profile.Metric switch
    {
        Vector.Primitives.KnnMetric.Cosine => Query.KnnMetric.Cosine,
        Vector.Primitives.KnnMetric.L2 => Query.KnnMetric.L2,
        Vector.Primitives.KnnMetric.InnerProduct => Query.KnnMetric.InnerProduct,
        _ => throw new InvalidOperationException("不支持的向量距离度量。"),
    };

    private RagIngestionWriterCheckpoint? ReadPending(CancellationToken token)
        => DecodeCheckpoint(Jobs.Get(JobKey), token);

    private RagIngestionWriterCheckpoint? ReadPublished(DatabaseGenerationQueryLease? active, CancellationToken token)
    {
        if (active is null)
            return null;
        string name = active.GetRequiredResource(SnapshotResourceRole, DatabaseGenerationResourceKind.KvKeyspace).Name;
        RagIngestionWriterCheckpoint checkpoint = DecodeCheckpoint(_database.Keyspaces.Open(name).Get("snapshot"), token)
            ?? throw new InvalidDataException("已发布 RAG generation 缺少快照。");
        if (checkpoint.GenerationId != active.Generation.GenerationId
            || checkpoint.ExpectedRevision != active.Generation.Revision - 1
            || name != ResourceName(checkpoint))
            throw new InvalidDataException("已发布快照与 generation 身份不一致。");
        return checkpoint;
    }

    private byte[] EncodeCheckpoint(RagIngestionWriterCheckpoint checkpoint)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(checkpoint, RagIngestionWriterJsonContext.Default.RagIngestionWriterCheckpoint);
        if (bytes.Length > _options.MaxCheckpointBytes)
            throw new InvalidOperationException("摄取任务 JSON 超过 checkpoint 字节预算。");
        return bytes;
    }

    private RagIngestionWriterCheckpoint? DecodeCheckpoint(byte[]? bytes, CancellationToken token)
    {
        if (bytes is null)
            return null;
        if (bytes.Length > _options.MaxCheckpointBytes)
            throw new InvalidOperationException("持久化任务超过本 writer 的 checkpoint 字节预算。");
        ValidatePersistedSnapshotShape(bytes, token);
        RagIngestionWriterCheckpoint? checkpoint = JsonSerializer.Deserialize(bytes, RagIngestionWriterJsonContext.Default.RagIngestionWriterCheckpoint);
        if (checkpoint is null || checkpoint.SchemaVersion != 1 || checkpoint.Stream != _stream
            || !Guid.TryParseExact(checkpoint.GenerationId, "N", out _)
            || checkpoint.ExpectedRevision is < 0 or long.MaxValue
            || checkpoint.AddedContents < 0 || checkpoint.UpdatedContents < 0 || checkpoint.DeletedContents < 0
            || checkpoint.Profile is null || checkpoint.Snapshot is null)
            throw new InvalidDataException("RAG 持久化任务合同无效。");
        return checkpoint;
    }

    private void ValidatePersistedSnapshotShape(byte[] bytes, CancellationToken token)
    {
        // 公共 snapshot DTO 为兼容既有 wire 合同保留默认空集合；持久化恢复不能把缺字段
        // 当作显式空快照，否则损坏 checkpoint 可能被解释为删除。只在本 writer 的内部边界加严。
        using JsonDocument document = JsonDocument.Parse(bytes);
        token.ThrowIfCancellationRequested();
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("snapshot", out JsonElement snapshot)
            || snapshot.ValueKind != JsonValueKind.Object
            || !snapshot.TryGetProperty("schemaVersion", out JsonElement version)
            || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int schemaVersion) || schemaVersion != 1
            || !snapshot.TryGetProperty("manifests", out JsonElement manifests)
            || manifests.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("持久化 snapshot 必须显式包含版本 1 和 manifests 数组。");
        if (manifests.GetArrayLength() > _options.MaxManifests)
            throw new InvalidOperationException("持久化 snapshot 超过清单预算。");
        int chunkCount = 0;
        foreach (JsonElement manifest in manifests.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            if (manifest.ValueKind != JsonValueKind.Object
                || !manifest.TryGetProperty("schemaVersion", out JsonElement manifestVersion)
                || manifestVersion.ValueKind != JsonValueKind.Number || !manifestVersion.TryGetInt32(out int manifestSchemaVersion) || manifestSchemaVersion != 1
                || !manifest.TryGetProperty("chunks", out JsonElement chunks) || chunks.ValueKind != JsonValueKind.Array
                || !manifest.TryGetProperty("segments", out JsonElement segments) || segments.ValueKind != JsonValueKind.Array
                || !manifest.TryGetProperty("embeddings", out JsonElement embeddings) || embeddings.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("持久化清单必须显式包含 chunks、segments 和 embeddings 数组。");
            if (segments.GetArrayLength() != 0 || embeddings.GetArrayLength() != 0)
                throw new InvalidDataException("持久化 writer 不支持 segments 或命名 embedding 绑定。");
            chunkCount = checked(chunkCount + chunks.GetArrayLength());
            if (chunkCount > _options.MaxChunks)
                throw new InvalidOperationException("持久化 snapshot 超过分块预算。");
        }
    }

    private DatabaseGenerationQueryLease? AcquireActive()
    {
        try { return _database.Generations.AcquireActive(_stream); }
        catch (DatabaseGenerationException exception) when (exception.Code == DatabaseGenerationErrorCodes.NoActiveGeneration)
        { return null; }
    }

    private static bool IsPublished(RagIngestionWriterCheckpoint checkpoint, DatabaseGenerationQueryLease? active)
        => active?.Generation.GenerationId == checkpoint.GenerationId;

    private static bool ProfilesEqual(EmbeddingProfile left, EmbeddingProfile right)
        => JsonSerializer.Serialize(left, RagIngestionWriterJsonContext.Default.EmbeddingProfile)
            == JsonSerializer.Serialize(right, RagIngestionWriterJsonContext.Default.EmbeddingProfile);

    private static RagIngestionChunkDocument? ReadChunk(DocumentRow? row)
        => row is null ? null : JsonSerializer.Deserialize(row.Json, RagIngestionWriterJsonContext.Default.RagIngestionChunkDocument)
            ?? throw new InvalidDataException("RAG chunk 文档无效。");

    private bool IsReusable(RagIngestionChunkDocument document, string contentId, SemanticContentChunk chunk)
        => document.ContentId == contentId && document.ChunkId == chunk.Id
            && document.ProfileId == _profile.Id && document.Text == chunk.Text;

    private void EnsureReusable(RagIngestionChunkDocument document, string contentId, SemanticContentChunk chunk)
    {
        if (!IsReusable(document, contentId, chunk))
            throw new InvalidDataException("恢复的 chunk 与冻结任务不一致。");
        ValidateVector(document.Embedding);
    }

    private void ValidateVector(float[] vector)
    {
        if (vector is null || vector.Length != _profile.Dimensions || vector.Any(static value => !float.IsFinite(value)))
            throw new InvalidDataException("provider 向量维度不匹配或包含非有限值。");
        if (_profile.Normalization == EmbeddingNormalization.L2)
        {
            double squaredNorm = 0;
            for (int index = 0; index < vector.Length; index++)
                squaredNorm += (double)vector[index] * vector[index];
            if (Math.Abs(squaredNorm - 1) > 0.001)
                throw new InvalidDataException("provider 向量不满足 profile 的 L2 归一化合同。");
        }
    }

    private CancellationTokenSource CreateDeadline(CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(_options.MaxDuration);
        return source;
    }

    private static int CountChunks(RagIngestionSnapshot snapshot)
        => snapshot.Manifests.Sum(static manifest => manifest.Chunks.Count);

    private static RagIngestionWriteResult Result(RagIngestionWriterCheckpoint checkpoint, DatabaseGeneration generation, int embedded, int reused)
        => new(generation, checkpoint.AddedContents, checkpoint.UpdatedContents, checkpoint.DeletedContents, embedded, reused);

    private static void ValidateOptions(RagIngestionWriterOptions options)
    {
        if (options.MaxManifests is <= 0 or > 100_000 || options.MaxChunks is <= 0 or > 1_000_000
            || options.MaxTextCharacters <= 0 || options.MaxCheckpointBytes <= 0
            || options.MaxEmbeddingAttempts is < 1 or > 10
            || options.RetryDelay < TimeSpan.Zero || options.RetryDelay > TimeSpan.FromMinutes(1)
            || options.MaxDuration <= TimeSpan.Zero || options.MaxDuration > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(options), "writer 预算、重试次数或超时无效。");
    }
}
