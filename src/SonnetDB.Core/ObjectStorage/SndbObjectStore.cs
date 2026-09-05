using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SonnetDB.Engine;
using SonnetDB.Kv;

namespace SonnetDB.ObjectStorage;

/// <summary>
/// SonnetDB 数据库内置对象桶存储。
/// </summary>
public sealed partial class SndbObjectStore
{
    private const string MetadataKeyspace = "__object_storage";
    private const string BucketPrefix = "bucket:";
    private const string ObjectPrefix = "object:";
    private const string LatestPrefix = "latest:";
    private const string PolicyPrefix = "policy:";
    private const string LifecyclePrefix = "lifecycle:";
    private const string RetentionPrefix = "retention:";
    private const string QuotaPrefix = "quota:";
    private const string SemanticOptionsPrefix = "semantic-options:";
    private const string LegalHoldPrefix = "legalhold:";
    private const string AuditPrefix = "audit:";
    private const string UploadPrefix = "multipart:";
    private const string PartPrefix = "part:";
    private const string PresignPrefix = "presign:";
    private const string Active = "active";
    private const string Completed = "completed";
    private const string Aborted = "aborted";
    private const int ObjectIoBufferSize = 128 * 1024;
    private static readonly Encoding Utf8 = new UTF8Encoding(false);
    private static readonly ConditionalWeakTable<KvKeyspace, ObjectMutationState> ObjectMutationStates = new();

    private readonly KvKeyspace _metadata;
    private readonly string _contentRoot;
    private readonly ObjectMutationState _objectMutationState;

    /// <summary>
    /// 构造对象存储门面。
    /// </summary>
    public SndbObjectStore(Tsdb tsdb)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        _metadata = tsdb.Keyspaces.Open(MetadataKeyspace);
        // 同一 keyspace 的多个存储门面共享 bucket gate，避免不同 bucket 的生命周期互相阻塞。
        _objectMutationState = ObjectMutationStates.GetValue(_metadata, static _ => new ObjectMutationState());
        _contentRoot = Path.Combine(tsdb.RootDirectory, "objects");
        Directory.CreateDirectory(_contentRoot);
    }

    /// <summary>
    /// 列出所有 bucket。
    /// </summary>
    public IReadOnlyList<SndbBucketInfo> ListBuckets()
    {
        return _metadata.ScanPrefix(BucketPrefix)
            .Select(static entry => Deserialize(entry.Value.Span, SndbObjectStoreJsonContext.Default.SndbBucketRecord))
            .Select(static record => new SndbBucketInfo(record.Name, record.Purpose, record.CreatedUtc, record.UpdatedUtc))
            .OrderBy(static bucket => bucket.Name, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// 创建 bucket；已存在时返回当前 bucket。
    /// </summary>
    public SndbBucketInfo CreateBucket(string bucket, string? purpose = null)
    {
        ValidateBucket(bucket);
        string normalizedPurpose = NormalizePurpose(purpose);
        var bucketMutation = GetOrCreateBucketMutationState(bucket);
        lock (bucketMutation.Gate)
        {
            string key = BucketKey(bucket);
            var existing = _metadata.GetEntry(key);
            if (existing is not null)
            {
                var record = Deserialize(existing.Value.Span, SndbObjectStoreJsonContext.Default.SndbBucketRecord);
                return new SndbBucketInfo(record.Name, record.Purpose, record.CreatedUtc, record.UpdatedUtc);
            }

            var now = DateTimeOffset.UtcNow;
            var created = new SndbBucketRecord(bucket, normalizedPurpose, now, now);
            _metadata.Put(key, Serialize(created, SndbObjectStoreJsonContext.Default.SndbBucketRecord));
            Directory.CreateDirectory(Path.Combine(_contentRoot, BucketHash(bucket)));
            AppendAudit("bucket.create", bucket, null, null, new Dictionary<string, string> { ["purpose"] = normalizedPurpose });
            return new SndbBucketInfo(bucket, normalizedPurpose, now, now);
        }
    }

    /// <summary>
    /// 获取 bucket。
    /// </summary>
    public SndbBucketInfo? GetBucket(string bucket)
    {
        ValidateBucket(bucket);
        var entry = _metadata.GetEntry(BucketKey(bucket));
        if (entry is null)
            return null;

        var record = Deserialize(entry.Value.Span, SndbObjectStoreJsonContext.Default.SndbBucketRecord);
        return new SndbBucketInfo(record.Name, record.Purpose, record.CreatedUtc, record.UpdatedUtc);
    }

    /// <summary>
    /// 删除空 bucket。
    /// </summary>
    public bool DeleteBucket(string bucket)
    {
        var bucketMutation = GetBucketMutationState(bucket);
        lock (bucketMutation.Gate)
        {
            EnsureBucket(bucket);
            if (_metadata.ScanPrefix(LatestObjectPrefix(bucket), limit: 1).Count > 0
                || _metadata.ScanPrefix(ObjectBucketPrefix(bucket), limit: 1).Count > 0
                || HasActiveMultipartUploads(bucket))
            {
                throw new SndbObjectStorageException("bucket_not_empty", $"Bucket '{bucket}' is not empty.");
            }

            var mutations = new List<KvBatchMutation>(8)
            {
                KvBatchMutation.Delete(Utf8.GetBytes(BucketKey(bucket))),
                KvBatchMutation.Delete(Utf8.GetBytes(PolicyKey(bucket))),
                KvBatchMutation.Delete(Utf8.GetBytes(LifecycleKey(bucket))),
                KvBatchMutation.Delete(Utf8.GetBytes(RetentionKey(bucket))),
                KvBatchMutation.Delete(Utf8.GetBytes(QuotaKey(bucket))),
                KvBatchMutation.Delete(Utf8.GetBytes(SemanticOptionsKey(bucket))),
                KvBatchMutation.Delete(Utf8.GetBytes(ObjectListReadyKey(bucket))),
            };

            SndbObjectAuditRecord audit = CreateAuditRecord("bucket.delete", bucket, null, null);
            mutations.Add(KvBatchMutation.Put(
                Utf8.GetBytes(AuditKey(bucket, audit.Id)),
                Serialize(audit, SndbObjectStoreJsonContext.Default.SndbObjectAuditRecord)));
            _metadata.ApplyBatch(mutations);
            return true;
        }
    }

    /// <summary>
    /// 写入对象。
    /// </summary>
    public async Task<SndbObjectInfo> PutObjectAsync(
        string bucket,
        string key,
        Stream content,
        string? contentType = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        IReadOnlyDictionary<string, string>? tags = null,
        CancellationToken cancellationToken = default)
    {
        ValidateObjectKey(key);
        ArgumentNullException.ThrowIfNull(content);

        string normalizedContentType = NormalizeContentType(contentType);
        Dictionary<string, string> normalizedMetadata = NormalizeMap(metadata);
        Dictionary<string, string> normalizedTags = NormalizeMap(tags);
        var bucketMutation = GetBucketMutationState(bucket);
        long initialBucketVersion;
        lock (bucketMutation.Gate)
        {
            // 记录本次写入所属 bucket 的 KV 版本，防止删除后同名重建造成 ABA 发布。
            initialBucketVersion = GetRequiredBucketEntry(bucket).Version;
        }

        string versionId = CreateVersionId();
        string storagePath = BuildObjectStoragePath(bucket, key, versionId);
        string storageDirectory = Path.GetDirectoryName(storagePath)!;
        Directory.CreateDirectory(storageDirectory);
        string temporaryPath = Path.Combine(
            storageDirectory,
            $".{Path.GetFileName(storagePath)}.{Guid.NewGuid():N}.tmp");

        long size;
        string etag;
        string sha256;
        bool finalFileMoved = false;
        SndbObjectRecord record;
        try
        {
            // 临时文件与最终文件位于同一目录，校验完成后通过原子改名发布完整对象。
            (size, etag, sha256) = await WriteContentAndHashAsync(content, temporaryPath, cancellationToken).ConfigureAwait(false);
            lock (bucketMutation.Gate)
            {
                // 内容落盘期间 bucket 可能被删除并重建，提交元数据前必须确认仍是原 bucket 实例。
                KvEntry currentBucketEntry = GetRequiredBucketEntry(bucket);
                if (currentBucketEntry.Version != initialBucketVersion)
                {
                    throw new SndbObjectStorageException(
                        "bucket_recreated",
                        $"Bucket '{bucket}' was deleted and recreated while the object was being written.");
                }
                EnsureQuotaAllowsDelta(bucket, size, additionalObjectVersions: 1);
                File.Move(temporaryPath, storagePath, overwrite: false);
                finalFileMoved = true;
                // 先持久化 rename 对应的目录项，再提交引用该正文的 KV 元数据。
                SonnetDB.Wal.DirectoryFsync.FlushRequired(storageDirectory);

                var now = bucketMutation.GetNextVersionTimestamp();
                record = new SndbObjectRecord(
                    bucket,
                    key,
                    versionId,
                    normalizedContentType,
                    size,
                    etag,
                    sha256,
                    ToRelativeStoragePath(storagePath),
                    IsDeleteMarker: false,
                    now,
                    now,
                    normalizedMetadata,
                    normalizedTags);
                var audit = CreateAuditRecord("object.put", bucket, key, versionId, new Dictionary<string, string>
                {
                    ["sizeBytes"] = size.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["etag"] = etag,
                    ["sha256"] = sha256,
                });

                // 对象版本、latest 指针和审计记录作为一个 KV 批次发布。
                PersistObjectRecord(record, audit);
            }
        }
        catch (Exception ex)
        {
            // WAL 已开始追加时提交结果不确定，保留完整文件供重启恢复；明确的提交前失败才可安全清理。
            if (finalFileMoved && !_metadata.IsWriteCommitOutcomeUnknown(ex))
            {
                TryDeleteFile(storagePath);
                SonnetDB.Wal.DirectoryFsync.FlushBestEffort(storageDirectory);
            }
            throw;
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }

        return ToInfo(record);
    }

    /// <summary>
    /// 列出 bucket 内当前可见对象。
    /// </summary>
    public SndbObjectListResult ListObjects(string bucket, string? prefix = null, int maxKeys = 1000, string? continuationToken = null)
        => ListObjects(bucket, prefix, maxKeys, continuationToken, delimiter: null, CancellationToken.None);

    /// <summary>
    /// 列出对象版本；key 为空时列出整个 bucket 的版本。
    /// </summary>
    public SndbObjectVersionListResult ListObjectVersions(string bucket, string? key = null)
    {
        var records = LoadOrderedObjectVersionRecords(bucket, key);
        var versions = new SndbObjectInfo[records.Length];
        for (int index = 0; index < records.Length; index++)
            versions[index] = ToInfo(records[index]);

        string? normalizedKey = string.IsNullOrWhiteSpace(key) ? null : key;
        AppendObjectVersionsListAudit(bucket, normalizedKey, versions.Length);
        return new SndbObjectVersionListResult(bucket, normalizedKey, versions);
    }

    /// <summary>
    /// 获取对象元数据。
    /// </summary>
    public SndbObjectInfo? HeadObject(string bucket, string key, string? versionId = null)
    {
        EnsureBucket(bucket);
        ValidateObjectKey(key);
        var record = LoadObjectRecord(bucket, key, versionId);
        AppendAudit(
            record is null || record.IsDeleteMarker ? "object.head.miss" : "object.head",
            bucket,
            key,
            record?.VersionId ?? versionId);
        return record is null || record.IsDeleteMarker ? null : ToInfo(record);
    }

    /// <summary>
    /// 读取对象内容。
    /// </summary>
    public SndbObjectReadResult? OpenRead(string bucket, string key, SndbObjectRange? range = null, string? versionId = null)
    {
        EnsureBucket(bucket);
        ValidateObjectKey(key);
        var record = LoadObjectRecord(bucket, key, versionId);
        if (record is null || record.IsDeleteMarker)
        {
            AppendAudit("object.get.miss", bucket, key, record?.VersionId ?? versionId);
            return null;
        }

        var path = ResolveStoragePath(record.StoragePath);
        if (!File.Exists(path))
            throw new SndbObjectStorageException("object_content_missing", $"Object content for '{bucket}/{key}' is missing.");

        var (offset, length) = range?.Resolve(record.SizeBytes) ?? (0, record.SizeBytes);
        Stream stream = File.OpenRead(path);
        if (offset > 0)
            stream.Seek(offset, SeekOrigin.Begin);

        AppendAudit("object.get", bucket, key, record.VersionId, new Dictionary<string, string>
        {
            ["offset"] = offset.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["length"] = length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["range"] = range.HasValue ? "true" : "false",
        });
        return new SndbObjectReadResult(
            ToInfo(record),
            new BoundedReadStream(stream, length),
            offset,
            length,
            range.HasValue,
            record.SizeBytes);
    }

    /// <summary>
    /// 复制对象。
    /// </summary>
    public async Task<SndbObjectInfo> CopyObjectAsync(
        string sourceBucket,
        string sourceKey,
        string destinationBucket,
        string destinationKey,
        IReadOnlyDictionary<string, string>? metadata = null,
        IReadOnlyDictionary<string, string>? tags = null,
        CancellationToken cancellationToken = default)
    {
        var source = OpenRead(sourceBucket, sourceKey)
            ?? throw new SndbObjectStorageException("object_not_found", $"Object '{sourceBucket}/{sourceKey}' was not found.");
        await using (source.Content)
        {
            return await PutObjectAsync(
                destinationBucket,
                destinationKey,
                source.Content,
                source.Info.ContentType,
                metadata ?? source.Info.Metadata,
                tags ?? source.Info.Tags,
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 创建 delete marker。
    /// </summary>
    public SndbObjectInfo DeleteObject(string bucket, string key)
    {
        ValidateObjectKey(key);
        var bucketMutation = GetBucketMutationState(bucket);
        lock (bucketMutation.Gate)
        {
            EnsureBucket(bucket);
            var existing = LoadObjectRecord(bucket, key);
            if (existing is { IsDeleteMarker: false })
                EnsureObjectVersionCanBeDeleted(existing, isLatest: true);

            var now = bucketMutation.GetNextVersionTimestamp();
            var record = new SndbObjectRecord(
                bucket,
                key,
                CreateVersionId(),
                "application/x-sonnetdb-delete-marker",
                0,
                "\"delete-marker\"",
                new string('0', 64),
                string.Empty,
                IsDeleteMarker: true,
                now,
                now,
                [],
                []);

            PersistObjectRecord(record, CreateAuditRecord("object.delete_marker", bucket, key, record.VersionId));
            return ToInfo(record);
        }
    }

    /// <summary>
    /// 批量删除对象并为每个 key 创建 delete marker。
    /// </summary>
    public SndbObjectDeleteManyResult DeleteObjects(string bucket, IReadOnlyList<string> keys)
    {
        EnsureBucket(bucket);
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0)
            return new SndbObjectDeleteManyResult(bucket, []);

        var bucketMutation = GetBucketMutationState(bucket);
        lock (bucketMutation.Gate)
        {
            EnsureBucket(bucket);
            var deleted = new List<SndbObjectDeleteResult>(keys.Count);
            foreach (string key in keys)
            {
                try
                {
                    var marker = DeleteObject(bucket, key);
                    deleted.Add(new SndbObjectDeleteResult(key, marker.VersionId, DeleteMarker: true));
                }
                catch (Exception ex) when (ex is ArgumentException or SndbObjectStorageException)
                {
                    deleted.Add(new SndbObjectDeleteResult(
                        key,
                        string.Empty,
                        DeleteMarker: false,
                        ex is SndbObjectStorageException storage ? storage.Code : "bad_request",
                        ex.Message));
                }
            }

            AppendAudit("object.delete_many", bucket, null, null, new Dictionary<string, string>
            {
                ["count"] = deleted.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
            return new SndbObjectDeleteManyResult(bucket, deleted);
        }
    }

    /// <summary>
    /// 设置对象标签。
    /// </summary>
    public SndbObjectInfo SetObjectTags(string bucket, string key, IReadOnlyDictionary<string, string> tags)
    {
        ValidateObjectKey(key);
        ArgumentNullException.ThrowIfNull(tags);
        var bucketMutation = GetBucketMutationState(bucket);
        lock (bucketMutation.Gate)
        {
            EnsureBucket(bucket);
            var record = LoadObjectRecord(bucket, key)
                ?? throw new SndbObjectStorageException("object_not_found", $"Object '{bucket}/{key}' was not found.");
            if (record.IsDeleteMarker)
                throw new SndbObjectStorageException("object_not_found", $"Object '{bucket}/{key}' was deleted.");

            var updated = record with { Tags = NormalizeMap(tags), UpdatedUtc = DateTimeOffset.UtcNow };
            // 标签更新只覆盖既有版本，不能把并发 delete marker 回退为旧的 latest 指针。
            PersistObjectRecord(updated, updateLatest: false);
            AppendAudit("object.tags.set", bucket, key, updated.VersionId, updated.Tags);
            return ToInfo(updated);
        }
    }

    /// <summary>
    /// 获取 bucket policy 占位配置。
    /// </summary>
    public SndbBucketPolicyInfo GetPolicy(string bucket)
    {
        EnsureBucket(bucket);
        var entry = _metadata.GetEntry(PolicyKey(bucket));
        if (entry is null)
            return new SndbBucketPolicyInfo(bucket, null, DateTimeOffset.MinValue);

        var record = Deserialize(entry.Value.Span, SndbObjectStoreJsonContext.Default.SndbBucketPolicyRecord);
        return new SndbBucketPolicyInfo(record.Bucket, record.PolicyJson, record.UpdatedUtc);
    }

    /// <summary>
    /// 设置 bucket policy 占位配置；当前仅做 JSON 格式校验、持久化和审计。
    /// </summary>
    public SndbBucketPolicyInfo SetPolicy(string bucket, string? policyJson)
    {
        string? normalizedPolicy = NormalizePolicyJson(policyJson);
        var bucketMutation = GetBucketMutationState(bucket);
        lock (bucketMutation.Gate)
        {
            EnsureBucket(bucket);
            var now = DateTimeOffset.UtcNow;
            var record = new SndbBucketPolicyRecord(bucket, normalizedPolicy, now);

            if (normalizedPolicy is null)
            {
                _metadata.Delete(PolicyKey(bucket));
                AppendAudit("bucket.policy.clear", bucket, null, null);
            }
            else
            {
                _metadata.Put(PolicyKey(bucket), Serialize(record, SndbObjectStoreJsonContext.Default.SndbBucketPolicyRecord));
                AppendAudit("bucket.policy.set", bucket, null, null);
            }

            return new SndbBucketPolicyInfo(bucket, normalizedPolicy, now);
        }
    }

    /// <summary>
    /// 获取 bucket 生命周期策略。
    /// </summary>
    public SndbBucketLifecycleInfo GetLifecycle(string bucket)
    {
        EnsureBucket(bucket);
        var entry = _metadata.GetEntry(LifecycleKey(bucket));
        if (entry is null)
        {
            return new SndbBucketLifecycleInfo(bucket, null, null, null, DateTimeOffset.MinValue);
        }

        var record = Deserialize(entry.Value.Span, SndbObjectStoreJsonContext.Default.SndbBucketLifecycleRecord);
        return ToLifecycleInfo(record);
    }

    /// <summary>
    /// 设置 bucket 生命周期策略；null 表示对应规则关闭。
    /// </summary>
    public SndbBucketLifecycleInfo SetLifecycle(
        string bucket,
        int? expireCurrentAfterDays,
        int? expireNoncurrentAfterDays,
        int? expireDeleteMarkerAfterDays)
    {
        ValidateLifecycleDays(expireCurrentAfterDays);
        ValidateLifecycleDays(expireNoncurrentAfterDays);
        ValidateLifecycleDays(expireDeleteMarkerAfterDays);
        var bucketMutation = GetBucketMutationState(bucket);
        lock (bucketMutation.Gate)
        {
            EnsureBucket(bucket);
            var record = new SndbBucketLifecycleRecord(
                bucket,
                expireCurrentAfterDays,
                expireNoncurrentAfterDays,
                expireDeleteMarkerAfterDays,
                DateTimeOffset.UtcNow);
            _metadata.Put(LifecycleKey(bucket), Serialize(record, SndbObjectStoreJsonContext.Default.SndbBucketLifecycleRecord));
            AppendAudit("bucket.lifecycle.set", bucket, null, null, new Dictionary<string, string>
            {
                ["expireCurrentAfterDays"] = expireCurrentAfterDays?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
                ["expireNoncurrentAfterDays"] = expireNoncurrentAfterDays?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
                ["expireDeleteMarkerAfterDays"] = expireDeleteMarkerAfterDays?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
            });
            return ToLifecycleInfo(record);
        }
    }

    /// <summary>
    /// 执行 bucket 生命周期策略。
    /// </summary>
    public SndbBucketLifecycleApplyResult ApplyLifecycle(string bucket)
    {
        var bucketMutation = GetBucketMutationState(bucket);
        lock (bucketMutation.Gate)
        {
            EnsureBucket(bucket);
            var lifecycle = GetLifecycle(bucket);
            var versions = LoadObjectVersionRecords(bucket, key: null);
            var latestVersionByKey = LoadLatestObjectVersionIds(bucket);
            DateTimeOffset utcNow = DateTimeOffset.UtcNow;
            AppendObjectVersionsListAudit(bucket, key: null, versions.Length);

            var versionsByKey = new Dictionary<string, List<SndbObjectRecord>>(StringComparer.Ordinal);
            foreach (var version in versions)
            {
                if (!versionsByKey.TryGetValue(version.Key, out var keyVersions))
                {
                    keyVersions = [];
                    versionsByKey.Add(version.Key, keyVersions);
                }
                keyVersions.Add(version);
            }

            int expiredCurrent = 0;
            int removedNoncurrent = 0;
            int removedDeleteMarkers = 0;
            var expiredObjects = new List<SndbLifecycleExpiredObject>();
            var removalsByKey = new Dictionary<string, List<SndbObjectRecord>>(StringComparer.Ordinal);
            var keysWithNewDeleteMarker = new HashSet<string>(StringComparer.Ordinal);
            foreach (var version in versions)
            {
                // 生命周期的当前/非当前分类以扫描开始时的 latest 指针为准，不能从时间戳排序推断。
                bool isLatest = latestVersionByKey.TryGetValue(version.Key, out string? latestVersionId)
                    && string.Equals(latestVersionId, version.VersionId, StringComparison.Ordinal);

                if (version.IsDeleteMarker)
                {
                    if (ShouldExpire(version.CreatedUtc, lifecycle.ExpireDeleteMarkerAfterDays, utcNow)
                        && !IsObjectVersionProtected(version, isLatest, out _, out _))
                    {
                        AddLifecycleRemoval(removalsByKey, version);
                        removedDeleteMarkers++;
                    }
                    continue;
                }

                if (isLatest)
                {
                    if (ShouldExpire(version.CreatedUtc, lifecycle.ExpireCurrentAfterDays, utcNow)
                        && !IsObjectVersionProtected(version, isLatest, out _, out _))
                    {
                        DeleteObject(bucket, version.Key);
                        keysWithNewDeleteMarker.Add(version.Key);
                        expiredObjects.Add(new SndbLifecycleExpiredObject(
                            version.Key,
                            version.VersionId,
                            version.ContentType));
                        expiredCurrent++;
                    }
                    continue;
                }

                if (ShouldExpire(version.CreatedUtc, lifecycle.ExpireNoncurrentAfterDays, utcNow)
                    && !IsObjectVersionProtected(version, isLatest, out _, out _))
                {
                    AddLifecycleRemoval(removalsByKey, version);
                    removedNoncurrent++;
                }
            }

            // 每个 key 只提交一次删除批次；不再在每次删版本后重复扫描该 key 的全部历史。
            foreach (var (key, removals) in removalsByKey)
            {
                RemoveObjectVersionsForLifecycle(
                    bucket,
                    key,
                    versionsByKey[key],
                    removals,
                    latestVersionByKey.GetValueOrDefault(key),
                    keysWithNewDeleteMarker.Contains(key));
            }

            AppendAudit("bucket.lifecycle.apply", bucket, null, null, new Dictionary<string, string>
            {
                ["expiredCurrentObjects"] = expiredCurrent.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["removedNoncurrentVersions"] = removedNoncurrent.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["removedDeleteMarkers"] = removedDeleteMarkers.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
            return new SndbBucketLifecycleApplyResult(bucket, expiredCurrent, removedNoncurrent, removedDeleteMarkers)
            {
                ExpiredObjects = expiredObjects,
            };
        }
    }

    /// <summary>
    /// 获取 bucket 对象保留策略。
    /// </summary>
    public SndbBucketRetentionInfo GetRetention(string bucket)
    {
        EnsureBucket(bucket);
        var entry = _metadata.GetEntry(RetentionKey(bucket));
        if (entry is null)
            return new SndbBucketRetentionInfo(bucket, null, null, DateTimeOffset.MinValue);

        var record = Deserialize(entry.Value.Span, SndbObjectStoreJsonContext.Default.SndbBucketRetentionRecord);
        return ToRetentionInfo(record);
    }

    /// <summary>
    /// 设置 bucket 对象保留策略；null 表示对应保留规则关闭。
    /// </summary>
    public SndbBucketRetentionInfo SetRetention(
        string bucket,
        int? retainCurrentForDays,
        int? retainNoncurrentForDays)
    {
        ValidateLifecycleDays(retainCurrentForDays);
        ValidateLifecycleDays(retainNoncurrentForDays);
        var bucketMutation = GetBucketMutationState(bucket);
        lock (bucketMutation.Gate)
        {
            EnsureBucket(bucket);
            var record = new SndbBucketRetentionRecord(
                bucket,
                retainCurrentForDays,
                retainNoncurrentForDays,
                DateTimeOffset.UtcNow);
            _metadata.Put(RetentionKey(bucket), Serialize(record, SndbObjectStoreJsonContext.Default.SndbBucketRetentionRecord));
            AppendAudit("bucket.retention.set", bucket, null, null, new Dictionary<string, string>
            {
                ["retainCurrentForDays"] = retainCurrentForDays?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
                ["retainNoncurrentForDays"] = retainNoncurrentForDays?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
            });
            return ToRetentionInfo(record);
        }
    }

    /// <summary>
    /// 获取 bucket 配额配置。
    /// </summary>
    public SndbBucketQuotaInfo GetQuota(string bucket)
    {
        EnsureBucket(bucket);
        var entry = _metadata.GetEntry(QuotaKey(bucket));
        if (entry is null)
            return new SndbBucketQuotaInfo(bucket, null, null, DateTimeOffset.MinValue);

        var record = Deserialize(entry.Value.Span, SndbObjectStoreJsonContext.Default.SndbBucketQuotaRecord);
        return ToQuotaInfo(record);
    }

    /// <summary>
    /// 设置 bucket 配额配置；null 表示对应配额不限制。
    /// </summary>
    public SndbBucketQuotaInfo SetQuota(string bucket, long? maxSizeBytes, long? maxObjectVersions)
    {
        ValidateQuota(maxSizeBytes, nameof(maxSizeBytes));
        ValidateQuota(maxObjectVersions, nameof(maxObjectVersions));
        var bucketMutation = GetBucketMutationState(bucket);
        lock (bucketMutation.Gate)
        {
            EnsureBucket(bucket);
            var record = new SndbBucketQuotaRecord(bucket, maxSizeBytes, maxObjectVersions, DateTimeOffset.UtcNow);
            _metadata.Put(QuotaKey(bucket), Serialize(record, SndbObjectStoreJsonContext.Default.SndbBucketQuotaRecord));
            AppendAudit("bucket.quota.set", bucket, null, null, new Dictionary<string, string>
            {
                ["maxSizeBytes"] = maxSizeBytes?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
                ["maxObjectVersions"] = maxObjectVersions?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
            });
            return ToQuotaInfo(record);
        }
    }

    /// <summary>
    /// 获取 Bucket 图片语义摄取与缩略图选项；未配置时返回全关闭默认值。
    /// </summary>
    public SndbBucketSemanticOptionsInfo GetSemanticOptions(string bucket)
    {
        EnsureBucket(bucket);
        var entry = _metadata.GetEntry(SemanticOptionsKey(bucket));
        if (entry is null)
        {
            return new SndbBucketSemanticOptionsInfo(
                bucket,
                AsyncIngestionEnabled: false,
                ThumbnailEnabled: false,
                ThumbnailMaxWidth: 320,
                ThumbnailMaxHeight: 320,
                ThumbnailQuality: 80,
                DateTimeOffset.MinValue);
        }

        var record = Deserialize(
            entry.Value.Span,
            SndbObjectStoreJsonContext.Default.SndbBucketSemanticOptionsRecord);
        return ToSemanticOptionsInfo(record);
    }

    /// <summary>
    /// 设置 Bucket 图片语义摄取与缩略图选项；功能保持显式 opt-in。
    /// </summary>
    public SndbBucketSemanticOptionsInfo SetSemanticOptions(
        string bucket,
        bool asyncIngestionEnabled,
        bool thumbnailEnabled,
        int thumbnailMaxWidth,
        int thumbnailMaxHeight,
        int thumbnailQuality)
    {
        ValidateThumbnailDimension(thumbnailMaxWidth, nameof(thumbnailMaxWidth));
        ValidateThumbnailDimension(thumbnailMaxHeight, nameof(thumbnailMaxHeight));
        if (thumbnailQuality is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(thumbnailQuality), "缩略图质量必须位于 1 到 100。");
        var bucketMutation = GetBucketMutationState(bucket);
        lock (bucketMutation.Gate)
        {
            EnsureBucket(bucket);
            var record = new SndbBucketSemanticOptionsRecord(
                bucket,
                asyncIngestionEnabled,
                thumbnailEnabled,
                thumbnailMaxWidth,
                thumbnailMaxHeight,
                thumbnailQuality,
                DateTimeOffset.UtcNow);
            _metadata.Put(
                SemanticOptionsKey(bucket),
                Serialize(record, SndbObjectStoreJsonContext.Default.SndbBucketSemanticOptionsRecord));
            AppendAudit("bucket.semantic_options.set", bucket, null, null, new Dictionary<string, string>
            {
                ["asyncIngestionEnabled"] = asyncIngestionEnabled ? "true" : "false",
                ["thumbnailEnabled"] = thumbnailEnabled ? "true" : "false",
                ["thumbnailMaxWidth"] = thumbnailMaxWidth.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["thumbnailMaxHeight"] = thumbnailMaxHeight.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["thumbnailQuality"] = thumbnailQuality.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
            return ToSemanticOptionsInfo(record);
        }
    }

    /// <summary>
    /// 统计 bucket 容量使用情况。
    /// </summary>
    public SndbBucketStatsInfo GetStats(string bucket)
    {
        EnsureBucket(bucket);
        var usage = ComputeUsage(bucket);
        var quota = GetQuota(bucket);
        long? remainingBytes = quota.MaxSizeBytes is null
            ? null
            : Math.Max(0, quota.MaxSizeBytes.Value - usage.ObjectVersionSizeBytes - usage.MultipartPartSizeBytes);
        long? remainingVersions = quota.MaxObjectVersions is null
            ? null
            : Math.Max(0, quota.MaxObjectVersions.Value - usage.ObjectVersionCount);

        AppendAudit("bucket.stats.get", bucket, null, null);
        return new SndbBucketStatsInfo(
            bucket,
            usage.CurrentObjectCount,
            usage.CurrentSizeBytes,
            usage.ObjectVersionCount,
            usage.ObjectVersionSizeBytes,
            usage.DeleteMarkerCount,
            usage.MultipartUploadCount,
            usage.MultipartPartCount,
            usage.MultipartPartSizeBytes,
            quota.MaxSizeBytes,
            quota.MaxObjectVersions,
            remainingBytes,
            remainingVersions);
    }

    /// <summary>
    /// 获取对象版本 legal hold 状态。
    /// </summary>
    public SndbObjectLegalHoldInfo GetLegalHold(string bucket, string key, string? versionId = null)
    {
        var record = ResolveExistingObjectVersion(bucket, key, versionId);
        var hold = LoadLegalHold(record.Bucket, record.Key, record.VersionId);
        AppendAudit("object.legal_hold.get", record.Bucket, record.Key, record.VersionId);
        return hold is null
            ? new SndbObjectLegalHoldInfo(record.Bucket, record.Key, record.VersionId, Enabled: false, Reason: null, DateTimeOffset.MinValue)
            : ToLegalHoldInfo(hold);
    }

    /// <summary>
    /// 设置对象版本 legal hold 状态。
    /// </summary>
    public SndbObjectLegalHoldInfo SetLegalHold(string bucket, string key, bool enabled, string? reason = null, string? versionId = null)
    {
        var bucketMutation = GetBucketMutationState(bucket);
        lock (bucketMutation.Gate)
        {
            var record = ResolveExistingObjectVersion(bucket, key, versionId);
            var hold = new SndbObjectLegalHoldRecord(
                record.Bucket,
                record.Key,
                record.VersionId,
                enabled,
                NormalizeReason(reason),
                DateTimeOffset.UtcNow);
            _metadata.Put(LegalHoldKey(record.Bucket, record.Key, record.VersionId), Serialize(hold, SndbObjectStoreJsonContext.Default.SndbObjectLegalHoldRecord));
            AppendAudit(enabled ? "object.legal_hold.enable" : "object.legal_hold.disable", record.Bucket, record.Key, record.VersionId, new Dictionary<string, string>
            {
                ["reason"] = hold.Reason ?? "",
            });
            return ToLegalHoldInfo(hold);
        }
    }

    /// <summary>
    /// 列出 bucket 审计记录。
    /// </summary>
    public IReadOnlyList<SndbObjectAuditEntry> ListAudit(string bucket, string? keyPrefix = null, int maxEntries = 1000)
    {
        EnsureBucket(bucket);
        if (maxEntries <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxEntries));

        string normalizedPrefix = keyPrefix?.TrimStart('/') ?? string.Empty;
        var entries = _metadata.ScanPrefix(AuditBucketPrefix(bucket), limit: int.MaxValue)
            .Select(entry => Deserialize(entry.Value.Span, SndbObjectStoreJsonContext.Default.SndbObjectAuditRecord))
            .Where(record => string.IsNullOrEmpty(normalizedPrefix)
                || (record.Key is not null && record.Key.StartsWith(normalizedPrefix, StringComparison.Ordinal)))
            .OrderByDescending(static record => record.TimestampUtc)
            .Take(maxEntries)
            .Select(static record => new SndbObjectAuditEntry(
                record.Id,
                record.Action,
                record.Bucket,
                record.Key,
                record.VersionId,
                record.TimestampUtc,
                record.Details))
            .ToArray();
        AppendAudit("bucket.audit.list", bucket, null, null, new Dictionary<string, string>
        {
            ["prefix"] = normalizedPrefix,
            ["count"] = entries.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
        return entries;
    }

    /// <summary>
    /// 创建 multipart upload 会话。
    /// </summary>
    public SndbMultipartUploadInfo InitiateMultipartUpload(
        string bucket,
        string key,
        string? contentType = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        IReadOnlyDictionary<string, string>? tags = null,
        TimeSpan? expiresAfter = null)
    {
        ValidateObjectKey(key);
        var bucketMutation = GetBucketMutationState(bucket);
        lock (bucketMutation.Gate)
        {
            EnsureBucket(bucket);
            string uploadId = "mpu_" + Guid.NewGuid().ToString("N");
            var now = DateTimeOffset.UtcNow;
            var record = new SndbMultipartUploadRecord(
                bucket,
                key,
                uploadId,
                NormalizeContentType(contentType),
                now,
                now.Add(expiresAfter ?? TimeSpan.FromHours(24)),
                Active,
                NormalizeMap(metadata),
                NormalizeMap(tags));

            _metadata.Put(UploadKey(uploadId), Serialize(record, SndbObjectStoreJsonContext.Default.SndbMultipartUploadRecord));
            AppendAudit("multipart.initiate", bucket, key, null, new Dictionary<string, string> { ["uploadId"] = uploadId });
            return ToUploadInfo(record);
        }
    }

    /// <summary>
    /// 分页列出 bucket 内仍可恢复的 multipart upload 会话。
    /// </summary>
    /// <param name="bucket">对象桶名称。</param>
    /// <param name="maxUploads">单页最大会话数。</param>
    /// <param name="continuationToken">上一页返回的继续令牌。</param>
    /// <returns>按发起时间倒序排列的活动或已过期会话。</returns>
    public SndbMultipartUploadListResult ListMultipartUploads(
        string bucket,
        int maxUploads = 100,
        string? continuationToken = null)
    {
        EnsureBucket(bucket);
        if (maxUploads <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxUploads));

        string? afterUploadId = DecodeContinuationToken(continuationToken);
        var sessions = _metadata.ScanPrefix(UploadPrefix, limit: int.MaxValue)
            .Select(entry => Deserialize(entry.Value.Span, SndbObjectStoreJsonContext.Default.SndbMultipartUploadRecord))
            .Where(upload => string.Equals(upload.Bucket, bucket, StringComparison.Ordinal) && upload.Status == Active)
            .Select(ToMultipartSessionInfo)
            .OrderByDescending(static session => session.Upload.InitiatedUtc)
            .ThenBy(static session => session.Upload.UploadId, StringComparer.Ordinal)
            .ToList();

        if (!string.IsNullOrEmpty(afterUploadId))
        {
            int markerIndex = sessions.FindIndex(session => string.Equals(session.Upload.UploadId, afterUploadId, StringComparison.Ordinal));
            sessions = markerIndex < 0 ? [] : sessions.Skip(markerIndex + 1).ToList();
        }

        bool isTruncated = sessions.Count > maxUploads;
        var page = sessions.Take(maxUploads).ToArray();
        string? nextToken = isTruncated && page.Length > 0
            ? EncodeContinuationToken(page[^1].Upload.UploadId)
            : null;
        return new SndbMultipartUploadListResult(
            bucket,
            maxUploads,
            continuationToken,
            nextToken,
            isTruncated,
            page);
    }

    /// <summary>
    /// 获取一个 multipart upload 会话及其已上传分片，用于跨客户端恢复。
    /// </summary>
    /// <param name="uploadId">会话标识。</param>
    /// <returns>会话和分片详情。</returns>
    public SndbMultipartUploadSessionInfo GetMultipartUpload(string uploadId)
        => ToMultipartSessionInfo(LoadUpload(uploadId));

    /// <summary>
    /// 上传 multipart 分片。
    /// </summary>
    public async Task<SndbMultipartPartInfo> UploadPartAsync(
        string uploadId,
        int partNumber,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        var initialUpload = LoadUpload(uploadId);
        EnsureActiveUpload(initialUpload);
        if (partNumber is < 1 or > 10_000)
            throw new ArgumentOutOfRangeException(nameof(partNumber), "Part number must be between 1 and 10000.");
        ArgumentNullException.ThrowIfNull(content);

        var bucketMutation = GetBucketMutationState(initialUpload.Bucket);
        string storagePath = BuildMultipartStoragePath(initialUpload.Bucket, initialUpload.UploadId, partNumber, CreateVersionId());
        string partDirectory = Path.GetDirectoryName(storagePath)!;
        Directory.CreateDirectory(partDirectory);
        bool partPublished = false;
        try
        {
            var (size, etag, sha256) = await WriteContentAndHashAsync(content, storagePath, cancellationToken).ConfigureAwait(false);
            string? replacedStoragePath;
            lock (bucketMutation.Gate)
            {
                // 正文写入期间会话或 bucket 可能变化，发布前必须重新读取并校验。
                var upload = LoadUpload(uploadId);
                EnsureActiveUpload(upload);
                EnsureBucket(upload.Bucket);
                var existingPart = LoadPart(upload.UploadId, partNumber);
                EnsureQuotaAllowsDelta(upload.Bucket, size - (existingPart?.SizeBytes ?? 0), additionalObjectVersions: 0);

                var record = new SndbMultipartPartRecord(upload.UploadId, partNumber, size, etag, sha256, ToRelativeStoragePath(storagePath), DateTimeOffset.UtcNow);
                // 正文和目录项必须先于 KV 元数据持久化，避免掉电后留下可见分片但正文缺失。
                SonnetDB.Wal.DirectoryFsync.FlushRequired(partDirectory);
                _metadata.Put(PartKey(upload.UploadId, partNumber), Serialize(record, SndbObjectStoreJsonContext.Default.SndbMultipartPartRecord));
                partPublished = true;
                replacedStoragePath = existingPart?.StoragePath;
                AppendAudit("multipart.part.put", upload.Bucket, upload.Key, null, new Dictionary<string, string>
                {
                    ["uploadId"] = upload.UploadId,
                    ["partNumber"] = partNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["sizeBytes"] = size.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
            }

            // 新分片发布后，旧正文已经不可见；回收失败最多留下孤儿文件，无需继续占用 bucket gate。
            if (replacedStoragePath is not null)
                TryDeleteStorageFile(replacedStoragePath);
            return new SndbMultipartPartInfo(partNumber, size, etag, sha256);
        }
        catch (Exception ex)
        {
            // 分片元数据提交结果不确定时保留正文，供重启恢复已提交的元数据引用。
            if (!partPublished && !_metadata.IsWriteCommitOutcomeUnknown(ex))
            {
                TryDeleteFile(storagePath);
                SonnetDB.Wal.DirectoryFsync.FlushBestEffort(partDirectory);
            }
            throw;
        }
    }

    /// <summary>
    /// 完成 multipart upload。
    /// </summary>
    public async Task<SndbObjectInfo> CompleteMultipartUploadAsync(
        string uploadId,
        IReadOnlyList<int> partNumbers,
        CancellationToken cancellationToken = default)
    {
        var initialUpload = LoadUpload(uploadId);
        EnsureActiveUpload(initialUpload);
        ArgumentNullException.ThrowIfNull(partNumbers);
        if (partNumbers.Count == 0)
            throw new SndbObjectStorageException("multipart_parts_required", "At least one multipart part is required.");

        int[] requestedPartNumbers = partNumbers.Distinct().Order().ToArray();
        string versionId = CreateVersionId();
        string storagePath = BuildObjectStoragePath(initialUpload.Bucket, initialUpload.Key, versionId);
        string storageDirectory = Path.GetDirectoryName(storagePath)!;
        string temporaryPath = Path.Combine(
            storageDirectory,
            $".{Path.GetFileName(storagePath)}.{Guid.NewGuid():N}.tmp");
        var initialPartEntries = _metadata.ScanPrefix(PartPrefix + initialUpload.UploadId + ":", limit: int.MaxValue);
        var initialPartsByNumber = new Dictionary<int, SndbMultipartPartRecord>(initialPartEntries.Count);
        foreach (var entry in initialPartEntries)
        {
            var part = Deserialize(entry.Value.Span, SndbObjectStoreJsonContext.Default.SndbMultipartPartRecord);
            initialPartsByNumber.TryAdd(part.PartNumber, part);
        }

        var selectedInitialParts = new SndbMultipartPartRecord[requestedPartNumbers.Length];
        for (int index = 0; index < requestedPartNumbers.Length; index++)
        {
            int partNumber = requestedPartNumbers[index];
            if (!initialPartsByNumber.TryGetValue(partNumber, out SndbMultipartPartRecord? part))
                throw new SndbObjectStorageException("multipart_part_not_found", $"Multipart part {partNumber} was not found.");
            selectedInitialParts[index] = part;
        }

        SndbObjectRecord record;
        SndbMultipartPartRecord[] publishedParts = [];
        bool finalFileMoved = false;
        bool metadataPublished = false;
        var bucketMutation = GetBucketMutationState(initialUpload.Bucket);
        try
        {
            Directory.CreateDirectory(storageDirectory);
            // 合并时同步计算摘要并强制正文落盘，避免二次读取和元数据先于内容持久化。
            var (size, etag, sha256) = await MergeMultipartPartsAsync(
                selectedInitialParts,
                temporaryPath,
                cancellationToken).ConfigureAwait(false);
            lock (bucketMutation.Gate)
            {
                // 合并正文时允许分片继续上传，提交前确认读取到的分片路径仍是当前版本。
                var upload = LoadUpload(uploadId);
                EnsureActiveUpload(upload);
                EnsureBucket(upload.Bucket);
                var currentParts = _metadata.ScanPrefix(PartPrefix + upload.UploadId + ":", limit: int.MaxValue)
                    .Select(entry => Deserialize(entry.Value.Span, SndbObjectStoreJsonContext.Default.SndbMultipartPartRecord))
                    .ToArray();
                var currentPartsByNumber = new Dictionary<int, SndbMultipartPartRecord>(currentParts.Length);
                foreach (var part in currentParts)
                    currentPartsByNumber.TryAdd(part.PartNumber, part);

                var selectedCurrentParts = new SndbMultipartPartRecord[requestedPartNumbers.Length];
                for (int index = 0; index < requestedPartNumbers.Length; index++)
                {
                    int partNumber = requestedPartNumbers[index];
                    if (!currentPartsByNumber.TryGetValue(partNumber, out SndbMultipartPartRecord? part))
                        throw new SndbObjectStorageException("multipart_part_not_found", $"Multipart part {partNumber} was not found.");
                    selectedCurrentParts[index] = part;
                }

                for (int index = 0; index < selectedInitialParts.Length; index++)
                {
                    if (!string.Equals(
                        selectedInitialParts[index].StoragePath,
                        selectedCurrentParts[index].StoragePath,
                        StringComparison.Ordinal))
                    {
                        throw new SndbObjectStorageException("multipart_parts_changed", "Multipart parts changed while the upload was being completed.");
                    }
                }

                EnsureQuotaAllowsDelta(upload.Bucket, size - currentParts.Sum(static part => part.SizeBytes), additionalObjectVersions: 1);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryPath, storagePath, overwrite: false);
                finalFileMoved = true;
                // 与普通 PUT 一致，先持久化最终目录项，再让 KV 元数据引用该正文。
                SonnetDB.Wal.DirectoryFsync.FlushRequired(storageDirectory);

                var now = bucketMutation.GetNextVersionTimestamp();
                record = new SndbObjectRecord(
                    upload.Bucket,
                    upload.Key,
                    versionId,
                    upload.ContentType,
                    size,
                    etag,
                    sha256,
                    ToRelativeStoragePath(storagePath),
                    IsDeleteMarker: false,
                    now,
                    now,
                    upload.Metadata,
                    upload.Tags);

                var audit = CreateAuditRecord("multipart.complete", upload.Bucket, upload.Key, record.VersionId, new Dictionary<string, string>
                {
                    ["uploadId"] = upload.UploadId,
                    ["parts"] = requestedPartNumbers.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
                var mutations = new List<KvBatchMutation>(4 + currentParts.Length);
                AddObjectRecordMutations(mutations, record, audit);
                mutations.Add(KvBatchMutation.Put(
                    Utf8.GetBytes(UploadKey(upload.UploadId)),
                    Serialize(upload with { Status = Completed }, SndbObjectStoreJsonContext.Default.SndbMultipartUploadRecord)));
                foreach (var part in currentParts)
                    mutations.Add(KvBatchMutation.Delete(Utf8.GetBytes(PartKey(upload.UploadId, part.PartNumber))));

                // 对象可见性、upload 状态和分片元数据必须作为一个 WAL record 发布。
                _metadata.ApplyBatch(mutations);
                metadataPublished = true;
                publishedParts = currentParts;
            }

            // 元数据提交后才回收分片正文；失败最多留下不可见孤儿，不能破坏活动上传。
            foreach (var part in publishedParts)
                TryDeleteStorageFile(part.StoragePath);
        }
        catch (Exception ex)
        {
            // WAL 已开始追加时提交结果不确定，保留完整正文供重启恢复。
            if (finalFileMoved && !metadataPublished && !_metadata.IsWriteCommitOutcomeUnknown(ex))
            {
                TryDeleteFile(storagePath);
                SonnetDB.Wal.DirectoryFsync.FlushBestEffort(storageDirectory);
            }
            throw;
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
        return ToInfo(record);
    }

    /// <summary>
    /// 中止 multipart upload。
    /// </summary>
    public void AbortMultipartUpload(string uploadId)
    {
        var initialUpload = LoadUpload(uploadId);
        // 持久化 upload 已证明该 bucket 名曾真实存在；删桶重启后仍需允许幂等清理残余分片。
        var bucketMutation = GetOrCreateBucketMutationState(initialUpload.Bucket);
        lock (bucketMutation.Gate)
        {
            var upload = LoadUpload(uploadId);
            if (upload.Status == Completed)
                throw new SndbObjectStorageException("multipart_already_completed", "Multipart upload has already completed.");

            _metadata.Put(UploadKey(upload.UploadId), Serialize(upload with { Status = Aborted }, SndbObjectStoreJsonContext.Default.SndbMultipartUploadRecord));
            AppendAudit("multipart.abort", upload.Bucket, upload.Key, null, new Dictionary<string, string> { ["uploadId"] = upload.UploadId });
        }

        // 状态提交后上传与完成都会被 gate 内复检拒绝，分片文件回收无需继续阻塞整个 bucket。
        CleanupParts(uploadId);
    }

    /// <summary>
    /// 创建预签名访问令牌。
    /// </summary>
    public SndbPresignedObjectUrl CreatePresignedUrl(
        string baseUrl,
        string method,
        string bucket,
        string key,
        TimeSpan expiresAfter)
    {
        ValidateObjectKey(key);
        method = NormalizeMethod(method);
        if (expiresAfter <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(expiresAfter));
        var bucketMutation = GetBucketMutationState(bucket);
        lock (bucketMutation.Gate)
        {
            KvEntry bucketEntry = GetRequiredBucketEntry(bucket);
            string token = "sop_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            string tokenHash = Sha256Hex(Utf8.GetBytes(token));
            var now = DateTimeOffset.UtcNow;
            // 令牌绑定 bucket 的 KV 版本，同名 bucket 删除重建后无需枚举令牌即可全部失效。
            var record = new SndbPresignedTokenRecord(
                tokenHash,
                method,
                bucket,
                key,
                now,
                now.Add(expiresAfter),
                bucketEntry.Version);
            _metadata.Put(PresignKey(tokenHash), Serialize(record, SndbObjectStoreJsonContext.Default.SndbPresignedTokenRecord), record.ExpiresUtc);
            AppendAudit("object.presign.create", bucket, key, null, new Dictionary<string, string>
            {
                ["method"] = method,
                ["expiresUtc"] = record.ExpiresUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            });

            string separator = baseUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?";
            string url = baseUrl + separator + "sndb-presigned=" + Uri.EscapeDataString(token);
            return new SndbPresignedObjectUrl(url, method, bucket, key, record.ExpiresUtc);
        }
    }

    /// <summary>
    /// 校验并解析预签名令牌。
    /// </summary>
    public bool TryValidatePresignedToken(string token, string method, string bucket, string key)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;

        string tokenHash = Sha256Hex(Utf8.GetBytes(token.Trim()));
        var entry = _metadata.GetEntry(PresignKey(tokenHash));
        if (entry is null)
            return false;

        var record = Deserialize(entry.Value.Span, SndbObjectStoreJsonContext.Default.SndbPresignedTokenRecord);
        KvEntry? bucketEntry = _metadata.GetEntry(BucketKey(bucket));
        // 旧记录没有 BucketVersion；同一 keyspace 中原 bucket 必然先于令牌写入，
        // 而删除后重建的 bucket 版本必然晚于旧令牌，因此无需迁移或额外撤销状态。
        bool bucketMatches = bucketEntry is not null
            && (record.BucketVersion > 0
                ? record.BucketVersion == bucketEntry.Version
                : bucketEntry.Version < entry.Version);
        return bucketMatches
            && record.ExpiresUtc > DateTimeOffset.UtcNow
            && string.Equals(record.Method, NormalizeMethod(method), StringComparison.Ordinal)
            && string.Equals(record.Bucket, bucket, StringComparison.Ordinal)
            && string.Equals(record.Key, key, StringComparison.Ordinal);
    }

    private void EnsureBucket(string bucket)
    {
        _ = GetRequiredBucketEntry(bucket);
    }

    /// <summary>
    /// 读取指定 bucket 的元数据条目；不存在时抛出统一错误。
    /// </summary>
    private KvEntry GetRequiredBucketEntry(string bucket, CancellationToken cancellationToken = default)
    {
        ValidateBucket(bucket);
        return _metadata.GetEntry(BucketKey(bucket), cancellationToken)
            ?? throw new SndbObjectStorageException("bucket_not_found", $"Bucket '{bucket}' was not found.");
    }

    /// <summary>
    /// 获取指定 bucket 的共享提交状态。
    /// </summary>
    private BucketMutationState GetBucketMutationState(string bucket, CancellationToken cancellationToken = default)
    {
        ValidateBucket(bucket);
        BucketMutationState? existing = _objectMutationState.FindBucketMutationState(bucket);
        if (existing is not null)
            return existing;

        // 只为真实 bucket 缓存 gate，避免随机不存在名称造成常驻字典无界增长。
        GetRequiredBucketEntry(bucket, cancellationToken);
        return _objectMutationState.GetBucketMutationState(bucket);
    }

    /// <summary>
    /// 获取允许 bucket 尚不存在的共享提交状态，仅供创建入口使用。
    /// </summary>
    private BucketMutationState GetOrCreateBucketMutationState(string bucket)
    {
        ValidateBucket(bucket);
        return _objectMutationState.GetBucketMutationState(bucket);
    }

    private SndbObjectRecord? LoadObjectRecord(string bucket, string key, string? versionId = null, CancellationToken cancellationToken = default)
    {
        string? resolvedVersion = versionId;
        if (string.IsNullOrWhiteSpace(resolvedVersion))
        {
            var latest = _metadata.Get(LatestObjectKey(bucket, key));
            if (latest is null)
                return null;
            resolvedVersion = Utf8.GetString(latest);
        }

        var entry = _metadata.GetEntry(ObjectKey(bucket, key, resolvedVersion), cancellationToken);
        return entry is null
            ? null
            : Deserialize(entry.Value.Span, SndbObjectStoreJsonContext.Default.SndbObjectRecord);
    }

    /// <summary>
    /// 加载对象版本记录；生命周期使用该无排序快照避免为内部扫描额外排序。
    /// </summary>
    private SndbObjectRecord[] LoadObjectVersionRecords(string bucket, string? key)
    {
        EnsureBucket(bucket);
        if (!string.IsNullOrWhiteSpace(key))
            ValidateObjectKey(key);

        string prefix = string.IsNullOrWhiteSpace(key)
            ? ObjectBucketPrefix(bucket)
            : ObjectKeyPrefix(bucket, key);
        return _metadata.ScanPrefix(prefix, limit: int.MaxValue)
            .Select(entry => Deserialize(entry.Value.Span, SndbObjectStoreJsonContext.Default.SndbObjectRecord))
            .ToArray();
    }

    /// <summary>
    /// 加载并按公开版本列表约定排序对象记录。
    /// </summary>
    private SndbObjectRecord[] LoadOrderedObjectVersionRecords(string bucket, string? key)
    {
        return LoadObjectVersionRecords(bucket, key)
            .OrderBy(static record => record.Key, StringComparer.Ordinal)
            .ThenByDescending(static record => record.CreatedUtc)
            .ToArray();
    }

    /// <summary>
    /// 加载 bucket 的 latest 指针快照，用于准确区分当前版本和非当前版本。
    /// </summary>
    private Dictionary<string, string> LoadLatestObjectVersionIds(string bucket)
    {
        string prefix = LatestObjectPrefix(bucket);
        var latestVersionByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in _metadata.ScanPrefix(prefix, limit: int.MaxValue))
        {
            string key = UnescapeKey(Utf8.GetString(entry.Key.Span)[prefix.Length..]);
            latestVersionByKey[key] = Utf8.GetString(entry.Value.Span);
        }

        return latestVersionByKey;
    }

    /// <summary>
    /// 写入对象版本列表审计，确保公开列表与生命周期内部扫描保持相同审计语义。
    /// </summary>
    private void AppendObjectVersionsListAudit(string bucket, string? key, int count)
    {
        AppendAudit("object.versions.list", bucket, key, null, new Dictionary<string, string>
        {
            ["count"] = count.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
    }

    /// <summary>
    /// 原子发布对象版本元数据、latest 指针及可选审计记录。
    /// </summary>
    private void PersistObjectRecord(
        SndbObjectRecord record,
        SndbObjectAuditRecord? audit = null,
        bool updateLatest = true)
    {
        var mutations = new List<KvBatchMutation>((updateLatest ? 3 : 1) + (audit is null ? 0 : 1));
        AddObjectRecordMutations(mutations, record, audit, updateLatest);
        _metadata.ApplyBatch(mutations);
    }

    /// <summary>
    /// 把对象版本、latest 指针和可选审计追加到调用方批次，供普通 PUT 与 multipart 共用原子发布语义。
    /// </summary>
    private static void AddObjectRecordMutations(
        List<KvBatchMutation> mutations,
        SndbObjectRecord record,
        SndbObjectAuditRecord? audit,
        bool updateLatest = true)
    {
        mutations.Add(KvBatchMutation.Put(
            Utf8.GetBytes(ObjectKey(record.Bucket, record.Key, record.VersionId)),
            Serialize(record, SndbObjectStoreJsonContext.Default.SndbObjectRecord)));
        if (updateLatest)
        {
            mutations.Add(KvBatchMutation.Put(
                Utf8.GetBytes(LatestObjectKey(record.Bucket, record.Key)),
                Utf8.GetBytes(record.VersionId)));
            mutations.Add(ObjectListMutation(record));
        }
        if (audit is not null)
        {
            mutations.Add(KvBatchMutation.Put(
                Utf8.GetBytes(AuditKey(audit.Bucket, audit.Id)),
                Serialize(audit, SndbObjectStoreJsonContext.Default.SndbObjectAuditRecord)));
        }
    }

    /// <summary>
    /// 将待删除版本加入按 key 汇总的生命周期删除计划。
    /// </summary>
    private static void AddLifecycleRemoval(
        Dictionary<string, List<SndbObjectRecord>> removalsByKey,
        SndbObjectRecord version)
    {
        if (!removalsByKey.TryGetValue(version.Key, out var removals))
        {
            removals = [];
            removalsByKey.Add(version.Key, removals);
        }
        removals.Add(version);
    }

    /// <summary>
    /// 原子移除同一 key 的生命周期过期版本，并仅在删除原 latest 时重建指针。
    /// </summary>
    private void RemoveObjectVersionsForLifecycle(
        string bucket,
        string key,
        IReadOnlyList<SndbObjectRecord> keyVersions,
        IReadOnlyList<SndbObjectRecord> removals,
        string? initialLatestVersionId,
        bool hasNewDeleteMarker)
    {
        var removedVersionIds = new HashSet<string>(
            removals.Select(static version => version.VersionId),
            StringComparer.Ordinal);
        bool removesLatest = initialLatestVersionId is not null && removedVersionIds.Contains(initialLatestVersionId);
        var mutations = new List<KvBatchMutation>(removals.Count * 3 + 1);
        foreach (var version in removals)
        {
            mutations.Add(KvBatchMutation.Delete(Utf8.GetBytes(ObjectKey(bucket, key, version.VersionId))));
            mutations.Add(KvBatchMutation.Delete(Utf8.GetBytes(LegalHoldKey(bucket, key, version.VersionId))));
            var audit = CreateAuditRecord("object.version.remove", bucket, key, version.VersionId);
            mutations.Add(KvBatchMutation.Put(
                Utf8.GetBytes(AuditKey(audit.Bucket, audit.Id)),
                Serialize(audit, SndbObjectStoreJsonContext.Default.SndbObjectAuditRecord)));
        }

        if (removesLatest && !hasNewDeleteMarker)
        {
            SndbObjectRecord? replacement = null;
            foreach (var version in keyVersions)
            {
                if (removedVersionIds.Contains(version.VersionId))
                    continue;

                if (replacement is null
                    || version.CreatedUtc > replacement.CreatedUtc
                    || (version.CreatedUtc == replacement.CreatedUtc
                        && string.CompareOrdinal(version.VersionId, replacement.VersionId) > 0))
                {
                    replacement = version;
                }
            }

            mutations.Add(replacement is null
                ? KvBatchMutation.Delete(Utf8.GetBytes(LatestObjectKey(bucket, key)))
                : KvBatchMutation.Put(Utf8.GetBytes(LatestObjectKey(bucket, key)), Utf8.GetBytes(replacement.VersionId)));
            mutations.Add(replacement is null
                ? KvBatchMutation.Delete(ObjectListKey(bucket, key))
                : ObjectListMutation(replacement));
        }

        _metadata.ApplyBatch(mutations);

        // 先提交元数据删除，正文回收失败时最多遗留孤儿文件，不能反向制造可见对象缺正文。
        foreach (var version in removals)
        {
            if (!string.IsNullOrWhiteSpace(version.StoragePath))
                TryDeleteStorageFile(version.StoragePath);
        }
    }

    private SndbMultipartUploadRecord LoadUpload(string uploadId)
    {
        ValidateUploadId(uploadId);
        var entry = _metadata.GetEntry(UploadKey(uploadId));
        if (entry is null)
            throw new SndbObjectStorageException("multipart_not_found", $"Multipart upload '{uploadId}' was not found.");

        return Deserialize(entry.Value.Span, SndbObjectStoreJsonContext.Default.SndbMultipartUploadRecord);
    }

    private SndbMultipartPartRecord? LoadPart(string uploadId, int partNumber)
    {
        var entry = _metadata.GetEntry(PartKey(uploadId, partNumber));
        return entry is null
            ? null
            : Deserialize(entry.Value.Span, SndbObjectStoreJsonContext.Default.SndbMultipartPartRecord);
    }

    private void CleanupParts(string uploadId)
    {
        foreach (var entry in _metadata.ScanPrefix(PartPrefix + uploadId + ":", limit: int.MaxValue))
        {
            string key = Utf8.GetString(entry.Key.Span);
            var part = Deserialize(entry.Value.Span, SndbObjectStoreJsonContext.Default.SndbMultipartPartRecord);
            TryDeleteStorageFile(part.StoragePath);
            _metadata.Delete(key);
        }
    }

    private static void EnsureActiveUpload(SndbMultipartUploadRecord upload)
    {
        if (upload.Status != Active)
            throw new SndbObjectStorageException("multipart_not_active", "Multipart upload is not active.");
        if (upload.ExpiresUtc <= DateTimeOffset.UtcNow)
            throw new SndbObjectStorageException("multipart_expired", "Multipart upload has expired.");
    }

    private static SndbObjectInfo ToInfo(SndbObjectRecord record) =>
        new(
            record.Bucket,
            record.Key,
            record.VersionId,
            record.ContentType,
            record.SizeBytes,
            record.ETag,
            record.Sha256,
            record.IsDeleteMarker,
            record.CreatedUtc,
            record.UpdatedUtc,
            record.Metadata,
            record.Tags);

    private static SndbMultipartUploadInfo ToUploadInfo(SndbMultipartUploadRecord record) =>
        new(record.Bucket, record.Key, record.UploadId, record.ContentType, record.InitiatedUtc, record.ExpiresUtc, record.Metadata, record.Tags);

    private SndbMultipartUploadSessionInfo ToMultipartSessionInfo(SndbMultipartUploadRecord record)
    {
        string status = record.Status == Active && record.ExpiresUtc <= DateTimeOffset.UtcNow
            ? "expired"
            : record.Status;
        var parts = _metadata.ScanPrefix(PartPrefix + record.UploadId + ":", limit: int.MaxValue)
            .Select(entry => Deserialize(entry.Value.Span, SndbObjectStoreJsonContext.Default.SndbMultipartPartRecord))
            .OrderBy(static part => part.PartNumber)
            .Select(static part => new SndbMultipartPartInfo(part.PartNumber, part.SizeBytes, part.ETag, part.Sha256))
            .ToArray();
        return new SndbMultipartUploadSessionInfo(ToUploadInfo(record), status, parts);
    }

    private static SndbBucketLifecycleInfo ToLifecycleInfo(SndbBucketLifecycleRecord record) =>
        new(
            record.Bucket,
            record.ExpireCurrentAfterDays,
            record.ExpireNoncurrentAfterDays,
            record.ExpireDeleteMarkerAfterDays,
            record.UpdatedUtc);

    private static SndbBucketRetentionInfo ToRetentionInfo(SndbBucketRetentionRecord record) =>
        new(
            record.Bucket,
            record.RetainCurrentForDays,
            record.RetainNoncurrentForDays,
            record.UpdatedUtc);

    private static SndbBucketQuotaInfo ToQuotaInfo(SndbBucketQuotaRecord record) =>
        new(
            record.Bucket,
            record.MaxSizeBytes,
            record.MaxObjectVersions,
            record.UpdatedUtc);

    private static SndbBucketSemanticOptionsInfo ToSemanticOptionsInfo(
        SndbBucketSemanticOptionsRecord record) =>
        new(
            record.Bucket,
            record.AsyncIngestionEnabled,
            record.ThumbnailEnabled,
            record.ThumbnailMaxWidth,
            record.ThumbnailMaxHeight,
            record.ThumbnailQuality,
            record.UpdatedUtc);

    private static SndbObjectLegalHoldInfo ToLegalHoldInfo(SndbObjectLegalHoldRecord record) =>
        new(
            record.Bucket,
            record.Key,
            record.VersionId,
            record.Enabled,
            record.Reason,
            record.UpdatedUtc);

    private SndbObjectRecord ResolveExistingObjectVersion(string bucket, string key, string? versionId)
    {
        EnsureBucket(bucket);
        ValidateObjectKey(key);
        var record = LoadObjectRecord(bucket, key, versionId);
        if (record is null)
            throw new SndbObjectStorageException("object_not_found", $"Object '{bucket}/{key}' was not found.");

        return record;
    }

    private SndbObjectLegalHoldRecord? LoadLegalHold(string bucket, string key, string versionId)
    {
        var entry = _metadata.GetEntry(LegalHoldKey(bucket, key, versionId));
        return entry is null
            ? null
            : Deserialize(entry.Value.Span, SndbObjectStoreJsonContext.Default.SndbObjectLegalHoldRecord);
    }

    private void EnsureObjectVersionCanBeDeleted(SndbObjectRecord record, bool isLatest)
    {
        if (IsObjectVersionProtected(record, isLatest, out string code, out string message))
            throw new SndbObjectStorageException(code, message);
    }

    private bool IsObjectVersionProtected(string bucket, string key, string versionId, bool isLatest)
    {
        var record = LoadObjectRecord(bucket, key, versionId);
        return record is not null && IsObjectVersionProtected(record, isLatest, out _, out _);
    }

    private bool IsObjectVersionProtected(SndbObjectRecord record, bool isLatest, out string code, out string message)
    {
        var hold = LoadLegalHold(record.Bucket, record.Key, record.VersionId);
        if (hold?.Enabled == true)
        {
            code = "object_legal_hold";
            message = $"Object version '{record.Bucket}/{record.Key}@{record.VersionId}' is under legal hold.";
            return true;
        }

        if (!record.IsDeleteMarker)
        {
            var retention = LoadRetention(record.Bucket);
            int? days = isLatest ? retention?.RetainCurrentForDays : retention?.RetainNoncurrentForDays;
            if (IsRetained(record.CreatedUtc, days))
            {
                code = "object_retained";
                message = $"Object version '{record.Bucket}/{record.Key}@{record.VersionId}' is retained by bucket policy.";
                return true;
            }
        }

        code = string.Empty;
        message = string.Empty;
        return false;
    }

    private SndbBucketRetentionRecord? LoadRetention(string bucket)
    {
        var entry = _metadata.GetEntry(RetentionKey(bucket));
        return entry is null
            ? null
            : Deserialize(entry.Value.Span, SndbObjectStoreJsonContext.Default.SndbBucketRetentionRecord);
    }

    private SndbBucketQuotaRecord? LoadQuota(string bucket)
    {
        var entry = _metadata.GetEntry(QuotaKey(bucket));
        return entry is null
            ? null
            : Deserialize(entry.Value.Span, SndbObjectStoreJsonContext.Default.SndbBucketQuotaRecord);
    }

    private void EnsureQuotaAllowsDelta(string bucket, long additionalBytes, long additionalObjectVersions)
    {
        var quota = LoadQuota(bucket);
        if (quota is null)
            return;

        var usage = ComputeUsage(bucket);
        long projectedBytes = usage.ObjectVersionSizeBytes + usage.MultipartPartSizeBytes + additionalBytes;
        long projectedVersions = usage.ObjectVersionCount + additionalObjectVersions;
        if (quota.MaxSizeBytes is { } maxBytes && projectedBytes > maxBytes)
            throw new SndbObjectStorageException("quota_exceeded", $"Bucket '{bucket}' size quota would be exceeded.");
        if (quota.MaxObjectVersions is { } maxVersions && projectedVersions > maxVersions)
            throw new SndbObjectStorageException("quota_exceeded", $"Bucket '{bucket}' object version quota would be exceeded.");
    }

    private BucketUsage ComputeUsage(string bucket)
    {
        long currentObjectCount = 0;
        long currentSizeBytes = 0;
        long objectVersionCount = 0;
        long objectVersionSizeBytes = 0;
        long deleteMarkerCount = 0;

        foreach (var entry in _metadata.ScanPrefix(ObjectBucketPrefix(bucket), limit: int.MaxValue))
        {
            var record = Deserialize(entry.Value.Span, SndbObjectStoreJsonContext.Default.SndbObjectRecord);
            if (record.IsDeleteMarker)
            {
                deleteMarkerCount++;
                continue;
            }

            objectVersionCount++;
            objectVersionSizeBytes += record.SizeBytes;
        }

        foreach (var latest in _metadata.ScanPrefix(LatestObjectPrefix(bucket), limit: int.MaxValue))
        {
            string key = UnescapeKey(Utf8.GetString(latest.Key.Span)[LatestObjectPrefix(bucket).Length..]);
            string versionId = Utf8.GetString(latest.Value.Span);
            var record = LoadObjectRecord(bucket, key, versionId);
            if (record is null || record.IsDeleteMarker)
                continue;

            currentObjectCount++;
            currentSizeBytes += record.SizeBytes;
        }

        long multipartUploadCount = 0;
        long multipartPartCount = 0;
        long multipartPartSizeBytes = 0;
        foreach (var uploadEntry in _metadata.ScanPrefix(UploadPrefix, limit: int.MaxValue))
        {
            var upload = Deserialize(uploadEntry.Value.Span, SndbObjectStoreJsonContext.Default.SndbMultipartUploadRecord);
            if (!string.Equals(upload.Bucket, bucket, StringComparison.Ordinal) || upload.Status != Active)
                continue;

            multipartUploadCount++;
            foreach (var partEntry in _metadata.ScanPrefix(PartPrefix + upload.UploadId + ":", limit: int.MaxValue))
            {
                var part = Deserialize(partEntry.Value.Span, SndbObjectStoreJsonContext.Default.SndbMultipartPartRecord);
                multipartPartCount++;
                multipartPartSizeBytes += part.SizeBytes;
            }
        }

        return new BucketUsage(
            currentObjectCount,
            currentSizeBytes,
            objectVersionCount,
            objectVersionSizeBytes,
            deleteMarkerCount,
            multipartUploadCount,
            multipartPartCount,
            multipartPartSizeBytes);
    }

    private bool HasActiveMultipartUploads(string bucket)
    {
        foreach (var uploadEntry in _metadata.ScanPrefix(UploadPrefix, limit: int.MaxValue))
        {
            var upload = Deserialize(uploadEntry.Value.Span, SndbObjectStoreJsonContext.Default.SndbMultipartUploadRecord);
            if (string.Equals(upload.Bucket, bucket, StringComparison.Ordinal) && upload.Status == Active)
                return true;
        }

        return false;
    }

    private void AppendAudit(
        string action,
        string bucket,
        string? key,
        string? versionId,
        IReadOnlyDictionary<string, string>? details = null)
    {
        SndbObjectAuditRecord record = CreateAuditRecord(action, bucket, key, versionId, details);
        _metadata.Put(AuditKey(bucket, record.Id), Serialize(record, SndbObjectStoreJsonContext.Default.SndbObjectAuditRecord));
    }

    /// <summary>
    /// 创建规范化的对象存储审计记录。
    /// </summary>
    private static SndbObjectAuditRecord CreateAuditRecord(
        string action,
        string bucket,
        string? key,
        string? versionId,
        IReadOnlyDictionary<string, string>? details = null)
    {
        var now = DateTimeOffset.UtcNow;
        string id = now.ToUnixTimeMilliseconds().ToString("D13") + "-" + Guid.NewGuid().ToString("N");
        return new SndbObjectAuditRecord(
            id,
            action,
            bucket,
            key,
            versionId,
            now,
            NormalizeMap(details));
    }

    /// <summary>
    /// 将对象内容异步写入文件，并在单次遍历中计算大小、ETag 与 SHA-256。
    /// </summary>
    private static async Task<(long Size, string ETag, string Sha256)> WriteContentAndHashAsync(
        Stream content,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            ObjectIoBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var md5 = MD5.Create();
        using var sha256 = SHA256.Create();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ObjectIoBufferSize);
        long size = 0;
        try
        {
            while (true)
            {
                int read = await content.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                md5.TransformBlock(buffer, 0, read, null, 0);
                sha256.TransformBlock(buffer, 0, read, null, 0);
                size += read;
            }

            md5.TransformFinalBlock([], 0, 0);
            sha256.TransformFinalBlock([], 0, 0);
            // 单次强制落盘确保正文先于后续 rename 与元数据 WAL 持久化。
            destination.Flush(flushToDisk: true);
            return (size, QuoteHex(md5.Hash!), Convert.ToHexString(sha256.Hash!).ToLowerInvariant());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// 按请求顺序合并 multipart 分片，在单遍 I/O 中计算摘要并把完整临时文件强制落盘。
    /// </summary>
    private async Task<(long Size, string ETag, string Sha256)> MergeMultipartPartsAsync(
        IReadOnlyList<SndbMultipartPartRecord> parts,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            ObjectIoBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var md5 = MD5.Create();
        using var sha256 = SHA256.Create();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ObjectIoBufferSize);
        long size = 0;
        try
        {
            foreach (var part in parts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var input = new FileStream(
                    ResolveStoragePath(part.StoragePath),
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read | FileShare.Delete,
                    ObjectIoBufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                while (true)
                {
                    int read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                        break;

                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    md5.TransformBlock(buffer, 0, read, null, 0);
                    sha256.TransformBlock(buffer, 0, read, null, 0);
                    size += read;
                }
            }

            md5.TransformFinalBlock([], 0, 0);
            sha256.TransformFinalBlock([], 0, 0);
            destination.Flush(flushToDisk: true);
            return (size, QuoteHex(md5.Hash!), Convert.ToHexString(sha256.Hash!).ToLowerInvariant());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private string BuildObjectStoragePath(string bucket, string key, string versionId)
    {
        string objectHash = Sha256Hex(Utf8.GetBytes(bucket + "/" + key));
        return Path.Combine(_contentRoot, BucketHash(bucket), objectHash[..2], objectHash[2..4], versionId + ".bin");
    }

    private string BuildMultipartStoragePath(string bucket, string uploadId, int partNumber, string partId)
    {
        string uploadHash = Sha256Hex(Utf8.GetBytes(uploadId));
        return Path.Combine(_contentRoot, BucketHash(bucket), "multipart", uploadHash[..2], uploadId, partNumber.ToString("D5") + "-" + partId + ".part");
    }

    private string ToRelativeStoragePath(string fullPath) =>
        Path.GetRelativePath(_contentRoot, fullPath).Replace('\\', '/');

    private string ResolveStoragePath(string relativePath)
    {
        string path = Path.GetFullPath(Path.Combine(_contentRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_contentRoot));
        string rootWithSeparator = root + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(path, root, comparison)
            && !path.StartsWith(rootWithSeparator, comparison))
        {
            throw new SndbObjectStorageException("invalid_storage_path", "Object storage path is invalid.");
        }

        return path;
    }

    private static string BucketKey(string bucket) => BucketPrefix + bucket;

    private static string ObjectBucketPrefix(string bucket) => ObjectPrefix + bucket + "/";

    private static string LatestObjectPrefix(string bucket) => LatestPrefix + bucket + "/";

    private static string LatestObjectKey(string bucket, string key) => LatestObjectPrefix(bucket) + EscapeKey(key);

    private static string PolicyKey(string bucket) => PolicyPrefix + bucket;

    private static string LifecycleKey(string bucket) => LifecyclePrefix + bucket;

    private static string RetentionKey(string bucket) => RetentionPrefix + bucket;

    private static string QuotaKey(string bucket) => QuotaPrefix + bucket;

    private static string SemanticOptionsKey(string bucket) => SemanticOptionsPrefix + bucket;

    private static string LegalHoldKey(string bucket, string key, string versionId) =>
        LegalHoldPrefix + bucket + "/" + EscapeKey(key) + "/" + versionId;

    private static string AuditBucketPrefix(string bucket) => AuditPrefix + bucket + "/";

    private static string AuditKey(string bucket, string id) => AuditBucketPrefix(bucket) + id;

    private static string ObjectKeyPrefix(string bucket, string key) =>
        ObjectPrefix + bucket + "/" + EscapeKey(key) + "/";

    private static string ObjectKey(string bucket, string key, string versionId) =>
        ObjectKeyPrefix(bucket, key) + versionId;

    private static string UploadKey(string uploadId) => UploadPrefix + uploadId;

    private static string PartKey(string uploadId, int partNumber) => PartPrefix + uploadId + ":" + partNumber.ToString("D5");

    private static string PresignKey(string tokenHash) => PresignPrefix + tokenHash;

    private static string CreateVersionId() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString("D13") + "-" + Guid.NewGuid().ToString("N");

    private static string BucketHash(string bucket) => Sha256Hex(Utf8.GetBytes(bucket))[..16];

    private static string EscapeKey(string key) => Convert.ToBase64String(Utf8.GetBytes(key)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static string UnescapeKey(string key)
    {
        string padded = key.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
        return Utf8.GetString(Convert.FromBase64String(padded));
    }

    private static string EncodeContinuationToken(string key) => EscapeKey("v1:" + key);

    private static string? DecodeContinuationToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        try
        {
            string decoded = UnescapeKey(token.Trim());
            return decoded.StartsWith("v1:", StringComparison.Ordinal)
                ? decoded["v1:".Length..]
                : throw new ArgumentException("Invalid continuation token.", nameof(token));
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("Invalid continuation token.", nameof(token), ex);
        }
    }

    private static string NormalizeContentType(string? contentType) =>
        string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType.Trim();

    private static string NormalizePurpose(string? purpose) =>
        string.IsNullOrWhiteSpace(purpose) ? SndbBucketPurpose.General : purpose.Trim();

    private static string? NormalizePolicyJson(string? policyJson)
    {
        if (string.IsNullOrWhiteSpace(policyJson))
            return null;

        try
        {
            using var document = JsonDocument.Parse(policyJson);
            return document.RootElement.GetRawText();
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("Bucket policy must be valid JSON.", nameof(policyJson), ex);
        }
    }

    private static string? NormalizeReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return null;

        string normalized = reason.Trim();
        if (normalized.Length > 1_024)
            throw new ArgumentOutOfRangeException(nameof(reason), "Legal hold reason cannot exceed 1024 characters.");
        return normalized;
    }

    private static string NormalizeMethod(string method)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        string normalized = method.Trim().ToUpperInvariant();
        return normalized is "GET" or "HEAD" or "PUT" or "DELETE"
            ? normalized
            : throw new ArgumentException($"Unsupported object method '{method}'.", nameof(method));
    }

    private static Dictionary<string, string> NormalizeMap(IReadOnlyDictionary<string, string>? values)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (values is null)
            return result;

        foreach (var pair in values)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
                continue;
            result[pair.Key.Trim()] = pair.Value ?? string.Empty;
        }

        return result;
    }

    private static void ValidateBucket(string bucket)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        if (bucket.Length is < 3 or > 63)
            throw new ArgumentException("Bucket name length must be between 3 and 63.", nameof(bucket));

        foreach (char ch in bucket)
        {
            bool valid = ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.';
            if (!valid)
                throw new ArgumentException("Bucket name must contain only lowercase letters, digits, '-' or '.'.", nameof(bucket));
        }
    }

    private static void ValidateObjectKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Length > 1_024)
            throw new ArgumentOutOfRangeException(nameof(key), "Object key cannot exceed 1024 characters.");
    }

    private static void ValidateUploadId(string uploadId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uploadId);
        if (!uploadId.StartsWith("mpu_", StringComparison.Ordinal) || uploadId.Length > 80)
            throw new ArgumentException("Invalid multipart upload id.", nameof(uploadId));
    }

    private static void ValidateLifecycleDays(int? days)
    {
        if (days is < 0)
            throw new ArgumentOutOfRangeException(nameof(days), "Lifecycle days cannot be negative.");
    }

    private static void ValidateQuota(long? value, string parameterName)
    {
        if (value is < 0)
            throw new ArgumentOutOfRangeException(parameterName, "Quota cannot be negative.");
    }

    private static void ValidateThumbnailDimension(int value, string parameterName)
    {
        if (value is < 16 or > 4_096)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "缩略图宽高必须位于 16 到 4096 像素。");
        }
    }

    private static bool IsRetained(DateTimeOffset createdUtc, int? days)
    {
        if (days is null or 0)
            return false;

        return createdUtc > DateTimeOffset.UtcNow.AddDays(-days.Value);
    }

    private static bool ShouldExpire(DateTimeOffset createdUtc, int? days, DateTimeOffset utcNow)
    {
        if (days is null)
            return false;
        if (days.Value == 0)
            return true;

        return createdUtc <= utcNow.AddDays(-days.Value);
    }

    private static string QuoteHex(byte[] hash) => "\"" + Convert.ToHexString(hash).ToLowerInvariant() + "\"";

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static byte[] Serialize<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);

    private static T Deserialize<T>(ReadOnlySpan<byte> json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.Deserialize(json, typeInfo)
        ?? throw new InvalidDataException("Object storage metadata is empty.");

    /// <summary>只回收内容根目录内的存储文件，损坏的越界元数据保持拒绝且不触碰外部路径。</summary>
    private void TryDeleteStorageFile(string relativePath)
    {
        try
        {
            string path = ResolveStoragePath(relativePath);
            TryDeleteFile(path);
            SonnetDB.Wal.DirectoryFsync.FlushBestEffort(Path.GetDirectoryName(path)!);
        }
        catch (SndbObjectStorageException ex) when (ex.Code == "invalid_storage_path")
        {
            // 损坏元数据不能把清理范围扩大到对象根目录之外。
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record BucketUsage(
        long CurrentObjectCount,
        long CurrentSizeBytes,
        long ObjectVersionCount,
        long ObjectVersionSizeBytes,
        long DeleteMarkerCount,
        long MultipartUploadCount,
        long MultipartPartCount,
        long MultipartPartSizeBytes);

    /// <summary>
    /// 保存同一 keyspace 各 bucket 对象提交的共享互斥状态。
    /// </summary>
    private sealed class ObjectMutationState
    {
        private readonly ConcurrentDictionary<string, BucketMutationState> _bucketMutations =
            new(StringComparer.Ordinal);

        /// <summary>
        /// 获取指定 bucket 的共享提交状态。
        /// </summary>
        public BucketMutationState GetBucketMutationState(string bucket)
        {
            // 不移除 gate，避免删除并重建同名 bucket 时并发操作落到不同锁。
            return _bucketMutations.GetOrAdd(bucket, static _ => new BucketMutationState());
        }

        /// <summary>
        /// 查找已经存在的 bucket 提交状态，不因无效请求创建常驻条目。
        /// </summary>
        public BucketMutationState? FindBucketMutationState(string bucket)
        {
            _bucketMutations.TryGetValue(bucket, out BucketMutationState? state);
            return state;
        }
    }

    /// <summary>
    /// 协调单个 bucket 的对象与 multipart 元数据提交。
    /// </summary>
    private sealed class BucketMutationState
    {
        private long _lastVersionTimestampTicks = DateTimeOffset.MinValue.Ticks;

        /// <summary>
        /// 获取严格单调的版本时间戳；调用方必须已持有 <see cref="Gate"/>。
        /// </summary>
        public DateTimeOffset GetNextVersionTimestamp()
        {
            long nowTicks = DateTimeOffset.UtcNow.Ticks;
            long nextTicks = Math.Max(nowTicks, checked(_lastVersionTimestampTicks + 1));
            _lastVersionTimestampTicks = nextTicks;
            return new DateTimeOffset(nextTicks, TimeSpan.Zero);
        }

        /// <summary>
        /// 协调同一 bucket 元数据提交的共享锁。
        /// </summary>
        public object Gate { get; } = new();
    }

    private sealed class BoundedReadStream : Stream
    {
        private readonly Stream _inner;
        private long _remaining;

        public BoundedReadStream(Stream inner, long length)
        {
            _inner = inner;
            _remaining = length;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _remaining;
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0)
                return 0;
            int toRead = (int)Math.Min(count, _remaining);
            int read = _inner.Read(buffer, offset, toRead);
            _remaining -= read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining <= 0)
                return 0;
            int toRead = (int)Math.Min(buffer.Length, _remaining);
            int read = await _inner.ReadAsync(buffer[..toRead], cancellationToken).ConfigureAwait(false);
            _remaining -= read;
            return read;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
