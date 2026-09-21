using System.Security.Cryptography;

namespace SonnetMQ;

public sealed partial class SonnetMqStore
{
    /// <summary>
    /// 创建全局 SonnetMQ 实例的一致快照。
    /// 快照期间发布、ACK、NACK、offset reset、retention 和新 Topic 创建均被阻断；
    /// 单库数据库备份不会自动包含该目录。
    /// </summary>
    /// <param name="destinationDirectory">快照目录；目录模式不可位于队列源目录内，单文件模式不可覆盖源文件。</param>
    /// <returns>包含 manifest 路径和文件统计的快照结果。</returns>
    public SonnetMqInstanceSnapshotResult CreateSnapshot(string destinationDirectory)
    {
        EnsureNotDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        string sourceRoot = SnapshotSourceRoot();
        string destination = Path.GetFullPath(destinationDirectory);
        string sourceBoundary = _options.OpenMode == SonnetMqOpenMode.SingleFile
            ? RootDirectory
            : sourceRoot;
        if (PathsOverlap(sourceBoundary, destination))
            throw new ArgumentException("SonnetMQ 快照目录不能位于当前实例源目录内。", nameof(destinationDirectory));
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            throw new IOException("SonnetMQ 快照目标目录必须为空。");

        Directory.CreateDirectory(destination);
        _snapshotGate.EnterWriteLock();
        try
        {
            EnsureNotDisposed();
            TopicState[] states = _topics.Values.OrderBy(static state => state.Topic, StringComparer.Ordinal).ToArray();
            var lockedStates = new List<TopicState>(states.Length);
            try
            {
                foreach (TopicState state in states)
                {
                    Monitor.Enter(state.SyncRoot);
                    lockedStates.Add(state);
                }

                FlushSnapshotFiles(states);
                var entries = new List<SonnetMqSnapshotFile>();
                foreach (string sourcePath in EnumerateSnapshotFiles(sourceRoot))
                {
                    string relativePath = Path.GetRelativePath(sourceRoot, sourcePath);
                    string targetPath = ResolveSnapshotPath(destination, relativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                    File.Copy(sourcePath, targetPath, overwrite: false);
                    entries.Add(new SonnetMqSnapshotFile(
                        NormalizeRelativePath(relativePath),
                        new FileInfo(targetPath).Length,
                        ComputeSha256(targetPath)));
                }

                entries.Sort(static (left, right) => string.CompareOrdinal(left.Path, right.Path));
                var manifest = new SonnetMqInstanceSnapshotManifest(
                    SonnetMqInstanceSnapshotManifest.CurrentFormatVersion,
                    DateTimeOffset.UtcNow,
                    entries.AsReadOnly());
                SonnetMqInstanceSnapshotCodec.WriteManifest(destination, manifest);
                return new SonnetMqInstanceSnapshotResult(
                    destination,
                    Path.Combine(destination, SonnetMqInstanceSnapshotManifest.FileName),
                    entries.Count,
                    entries.Sum(static entry => entry.Length));
            }
            finally
            {
                for (int i = lockedStates.Count - 1; i >= 0; i--)
                    Monitor.Exit(lockedStates[i].SyncRoot);
            }
        }
        catch
        {
            TryDeleteDirectory(destination);
            throw;
        }
        finally
        {
            _snapshotGate.ExitWriteLock();
        }
    }

    /// <summary>读取并校验快照 manifest 的版本和文件路径合同。</summary>
    /// <param name="snapshotDirectory">快照目录。</param>
    /// <returns>快照 manifest。</returns>
    public static SonnetMqInstanceSnapshotManifest ReadSnapshotManifest(string snapshotDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotDirectory);
        var manifest = SonnetMqInstanceSnapshotCodec.ReadManifest(snapshotDirectory);
        ValidateManifest(snapshotDirectory, manifest, verifyFiles: true);
        return manifest;
    }

    /// <summary>
    /// 将实例快照恢复到新的 SonnetMQ 目录，并在发布前校验每个文件的长度和 SHA-256。
    /// </summary>
    /// <param name="snapshotDirectory">快照目录。</param>
    /// <param name="targetDirectory">恢复目标目录。</param>
    /// <param name="overwrite">目标目录已有内容时是否替换整个目标目录。</param>
    /// <returns>恢复使用的 manifest。</returns>
    public static SonnetMqInstanceSnapshotManifest RestoreSnapshot(
        string snapshotDirectory,
        string targetDirectory,
        bool overwrite = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        string source = Path.GetFullPath(snapshotDirectory);
        string target = Path.GetFullPath(targetDirectory);
        if (PathsOverlap(source, target))
            throw new ArgumentException("SonnetMQ 恢复目标不能覆盖快照源目录。", nameof(targetDirectory));

        var manifest = SonnetMqInstanceSnapshotCodec.ReadManifest(source);
        ValidateManifest(source, manifest, verifyFiles: true);
        if (Directory.Exists(target)
            && Directory.EnumerateFileSystemEntries(target).Any()
            && !overwrite)
            throw new IOException("SonnetMQ 恢复目标目录非空，必须显式允许覆盖。");

        string staging = target + ".restore-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(staging);
            foreach (SonnetMqSnapshotFile entry in manifest.Files)
            {
                string sourcePath = ResolveSnapshotPath(source, entry.Path);
                string targetPath = ResolveSnapshotPath(staging, entry.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                File.Copy(sourcePath, targetPath, overwrite: false);
            }

            ValidateManifest(staging, manifest, verifyFiles: true);
            if (Directory.Exists(target))
                Directory.Delete(target, recursive: true);
            Directory.Move(staging, target);
            return manifest;
        }
        catch
        {
            TryDeleteDirectory(staging);
            throw;
        }
    }

    private string SnapshotSourceRoot()
        => _options.OpenMode == SonnetMqOpenMode.SingleFile
            ? Path.GetDirectoryName(RootDirectory) ?? throw new InvalidOperationException("SonnetMQ 单文件路径没有父目录。")
            : RootDirectory;

    private void FlushSnapshotFiles(IReadOnlyList<TopicState> states)
    {
        if (_options.OpenMode == SonnetMqOpenMode.SingleFile)
        {
            _singleFileStream?.Flush(flushToDisk: true);
            return;
        }

        foreach (TopicState state in states)
            state.Writer?.Flush(flushToDisk: true);
    }

    private IEnumerable<string> EnumerateSnapshotFiles(string sourceRoot)
    {
        if (_options.OpenMode == SonnetMqOpenMode.SingleFile)
        {
            if (File.Exists(RootDirectory))
                yield return RootDirectory;
            yield break;
        }

        foreach (string path in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
            yield return path;
    }

    private static void ValidateManifest(
        string directory,
        SonnetMqInstanceSnapshotManifest manifest,
        bool verifyFiles)
    {
        if (manifest.FormatVersion != SonnetMqInstanceSnapshotManifest.CurrentFormatVersion)
            throw new InvalidDataException($"不支持的 SonnetMQ 快照版本 {manifest.FormatVersion}。");
        if (manifest.Files is null)
            throw new InvalidDataException("SonnetMQ 快照 manifest 缺少文件列表。");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (SonnetMqSnapshotFile entry in manifest.Files)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Path) || entry.Length < 0)
                throw new InvalidDataException("SonnetMQ 快照 manifest 包含无效文件项。");
            string normalized = NormalizeRelativePath(entry.Path);
            if (!seen.Add(normalized) || !string.Equals(normalized, entry.Path, StringComparison.Ordinal))
                throw new InvalidDataException("SonnetMQ 快照 manifest 包含重复或非规范路径。");
            if (!verifyFiles)
                continue;

            string path = ResolveSnapshotPath(directory, entry.Path);
            if (!File.Exists(path))
                throw new InvalidDataException($"SonnetMQ 快照缺少文件：{entry.Path}。");
            var info = new FileInfo(path);
            if (info.Length != entry.Length || !string.Equals(ComputeSha256(path), entry.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"SonnetMQ 快照文件校验失败：{entry.Path}。");
        }
    }

    private static string ResolveSnapshotPath(string root, string relativePath)
    {
        string normalized = NormalizeRelativePath(relativePath);
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(fullRoot, normalized));
        if (!path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SonnetMQ 快照路径越过目录边界。");
        return path;
    }

    private static string NormalizeRelativePath(string path)
    {
        string normalized = path.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized)
            || Path.IsPathRooted(normalized)
            || normalized.Split('/').Any(static part => part is "" or "." or ".."))
            throw new InvalidDataException("SonnetMQ 快照包含无效相对路径。");
        return normalized;
    }

    private static bool PathsOverlap(string left, string right)
    {
        string normalizedLeft = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string normalizedRight = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return normalizedLeft.StartsWith(normalizedRight, StringComparison.OrdinalIgnoreCase)
            || normalizedRight.StartsWith(normalizedLeft, StringComparison.OrdinalIgnoreCase);
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 128 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
