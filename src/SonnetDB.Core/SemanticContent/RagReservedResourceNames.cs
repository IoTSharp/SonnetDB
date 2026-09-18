namespace SonnetDB.SemanticContent;

/// <summary>RAG 治理入口使用的内部资源名称规则；可信嵌入式 SDK 可直接访问这些资源。</summary>
public static class RagReservedResourceNames
{
    /// <summary>判断名称是否为 RAG 任务、管理审计或 generation 物理资源，包含 Windows 尾点/尾空格别名。</summary>
    /// <param name="name">资源名称。</param>
    /// <returns>名称属于保留资源时为 true。</returns>
    public static bool IsReserved(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        // Windows 可交替忽略尾点与空格，用一次有界 span 扫描归一化。
        int length = name.Length;
        for (int i = name.Length - 1; i >= 0 && (name[i] == '.' || name[i] == ' '); i--)
            length = i;
        ReadOnlySpan<char> normalized = name.AsSpan(0, length);
        return normalized.Equals("rag-ingestion-jobs", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("__rag_management_audit", StringComparison.OrdinalIgnoreCase)
            || (normalized.Length == 36 && normalized[..4].Equals("rag_", StringComparison.OrdinalIgnoreCase)
                && Guid.TryParseExact(normalized[4..], "N", out _));
    }
}
