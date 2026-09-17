using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using SonnetDB.Kv;

namespace SonnetDB.ObjectStorage;

public sealed partial class SndbObjectStore
{
    private const int ObjectListPageSize = 256;
    private static readonly TimeSpan ObjectListTimeout = TimeSpan.FromMinutes(2);

    internal Action? ListCandidateTestHook { get; set; }
    internal Action? ListRebuildEntryTestHook { get; set; }
    internal Action? ListPhysicalCandidateTestHook { get; set; }

    /// <summary>按原始 key 的 ordinal 顺序列出当前可见对象，支持目录分组和取消。</summary>
    /// <param name="bucket">对象桶。</param>
    /// <param name="prefix">对象前缀；保留既有移除前导斜杠的约定。</param>
    /// <param name="maxKeys">对象和公共前缀的合计上限，必须大于零。</param>
    /// <param name="continuationToken">上一页令牌；普通列表继续兼容 v1 令牌。</param>
    /// <param name="delimiter">目录分隔符；空值表示不分组。</param>
    /// <param name="cancellationToken">取消令牌；锁等待、重建及分页均观察取消。</param>
    /// <returns>当前页；只在存在下一项时返回后续令牌。</returns>
    /// <remarks>旧数据库首次列表需重建派生索引。每次调用最多两分钟，超时不返回半页。
    /// 各页读取调用时的当前状态，不固定跨页快照。索引与对象版本共用原有 KV/WAL 批次。</remarks>
    public SndbObjectListResult ListObjects(
        string bucket, string? prefix, int maxKeys, string? continuationToken,
        string? delimiter, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxKeys);
        cancellationToken.ThrowIfCancellationRequested();
        string normalizedPrefix = prefix?.TrimStart('/') ?? string.Empty;
        delimiter = string.IsNullOrEmpty(delimiter) ? null : delimiter;
        SndbObjectPageCursor? cursor = DecodeObjectPageCursor(bucket, normalizedPrefix, delimiter, continuationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ObjectListTimeout);
        CancellationToken token = deadline.Token;
        var state = GetBucketMutationState(bucket, token);
        bool entered = false;
        long started = Stopwatch.GetTimestamp();
        try
        {
            for (int attempt = 0; attempt < 2400 && Stopwatch.GetElapsedTime(started) < ObjectListTimeout; attempt++)
            {
                token.ThrowIfCancellationRequested();
                if (Monitor.TryEnter(state.Gate, 50)) { entered = true; break; }
            }
            token.ThrowIfCancellationRequested();
            if (!entered)
                throw new TimeoutException("Object list timed out waiting for the bucket lock.");
            GetRequiredBucketEntry(bucket, token);
            EnsureObjectListIndex(bucket, token);

            byte[] scanPrefix = ObjectListKey(bucket, normalizedPrefix);
            byte[]? after = cursor is null ? null : ObjectListKey(bucket, cursor.Key);
            byte[]? start = cursor?.IsCommonPrefix == true ? PrefixSuccessor(after!) : null;
            if (cursor?.IsCommonPrefix == true)
                after = null;
            var objects = new List<SndbObjectInfo>(Math.Min(maxKeys, ObjectListPageSize));
            var commonPrefixes = new List<string>();
            SndbObjectPageCursor? last = null;
            bool truncated = false;
            bool exhausted = cursor?.IsCommonPrefix == true && start is null;
            int count = 0;
            long physicalCandidates = 0;
            long candidateBudget = Math.Max(1024, ((long)maxKeys + 1) * 8);
            void VisitCandidate()
            {
                token.ThrowIfCancellationRequested();
                ListPhysicalCandidateTestHook?.Invoke();
                if (++physicalCandidates > candidateBudget)
                    throw new SndbObjectStorageException("object_list_scan_budget_exceeded",
                        "Object list exceeded its physical candidate budget. Retry after the object metadata keyspace has completed a checkpoint or compaction.");
            }

            // 一次只读剩余名额加一条探测；分组每次 seek 直接越过整个公共前缀。
            for (long iteration = 0; !exhausted && iteration <= (long)maxKeys + 1; iteration++)
            {
                token.ThrowIfCancellationRequested();
                int take = delimiter is null ? (int)Math.Min(ObjectListPageSize, (long)maxKeys - count + 1) : 1;
                var entries = _metadata.ScanRange(scanPrefix, start, null, after, take, token, VisitCandidate);
                if (entries.Count == 0)
                    break;
                foreach (var entry in entries)
                {
                    token.ThrowIfCancellationRequested();
                    ListCandidateTestHook?.Invoke();
                    token.ThrowIfCancellationRequested();
                    string key = DecodeObjectListKey(bucket, entry.Key.Span);
                    int separator = delimiter is null ? -1 : key.IndexOf(delimiter, normalizedPrefix.Length, StringComparison.Ordinal);
                    string? common = separator < 0 ? null : key[..(separator + delimiter!.Length)];
                    if (common is not null && cursor is not null && string.CompareOrdinal(common, cursor.Key) <= 0)
                    {
                        start = PrefixSuccessor(ObjectListKey(bucket, common));
                        after = null;
                        exhausted = start is null;
                        continue;
                    }
                    if (count == maxKeys) { truncated = true; exhausted = true; break; }
                    if (common is not null)
                    {
                        commonPrefixes.Add(common);
                        start = PrefixSuccessor(ObjectListKey(bucket, common));
                        after = null;
                        exhausted = start is null;
                    }
                    else
                    {
                        var record = LoadObjectRecord(bucket, key, Utf8.GetString(entry.Value.Span), token);
                        if (record is null || record.IsDeleteMarker)
                            throw new InvalidDataException("Object listing index references missing or deleted metadata; rebuild the listing index.");
                        objects.Add(ToInfo(record));
                        after = entry.Key.ToArray();
                    }
                    last = new SndbObjectPageCursor(bucket, normalizedPrefix, delimiter, common ?? key, common is not null);
                    count++;
                }
            }
            token.ThrowIfCancellationRequested();
            string? next = truncated && last is not null ? EncodeObjectPageCursor(last) : null;
            var audit = CreateAuditRecord("bucket.objects.list", bucket, null, null, new Dictionary<string, string>
            {
                ["prefix"] = normalizedPrefix,
                ["count"] = objects.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
            _metadata.ApplyConditionalBatch([KvBatchMutation.Put(Utf8.GetBytes(AuditKey(bucket, audit.Id)),
                Serialize(audit, SndbObjectStoreJsonContext.Default.SndbObjectAuditRecord))], [], token);
            return new SndbObjectListResult(bucket, normalizedPrefix, maxKeys, continuationToken, next, truncated, objects.ToArray())
            {
                Delimiter = delimiter,
                CommonPrefixes = commonPrefixes.ToArray(),
            };
        }
        finally
        {
            if (entered)
                Monitor.Exit(state.Gate);
        }
    }

    private void EnsureObjectListIndex(string bucket, CancellationToken token)
    {
        _metadata.EnableOrderedOverlayScans(token);
        if (_metadata.GetEntry(ObjectListReadyKey(bucket), token) is { } ready && ready.Value.Span.SequenceEqual("1"u8))
            return;
        // 完成标记最后提交；中断后先清掉旧派生项，再从权威 latest 重建，不发布部分索引。
        using var budget = _metadata.EnterIndexRebuildBudgetScope(token);
        byte[] derivedPrefix = ObjectListKey(bucket, string.Empty);
        byte[]? after = null;
        for (int page = 0; page < 1_000_000; page++)
        {
            token.ThrowIfCancellationRequested();
            var entries = _metadata.ScanRange(derivedPrefix, null, null, after, ObjectListPageSize, token);
            if (entries.Count == 0)
                break;
            after = entries[^1].Key.ToArray();
            _metadata.ApplyIndexRebuildBatch(entries.Select(static entry => KvBatchMutation.Delete(entry.Key.ToArray())).ToArray(), token);
            if (page == 999_999)
                throw new IOException("Object listing index cleanup exceeded its page budget.");
        }

        string latestPrefix = LatestObjectPrefix(bucket);
        after = null;
        for (int page = 0; page < 1_000_000; page++)
        {
            token.ThrowIfCancellationRequested();
            var entries = _metadata.ScanRange(Utf8.GetBytes(latestPrefix), null, null, after, ObjectListPageSize, token);
            if (entries.Count == 0)
                break;
            var mutations = new List<KvBatchMutation>(entries.Count);
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                ListRebuildEntryTestHook?.Invoke();
                string key = UnescapeKey(Utf8.GetString(entry.Key.Span)[latestPrefix.Length..]);
                var record = LoadObjectRecord(bucket, key, Utf8.GetString(entry.Value.Span), token);
                if (record is not null && !record.IsDeleteMarker)
                    mutations.Add(ObjectListMutation(record));
            }
            token.ThrowIfCancellationRequested();
            if (mutations.Count > 0)
                _metadata.ApplyIndexRebuildBatch(mutations, token);
            after = entries[^1].Key.ToArray();
            if (page == 999_999)
                throw new IOException("Object listing index rebuild exceeded its page budget.");
        }
        token.ThrowIfCancellationRequested();
        _metadata.ApplyIndexRebuildBatch([KvBatchMutation.Put(Utf8.GetBytes(ObjectListReadyKey(bucket)), "1"u8.ToArray())], token);
    }

    private static string ObjectListReadyKey(string bucket) => "object-list-ready-v1:" + bucket;

    private static KvBatchMutation ObjectListMutation(SndbObjectRecord record) => record.IsDeleteMarker
        ? KvBatchMutation.Delete(ObjectListKey(record.Bucket, record.Key))
        : KvBatchMutation.Put(ObjectListKey(record.Bucket, record.Key), Utf8.GetBytes(record.VersionId));

    // UTF-16 code unit 大端字节顺序等于 .NET ordinal；不使用 UTF-8 或 Base64 的排序。
    private static byte[] ObjectListKey(string bucket, string key)
    {
        byte[] prefix = Utf8.GetBytes("object-list-v1:" + bucket + ":");
        byte[] bytes = new byte[checked(prefix.Length + key.Length * 2)];
        prefix.CopyTo(bytes, 0);
        for (int index = 0; index < key.Length; index++)
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(prefix.Length + index * 2), key[index]);
        return bytes;
    }

    private static string DecodeObjectListKey(string bucket, ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<byte> key = bytes[Utf8.GetByteCount("object-list-v1:" + bucket + ":")..];
        if (key.Length % 2 != 0)
            throw new InvalidDataException("Invalid object listing index key.");
        char[] chars = new char[key.Length / 2];
        for (int index = 0; index < chars.Length; index++)
            chars[index] = (char)BinaryPrimitives.ReadUInt16BigEndian(key[(index * 2)..]);
        return new string(chars);
    }

    private static byte[]? PrefixSuccessor(byte[] prefix)
    {
        for (int index = prefix.Length - 1; index >= 0; index--)
        {
            if (prefix[index] == byte.MaxValue)
                continue;
            byte[] successor = prefix[..(index + 1)];
            successor[index]++;
            return successor;
        }
        return null;
    }

    private static string EncodeObjectPageCursor(SndbObjectPageCursor cursor) => cursor.Delimiter is null
        ? EncodeContinuationToken(cursor.Key)
        : EscapeKey("v2:" + JsonSerializer.Serialize(cursor, SndbObjectStoreJsonContext.Default.SndbObjectPageCursor));

    private static SndbObjectPageCursor? DecodeObjectPageCursor(string bucket, string prefix, string? delimiter, string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;
        if (token.Length > 32_768)
            throw new ArgumentException("Invalid continuation token.", nameof(token));
        try
        {
            string decoded = UnescapeKey(token.Trim());
            if (decoded.StartsWith("v1:", StringComparison.Ordinal))
                return new SndbObjectPageCursor(bucket, prefix, delimiter, decoded[3..], false);
            if (decoded.StartsWith("v2:", StringComparison.Ordinal))
            {
                var cursor = JsonSerializer.Deserialize(decoded[3..], SndbObjectStoreJsonContext.Default.SndbObjectPageCursor);
                if (cursor is not null && cursor.Bucket == bucket && cursor.Prefix == prefix
                    && cursor.Delimiter == delimiter && cursor.Key is not null
                    && cursor.Key.StartsWith(prefix, StringComparison.Ordinal)
                    && (!cursor.IsCommonPrefix || (delimiter is not null && cursor.Key.EndsWith(delimiter, StringComparison.Ordinal))))
                    return cursor;
            }
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new ArgumentException("Invalid continuation token.", nameof(token), ex);
        }
        throw new ArgumentException("Invalid continuation token.", nameof(token));
    }
}

internal sealed record SndbObjectPageCursor(string Bucket, string Prefix, string? Delimiter, string Key, bool IsCommonPrefix);
