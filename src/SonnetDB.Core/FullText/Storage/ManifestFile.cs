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
            IndexManifest created = new();
            Save(directory, created);
            return created;
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

    public static void Save(string directory, IndexManifest manifest)
    {
        Directory.CreateDirectory(directory);
        string path = GetPath(directory);
        string tempPath = path + ".tmp";

        using (FileStream stream = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            System.Text.Json.JsonSerializer.Serialize(stream, manifest, IndexManifestJsonContext.Default.IndexManifest);
        }

        if (File.Exists(path))
        {
            File.Delete(path);
        }
        File.Move(tempPath, path);
    }

    public static string GetPath(string directory) => Path.Combine(directory, "manifest.json");
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(IndexManifest))]
internal sealed partial class IndexManifestJsonContext : JsonSerializerContext
{
}
