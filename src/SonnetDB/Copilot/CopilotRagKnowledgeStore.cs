using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Engine;
using SonnetDB.Generations;
using SonnetDB.Hosting;
using SonnetDB.ObjectStorage;
using SonnetDB.SemanticContent;
using SonnetDB.SemanticSearch;

namespace SonnetDB.Copilot;

/// <summary>显式启用的 RAG 知识库；旧 measurement 保持可回滚，失败不暴露 staging。</summary>
internal sealed class CopilotRagKnowledgeStore(
    TsdbRegistry registry, DocsSourceScanner scanner, IEmbeddingProvider provider, IOptions<ServerOptions> options)
{
    private const string SourceBucket = "copilot-rag-sources";
    private readonly CopilotDocsOptions _docs = options.Value.Copilot.Docs;
    private readonly CopilotEmbeddingOptions _embedding = options.Value.Copilot.Embedding;

    internal bool Enabled => _docs.StorageMode switch
    {
        "legacy" => false,
        "rag" => true,
        _ => throw new InvalidOperationException("Copilot Docs.StorageMode 必须为 legacy 或 rag。"),
    };

    internal async Task<DocsIngestStats> IngestAsync(IReadOnlyList<string> roots, bool dryRun, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        CancellationToken token = deadline.Token;
        EmbeddingProfile profile = ValidateProfile();
        if (roots.Count is < 1 or > 32 || roots.Any(static root => !Path.IsPathFullyQualified(root) || !Directory.Exists(root)
            || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0))
            throw new InvalidOperationException("RAG 摄取需要 1 至 32 个存在的绝对文档根目录；根目录缺失时不会发布删除。");
        var files = scanner.Scan(roots, token);
        var sources = new List<(SemanticContentManifest Manifest, byte[] Bytes)>();
        long totalBytes = 0;
        int totalChunks = 0;
        foreach (DocsSourceFile file in files)
        {
            token.ThrowIfCancellationRequested();
            await using var input = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            totalBytes += input.Length;
            if (totalBytes > 4 * 1024 * 1024) throw new InvalidOperationException("RAG 文档快照超过 4 MiB 预算。");
            byte[] bytes = new byte[checked((int)input.Length)];
            await input.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            if (input.ReadByte() != -1) throw new IOException("文档在摄取期间发生变化。");
            string text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
            var chunked = RagTextChunker.Chunk(file.Source, text, new RagTextChunkingOptions
            {
                MaxCharacters = _docs.ChunkSize,
                OverlapCharacters = _docs.ChunkOverlap,
            }, token);
            totalChunks += chunked.Chunks.Count;
            if (totalChunks > 100_000) throw new InvalidOperationException("RAG 文档超过 100000 个分块。");
            string hash = Convert.ToHexString(SHA256.HashData(bytes));
            var manifest = new SemanticContentManifest(file.Source,
                new(SourceBucket, hash, eTag: hash), hash, "text/plain", SemanticContentModality.Document,
                bytes.Length, file.Source, profile.Id)
            {
                Chunks = chunked.Chunks.Select(chunk => chunk with { Section = file.Title }).ToArray(),
            };
            sources.Add((manifest, bytes));
        }
        // 在打开数据库和写原始对象前校验完整快照预算。
        _ = RagIngestionPlanner.CreatePlan(null, new(sources.Select(static source => source.Manifest).ToArray()),
            cancellationToken: token);
        if (dryRun) return new(files.Count, files.Count, 0, 0, totalChunks, true);

        Tsdb database = Database;
        var writer = new RagIngestionWriter(database, _docs.RagStream, profile,
            new RagIngestionWriterOptions { MaxDuration = TimeSpan.FromMinutes(2) });
        // 如果前次失败，先完成冻结任务，再处理本次完整快照；不丢弃原任务。
        await writer.ResumeAsync((chunk, ct) => EmbedAsync(database, profile, chunk.Text, ct), token).ConfigureAwait(false);
        var objects = new SndbObjectStore(database);
        objects.CreateBucket(SourceBucket, "Copilot RAG 原始内容快照");
        var manifests = new List<SemanticContentManifest>(sources.Count);
        foreach (var source in sources)
        {
            token.ThrowIfCancellationRequested();
            string key = source.Manifest.ContentHash;
            SndbObjectInfo? stored = objects.HeadObject(SourceBucket, key);
            if (stored is null)
            {
                using var input = new MemoryStream(source.Bytes, writable: false);
                stored = await objects.PutObjectAsync(SourceBucket, key, input, "text/plain", cancellationToken: token).ConfigureAwait(false);
            }
            manifests.Add(source.Manifest with { ObjectRef = new(SourceBucket, key, stored.VersionId, stored.ETag) });
        }
        RagIngestionWriteResult result = await writer.WriteAsync(new(manifests),
            (chunk, ct) => EmbedAsync(database, profile, chunk.Text, ct), token).ConfigureAwait(false);
        return new(files.Count, result.AddedContents + result.UpdatedContents,
            files.Count - result.AddedContents - result.UpdatedContents, result.DeletedContents, result.EmbeddedChunks, false);
    }

    internal async Task<IReadOnlyList<DocsSearchResult>> SearchAsync(string query, int k, CancellationToken cancellationToken)
    {
        if (query.Length > 16_384 || k is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(k));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(1));
        CancellationToken token = deadline.Token;
        EmbeddingProfile profile = ValidateProfile();
        Tsdb database = Database;
        using DatabaseGenerationQueryLease lease = database.Generations.AcquireActive(_docs.RagStream);
        using JsonDocument checkpoint = ReadCheckpoint(database, lease, token);
        var publishedProfile = checkpoint.RootElement.GetProperty("profile").Deserialize(CopilotRagJsonContext.Default.EmbeddingProfile);
        if (publishedProfile is null || JsonSerializer.Serialize(profile, CopilotRagJsonContext.Default.EmbeddingProfile)
            != JsonSerializer.Serialize(publishedProfile, CopilotRagJsonContext.Default.EmbeddingProfile))
            throw new InvalidOperationException("当前 RAG generation 与查询 provider profile 不匹配；请先完成新 stream 摄取。");
        float[] vector = await EmbedAsync(database, profile, query, token).ConfigureAwait(false);
        var collection = database.Documents.Open(lease.GetRequiredResource(RagIngestionWriter.ChunksResourceRole,
            DatabaseGenerationResourceKind.DocumentCollection).Name);
        var hits = collection.SearchVector(collection.Schema.TryGetVectorIndex(RagIngestionWriter.VectorIndexName)!, vector, k);
        var results = new List<DocsSearchResult>(hits.Count);
        foreach (var hit in hits)
        {
            token.ThrowIfCancellationRequested();
            var row = collection.Get(hit.Id) ?? throw new InvalidDataException("RAG generation 分块缺失。");
            using JsonDocument document = JsonDocument.Parse(row.Json);
            JsonElement root = document.RootElement;
            string source = root.GetProperty("source").GetString() ?? string.Empty;
            string section = root.GetProperty("section").GetString() ?? string.Empty;
            results.Add(new(source, section, section, root.GetProperty("text").GetString()!, hit.Distance,
                lease.Generation.PublishedAtUtc.ToUnixTimeMilliseconds()));
        }
        CopilotDiagnostics.RecordKnowledgeRecall(results.Count > 0);
        return results;
    }

    internal DocsIngestor.DocsIndexState GetIndexState(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Tsdb database = Database;
        try
        {
            using var lease = database.Generations.AcquireActive(_docs.RagStream);
            using JsonDocument checkpoint = ReadCheckpoint(database, lease, token);
            JsonElement manifests = checkpoint.RootElement.GetProperty("snapshot").GetProperty("manifests");
            if (manifests.GetArrayLength() > 10_000) throw new InvalidDataException("RAG 快照超过清单预算。");
            int chunks = 0;
            foreach (JsonElement manifest in manifests.EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
                chunks = checked(chunks + manifest.GetProperty("chunks").GetArrayLength());
                if (chunks > 100_000) throw new InvalidDataException("RAG 快照超过分块预算。");
            }
            return new(manifests.GetArrayLength(), chunks, lease.Generation.PublishedAtUtc.ToString("O"));
        }
        catch (DatabaseGenerationException exception) when (exception.Code == DatabaseGenerationErrorCodes.NoActiveGeneration)
        {
            return new(0, 0, null);
        }
    }

    private Tsdb Database { get { registry.TryCreate(DocsIngestor.CopilotDatabaseName, out var database); return database; } }

    private static JsonDocument ReadCheckpoint(Tsdb database, DatabaseGenerationQueryLease lease, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string name = lease.GetRequiredResource(RagIngestionWriter.SnapshotResourceRole, DatabaseGenerationResourceKind.KvKeyspace).Name;
        byte[]? bytes = database.Keyspaces.Open(name).Get("snapshot");
        if (bytes is null || bytes.Length > 8 * 1024 * 1024) throw new InvalidDataException("RAG generation 快照缺失或超过预算。");
        return JsonDocument.Parse(bytes);
    }

    private EmbeddingProfile ValidateProfile()
    {
        var configured = _docs.RagProfile ?? throw new InvalidOperationException("RAG 模式必须配置 Docs.RagProfile。");
        if (configured.SupportedModalities is null || configured.DataEgressPolicy is null)
            throw new InvalidOperationException("RAG profile 必须提供模态和外发策略。");
        var profile = configured.ToProfile();
        if (profile.SupportedModalities is null || profile.DataEgressPolicy is null
            || SemanticContentValidator.ValidateProfile(profile).Count != 0 || !profile.Supports(SemanticContentModality.Document)
            || !profile.Supports(SemanticContentModality.Text) || profile.Dimensions > 32_768
            || string.IsNullOrWhiteSpace(_docs.RagStream)
            || !string.Equals(profile.Provider, _embedding.Provider, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("RAG profile、模态或 stream 无效，或与 Copilot embedding provider 不匹配。");
        if (_embedding.Provider == "builtin")
        {
            if (profile.Model != "builtin-hash" || profile.Revision != "1" || profile.Dimensions != 384)
                throw new InvalidOperationException("builtin 仅可使用 builtin-hash revision 1 / 384 维合同，不能标记为真实语义模型。");
        }
        else if (profile.Model != _embedding.Model || _embedding.Provider is not ("openai" or "local"))
            throw new InvalidOperationException("RAG profile 模型与配置的 provider 模型不匹配。");
        if (_embedding.Provider == "local"
            && (_embedding.ModelProfile is not { } local || local.ModelId != profile.Model || local.Revision != profile.Revision
                || local.Dimensions != profile.Dimensions
                || local.Normalize != (profile.Normalization == EmbeddingNormalization.L2)))
            throw new InvalidOperationException("本地 ONNX 的显式模型身份、版本、维度与归一化必须与 RAG profile 一致。");
        if (_embedding.Provider == "openai"
            && (profile.DataEgressPolicy.Mode == SemanticDataEgressMode.LocalOnly
                || !Uri.TryCreate(_embedding.Endpoint, UriKind.Absolute, out var endpoint)
                || endpoint.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(endpoint.UserInfo)
                || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment)
                || !endpoint.AbsolutePath.EndsWith("/", StringComparison.Ordinal)
                || profile.DataEgressPolicy.Target != endpoint.AbsoluteUri))
            throw new InvalidOperationException("RAG 外发策略必须显式匹配 HTTPS embedding 基地址。");
        return profile with { SupportedModalities = profile.SupportedModalities.ToArray() };
    }

    private async ValueTask<float[]> EmbedAsync(Tsdb database, EmbeddingProfile profile, string text, CancellationToken token)
    {
        long started = Stopwatch.GetTimestamp();
        var audit = profile.DataEgressPolicy.AuditRequired ? new SemanticEmbeddingAuditStore(database) : null;
        var entry = new SemanticEmbeddingAuditEntry(Guid.NewGuid(), DateTimeOffset.UtcNow, "background", profile.Provider,
            profile.Id, "text", Encoding.UTF8.GetByteCount(text), null, profile.DataEgressPolicy.Mode,
            _embedding.Provider != "openai", "started", 0, null);
        audit?.Write(entry, token);
        try
        {
            float[] vector = provider is OpenAICompatibleEmbeddingProvider online
                ? await online.EmbedGovernedAsync(text, token).ConfigureAwait(false)
                : await provider.EmbedAsync(text, token).ConfigureAwait(false);
            if (provider is LocalOnnxEmbeddingProvider { IsFallback: true } or BuiltinHashEmbeddingProvider { IsFallback: true })
                throw new InvalidOperationException("RAG 不允许把 hash fallback 写入配置的语义模型空间。");
            if (vector.Length != profile.Dimensions || vector.Any(static value => !float.IsFinite(value))
                || profile.Normalization == EmbeddingNormalization.L2
                    && Math.Abs(vector.Sum(static value => (double)value * value) - 1) > 0.001)
                throw new InvalidOperationException("RAG provider 输出与 profile 不匹配。");
            audit?.Write(entry with { Status = "succeeded", DurationMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds }, token);
            return vector;
        }
        catch
        {
            using var auditDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            audit?.Write(entry with { Status = token.IsCancellationRequested ? "cancelled" : "failed", ErrorCode = "rag_embedding_failed" }, auditDeadline.Token);
            throw;
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(EmbeddingProfile))]
internal sealed partial class CopilotRagJsonContext : JsonSerializerContext;
