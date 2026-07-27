using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.Json;
using SonnetDB.ObjectStorage;
using SonnetDB.Query;
using SonnetDB.Vector.Compute;

namespace SonnetDB.SemanticSearch;

/// <summary>
/// 编排图片对象存储、元数据文档、embedding 和 ANN 查询。
/// </summary>
internal sealed class SemanticImageSearchService : IDisposable
{
    private const string ObjectBucket = "sonnetdb-semantic-images";
    private const string VectorIndexName = "embedding";
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _databaseGates = new(StringComparer.Ordinal);
    private readonly SemanticSearchOptions _options;
    private readonly IMultimodalEmbeddingProvider _provider;
    private readonly USearchSemanticIndexRegistry _usearch;
    private readonly ILogger<SemanticImageSearchService> _logger;

    public SemanticImageSearchService(
        IOptions<ServerOptions> options,
        IMultimodalEmbeddingProvider provider,
        USearchSemanticIndexRegistry usearch,
        ILogger<SemanticImageSearchService> logger)
    {
        _options = options.Value.SemanticSearch;
        _provider = provider;
        _usearch = usearch;
        _logger = logger;
    }

    public SemanticSearchStatusResponse GetStatus()
    {
        string configuredBackend = NormalizeBackend(_options.Backend);
        string effectiveBackend = ShouldUseUSearch() ? "usearch" : "managed";
        string? reason = _provider.Info.Reason;
        bool ready = _options.Enabled && _provider.Info.Ready;

        if (effectiveBackend == "usearch")
        {
            string? usearchReason = _usearch.RuntimeFailure;
            if (!USearchSemanticIndexRegistry.IsSupportedPlatform)
                usearchReason ??= "USearch NuGet 未提供当前 OS/CPU 的原生资产。";
            if (usearchReason is not null)
            {
                if (_options.FallbackToManaged || configuredBackend == "auto")
                    effectiveBackend = "managed";
                else
                    ready = false;
                reason = JoinReasons(reason, usearchReason);
            }
        }

        if (!_options.Enabled)
        {
            ready = false;
            reason = JoinReasons(reason, "语义图片检索未启用。");
        }

        return new SemanticSearchStatusResponse(
            _options.Enabled,
            ready,
            _provider.Info.Name,
            _provider.Info.Profile,
            _provider.Info.Dimensions,
            configuredBackend,
            effectiveBackend,
            ["text-embedding", "image-embedding", "text-to-image", "image-to-image"],
            reason);
    }

    public async Task<ImageIngestResponse> IngestAsync(
        string database,
        Tsdb tsdb,
        string id,
        ReadOnlyMemory<byte> image,
        string contentType,
        string? fileName,
        string? sourceUri,
        CancellationToken cancellationToken)
    {
        EnsureReady();
        ValidateId(id);
        ValidateContentType(contentType);
        if (image.IsEmpty || image.Length > _options.MaxImageBytes)
            throw new ArgumentOutOfRangeException(nameof(image), $"图片大小必须在 1 到 {_options.MaxImageBytes} 字节之间。");

        float[] embedding = await _provider.EmbedImageAsync(image, cancellationToken).ConfigureAwait(false);
        ValidateEmbedding(embedding);
        string sha256 = Convert.ToHexString(SHA256.HashData(image.Span)).ToLowerInvariant();
        string profileKey = ProfileKey();
        string objectKey = $"profiles/{profileKey}/{HashText(id)}/{sha256}";
        var gate = _databaseGates.GetOrAdd(database, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var store = EnsureCollection(tsdb);
            SemanticImageDocument? previous = ReadDocument(store.Get(id));
            var objectStore = new SndbObjectStore(tsdb);
            objectStore.CreateBucket(ObjectBucket, "semantic-image-source");

            SndbObjectInfo? storedObject = objectStore.HeadObject(ObjectBucket, objectKey);
            bool objectCreated = storedObject is null;
            if (objectCreated)
            {
                using var content = new MemoryStream(image.ToArray(), writable: false);
                storedObject = await objectStore.PutObjectAsync(
                    ObjectBucket,
                    objectKey,
                    content,
                    contentType,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            var document = new SemanticImageDocument(
                id,
                objectKey,
                NormalizeOptional(fileName),
                contentType,
                image.Length,
                sha256,
                NormalizeOptional(sourceUri),
                _provider.Info.Profile,
                _provider.Info.Dimensions,
                embedding,
                previous?.CreatedUtc ?? now,
                now,
                ObjectBucket,
                storedObject?.VersionId);

            try
            {
                store.Upsert(id, JsonSerializer.Serialize(document, ServerJsonContext.Default.SemanticImageDocument));
            }
            catch
            {
                if (objectCreated)
                    objectStore.DeleteObject(ObjectBucket, objectKey);
                throw;
            }

            try
            {
                UpdateUSearch(database, store, id, embedding);
            }
            catch
            {
                // 显式 usearch 且禁止回退时，原生写失败必须把权威文档恢复到请求前状态。
                if (previous is null)
                {
                    store.Delete(id);
                    CompensateUSearchRemove(database, store, id);
                }
                else
                {
                    store.Upsert(previous.Id, JsonSerializer.Serialize(previous, ServerJsonContext.Default.SemanticImageDocument));
                    CompensateUSearchUpsert(database, store, previous.Id, previous.Embedding);
                }
                if (objectCreated)
                    objectStore.DeleteObject(ObjectBucket, objectKey);
                throw;
            }
            if (previous is not null && !string.Equals(previous.ObjectKey, objectKey, StringComparison.Ordinal))
            {
                try
                {
                    objectStore.DeleteObject(ObjectBucket, previous.ObjectKey);
                }
                catch (Exception ex) when (ex is IOException or SndbObjectStorageException)
                {
                    _logger.LogWarning(ex, "Failed to delete superseded semantic image object {ObjectKey}.", previous.ObjectKey);
                }
            }

            return new ImageIngestResponse(
                document.Id,
                document.FileName,
                document.ContentType,
                document.SizeBytes,
                document.Sha256,
                document.Profile,
                document.Dimensions,
                document.CreatedUtc,
                document.UpdatedUtc);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ImageSearchResponse> SearchTextAsync(
        string database,
        Tsdb tsdb,
        string text,
        int? topK,
        double? minScore,
        ImageSearchFilter? filter,
        bool explain,
        CancellationToken cancellationToken)
    {
        EnsureReady();
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ValidateMinScore(minScore);
        float[] query = await _provider.EmbedTextAsync(text, cancellationToken).ConfigureAwait(false);
        ValidateEmbedding(query);
        return Search(
            database,
            tsdb,
            "text",
            query,
            NormalizeTopK(topK),
            minScore,
            filter,
            explain,
            excludedId: null,
            cancellationToken);
    }

    public async Task<ImageIngestResponse> IndexStoredObjectAsync(
        string database,
        Tsdb tsdb,
        string id,
        SndbObjectInfo source,
        ReadOnlyMemory<byte> image,
        string? thumbnailBucket,
        string? thumbnailKey,
        CancellationToken cancellationToken)
    {
        EnsureReady();
        ValidateId(id);
        ArgumentNullException.ThrowIfNull(source);
        ValidateContentType(source.ContentType);
        if (image.IsEmpty || image.Length > _options.MaxImageBytes)
            throw new ArgumentOutOfRangeException(nameof(image), $"图片大小必须在 1 到 {_options.MaxImageBytes} 字节之间。");

        float[] embedding = await _provider.EmbedImageAsync(image, cancellationToken).ConfigureAwait(false);
        ValidateEmbedding(embedding);
        var gate = _databaseGates.GetOrAdd(database, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var store = EnsureCollection(tsdb);
            SemanticImageDocument? previous = ReadDocument(store.Get(id));
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var document = new SemanticImageDocument(
                id,
                source.Key,
                Path.GetFileName(source.Key),
                source.ContentType,
                source.SizeBytes,
                source.Sha256,
                $"s3://{source.Bucket}/{source.Key}",
                _provider.Info.Profile,
                _provider.Info.Dimensions,
                embedding,
                previous?.CreatedUtc ?? now,
                now,
                source.Bucket,
                source.VersionId,
                thumbnailBucket,
                thumbnailKey,
                CopyMap(source.Metadata),
                CopyMap(source.Tags));

            store.Upsert(id, JsonSerializer.Serialize(document, ServerJsonContext.Default.SemanticImageDocument));
            try
            {
                UpdateUSearch(database, store, id, embedding);
            }
            catch
            {
                if (previous is null)
                {
                    store.Delete(id);
                    CompensateUSearchRemove(database, store, id);
                }
                else
                {
                    store.Upsert(previous.Id, JsonSerializer.Serialize(previous, ServerJsonContext.Default.SemanticImageDocument));
                    CompensateUSearchUpsert(database, store, previous.Id, previous.Embedding);
                }
                throw;
            }

            if (previous?.ThumbnailKey is not null
                && !string.Equals(previous.ThumbnailKey, thumbnailKey, StringComparison.Ordinal))
            {
                try
                {
                    var objectStore = new SndbObjectStore(tsdb);
                    if (previous.ThumbnailBucket is not null
                        && objectStore.GetBucket(previous.ThumbnailBucket) is not null
                        && objectStore.HeadObject(previous.ThumbnailBucket, previous.ThumbnailKey) is not null)
                    {
                        objectStore.DeleteObject(previous.ThumbnailBucket, previous.ThumbnailKey);
                    }
                }
                catch (Exception ex) when (ex is IOException or SndbObjectStorageException)
                {
                    _logger.LogWarning(ex, "Failed to delete superseded semantic thumbnail {ThumbnailKey}.", previous.ThumbnailKey);
                }
            }

            return new ImageIngestResponse(
                document.Id,
                document.FileName,
                document.ContentType,
                document.SizeBytes,
                document.Sha256,
                document.Profile,
                document.Dimensions,
                document.CreatedUtc,
                document.UpdatedUtc);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ImageSearchResponse> SearchImageAsync(
        string database,
        Tsdb tsdb,
        ReadOnlyMemory<byte> image,
        int? topK,
        double? minScore,
        ImageSearchFilter? filter,
        bool explain,
        CancellationToken cancellationToken)
    {
        EnsureReady();
        ValidateMinScore(minScore);
        if (image.IsEmpty || image.Length > _options.MaxImageBytes)
            throw new ArgumentOutOfRangeException(nameof(image), $"图片大小必须在 1 到 {_options.MaxImageBytes} 字节之间。");
        float[] query = await _provider.EmbedImageAsync(image, cancellationToken).ConfigureAwait(false);
        ValidateEmbedding(query);
        return Search(
            database,
            tsdb,
            "image",
            query,
            NormalizeTopK(topK),
            minScore,
            filter,
            explain,
            excludedId: null,
            cancellationToken);
    }

    public ValueTask<ImageSearchResponse?> SearchSimilarAsync(
        string database,
        Tsdb tsdb,
        string id,
        int? topK,
        double? minScore,
        ImageSearchFilter? filter,
        bool explain,
        CancellationToken cancellationToken)
    {
        EnsureReady();
        ValidateId(id);
        ValidateMinScore(minScore);
        var schema = tsdb.Documents.Catalog.TryGet(CollectionName());
        if (schema is null)
            return ValueTask.FromResult<ImageSearchResponse?>(null);

        var document = ReadDocument(tsdb.Documents.Open(schema.Name).Get(id));
        if (document is null
            || !string.Equals(document.Profile, _provider.Info.Profile, StringComparison.Ordinal))
        {
            return ValueTask.FromResult<ImageSearchResponse?>(null);
        }

        ValidateEmbedding(document.Embedding);
        return ValueTask.FromResult<ImageSearchResponse?>(Search(
            database,
            tsdb,
            "image",
            document.Embedding,
            NormalizeTopK(topK),
            minScore,
            filter,
            explain,
            document.Id,
            cancellationToken));
    }

    public ImageInfoResponse? GetInfo(string database, DocumentCollectionManager manager, string id)
    {
        _ = database;
        ValidateId(id);
        var schema = manager.Catalog.TryGet(CollectionName());
        if (schema is null)
            return null;
        var document = ReadDocument(manager.Open(schema.Name).Get(id));
        return document is null ? null : ToInfo(database, document);
    }

    public SndbObjectReadResult? OpenContent(Tsdb tsdb, string id)
    {
        ValidateId(id);
        var schema = tsdb.Documents.Catalog.TryGet(CollectionName());
        if (schema is null)
            return null;
        var document = ReadDocument(tsdb.Documents.Open(schema.Name).Get(id));
        return document is null
            ? null
            : new SndbObjectStore(tsdb).OpenRead(
                document.ObjectBucket ?? ObjectBucket,
                document.ObjectKey,
                range: null,
                versionId: document.ObjectVersionId);
    }

    public SndbObjectReadResult? OpenThumbnail(Tsdb tsdb, string id)
    {
        ValidateId(id);
        var schema = tsdb.Documents.Catalog.TryGet(CollectionName());
        if (schema is null)
            return null;
        var document = ReadDocument(tsdb.Documents.Open(schema.Name).Get(id));
        if (document?.ThumbnailBucket is null || document.ThumbnailKey is null)
            return null;
        return new SndbObjectStore(tsdb).OpenRead(document.ThumbnailBucket, document.ThumbnailKey);
    }

    public async Task<bool> DeleteAsync(string database, Tsdb tsdb, string id, CancellationToken cancellationToken)
        => await DeleteCoreAsync(
            database,
            tsdb,
            id,
            expectedSource: null,
            cancellationToken).ConfigureAwait(false);

    public async Task<bool> DeleteStoredObjectVersionAsync(
        string database,
        Tsdb tsdb,
        string id,
        string bucket,
        string key,
        string versionId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);
        return await DeleteCoreAsync(
            database,
            tsdb,
            id,
            new StoredObjectIdentity(bucket, key, versionId),
            cancellationToken).ConfigureAwait(false);
    }

    public bool IsStoredObjectIndexed(Tsdb tsdb, string id, SndbObjectInfo source)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(source);
        return IsStoredObjectVersionIndexed(
            tsdb,
            id,
            source.Bucket,
            source.Key,
            source.VersionId);
    }

    public bool IsStoredObjectVersionIndexed(
        Tsdb tsdb,
        string id,
        string bucket,
        string key,
        string versionId)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ValidateId(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);
        var schema = tsdb.Documents.Catalog.TryGet(CollectionName());
        if (schema is null)
            return false;
        var document = ReadDocument(tsdb.Documents.Open(schema.Name).Get(id));
        return document is not null
            && MatchesSource(document, new StoredObjectIdentity(
                bucket,
                key,
                versionId));
    }

    private async Task<bool> DeleteCoreAsync(
        string database,
        Tsdb tsdb,
        string id,
        StoredObjectIdentity? expectedSource,
        CancellationToken cancellationToken)
    {
        ValidateId(id);
        var gate = _databaseGates.GetOrAdd(database, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var schema = tsdb.Documents.Catalog.TryGet(CollectionName());
            if (schema is null)
                return false;
            var store = tsdb.Documents.Open(schema.Name);
            var document = ReadDocument(store.Get(id));
            if (document is null
                || expectedSource is not null && !MatchesSource(document, expectedSource.Value)
                || !store.Delete(id))
                return false;

            try
            {
                RemoveFromUSearch(database, store, id);
                if (document.ObjectBucket is null
                    || string.Equals(document.ObjectBucket, ObjectBucket, StringComparison.Ordinal))
                {
                    new SndbObjectStore(tsdb).DeleteObject(ObjectBucket, document.ObjectKey);
                }
                return true;
            }
            catch
            {
                // legal hold、retention 或显式 USearch 失败时恢复目录记录，避免半删除。
                store.Upsert(id, JsonSerializer.Serialize(document, ServerJsonContext.Default.SemanticImageDocument));
                CompensateUSearchUpsert(database, store, id, document.Embedding);
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose()
    {
        foreach (var gate in _databaseGates.Values)
            gate.Dispose();
        _databaseGates.Clear();
    }

    private ImageSearchResponse Search(
        string database,
        Tsdb tsdb,
        string queryKind,
        float[] query,
        int topK,
        double? minScore,
        ImageSearchFilter? filter,
        bool explain,
        string? excludedId,
        CancellationToken cancellationToken)
    {
        var store = EnsureCollection(tsdb);
        var vectorIndex = store.Schema.TryGetVectorIndex(VectorIndexName)
            ?? throw new InvalidOperationException("语义图片集合缺少向量索引。");

        if (HasFilter(filter))
        {
            return SearchFilteredExact(
                database,
                store,
                queryKind,
                query,
                topK,
                minScore,
                filter!,
                explain,
                excludedId,
                cancellationToken);
        }

        string backend = "managed";
        IReadOnlyList<(string Id, double Distance)> candidates;
        int candidateCount = Math.Min(Math.Max(topK * 4, topK), _options.MaxTopK * 4);
        if (ShouldUseUSearch())
        {
            if (_usearch.TrySearch(
                    database,
                    store,
                    _provider.Info.Dimensions,
                    query,
                    candidateCount,
                    out candidates,
                    out string? error))
            {
                backend = "usearch";
            }
            else if (_options.FallbackToManaged || NormalizeBackend(_options.Backend) == "auto")
            {
                _logger.LogDebug("USearch unavailable for database {Database}: {Reason}", database, error);
                candidates = store.SearchVector(vectorIndex, query, candidateCount);
            }
            else
            {
                throw new InvalidOperationException(error ?? "USearch 后端不可用。");
            }
        }
        else
        {
            candidates = store.SearchVector(vectorIndex, query, candidateCount);
        }

        var hits = new List<ImageSearchHit>(topK);
        int filteredCandidateCount = 0;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = ReadDocument(store.Get(candidate.Id));
            if (document is null
                || !string.Equals(document.Profile, _provider.Info.Profile, StringComparison.Ordinal)
                || string.Equals(document.Id, excludedId, StringComparison.Ordinal))
            {
                continue;
            }

            filteredCandidateCount++;
            double score = Math.Clamp(1d - candidate.Distance, -1d, 1d);
            if (minScore is not null && score < minScore.Value)
                continue;
            if (hits.Count < topK)
                hits.Add(ToHit(database, document, score, candidate.Distance));
            if (!explain && hits.Count == topK)
                break;
        }

        return new ImageSearchResponse(queryKind, _provider.Info.Profile, backend, hits)
        {
            SearchMode = explain ? "ann" : null,
            CandidateCount = explain ? candidates.Count : null,
            FilteredCandidateCount = explain ? filteredCandidateCount : null,
        };
    }

    private ImageSearchResponse SearchFilteredExact(
        string database,
        DocumentCollectionStore store,
        string queryKind,
        float[] query,
        int topK,
        double? minScore,
        ImageSearchFilter filter,
        bool explain,
        string? excludedId,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<DocumentRow> rows = store.Scan();
        var matches = new PriorityQueue<(SemanticImageDocument Document, double Distance), double>(topK);
        int filteredCandidateCount = 0;
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = ReadDocument(row);
            if (document is null
                || !string.Equals(document.Profile, _provider.Info.Profile, StringComparison.Ordinal)
                || string.Equals(document.Id, excludedId, StringComparison.Ordinal)
                || document.Embedding.Length != query.Length
                || !MatchesFilter(document, filter))
            {
                continue;
            }

            filteredCandidateCount++;
            double distance = Distance.Cosine(query, document.Embedding);
            double score = Math.Clamp(1d - distance, -1d, 1d);
            if (minScore is not null && score < minScore.Value)
                continue;

            double priority = -distance;
            if (matches.Count < topK)
            {
                matches.Enqueue((document, distance), priority);
            }
            else if (matches.TryPeek(out _, out double worstPriority) && priority > worstPriority)
            {
                _ = matches.Dequeue();
                matches.Enqueue((document, distance), priority);
            }
        }

        var orderedMatches = new List<(SemanticImageDocument Document, double Distance)>(matches.Count);
        while (matches.TryDequeue(out var match, out _))
            orderedMatches.Add(match);
        orderedMatches.Sort(static (left, right) =>
        {
            int distanceComparison = left.Distance.CompareTo(right.Distance);
            return distanceComparison != 0
                ? distanceComparison
                : string.CompareOrdinal(left.Document.Id, right.Document.Id);
        });

        int hitCount = orderedMatches.Count;
        var hits = new List<ImageSearchHit>(hitCount);
        for (int i = 0; i < hitCount; i++)
        {
            var match = orderedMatches[i];
            hits.Add(ToHit(
                database,
                match.Document,
                Math.Clamp(1d - match.Distance, -1d, 1d),
                match.Distance));
        }

        return new ImageSearchResponse(queryKind, _provider.Info.Profile, "exact-filtered", hits)
        {
            SearchMode = explain ? "exact-filtered" : null,
            CandidateCount = explain ? rows.Count : null,
            FilteredCandidateCount = explain ? filteredCandidateCount : null,
        };
    }

    private DocumentCollectionStore EnsureCollection(Tsdb tsdb)
    {
        string collectionName = CollectionName();
        var schema = tsdb.Documents.Catalog.TryGet(collectionName);
        if (schema is null)
        {
            try
            {
                tsdb.Documents.Create(DocumentCollectionSchema.Create(
                    collectionName,
                    vectorIndexes:
                    [
                        new DocumentVectorIndexDefinition(
                            VectorIndexName,
                            "$.embedding",
                            _provider.Info.Dimensions,
                            KnnMetric.Cosine),
                    ]));
            }
            catch (InvalidOperationException) when (tsdb.Documents.Catalog.TryGet(collectionName) is not null)
            {
                // 并发首写时另一请求已完成创建，继续打开既有集合。
            }
            schema = tsdb.Documents.Catalog.TryGet(collectionName);
        }

        if (schema is null)
            throw new InvalidOperationException("无法创建语义图片集合。");
        var index = schema.TryGetVectorIndex(VectorIndexName)
            ?? throw new InvalidOperationException("既有语义图片集合缺少 embedding 向量索引。");
        if (index.Dimensions != _provider.Info.Dimensions)
        {
            throw new InvalidOperationException(
                $"语义图片集合维度为 {index.Dimensions}，provider 输出维度为 {_provider.Info.Dimensions}。");
        }
        return tsdb.Documents.Open(collectionName);
    }

    private void UpdateUSearch(string database, DocumentCollectionStore store, string id, float[] embedding)
    {
        if (!ShouldUseUSearch())
            return;
        if (!_usearch.TryUpsert(database, store, _provider.Info.Dimensions, id, embedding, out string? error)
            && !_options.FallbackToManaged
            && NormalizeBackend(_options.Backend) != "auto")
        {
            throw new InvalidOperationException(error ?? "USearch 后端不可用。");
        }
    }

    private void RemoveFromUSearch(string database, DocumentCollectionStore store, string id)
    {
        if (!ShouldUseUSearch())
            return;
        if (!_usearch.TryRemove(database, store, _provider.Info.Dimensions, id, out string? error)
            && !_options.FallbackToManaged
            && NormalizeBackend(_options.Backend) != "auto")
        {
            throw new InvalidOperationException(error ?? "USearch 后端不可用。");
        }
    }

    private void CompensateUSearchUpsert(
        string database,
        DocumentCollectionStore store,
        string id,
        float[] embedding)
    {
        if (ShouldUseUSearch())
            _ = _usearch.TryUpsert(database, store, _provider.Info.Dimensions, id, embedding, out _);
    }

    private void CompensateUSearchRemove(string database, DocumentCollectionStore store, string id)
    {
        if (ShouldUseUSearch())
            _ = _usearch.TryRemove(database, store, _provider.Info.Dimensions, id, out _);
    }

    private void EnsureReady()
    {
        var status = GetStatus();
        if (!status.Ready)
            throw new InvalidOperationException(status.Reason ?? "语义图片检索未就绪。");
    }

    private void ValidateEmbedding(float[] embedding)
    {
        if (embedding.Length != _provider.Info.Dimensions)
        {
            throw new InvalidDataException(
                $"Provider 返回 {embedding.Length} 维向量，profile 声明 {_provider.Info.Dimensions} 维。");
        }
    }

    private int NormalizeTopK(int? topK)
    {
        int value = topK ?? _options.DefaultTopK;
        if (value <= 0 || value > _options.MaxTopK)
            throw new ArgumentOutOfRangeException(nameof(topK), $"topK 必须在 1 到 {_options.MaxTopK} 之间。");
        return value;
    }

    private static void ValidateMinScore(double? minScore)
    {
        if (minScore is < -1d or > 1d || double.IsNaN(minScore ?? 0d))
            throw new ArgumentOutOfRangeException(nameof(minScore), "minScore 必须在 -1 到 1 之间。");
    }

    private static void ValidateId(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (id.Length > 256)
            throw new ArgumentOutOfRangeException(nameof(id), "图片 id 不能超过 256 个字符。");
        if (id.IndexOfAny(['/', '\\']) >= 0)
            throw new ArgumentException("图片 id 不能包含路径分隔符。", nameof(id));
    }

    private static void ValidateContentType(string contentType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Content-Type 必须是 image/*。", nameof(contentType));
    }

    private static string NormalizeBackend(string? backend)
        => backend?.Trim().ToLowerInvariant() switch
        {
            "usearch" => "usearch",
            "managed" => "managed",
            _ => "auto",
        };

    private bool ShouldUseUSearch()
    {
        string backend = NormalizeBackend(_options.Backend);
        return backend == "usearch"
            || backend == "auto" && USearchSemanticIndexRegistry.IsSupportedPlatform;
    }

    private string CollectionName() => "__semantic_images_" + ProfileKey();

    private string ProfileKey() => HashText(_provider.Info.Profile)[..16];

    private static string HashText(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static SemanticImageDocument? ReadDocument(DocumentRow? row)
        => row is null
            ? null
            : JsonSerializer.Deserialize(row.Json, ServerJsonContext.Default.SemanticImageDocument);

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string JoinReasons(string? left, string right)
        => string.IsNullOrWhiteSpace(left) ? right : left + " " + right;

    private static Dictionary<string, string>? CopyMap(IReadOnlyDictionary<string, string> values)
    {
        if (values.Count == 0)
            return null;

        var result = new Dictionary<string, string>(values.Count, StringComparer.Ordinal);
        foreach (var pair in values)
            result[pair.Key] = pair.Value;
        return result;
    }

    private static bool HasFilter(ImageSearchFilter? filter)
        => filter is not null
            && (!string.IsNullOrWhiteSpace(filter.SourceBucket)
                || !string.IsNullOrWhiteSpace(filter.SourceKeyPrefix)
                || !string.IsNullOrWhiteSpace(filter.ContentType)
                || filter.Metadata is { Count: > 0 }
                || filter.Tags is { Count: > 0 });

    private static bool MatchesFilter(SemanticImageDocument document, ImageSearchFilter filter)
    {
        string? sourceBucket = ExternalSourceBucket(document);
        if (!string.IsNullOrWhiteSpace(filter.SourceBucket)
            && !string.Equals(sourceBucket, filter.SourceBucket.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(filter.SourceKeyPrefix)
            && (sourceBucket is null
                || !document.ObjectKey.StartsWith(filter.SourceKeyPrefix.Trim(), StringComparison.Ordinal)))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(filter.ContentType)
            && !string.Equals(document.ContentType, filter.ContentType.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return MatchesMap(document.Metadata, filter.Metadata)
            && MatchesMap(document.Tags, filter.Tags);
    }

    private static bool MatchesMap(
        IReadOnlyDictionary<string, string>? actual,
        IReadOnlyDictionary<string, string>? expected)
    {
        if (expected is null || expected.Count == 0)
            return true;
        if (actual is null || actual.Count < expected.Count)
            return false;

        foreach (var pair in expected)
        {
            if (!actual.TryGetValue(pair.Key, out string? value)
                || !string.Equals(value, pair.Value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string ContentUrl(string database, string id)
        => $"/v1/db/{Uri.EscapeDataString(database)}/images/{Uri.EscapeDataString(id)}/content";

    private static ImageSearchHit ToHit(
        string database,
        SemanticImageDocument document,
        double score,
        double distance)
        => new(
            document.Id,
            score,
            distance,
            document.FileName,
            document.ContentType,
            document.SizeBytes,
            document.Sha256,
            document.SourceUri,
            ContentUrl(database, document.Id),
            document.UpdatedUtc)
        {
            SourceBucket = ExternalSourceBucket(document),
            SourceKey = ExternalSourceBucket(document) is null ? null : document.ObjectKey,
            SourceVersionId = document.ObjectVersionId,
            ThumbnailUrl = ThumbnailUrl(database, document),
            Metadata = document.Metadata,
            Tags = document.Tags,
        };

    private static ImageInfoResponse ToInfo(string database, SemanticImageDocument document)
        => new(
            document.Id,
            document.FileName,
            document.ContentType,
            document.SizeBytes,
            document.Sha256,
            document.SourceUri,
            document.Profile,
            document.Dimensions,
            ContentUrl(database, document.Id),
            document.CreatedUtc,
            document.UpdatedUtc)
        {
            SourceBucket = ExternalSourceBucket(document),
            SourceKey = ExternalSourceBucket(document) is null ? null : document.ObjectKey,
            SourceVersionId = document.ObjectVersionId,
            ThumbnailUrl = ThumbnailUrl(database, document),
            Metadata = document.Metadata,
            Tags = document.Tags,
        };

    private static string? ExternalSourceBucket(SemanticImageDocument document)
        => string.Equals(document.ObjectBucket, ObjectBucket, StringComparison.Ordinal)
            ? null
            : document.ObjectBucket;

    private static string? ThumbnailUrl(string database, SemanticImageDocument document)
        => document.ThumbnailKey is null
            ? null
            : $"/v1/db/{Uri.EscapeDataString(database)}/images/{Uri.EscapeDataString(document.Id)}/thumbnail";

    private static bool MatchesSource(
        SemanticImageDocument document,
        StoredObjectIdentity source)
        => string.Equals(document.ObjectBucket, source.Bucket, StringComparison.Ordinal)
            && string.Equals(document.ObjectKey, source.Key, StringComparison.Ordinal)
            && string.Equals(document.ObjectVersionId, source.VersionId, StringComparison.Ordinal);

    private readonly record struct StoredObjectIdentity(string Bucket, string Key, string VersionId);
}
