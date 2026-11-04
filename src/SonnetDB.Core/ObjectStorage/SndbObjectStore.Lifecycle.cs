using System.Runtime.CompilerServices;
using SonnetDB.Kv;

namespace SonnetDB.ObjectStorage;

public sealed partial class SndbObjectStore
{
    private static readonly ConditionalWeakTable<object, SemaphoreSlim> _lifecycleRunners = new();
    internal Action<string>? LifecycleKeyReadyTestHook { get; set; }
    internal Action<string>? LifecycleKeyProcessedTestHook { get; set; }

    /// <summary>按稳定快照逐对象执行生命周期，读者只保留当前页与当前对象版本，不全桶装载。</summary>
    /// <param name="bucket">待维护的对象桶。</param>
    /// <param name="cancellationToken">调用方取消令牌；取消在对象之间及页读取时生效。</param>
    /// <returns>本次完成的版本回收统计及当前对象过期清单。</returns>
    public SndbBucketLifecycleApplyResult ApplyLifecycle(string bucket, CancellationToken cancellationToken)
        => ApplyLifecycleCore(bucket, objectKey: null, cancellationToken);

    /// <summary>只回收指定对象的过期历史版本，适合业务已知删除对象的增量维护。</summary>
    /// <param name="bucket">对象桶。</param>
    /// <param name="key">精确对象键，不能为空或作为前缀使用。</param>
    /// <param name="cancellationToken">调用方取消令牌。</param>
    /// <returns>该对象的生命周期执行结果。</returns>
    public SndbBucketLifecycleApplyResult ApplyObjectLifecycle(string bucket, string key, CancellationToken cancellationToken = default)
    {
        ValidateObjectKey(key);
        return ApplyLifecycleCore(bucket, key, cancellationToken);
    }

    /// <summary>全桶及精确对象维护共享同一版本保护、协同和分页执行路径。</summary>
    private SndbBucketLifecycleApplyResult ApplyLifecycleCore(string bucket, string? objectKey, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(15));
        CancellationToken token = deadline.Token;
        token.ThrowIfCancellationRequested();
        object mutationGate = GetBucketMutationState(bucket, token).Gate;
        SemaphoreSlim runner = _lifecycleRunners.GetValue(mutationGate, static _ => new SemaphoreSlim(1, 1));
        if (!runner.Wait(TimeSpan.FromMinutes(15), token)) throw new TimeoutException("Object lifecycle runner is busy.");
        try
        {
            KvReadSnapshot snapshot;
            SndbBucketLifecycleInfo lifecycle;
            lock (mutationGate)
            {
                token.ThrowIfCancellationRequested();
                EnsureBucket(bucket);
                lifecycle = GetLifecycle(bucket);
                snapshot = _metadata.AcquireReadSnapshot();
            }
            using (snapshot)
            using (KvRangeCursor cursor = snapshot.OpenRangeCursor(new KvRangeScanOptions
            {
                Prefix = Utf8.GetBytes(objectKey is null ? ObjectBucketPrefix(bucket) : ObjectKeyPrefix(bucket, objectKey)),
                PageSize = 256,
            }))
            {
                int expiredCurrent = 0, removedNoncurrent = 0, removedMarkers = 0, scanned = 0, changedKeys = 0;
                var expiredObjects = new List<SndbLifecycleExpiredObject>();
                var versions = new List<SndbObjectRecord>();
                string? key = null;
                DateTimeOffset now = DateTimeOffset.UtcNow;

                // 页数与墙钟均有退出边界；正在写入的新版本不进入本轮冻结扫描。
                for (int page = 0; page < 1_000_000 && !cursor.IsExhausted; page++)
                {
                    token.ThrowIfCancellationRequested();
                    foreach (KvEntry entry in cursor.ReadNextPage(token))
                    {
                        token.ThrowIfCancellationRequested();
                        SndbObjectRecord record = Deserialize(entry.Value.Span, SndbObjectStoreJsonContext.Default.SndbObjectRecord);
                        if (key is not null && !string.Equals(key, record.Key, StringComparison.Ordinal))
                        {
                            var result = ApplyLifecycleKey(bucket, key, versions, lifecycle, snapshot, now, expiredObjects, mutationGate, token);
                            expiredCurrent += result.Current; removedNoncurrent += result.Noncurrent;
                            removedMarkers += result.Markers; changedKeys += result.Changed ? 1 : 0;
                            versions.Clear();
                        }
                        key = record.Key;
                        versions.Add(record);
                        scanned = checked(scanned + 1);
                    }
                }
                if (!cursor.IsExhausted) throw new TimeoutException("Object lifecycle scan exceeded its page budget.");
                if (key is not null)
                {
                    var result = ApplyLifecycleKey(bucket, key, versions, lifecycle, snapshot, now, expiredObjects, mutationGate, token);
                    expiredCurrent += result.Current; removedNoncurrent += result.Noncurrent;
                    removedMarkers += result.Markers; changedKeys += result.Changed ? 1 : 0;
                }
                AppendObjectVersionsListAudit(bucket, objectKey, scanned);
                AppendAudit(objectKey is null ? "bucket.lifecycle.apply" : "object.lifecycle.apply", bucket, objectKey, null, new Dictionary<string, string>
                {
                    ["expiredCurrentObjects"] = expiredCurrent.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["removedNoncurrentVersions"] = removedNoncurrent.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["removedDeleteMarkers"] = removedMarkers.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["concurrentlyChangedObjects"] = changedKeys.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
                return new SndbBucketLifecycleApplyResult(bucket, expiredCurrent, removedNoncurrent, removedMarkers)
                {
                    ExpiredObjects = expiredObjects,
                };
            }
        }
        finally { runner.Release(); }
    }

    /// <summary>仅在当前对象提交期间占用桶写锁；快照后有新写入的对象留给后续维护，保护新版本。</summary>
    private (int Current, int Noncurrent, int Markers, bool Changed) ApplyLifecycleKey(
        string bucket, string key, IReadOnlyList<SndbObjectRecord> versions, SndbBucketLifecycleInfo lifecycle,
        KvReadSnapshot snapshot, DateTimeOffset now, List<SndbLifecycleExpiredObject> expiredObjects,
        object mutationGate, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        byte[] latestKey = Utf8.GetBytes(LatestObjectKey(bucket, key));
        KvEntry? initial = snapshot.GetEntry(latestKey, token);
        string? initialLatest = initial is null ? null : Utf8.GetString(initial.Value.Span);
        LifecycleKeyReadyTestHook?.Invoke(key);
        int currentCount = 0, noncurrentCount = 0, markerCount = 0;
        lock (mutationGate)
        {
            token.ThrowIfCancellationRequested();
            KvEntry? actual = _metadata.GetEntry(latestKey);
            string? actualLatest = actual is null ? null : Utf8.GetString(actual.Value.Span);
            if (!string.Equals(initialLatest, actualLatest, StringComparison.Ordinal)) return (0, 0, 0, true);
            var removals = new List<SndbObjectRecord>();
            bool newDeleteMarker = false;
            foreach (SndbObjectRecord version in versions)
            {
                token.ThrowIfCancellationRequested();
                bool latest = string.Equals(version.VersionId, initialLatest, StringComparison.Ordinal);
                if (version.IsDeleteMarker)
                {
                    if (ShouldExpire(version.CreatedUtc, lifecycle.ExpireDeleteMarkerAfterDays, now)
                        && !IsObjectVersionProtected(version, latest, out _, out _))
                    {
                        removals.Add(version); markerCount++;
                    }
                }
                else if (latest)
                {
                    if (ShouldExpire(version.CreatedUtc, lifecycle.ExpireCurrentAfterDays, now)
                        && !IsObjectVersionProtected(version, latest, out _, out _))
                    {
                        DeleteObject(bucket, key);
                        newDeleteMarker = true;
                        expiredObjects.Add(new SndbLifecycleExpiredObject(key, version.VersionId, version.ContentType));
                        currentCount++;
                    }
                }
                else if (ShouldExpire(version.CreatedUtc, lifecycle.ExpireNoncurrentAfterDays, now)
                    && !IsObjectVersionProtected(version, latest, out _, out _))
                {
                    removals.Add(version); noncurrentCount++;
                }
            }
            if (removals.Count > 0)
                RemoveObjectVersionsForLifecycle(bucket, key, versions, removals, initialLatest, newDeleteMarker);
        }
        LifecycleKeyProcessedTestHook?.Invoke(key);
        return (currentCount, noncurrentCount, markerCount, false);
    }
}
