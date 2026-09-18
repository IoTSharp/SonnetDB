using System.Globalization;
using System.Text;
using System.Text.Json;
using SonnetDB.Contracts;
using SonnetDB.Engine;
using SonnetDB.Json;
using SonnetDB.Kv;

namespace SonnetDB.SemanticSearch;

internal sealed class RagManagementAuditStore(Tsdb database)
{
    internal const string KeyspaceName = "__rag_management_audit";
    private readonly KvKeyspace _keyspace = database.Keyspaces.Open(KeyspaceName);

    internal void Write(RagManagementAuditEntry entry, CancellationToken token)
    {
        string key = "call:" + (long.MaxValue - entry.StartedUtc.UtcTicks).ToString("D19", CultureInfo.InvariantCulture)
            + ":" + entry.OperationId.ToString("N");
        _ = _keyspace.ApplyConditionalBatch([KvBatchMutation.Put(Encoding.UTF8.GetBytes(key),
            JsonSerializer.SerializeToUtf8Bytes(entry, ServerJsonContext.Default.RagManagementAuditEntry),
            entry.StartedUtc.AddDays(30))], [], token);
        _keyspace.SyncWalForMaintenance(token);
    }

    internal RagManagementAuditPage Read(int limit, string? continuationToken, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 200);
        byte[]? after = null;
        if (continuationToken is not null)
        {
            if (continuationToken.Length > 128) throw new ArgumentException("审计游标无效。");
            try { after = Convert.FromBase64String(continuationToken); }
            catch (FormatException) { throw new ArgumentException("审计游标无效。"); }
            if (after.Length != 57 || !after.AsSpan().StartsWith("call:"u8)) throw new ArgumentException("审计游标无效。");
        }
        _keyspace.EnableOrderedOverlayScans(token);
        var page = _keyspace.ScanCandidatePage("call:"u8.ToArray(), "call;"u8.ToArray(), after,
            limit, Math.Max(256, limit * 8), TimeSpan.FromMilliseconds(200), token);
        var rows = new List<RagManagementAuditEntry>(page.Entries.Count);
        foreach (var row in page.Entries)
        {
            token.ThrowIfCancellationRequested();
            rows.Add(JsonSerializer.Deserialize(row.Value.Span, ServerJsonContext.Default.RagManagementAuditEntry)
                ?? throw new InvalidDataException("RAG 审计记录损坏。"));
        }
        return new(rows, page.HasMore && page.ContinuationKey is not null ? Convert.ToBase64String(page.ContinuationKey) : null);
    }
}
