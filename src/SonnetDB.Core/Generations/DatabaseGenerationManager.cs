using System.Globalization;
using System.Text;
using SonnetDB.Documents;
using SonnetDB.Kv;

namespace SonnetDB.Generations;

/// <summary>
/// 管理跨 KV、Document 与 FullText 资源的原子 generation 发布、查询租约和延迟清理。
/// </summary>
/// <remarks>
/// generation 资源必须在发布前使用独立物理名称完成构建。发布只写入一个内部 durable active
/// 指针，不暴露 staging，也不要求调用方维护第二份提交日志。
/// </remarks>
public sealed class DatabaseGenerationManager
{
    private const int MaximumResourceCount = 256;
    private const int MaximumTextBytes = 4096;
    private const string CatalogKeyspaceName = "database-generations";
    private static ReadOnlySpan<byte> DescriptorPrefix => "generation:"u8;
    private static ReadOnlySpan<byte> ActivePrefix => "active:"u8;
    private static readonly UTF8Encoding _strictUtf8 = new(false, true);

    private readonly object _sync = new();
    private readonly object _schemaSync;
    private readonly KvKeyspaceManager _keyspaces;
    private readonly DocumentCollectionManager _documents;
    private readonly KvKeyspace _catalog;
    private readonly Dictionary<string, SortedDictionary<long, PersistedGeneration>> _generations =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, ActiveGeneration> _active = new(StringComparer.Ordinal);
    private readonly Dictionary<LeaseKey, int> _leaseCounts = [];
    private bool _disposed;

    internal DatabaseGenerationManager(
        string rootDirectory,
        KvOptions options,
        KvKeyspaceManager keyspaces,
        DocumentCollectionManager documents,
        object synchronizationRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(keyspaces);
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(synchronizationRoot);

        _keyspaces = keyspaces;
        _documents = documents;
        _schemaSync = synchronizationRoot;
        _catalog = KvKeyspace.Open(
            CatalogKeyspaceName,
            rootDirectory,
            options with
            {
                SyncWalOnEveryWrite = true,
                ExpirerEnabled = false,
                CleanupEnabled = false,
            });
        try
        {
            LoadCatalog();
        }
        catch
        {
            _catalog.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 原子发布一个已经完整构建的 generation，并返回新 active generation。
    /// </summary>
    /// <param name="request">发布参数与 generation 独占资源。</param>
    /// <param name="cancellationToken">内部 active 指针写入前可取消的令牌。</param>
    /// <returns>成功发布的新 active generation。</returns>
    /// <exception cref="DatabaseGenerationException">revision、资源或持久化合同不满足时抛出。</exception>
    public DatabaseGeneration Publish(
        DatabaseGenerationPublishRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidatePublishRequest(request);

        lock (_schemaSync)
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                cancellationToken.ThrowIfCancellationRequested();

                long currentRevision = _active.TryGetValue(request.Stream, out ActiveGeneration? active)
                    ? active.Generation.Revision
                    : 0;
                if (currentRevision != request.ExpectedRevision)
                    throw RevisionConflict(request.Stream, request.ExpectedRevision, currentRevision);

                long nextRevision;
                try
                {
                    nextRevision = checked(request.ExpectedRevision + 1);
                }
                catch (OverflowException exception)
                {
                    throw new DatabaseGenerationException(
                        DatabaseGenerationErrorCodes.RevisionConflict,
                        $"generation stream '{request.Stream}' 的 revision 已耗尽。",
                        exception);
                }

                DatabaseGeneration generation = CreateGeneration(request, nextRevision);
                byte[] descriptorKey = DescriptorKey(generation.Stream, generation.Revision);
                byte[] activeKey = ActiveKey(generation.Stream);
                byte[][] ownerKeys = generation.Resources.Select(OwnerKey).ToArray();

                EnsureIdentityAndResourcesAvailable(generation, descriptorKey, ownerKeys);
                CheckpointResources(generation, cancellationToken);
                BeforePublishTestHook?.Invoke(generation);
                cancellationToken.ThrowIfCancellationRequested();

                var mutations = new List<KvBatchMutation>(ownerKeys.Length + 2)
                {
                    KvBatchMutation.Put(descriptorKey, DatabaseGenerationCodec.Encode(generation)),
                    KvBatchMutation.Put(activeKey, DatabaseGenerationCodec.EncodeRevision(generation.Revision)),
                };
                byte[] ownerValue = OwnershipValue(generation);
                foreach (byte[] ownerKey in ownerKeys)
                    mutations.Add(KvBatchMutation.Put(ownerKey, ownerValue));

                long activeVersion = active?.Version ?? 0;
                var preconditions = new List<KvBatchPrecondition>(ownerKeys.Length + 2)
                {
                    KvBatchPrecondition.KeyVersion(activeKey, activeVersion),
                    KvBatchPrecondition.KeyVersion(descriptorKey, 0),
                };
                foreach (byte[] ownerKey in ownerKeys)
                    preconditions.Add(KvBatchPrecondition.KeyVersion(ownerKey, 0));

                KvConditionalBatchResult result = _catalog.ApplyConditionalBatch(
                    mutations,
                    preconditions,
                    cancellationToken);
                if (!result.Applied)
                {
                    if (result.FailedPreconditionIndex == 0)
                    {
                        long observed = TryReadActiveRevision(generation.Stream) ?? 0;
                        throw RevisionConflict(generation.Stream, request.ExpectedRevision, observed);
                    }

                    throw new DatabaseGenerationException(
                        DatabaseGenerationErrorCodes.ResourceConflict,
                        "generation 身份或物理资源已由另一个发布占用。");
                }

                var persisted = new PersistedGeneration(generation, result.Sequence);
                GetOrCreateStream(generation.Stream).Add(generation.Revision, persisted);
                _active[generation.Stream] = new ActiveGeneration(generation, result.Sequence);
                AfterPublishTestHook?.Invoke(generation);
                return generation;
            }
        }
    }

    /// <summary>
    /// 在查询开始时租用指定 stream 的 active generation。租约释放前，该 revision 不会被清理。
    /// </summary>
    /// <param name="stream">generation stream 名称。</param>
    /// <returns>固定 active revision 与资源描述的查询租约。</returns>
    /// <exception cref="DatabaseGenerationException">stream 尚无 active generation 时抛出。</exception>
    public DatabaseGenerationQueryLease AcquireActive(string stream)
    {
        ValidateText(stream, nameof(stream));
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!_active.TryGetValue(stream, out ActiveGeneration? active))
            {
                throw new DatabaseGenerationException(
                    DatabaseGenerationErrorCodes.NoActiveGeneration,
                    $"generation stream '{stream}' 尚无 active generation。");
            }

            var key = new LeaseKey(stream, active.Generation.Revision);
            _leaseCounts.TryGetValue(key, out int count);
            _leaseCounts[key] = checked(count + 1);
            return new DatabaseGenerationQueryLease(this, active.Generation);
        }
    }

    /// <summary>
    /// 租用指定 stream 中仍受 catalog 管理的 generation revision。
    /// </summary>
    /// <param name="stream">generation stream 名称。</param>
    /// <param name="revision">要固定的正数 generation revision。</param>
    /// <returns>固定指定 revision 与资源描述的查询租约。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="revision"/> 不是正数。</exception>
    /// <exception cref="DatabaseGenerationException">指定 revision 不存在或已经清理。</exception>
    /// <remarks>
    /// 获取租约与 retired generation 清理使用同一原子生命周期边界。方法返回后，该 revision
    /// 在租约释放前不会被清理；若清理先完成，则本方法稳定失败且不会暴露已删除资源。
    /// </remarks>
    public DatabaseGenerationQueryLease Acquire(string stream, long revision)
    {
        ValidateText(stream, nameof(stream));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(revision);
        lock (_schemaSync)
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                if (!_generations.TryGetValue(stream, out SortedDictionary<long, PersistedGeneration>? generations)
                    || !generations.TryGetValue(revision, out PersistedGeneration? persisted))
                {
                    throw RevisionUnavailable(stream, revision);
                }

                try
                {
                    ValidateResourcesExist(persisted.Generation);
                }
                catch (DatabaseGenerationException exception)
                    when (exception.Code == DatabaseGenerationErrorCodes.ResourceInvalid)
                {
                    throw RevisionUnavailable(stream, revision, exception);
                }

                var key = new LeaseKey(stream, revision);
                _leaseCounts.TryGetValue(key, out int count);
                _leaseCounts[key] = checked(count + 1);
                return new DatabaseGenerationQueryLease(this, persisted.Generation);
            }
        }
    }

    /// <summary>
    /// 枚举指定 stream 仍受 catalog 管理的 generation，按 revision 升序返回。
    /// </summary>
    /// <param name="stream">generation stream 名称。</param>
    /// <returns>active 及尚未清理的 retired generation 快照。</returns>
    public IReadOnlyList<DatabaseGeneration> List(string stream)
    {
        ValidateText(stream, nameof(stream));
        lock (_sync)
        {
            ThrowIfDisposed();
            if (!_generations.TryGetValue(stream, out SortedDictionary<long, PersistedGeneration>? generations))
                return Array.Empty<DatabaseGeneration>();
            return Array.AsReadOnly(generations.Values.Select(static item => item.Generation).ToArray());
        }
    }

    /// <summary>
    /// 删除指定 stream 中不再 active 且没有 query lease 的 generation 资源。
    /// </summary>
    /// <param name="stream">generation stream 名称。</param>
    /// <param name="cancellationToken">每个 generation 清理开始前可取消的令牌。</param>
    /// <returns>已删除和因租约延后的 revision。</returns>
    public DatabaseGenerationCleanupResult CleanupRetired(
        string stream,
        CancellationToken cancellationToken = default)
    {
        ValidateText(stream, nameof(stream));
        return CleanupRetiredCore(stream, publishedBeforeUtc: null, cancellationToken);
    }

    /// <summary>
    /// 删除指定 stream 中达到发布时间 cutoff、不再 active 且没有 query lease 的 generation 资源。
    /// </summary>
    /// <param name="stream">generation stream 名称。</param>
    /// <param name="options">按 durable 发布时间选择候选的清理选项。</param>
    /// <param name="cancellationToken">每个 generation 清理开始前可取消的令牌。</param>
    /// <returns>已删除、因租约延后和因 cutoff 未到期而保留的 revision。</returns>
    public DatabaseGenerationCleanupResult CleanupRetired(
        string stream,
        DatabaseGenerationCleanupOptions options,
        CancellationToken cancellationToken)
    {
        ValidateText(stream, nameof(stream));
        ArgumentNullException.ThrowIfNull(options);
        return CleanupRetiredCore(stream, options.PublishedBeforeUtc, cancellationToken);
    }

    private DatabaseGenerationCleanupResult CleanupRetiredCore(
        string stream,
        DateTimeOffset? publishedBeforeUtc,
        CancellationToken cancellationToken)
    {
        lock (_schemaSync)
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                var removed = new List<long>();
                var deferred = new List<long>();
                var retentionDeferred = new List<long>();
                if (!_generations.TryGetValue(stream, out SortedDictionary<long, PersistedGeneration>? generations))
                    return new DatabaseGenerationCleanupResult(removed, deferred, retentionDeferred);

                long activeRevision = _active.TryGetValue(stream, out ActiveGeneration? active)
                    ? active.Generation.Revision
                    : 0;
                PersistedGeneration[] candidates = generations.Values
                    .Where(item => item.Generation.Revision != activeRevision)
                    .ToArray();
                foreach (PersistedGeneration candidate in candidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_active.TryGetValue(stream, out active)
                        && candidate.Generation.Revision == active.Generation.Revision)
                    {
                        continue;
                    }

                    if (publishedBeforeUtc is DateTimeOffset cutoff
                        && candidate.Generation.PublishedAtUtc > cutoff)
                    {
                        retentionDeferred.Add(candidate.Generation.Revision);
                        continue;
                    }

                    var leaseKey = new LeaseKey(stream, candidate.Generation.Revision);
                    if (_leaseCounts.TryGetValue(leaseKey, out int count) && count != 0)
                    {
                        deferred.Add(candidate.Generation.Revision);
                        continue;
                    }

                    BeforeCleanupTestHook?.Invoke(candidate.Generation);
                    // Once a candidate starts, finish its physical and catalog deletion before
                    // observing cancellation again at the next candidate boundary.
                    DeletePhysicalResources(candidate.Generation);
                    AfterCleanupResourcesTestHook?.Invoke(candidate.Generation);
                    DeleteCatalogRecord(candidate);
                    generations.Remove(candidate.Generation.Revision);
                    removed.Add(candidate.Generation.Revision);
                }

                if (generations.Count == 0)
                    _generations.Remove(stream);
                return new DatabaseGenerationCleanupResult(removed, deferred, retentionDeferred);
            }
        }
    }

    internal Action<DatabaseGeneration>? BeforePublishTestHook { get; set; }

    internal Action<DatabaseGeneration>? AfterPublishTestHook { get; set; }

    internal Action<DatabaseGeneration>? BeforeCleanupTestHook { get; set; }

    internal Action<DatabaseGeneration>? AfterCleanupResourcesTestHook { get; set; }

    internal long CheckpointCatalog()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            return _catalog.CreateSnapshot();
        }
    }

    internal TResult ExecuteConsistentBackup<TResult>(Func<TResult> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_sync)
        {
            ThrowIfDisposed();
            _catalog.CreateSnapshot();
            return action();
        }
    }

    internal void Release(string stream, long revision)
    {
        lock (_sync)
        {
            var key = new LeaseKey(stream, revision);
            if (!_leaseCounts.TryGetValue(key, out int count))
                return;
            if (count <= 1)
                _leaseCounts.Remove(key);
            else
                _leaseCounts[key] = count - 1;
        }
    }

    internal void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            _catalog.Dispose();
        }
    }

    private void LoadCatalog()
    {
        using KvReadSnapshot snapshot = _catalog.AcquireReadSnapshot();
        foreach (KvEntry entry in ReadPrefix(snapshot, DescriptorPrefix))
        {
            DatabaseGeneration generation = DatabaseGenerationCodec.Decode(entry.Value.Span);
            if (!entry.Key.Span.SequenceEqual(DescriptorKey(generation.Stream, generation.Revision)))
                throw new InvalidDataException("generation catalog descriptor key 与内容不一致。");
            SortedDictionary<long, PersistedGeneration> stream = GetOrCreateStream(generation.Stream);
            if (!stream.TryAdd(generation.Revision, new PersistedGeneration(generation, entry.Version)))
                throw new InvalidDataException("generation catalog 含重复 revision。");

            byte[] expectedOwnerValue = OwnershipValue(generation);
            foreach (DatabaseGenerationResource resource in generation.Resources)
            {
                KvEntry? owner = snapshot.GetEntry(OwnerKey(resource));
                if (owner is null || !owner.Value.Span.SequenceEqual(expectedOwnerValue))
                    throw new InvalidDataException("generation catalog 资源 ownership 记录缺失或不一致。");
            }
        }

        foreach (KvEntry entry in ReadPrefix(snapshot, ActivePrefix))
        {
            string stream = DecodeNameKey(entry.Key.Span, ActivePrefix);
            long revision = DatabaseGenerationCodec.DecodeRevision(entry.Value.Span);
            if (!_generations.TryGetValue(stream, out SortedDictionary<long, PersistedGeneration>? generations)
                || !generations.TryGetValue(revision, out PersistedGeneration? generation))
            {
                throw new InvalidDataException("generation active 指针缺少对应的完整 descriptor。");
            }
            if (!_active.TryAdd(stream, new ActiveGeneration(generation.Generation, entry.Version)))
                throw new InvalidDataException("generation catalog 含重复 active stream。");
        }

        foreach (ActiveGeneration active in _active.Values)
            ValidateResourcesExist(active.Generation);
    }

    private static IReadOnlyList<KvEntry> ReadPrefix(KvReadSnapshot snapshot, ReadOnlySpan<byte> prefix)
    {
        using KvRangeCursor cursor = snapshot.OpenRangeCursor(new KvRangeScanOptions
        {
            Prefix = prefix.ToArray(),
            PageSize = 128,
        });
        var entries = new List<KvEntry>();
        while (!cursor.IsExhausted)
            entries.AddRange(cursor.ReadNextPage());
        return entries;
    }

    private void EnsureIdentityAndResourcesAvailable(
        DatabaseGeneration generation,
        byte[] descriptorKey,
        IReadOnlyList<byte[]> ownerKeys)
    {
        if (_catalog.GetEntry(descriptorKey) is not null)
        {
            throw new DatabaseGenerationException(
                DatabaseGenerationErrorCodes.ResourceConflict,
                $"generation '{generation.Stream}/{generation.Revision}' 已存在。");
        }
        if (_generations.TryGetValue(generation.Stream, out SortedDictionary<long, PersistedGeneration>? existing)
            && existing.Values.Any(item => string.Equals(
                item.Generation.GenerationId,
                generation.GenerationId,
                StringComparison.Ordinal)))
        {
            throw new DatabaseGenerationException(
                DatabaseGenerationErrorCodes.ResourceConflict,
                $"generation identity '{generation.GenerationId}' 已在 stream '{generation.Stream}' 中使用。");
        }

        for (int i = 0; i < ownerKeys.Count; i++)
        {
            if (_catalog.GetEntry(ownerKeys[i]) is not null)
            {
                throw new DatabaseGenerationException(
                    DatabaseGenerationErrorCodes.ResourceConflict,
                    $"generation 物理资源 '{generation.Resources[i].Name}' 已由其他 generation 占用。");
            }
        }
        ValidateResourcesExist(generation);
    }

    private void ValidateResourcesExist(DatabaseGeneration generation)
    {
        IReadOnlyList<string> keyspaceNames = _keyspaces.List();
        foreach (DatabaseGenerationResource resource in generation.Resources)
        {
            switch (resource.Kind)
            {
                case DatabaseGenerationResourceKind.KvKeyspace:
                    if (!keyspaceNames.Contains(resource.Name, StringComparer.Ordinal))
                        throw InvalidResource(resource, "KV keyspace 不存在。");
                    break;

                case DatabaseGenerationResourceKind.DocumentCollection:
                    if (_documents.Catalog.TryGet(resource.Name) is null)
                        throw InvalidResource(resource, "Document collection 不存在。");
                    break;

                case DatabaseGenerationResourceKind.DocumentFullTextIndex:
                    DocumentCollectionSchema? schema = _documents.Catalog.TryGet(resource.ParentName!);
                    if (schema?.TryGetFullTextIndex(resource.Name) is null)
                        throw InvalidResource(resource, "Document FullText index 不存在。");
                    break;

                default:
                    throw InvalidResource(resource, "资源类型不受支持。");
            }
        }

        foreach (DatabaseGenerationResource document in generation.Resources.Where(
            static resource => resource.Kind == DatabaseGenerationResourceKind.DocumentCollection))
        {
            DocumentCollectionSchema schema = _documents.Catalog.TryGet(document.Name)!;
            foreach (DocumentFullTextIndex index in schema.FullTextIndexes)
            {
                bool declared = generation.Resources.Any(resource =>
                    resource.Kind == DatabaseGenerationResourceKind.DocumentFullTextIndex
                    && string.Equals(resource.ParentName, document.Name, StringComparison.Ordinal)
                    && string.Equals(resource.Name, index.Name, StringComparison.Ordinal));
                if (!declared)
                {
                    throw InvalidResource(
                        document,
                        $"FullText index '{index.Name}' 未作为 generation 资源显式声明。");
                }
            }
        }
    }

    private void CheckpointResources(DatabaseGeneration generation, CancellationToken cancellationToken)
    {
        try
        {
            foreach (DatabaseGenerationResource resource in generation.Resources
                .Where(static resource => resource.Kind == DatabaseGenerationResourceKind.KvKeyspace)
                .OrderBy(static resource => resource.Name, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                _keyspaces.Open(resource.Name).CreateSnapshot();
            }

            foreach (DatabaseGenerationResource resource in generation.Resources
                .Where(static resource => resource.Kind == DatabaseGenerationResourceKind.DocumentCollection)
                .OrderBy(static resource => resource.Name, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                DocumentCollectionStore store = _documents.Open(resource.Name);
                DocumentIndexConsistencyReport report = store.VerifyIndexConsistency();
                if (!report.IsConsistent
                    || report.FullTextIndexes.Any(static index => !index.IsConsistent)
                    || report.VectorIndexes.Any(static index => !index.IsConsistent))
                {
                    throw InvalidResource(resource, "Document 派生索引与主数据不一致。");
                }
                store.CreateSnapshot();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DatabaseGenerationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or InvalidDataException
            or InvalidOperationException
            or UnauthorizedAccessException)
        {
            throw new DatabaseGenerationException(
                DatabaseGenerationErrorCodes.ResourceInvalid,
                "generation 资源 checkpoint 或一致性校验失败。",
                exception);
        }
    }

    private void DeletePhysicalResources(DatabaseGeneration generation)
    {
        foreach (DatabaseGenerationResource resource in generation.Resources
            .Where(static resource => resource.Kind == DatabaseGenerationResourceKind.KvKeyspace)
            .OrderBy(static resource => resource.Name, StringComparer.Ordinal))
        {
            _keyspaces.Drop(resource.Name);
        }

        foreach (DatabaseGenerationResource resource in generation.Resources
            .Where(static resource => resource.Kind == DatabaseGenerationResourceKind.DocumentCollection)
            .OrderBy(static resource => resource.Name, StringComparer.Ordinal))
        {
            _documents.DropGenerationResource(resource.Name);
        }
    }

    private void DeleteCatalogRecord(PersistedGeneration persisted)
    {
        DatabaseGeneration generation = persisted.Generation;
        byte[] descriptorKey = DescriptorKey(generation.Stream, generation.Revision);
        var mutations = new List<KvBatchMutation>(generation.Resources.Count + 1)
        {
            KvBatchMutation.Delete(descriptorKey),
        };
        var preconditions = new List<KvBatchPrecondition>(generation.Resources.Count + 1)
        {
            KvBatchPrecondition.KeyVersion(descriptorKey, persisted.Version),
        };
        foreach (DatabaseGenerationResource resource in generation.Resources)
        {
            byte[] ownerKey = OwnerKey(resource);
            KvEntry owner = _catalog.GetEntry(ownerKey)
                ?? throw new InvalidDataException("generation resource ownership 记录缺失。");
            mutations.Add(KvBatchMutation.Delete(ownerKey));
            preconditions.Add(KvBatchPrecondition.KeyVersion(ownerKey, owner.Version));
        }

        KvConditionalBatchResult result = _catalog.ApplyConditionalBatch(
            mutations,
            preconditions,
            CancellationToken.None);
        if (!result.Applied)
            throw new InvalidDataException("generation retired catalog 在清理期间发生冲突。");
    }

    private static DatabaseGeneration CreateGeneration(
        DatabaseGenerationPublishRequest request,
        long revision)
    {
        DatabaseGenerationResource[] resources = request.Resources
            .Select(static resource => new DatabaseGenerationResource(
                resource.Role,
                resource.Kind,
                resource.Name,
                resource.ParentName))
            .OrderBy(static resource => resource.Role, StringComparer.Ordinal)
            .ToArray();
        return new DatabaseGeneration(
            request.Stream,
            request.GenerationId,
            revision,
            DateTimeOffset.UtcNow,
            resources);
    }

    private static void ValidatePublishRequest(DatabaseGenerationPublishRequest request)
    {
        ValidateText(request.Stream, nameof(request.Stream));
        ValidateText(request.GenerationId, nameof(request.GenerationId));
        ArgumentOutOfRangeException.ThrowIfNegative(request.ExpectedRevision);
        ArgumentNullException.ThrowIfNull(request.Resources);
        if (request.Resources.Count == 0 || request.Resources.Count > MaximumResourceCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.Resources),
                $"generation 资源数量必须在 1 到 {MaximumResourceCount} 之间。");
        }

        var roles = new HashSet<string>(StringComparer.Ordinal);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var documentNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (DatabaseGenerationResource resource in request.Resources)
        {
            ArgumentNullException.ThrowIfNull(resource);
            ValidateText(resource.Role, nameof(request.Resources));
            ValidateText(resource.Name, nameof(request.Resources));
            if (resource.ParentName is not null)
                ValidateText(resource.ParentName, nameof(request.Resources));
            if (!roles.Add(resource.Role))
                throw new ArgumentException($"generation resource role '{resource.Role}' 重复。", nameof(request));
            if (!identities.Add(ResourceIdentity(resource)))
                throw new ArgumentException($"generation 物理资源 '{resource.Name}' 重复。", nameof(request));
            if (resource.Kind == DatabaseGenerationResourceKind.DocumentCollection)
                documentNames.Add(resource.Name);
        }

        foreach (DatabaseGenerationResource resource in request.Resources)
        {
            if (resource.Kind == DatabaseGenerationResourceKind.DocumentFullTextIndex
                && !documentNames.Contains(resource.ParentName!))
            {
                throw new ArgumentException(
                    $"FullText index '{resource.Name}' 必须与父 Document collection '{resource.ParentName}' 一同发布。",
                    nameof(request));
            }
        }
    }

    private static void ValidateText(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        int byteCount = _strictUtf8.GetByteCount(value);
        if (byteCount > MaximumTextBytes)
            throw new ArgumentOutOfRangeException(parameterName, $"UTF-8 文本不能超过 {MaximumTextBytes} 字节。");
    }

    private SortedDictionary<long, PersistedGeneration> GetOrCreateStream(string stream)
    {
        if (!_generations.TryGetValue(stream, out SortedDictionary<long, PersistedGeneration>? generations))
        {
            generations = [];
            _generations.Add(stream, generations);
        }
        return generations;
    }

    private long? TryReadActiveRevision(string stream)
    {
        KvEntry? entry = _catalog.GetEntry(ActiveKey(stream));
        return entry is null ? null : DatabaseGenerationCodec.DecodeRevision(entry.Value.Span);
    }

    private static byte[] DescriptorKey(string stream, long revision)
        => Encoding.ASCII.GetBytes(
            "generation:" + EncodeName(stream) + ":" + revision.ToString("D20", CultureInfo.InvariantCulture));

    private static byte[] ActiveKey(string stream)
        => Encoding.ASCII.GetBytes("active:" + EncodeName(stream));

    private static byte[] OwnerKey(DatabaseGenerationResource resource)
        => Encoding.ASCII.GetBytes("owner:" + ResourceIdentity(resource));

    private static string ResourceIdentity(DatabaseGenerationResource resource)
        => ((int)resource.Kind).ToString(CultureInfo.InvariantCulture)
            + ":" + EncodeName(resource.ParentName ?? string.Empty)
            + ":" + EncodeName(resource.Name);

    private static byte[] OwnershipValue(DatabaseGeneration generation)
        => _strictUtf8.GetBytes(
            generation.Stream
            + "\0"
            + generation.Revision.ToString(CultureInfo.InvariantCulture)
            + "\0"
            + generation.GenerationId);

    private static string EncodeName(string value)
        => Convert.ToHexString(_strictUtf8.GetBytes(value));

    private static string DecodeNameKey(ReadOnlySpan<byte> key, ReadOnlySpan<byte> prefix)
    {
        if (!key.StartsWith(prefix))
            throw new InvalidDataException("generation catalog key 前缀无效。");
        try
        {
            return _strictUtf8.GetString(Convert.FromHexString(
                Encoding.ASCII.GetString(key[prefix.Length..])));
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        {
            throw new InvalidDataException("generation catalog key 名称编码无效。", exception);
        }
    }

    private static DatabaseGenerationException InvalidResource(
        DatabaseGenerationResource resource,
        string reason)
        => new(
            DatabaseGenerationErrorCodes.ResourceInvalid,
            $"generation resource '{resource.Role}' ({resource.Kind}/{resource.Name}) 无效：{reason}");

    private static DatabaseGenerationException RevisionConflict(
        string stream,
        long expected,
        long observed)
        => new(
            DatabaseGenerationErrorCodes.RevisionConflict,
            $"generation stream '{stream}' 预期 active revision {expected}，实际为 {observed}。");

    private static DatabaseGenerationException RevisionUnavailable(
        string stream,
        long revision,
        Exception? innerException = null)
    {
        string message = $"generation stream '{stream}' 的 revision {revision} 不存在、已清理或资源不可用。";
        return innerException is null
            ? new DatabaseGenerationException(DatabaseGenerationErrorCodes.RevisionUnavailable, message)
            : new DatabaseGenerationException(
                DatabaseGenerationErrorCodes.RevisionUnavailable,
                message,
                innerException);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record PersistedGeneration(DatabaseGeneration Generation, long Version);

    private sealed record ActiveGeneration(DatabaseGeneration Generation, long Version);

    private readonly record struct LeaseKey(string Stream, long Revision);
}
