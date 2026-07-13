using System.Text.Json;
using System.Text.Json.Serialization;

namespace SonnetDB.Kv;

/// <summary>记录 generation 切换后待后台删除的旧 KV state 文件。</summary>
internal static class KvCleanupManifest
{
    public const string FileName = "cleanup.manifest.json";

    public static KvCleanupManifestModel? Load(string rootDirectory)
    {
        string path = Path.Combine(rootDirectory, FileName);
        if (!File.Exists(path))
            return null;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return JsonSerializer.Deserialize(stream, KvCleanupJsonContext.Default.KvCleanupManifestModel)
            ?? throw new InvalidDataException("KV cleanup manifest 为空。");
    }

    public static void Save(string rootDirectory, KvCleanupManifestModel manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        Directory.CreateDirectory(rootDirectory);
        string path = Path.Combine(rootDirectory, FileName);
        string tempPath = path + ".tmp";
        using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, manifest, KvCleanupJsonContext.Default.KvCleanupManifestModel);
            stream.Flush(flushToDisk: true);
        }

        File.Move(tempPath, path, overwrite: true);
    }

    public static void Delete(string rootDirectory)
    {
        string path = Path.Combine(rootDirectory, FileName);
        if (File.Exists(path))
            File.Delete(path);
    }
}

internal sealed record KvCleanupManifestModel(
    int Version,
    long Generation,
    long CreatedUtcTicks,
    IReadOnlyList<string> Files)
{
    public const int CurrentVersion = 1;
}

[JsonSerializable(typeof(KvCleanupManifestModel))]
internal sealed partial class KvCleanupJsonContext : JsonSerializerContext;
