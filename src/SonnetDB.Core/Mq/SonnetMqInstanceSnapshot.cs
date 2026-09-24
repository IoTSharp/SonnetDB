using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SonnetMQ;

/// <summary>SonnetMQ 实例快照中的文件校验项。</summary>
/// <param name="path">快照目录内的规范相对路径。</param>
/// <param name="length">文件长度（字节）。</param>
/// <param name="sha256">文件内容的 SHA-256 十六进制摘要。</param>
public sealed record SonnetMqSnapshotFile(string Path, long Length, string Sha256);

/// <summary>
/// SonnetMQ 实例级一致快照 manifest。快照包含全局 Topic 日志、消息头、offset、消费者组位点和 retention 记录。
/// </summary>
/// <param name="formatVersion">manifest 格式版本。</param>
/// <param name="createdUtc">快照创建时间（UTC）。</param>
/// <param name="files">快照文件校验项。</param>
public sealed record SonnetMqInstanceSnapshotManifest(
    int FormatVersion,
    DateTimeOffset CreatedUtc,
    IReadOnlyList<SonnetMqSnapshotFile> Files)
{
    /// <summary>当前快照 manifest 版本。</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>manifest 文件名。</summary>
    public const string FileName = "sonnetmq.snapshot.json";
}

/// <summary>SonnetMQ 实例快照结果。</summary>
/// <param name="snapshotDirectory">快照目录。</param>
/// <param name="manifestPath">manifest 文件路径。</param>
/// <param name="fileCount">快照文件数量。</param>
/// <param name="totalBytes">快照文件总字节数。</param>
public sealed record SonnetMqInstanceSnapshotResult(
    string SnapshotDirectory,
    string ManifestPath,
    int FileCount,
    long TotalBytes);

internal static class SonnetMqInstanceSnapshotCodec
{
    public static SonnetMqInstanceSnapshotManifest ReadManifest(string directory)
    {
        string path = Path.Combine(Path.GetFullPath(directory), SonnetMqInstanceSnapshotManifest.FileName);
        if (!File.Exists(path))
            throw new FileNotFoundException("SonnetMQ 实例快照 manifest 不存在。", path);

        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize(stream, SonnetMqSnapshotJsonContext.Default.SonnetMqInstanceSnapshotManifest)
            ?? throw new InvalidDataException("SonnetMQ 实例快照 manifest 为空。");
    }

    public static void WriteManifest(string directory, SonnetMqInstanceSnapshotManifest manifest)
    {
        string path = Path.Combine(directory, SonnetMqInstanceSnapshotManifest.FileName);
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(stream, manifest, SonnetMqSnapshotJsonContext.Default.SonnetMqInstanceSnapshotManifest);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}

[JsonSerializable(typeof(SonnetMqInstanceSnapshotManifest))]
[JsonSerializable(typeof(SonnetMqSnapshotFile))]
[JsonSerializable(typeof(IReadOnlyList<SonnetMqSnapshotFile>))]
internal sealed partial class SonnetMqSnapshotJsonContext : JsonSerializerContext;
