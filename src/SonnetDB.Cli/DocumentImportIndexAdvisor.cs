using System.Globalization;
using System.Text.Json;

namespace SonnetDB.Cli;

/// <summary>
/// 把 mongodump metadata 索引转换为 SonnetDB 建议，并为无 metadata 输入提供保守启发式建议。
/// </summary>
internal static class DocumentImportIndexAdvisor
{
    /// <summary>生成只读索引建议；不会自动创建索引。</summary>
    internal static IReadOnlyList<DocumentImportIndexSuggestion> Build(
        string? metadataPath,
        IReadOnlyDictionary<string, int> scalarPathCounts,
        int sampledDocuments,
        DocumentImportGapCollector gaps)
    {
        var suggestions = metadataPath is null
            ? new List<DocumentImportIndexSuggestion>()
            : ReadMetadata(metadataPath, gaps);
        var existing = suggestions.SelectMany(static item => item.Paths).ToHashSet(StringComparer.Ordinal);
        if (sampledDocuments > 0)
        {
            foreach (var pair in scalarPathCounts
                .Where(pair => pair.Key != "$._id" && pair.Value >= Math.Max(2, sampledDocuments * 4 / 5))
                .OrderByDescending(static pair => pair.Value)
                .ThenBy(static pair => pair.Key, StringComparer.Ordinal)
                .Take(8))
            {
                if (!existing.Add(pair.Key))
                    continue;
                suggestions.Add(new DocumentImportIndexSuggestion(
                    "suggest_" + SanitizeName(pair.Key),
                    [pair.Key],
                    "path",
                    Supported: true,
                    Unique: false,
                    Sparse: false,
                    TtlPath: null,
                    TtlSeconds: null,
                    Source: "sample_heuristic",
                    GapReason: "字段在样本文档中高频出现；必须结合真实 query/EXPLAIN 人工确认。"));
            }
        }
        return suggestions;
    }

    private static List<DocumentImportIndexSuggestion> ReadMetadata(
        string path,
        DocumentImportGapCollector gaps)
    {
        try
        {
            return ReadMetadataCore(path, gaps);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            gaps.Add(
                "mongodb_metadata_invalid",
                "partial",
                $"mongodump metadata 无法解析，已跳过索引建议: {ex.Message}");
            return [];
        }
    }

    private static List<DocumentImportIndexSuggestion> ReadMetadataCore(
        string path,
        DocumentImportGapCollector gaps)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        if (!document.RootElement.TryGetProperty("indexes", out var indexes)
            || indexes.ValueKind != JsonValueKind.Array)
        {
            gaps.Add("mongodb_metadata_without_indexes", "partial", "mongodump metadata 未包含 indexes 数组。");
            return [];
        }

        var result = new List<DocumentImportIndexSuggestion>();
        foreach (var index in indexes.EnumerateArray())
        {
            string name = index.TryGetProperty("name", out var nameValue) && nameValue.ValueKind == JsonValueKind.String
                ? nameValue.GetString() ?? "unnamed"
                : "unnamed";
            if (name == "_id_")
                continue;
            if (!index.TryGetProperty("key", out var key) || key.ValueKind != JsonValueKind.Object)
            {
                gaps.Add("mongodb_index_key_missing", "partial", $"索引 '{name}' 没有可读取的 key 定义。");
                continue;
            }

            var paths = new List<string>();
            bool wildcard = false;
            string? unsupported = null;
            foreach (var field in key.EnumerateObject())
            {
                if (field.Name == "$**" || field.Name.EndsWith(".$**", StringComparison.Ordinal))
                {
                    wildcard = true;
                    string root = field.Name == "$**" ? "$" : ToJsonPath(field.Name[..^4]);
                    paths.Add(root);
                    continue;
                }
                if (field.Value.ValueKind != JsonValueKind.Number)
                    unsupported = $"MongoDB index key kind '{field.Value.GetRawText()}' 不映射为 SonnetDB path index。";
                paths.Add(ToJsonPath(field.Name));
            }

            bool unique = index.TryGetProperty("unique", out var uniqueValue) && uniqueValue.ValueKind == JsonValueKind.True;
            bool sparse = index.TryGetProperty("sparse", out var sparseValue) && sparseValue.ValueKind == JsonValueKind.True;
            long? ttlSeconds = index.TryGetProperty("expireAfterSeconds", out var ttlValue)
                && ttlValue.TryGetInt64(out long ttl) ? ttl : null;
            bool partial = index.TryGetProperty("partialFilterExpression", out _);
            if (wildcard && paths.Count != 1)
                unsupported = "SonnetDB wildcard index 只支持一个 subtree root path。";
            if (wildcard && (unique || ttlSeconds is not null))
                unsupported ??= "SonnetDB wildcard index 不支持 unique 或 TTL。";
            if (partial)
                unsupported ??= "partialFilterExpression 需要人工转换为 SonnetDB 单条件 partial filter。";

            if (unsupported is not null)
                gaps.Add("mongodb_index_requires_review", "partial", unsupported);
            result.Add(new DocumentImportIndexSuggestion(
                name,
                paths,
                wildcard ? "wildcard" : "path",
                Supported: unsupported is null,
                unique,
                sparse,
                ttlSeconds is null ? null : paths.FirstOrDefault(),
                ttlSeconds,
                "mongodump_metadata",
                unsupported));
        }
        return result;
    }

    private static string ToJsonPath(string mongoPath)
        => "$" + string.Concat(mongoPath.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(static segment => "." + segment));

    private static string SanitizeName(string path)
    {
        string value = new(path.Where(static c => char.IsLetterOrDigit(c) ? true : c == '_').ToArray());
        if (value.Length == 0)
            value = "field";
        return value.Length <= 48 ? value : value[..48];
    }
}
