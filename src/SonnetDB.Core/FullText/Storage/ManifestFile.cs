using System.Text.Json.Serialization;

namespace SonnetDB.FullText.Storage;

internal sealed class SegmentManifestEntry
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("doc_count")]
    public int DocCount { get; set; }

    [JsonPropertyName("size_bytes")]
    public long SizeBytes { get; set; }
}

internal sealed class IndexManifest
{
    [JsonPropertyName("format_version")]
    public int FormatVersion { get; set; } = 1;

    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("next_segment_id")]
    public long NextSegmentId { get; set; } = 1;

    [JsonPropertyName("active_segments")]
    public List<SegmentManifestEntry> ActiveSegments { get; set; } = new();

    [JsonPropertyName("tombstones")]
    public Dictionary<string, List<int>> Tombstones { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("updated_document_ids")]
    public Dictionary<string, long> UpdatedDocumentIds { get; set; } = new(StringComparer.Ordinal);
}

internal static class ManifestFile
{
    public static IndexManifest LoadOrCreate(string directory)
    {
        Directory.CreateDirectory(directory);
        string path = GetPath(directory);
        if (!File.Exists(path))
        {
            // manifest 缺失：可能是 delete-then-move 中途崩溃（旧实现），或从未写过。
            // 若 segments 目录已有段文件，则从段文件重建 manifest，避免"整个全文索引静默变空"（#192）。
            IndexManifest recovered = TryRebuildFromSegments(directory);
            Save(directory, recovered);
            return recovered;
        }

        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        IndexManifest manifest = System.Text.Json.JsonSerializer.Deserialize(stream, IndexManifestJsonContext.Default.IndexManifest)
            ?? throw new FormatException($"Invalid manifest file: {path}");
        if (manifest.FormatVersion != 1)
        {
            throw new NotSupportedException($"Unsupported manifest format version: {manifest.FormatVersion}");
        }
        manifest.ActiveSegments ??= new List<SegmentManifestEntry>();
        manifest.Tombstones ??= new Dictionary<string, List<int>>(StringComparer.Ordinal);
        manifest.UpdatedDocumentIds ??= new Dictionary<string, long>(StringComparer.Ordinal);
        return manifest;
    }

    /// <summary>
    /// manifest 缺失时从 <c>segments/*.seg</c> 文件重建：枚举段文件、读取每段的 Id / 文档数 / 大小，
    /// 恢复 <see cref="IndexManifest.ActiveSegments"/> 与 <see cref="IndexManifest.NextSegmentId"/>。
    /// 无法从段文件恢复的墓碑（tombstones）留空——宁可少删（部分已删文档可能重现），也不整体丢失索引。
    /// 段目录不存在或无合法段文件时，退化为空 manifest（与旧行为一致）。
    /// </summary>
    private static IndexManifest TryRebuildFromSegments(string directory)
    {
        var manifest = new IndexManifest();
        string segmentsDir = Path.Combine(directory, "segments");
        if (!Directory.Exists(segmentsDir))
            return manifest;

        long maxId = 0;
        foreach (string segPath in Directory.EnumerateFiles(segmentsDir, "*.seg"))
        {
            SegmentReader reader;
            try
            {
                reader = SegmentFile.Read(segPath);
            }
            catch
            {
                // 跳过损坏 / 不完整的段文件（如崩溃中断的 .tmp 改名残留），不阻止重建。
                continue;
            }

            manifest.ActiveSegments.Add(new SegmentManifestEntry
            {
                Id = reader.Id,
                DocCount = reader.DocumentCount,
                SizeBytes = reader.SizeBytes,
            });
            if (reader.Id > maxId)
                maxId = reader.Id;
        }

        manifest.NextSegmentId = maxId + 1;
        return manifest;
    }

    public static void Save(string directory, IndexManifest manifest)
    {
        Directory.CreateDirectory(directory);
        string path = GetPath(directory);
        string tempPath = path + ".tmp";

        using (FileStream stream = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            System.Text.Json.JsonSerializer.Serialize(stream, manifest, IndexManifestJsonContext.Default.IndexManifest);
            stream.Flush();
            stream.Flush(flushToDisk: true); // fsync temp 内容，确保原子替换后内容完整
        }

        // 原子替换：绝不 delete-then-move（中途崩溃会丢失 manifest）。
        // File.Move(overwrite:true) 在 Windows/Unix 上都是原子 rename。#192
        File.Move(tempPath, path, overwrite: true);
        SonnetDB.Wal.DirectoryFsync.FlushBestEffort(directory);
    }

    public static string GetPath(string directory) => Path.Combine(directory, "manifest.json");
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(IndexManifest))]
internal sealed partial class IndexManifestJsonContext : JsonSerializerContext
{
}
