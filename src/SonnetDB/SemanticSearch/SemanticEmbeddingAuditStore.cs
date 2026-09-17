using System.Globalization;
using System.Text;
using System.Text.Json;
using SonnetDB.Contracts;
using SonnetDB.Engine;
using SonnetDB.Json;
using SonnetDB.Kv;

namespace SonnetDB.SemanticSearch;

/// <summary>复用数据库 KV 的 WAL、TTL 和有界分页保存调用审计，随单库备份恢复。</summary>
internal sealed class SemanticEmbeddingAuditStore(Tsdb tsdb)
{
    internal const string KeyspaceName = "__semantic_embedding_audit";

    /// <summary>该 keyspace 只允许服务内部访问；同时拒绝 Windows 大小写和末尾点路径别名。</summary>
    internal static bool IsReservedName(string name)
        => string.Equals(name.TrimEnd('.'), KeyspaceName, StringComparison.OrdinalIgnoreCase);

    private readonly KvKeyspace _keyspace = tsdb.Keyspaces.Open(KeyspaceName);

    /// <summary>验证弱 WAL 配置下显式同步失败也会阻断内容外发。</summary>
    internal Action? WalSyncForTest { set => _keyspace.WalSyncTestHook = value; }

    internal void Write(SemanticEmbeddingAuditEntry entry, CancellationToken cancellationToken)
    {
        string key = "call:" + (long.MaxValue - entry.StartedUtc.UtcTicks).ToString("D19", CultureInfo.InvariantCulture)
            + ":" + entry.CallId.ToString("N");
        _ = _keyspace.ApplyConditionalBatch(
            [KvBatchMutation.Put(Encoding.UTF8.GetBytes(key),
                JsonSerializer.SerializeToUtf8Bytes(entry, ServerJsonContext.Default.SemanticEmbeddingAuditEntry),
                entry.StartedUtc.AddDays(30))], [], cancellationToken);
        // 调用方可以关闭普通 KV 的逐写同步；审计始终在返回前建立单独的耐久屏障。
        _keyspace.SyncWalForMaintenance(cancellationToken);
    }

    internal SemanticEmbeddingAuditPage Read(int limit, string? continuationToken, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 200);
        byte[]? after = null;
        if (continuationToken is not null)
        {
            if (continuationToken.Length > 128)
                throw new ArgumentException("审计分页令牌无效。", nameof(continuationToken));
            try { after = Convert.FromBase64String(continuationToken); }
            catch (FormatException) { throw new ArgumentException("审计分页令牌无效。", nameof(continuationToken)); }
            if (after.Length != 57 || !after.AsSpan().StartsWith("call:"u8))
                throw new ArgumentException("审计分页令牌无效。", nameof(continuationToken));
        }
        _keyspace.EnableOrderedOverlayScans(cancellationToken);
        var page = _keyspace.ScanCandidatePage("call:"u8.ToArray(), "call;"u8.ToArray(), after,
            limit, Math.Max(256, limit * 8), TimeSpan.FromMilliseconds(200), cancellationToken);
        var entries = new List<SemanticEmbeddingAuditEntry>(page.Entries.Count);
        foreach (var row in page.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            entries.Add(JsonSerializer.Deserialize(row.Value.Span, ServerJsonContext.Default.SemanticEmbeddingAuditEntry)
                ?? throw new InvalidDataException("embedding 审计记录损坏。"));
        }
        return new SemanticEmbeddingAuditPage(entries,
            page.HasMore && page.ContinuationKey is not null ? Convert.ToBase64String(page.ContinuationKey) : null);
    }
}
