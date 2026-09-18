namespace SonnetDB.SemanticSearch;

internal static class RagManagementResourceNames
{
    internal static bool IsReserved(string name)
    {
        string canonical = name.TrimEnd('.', ' ');
        return string.Equals(canonical, RagManagementAuditStore.KeyspaceName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(canonical, "rag-ingestion-jobs", StringComparison.OrdinalIgnoreCase)
            || canonical.Length == 36 && canonical.StartsWith("rag_", StringComparison.OrdinalIgnoreCase)
                && Guid.TryParseExact(canonical.AsSpan(4), "N", out _);
    }
}
