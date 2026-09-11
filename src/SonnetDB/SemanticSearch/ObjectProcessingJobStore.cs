using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SonnetDB.Engine;
using SonnetDB.Json;
using SonnetDB.Kv;

namespace SonnetDB.SemanticSearch;

/// <summary>以同一 KV 原子批次维护任务正文和到期索引；终态仅保留审计正文。</summary>
internal sealed class ObjectProcessingJobStore
{
    internal const string KeyspaceName = "__semantic_object_processing";
    internal const string JobPrefix = "job:";
    internal const string DuePrefix = "due-v1:";
    private const string MigrationKey = "scheduler-v1:legacy-cursor";
    private const string MigrationComplete = "complete";
    private const string DueCursorKey = "scheduler-v1:due-cursor";
    private const string BackfillPrefix = "backfill-v1:";
    private static readonly TimeSpan CompletedRetention = TimeSpan.FromDays(7);
    private readonly KvKeyspace _keyspace;

    /// <summary>打开任务 keyspace；不在构造或业务请求中扫描旧记录。</summary>
    internal ObjectProcessingJobStore(Tsdb tsdb) => _keyspace = tsdb.Keyspaces.Open(KeyspaceName);

    /// <summary>仅供故障注入测试验证原子提交之前失败的恢复边界。</summary>
    internal Action? BeforeCommitForTest { get; set; }
    /// <summary>测试中统计到期范围实际访问候选数，不对业务暴露。</summary>
    internal Action? DueCandidateVisitedForTest { get; set; }
    /// <summary>测试中检测迁移完成后是否意外重新读取历史。</summary>
    internal Action? LegacyCandidateVisitedForTest { get; set; }

    /// <summary>按主键读取正文及乐观并发版本。</summary>
    internal StoredJob? Read(string id, CancellationToken cancellationToken)
    {
        var entry = _keyspace.GetEntry(JobPrefix + id, cancellationToken);
        return entry is null ? null : Deserialize(entry);
    }

    /// <summary>读取 Bucket 回填游标，不扫描任务正文。</summary>
    internal StoredBackfill? ReadBackfill(string bucket, CancellationToken cancellationToken)
    {
        var entry = _keyspace.GetEntry(BackfillKey(bucket), cancellationToken);
        if (entry is null)
            return null;
        var state = JsonSerializer.Deserialize(entry.Value.Span, ServerJsonContext.Default.ObjectSemanticBackfillState);
        return state is null ? null : new StoredBackfill(state, entry.Version);
    }

    /// <summary>以 CAS 持久化回填游标，避免并发请求覆盖已推进的页。</summary>
    internal bool TryWriteBackfill(StoredBackfill? previous, ObjectSemanticBackfillState state, CancellationToken cancellationToken)
    {
        byte[] key = Encode(BackfillKey(state.Bucket));
        byte[] value = JsonSerializer.SerializeToUtf8Bytes(state, ServerJsonContext.Default.ObjectSemanticBackfillState);
        return _keyspace.ApplyConditionalBatch(
            [KvBatchMutation.Put(key, value)],
            [KvBatchPrecondition.KeyVersion(key, previous?.Version ?? 0)],
            cancellationToken).Applied;
    }

    /// <summary>在一个原子批次中保存正文、替换 due 键，旧 worker 不能覆盖后来的任务。</summary>
    internal bool TryWrite(StoredJob? previous, SemanticObjectProcessingJob job, CancellationToken cancellationToken)
    {
        byte[] key = Encode(JobPrefix + job.Id);
        var mutations = new List<KvBatchMutation>(3);
        string? previousDue = previous is null ? null : DueKey(previous.Job);
        string? nextDue = DueKey(job);
        if (previousDue is not null && previousDue != nextDue)
            mutations.Add(KvBatchMutation.Delete(Encode(previousDue)));
        mutations.Add(KvBatchMutation.Put(key,
            JsonSerializer.SerializeToUtf8Bytes(job, ServerJsonContext.Default.SemanticObjectProcessingJob),
            nextDue is null ? DateTimeOffset.UtcNow.Add(CompletedRetention) : null));
        if (nextDue is not null)
            mutations.Add(KvBatchMutation.Put(Encode(nextDue), Encode(job.Id)));
        BeforeCommitForTest?.Invoke();
        return _keyspace.ApplyConditionalBatch(mutations,
            [KvBatchPrecondition.KeyVersion(key, previous?.Version ?? 0)], cancellationToken).Applied;
    }

    /// <summary>只读取已经到期的有界索引页；未来重试及已完成任务不会参与扫描。</summary>
    internal IReadOnlyList<KvEntry> ReadDue(DateTimeOffset now, int limit, CancellationToken cancellationToken)
    {
        _keyspace.EnableOrderedOverlayScans(cancellationToken);
        // 分隔符 ';' 在 ':' 后，包含当前 tick 的全部任务而不访问未来到期键。
        string end = DuePrefix + now.UtcTicks.ToString("D19", CultureInfo.InvariantCulture) + ";";
        var cursor = _keyspace.GetEntry(DueCursorKey, cancellationToken);
        byte[]? after = cursor is null || cursor.Value.IsEmpty ? null : cursor.Value.ToArray();
        var page = _keyspace.ScanCandidatePage(
            Encode(DuePrefix), Encode(end), after, limit, Math.Max(limit * 16, 256),
            TimeSpan.FromMilliseconds(200), cancellationToken);
        if (page.ContinuationKey is not null && page.HasMore)
        {
            _ = _keyspace.ApplyConditionalBatch(
                [KvBatchMutation.Put(Encode(DueCursorKey), page.ContinuationKey)],
                [KvBatchPrecondition.KeyVersion(Encode(DueCursorKey), cursor?.Version ?? 0)],
                cancellationToken);
        }
        else if (cursor is not null)
        {
            _ = _keyspace.ApplyConditionalBatch(
                [KvBatchMutation.Delete(Encode(DueCursorKey))],
                [KvBatchPrecondition.KeyVersion(Encode(DueCursorKey), cursor.Version)],
                cancellationToken);
        }
        if (DueCandidateVisitedForTest is { } hook)
            for (int index = 0; index < page.CandidatesVisited; index++) hook();
        return page.Entries;
    }

    /// <summary>按版本清理无正文或已被替换的索引条目，避免损坏条目永久挡住队头。</summary>
    internal void RemoveStaleDue(KvEntry entry, CancellationToken cancellationToken)
    {
        BeforeCommitForTest?.Invoke();
        _ = _keyspace.ApplyConditionalBatch([KvBatchMutation.Delete(entry.Key.ToArray())],
            [KvBatchPrecondition.KeyVersion(entry.Key.ToArray(), entry.Version)], cancellationToken);
    }

    /// <summary>以持久游标恢复旧版任务，每页有数量和取消预算；完成后不再扫描历史终态。</summary>
    internal MigrationPage MigrateLegacyPage(int limit, CancellationToken cancellationToken)
    {
        var cursor = _keyspace.GetEntry(MigrationKey, cancellationToken);
        string? after = cursor is null ? null : Encoding.UTF8.GetString(cursor.Value.Span);
        if (after == MigrationComplete)
            return new MigrationPage(0, 0, true, 0);
        _keyspace.EnableOrderedOverlayScans(cancellationToken);
        var rows = _keyspace.ScanRange(Encode(JobPrefix), null, null,
            after is null ? null : Encode(after), limit, cancellationToken, LegacyCandidateVisitedForTest);
        if (rows.Count == 0)
        {
            // 空 keyspace 也必须持久化完成标志，否则每个恢复周期都会重新打开并扫描前缀。
            BeforeCommitForTest?.Invoke();
            _ = _keyspace.ApplyConditionalBatch(
                [KvBatchMutation.Put(Encode(MigrationKey), Encode(MigrationComplete))],
                [KvBatchPrecondition.KeyVersion(Encode(MigrationKey), cursor?.Version ?? 0)],
                cancellationToken);
            return new MigrationPage(0, 0, true, 0);
        }
        int scheduled = 0;
        int corrupt = 0;
        var mutations = new List<KvBatchMutation>(rows.Count + 1);
        var conditions = new List<KvBatchPrecondition>(rows.Count + 1)
        {
            KvBatchPrecondition.KeyVersion(Encode(MigrationKey), cursor?.Version ?? 0),
        };
        foreach (var entry in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // 单条坏 JSON 保留原始审计并推进游标；不能让整个历史队列永远卡在同一条。
            StoredJob? stored;
            try { stored = Deserialize(entry); }
            catch (JsonException) { stored = null; corrupt++; }
            if (stored is not null && DueKey(stored.Job) is { } due)
            {
                mutations.Add(KvBatchMutation.Put(Encode(due), Encode(stored.Job.Id)));
                conditions.Add(KvBatchPrecondition.KeyVersion(entry.Key.ToArray(), entry.Version));
                scheduled++;
            }
        }
        bool complete = rows.Count < limit;
        string next = complete ? MigrationComplete : Encoding.UTF8.GetString(rows[^1].Key.Span);
        mutations.Add(KvBatchMutation.Put(Encode(MigrationKey), Encode(next)));
        BeforeCommitForTest?.Invoke();
        // 游标与本页 due 键只追加一次 WAL；冲突或取消整页重读，不出现游标越过未调度任务。
        bool applied = _keyspace.ApplyConditionalBatch(mutations, conditions, cancellationToken).Applied;
        return new MigrationPage(rows.Count, applied ? scheduled : 0, applied && complete, corrupt);
    }

    /// <summary>按持久状态判断是否可领取，processing 必须等租约到期。</summary>
    internal static bool IsDue(SemanticObjectProcessingJob job, DateTimeOffset now)
        => DueTime(job) is { } due && due <= now;

    /// <summary>生成固定宽度 UTC ticks 排序键，避免偏移分页及全量排序。</summary>
    internal static string? DueKey(SemanticObjectProcessingJob job)
        => DueTime(job) is { } due
            ? DuePrefix + due.UtcTicks.ToString("D19", CultureInfo.InvariantCulture) + ":" + job.Id
            : null;

    /// <summary>旧 processing 没有租约时按原更新时间恢复；新任务统一使用明确租约。</summary>
    private static DateTimeOffset? DueTime(SemanticObjectProcessingJob job) => job.Status switch
    {
        "pending" => job.NextAttemptUtc ?? job.CreatedUtc,
        "retry" => job.NextAttemptUtc ?? job.UpdatedUtc,
        "processing" => job.LeaseUntilUtc ?? job.NextAttemptUtc ?? job.UpdatedUtc,
        _ => null,
    };

    /// <summary>用 source-generated JSON 元数据读取持久正文。</summary>
    private static StoredJob? Deserialize(KvEntry entry)
    {
        var job = JsonSerializer.Deserialize(entry.Value.Span, ServerJsonContext.Default.SemanticObjectProcessingJob);
        return job is null ? null : new StoredJob(job, entry.Version);
    }

    /// <summary>将内部稳定索引键编码为 UTF-8。</summary>
    private static byte[] Encode(string value) => Encoding.UTF8.GetBytes(value);

    /// <summary>使用固定长度键隔离不同 Bucket 的回填状态。</summary>
    private static string BackfillKey(string bucket)
        => BackfillPrefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bucket))).ToLowerInvariant();

    /// <summary>持久任务及本次点读获得的 CAS 版本。</summary>
    internal sealed record StoredJob(SemanticObjectProcessingJob Job, long Version);
    /// <summary>回填状态及其 CAS 版本。</summary>
    internal sealed record StoredBackfill(ObjectSemanticBackfillState State, long Version);
    /// <summary>旧版恢复页的实际读取和排队数量。</summary>
    internal readonly record struct MigrationPage(int Scanned, int Scheduled, bool Complete, int Corrupt);
}
