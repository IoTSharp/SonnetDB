using System.Text;

namespace SonnetDB.Kv.Lsm;

/// <summary>数据库备份期间固定的 LSM 版本与 WAL 字节边界；文件复制期间后台维护可继续。</summary>
internal sealed class KvLsmBackupSet : IDisposable
{
    internal readonly List<KvLsmEngineBackup> Engines = [];

    internal bool ContainsManagedFile(string source)
        => Engines.Any(engine => engine.ContainsManagedFile(source));

    internal IReadOnlyList<string> CopyTo(string sourceRoot, string destinationRoot, Action<string>? copied, Predicate<string>? include = null)
    {
        var paths = new List<string>();
        foreach (KvLsmEngineBackup engine in Engines)
            engine.CopyTo(sourceRoot, destinationRoot, paths, copied, include);
        return paths;
    }

    public void Dispose()
    {
        foreach (KvLsmEngineBackup engine in Engines) engine.Dispose();
    }
}

internal sealed class KvLsmEngineBackup : IDisposable
{
    private readonly string _root;
    private readonly KvVersion _version;
    private readonly KvVersionEdit _snapshot;
    private readonly long _manifestNumber;
    private readonly Action _releaseReaders;
    private readonly List<(string Path, FileStream Stream, long Length)> _wal = [];
    private readonly byte[]? _marker;
    private bool _disposed;

    internal KvLsmEngineBackup(string root, KvVersion version, KvVersionEdit snapshot, long manifestNumber, Action releaseReaders)
    {
        _root = root;
        _version = version;
        _snapshot = snapshot;
        _manifestNumber = manifestNumber;
        _releaseReaders = releaseReaders;
        string markerPath = Path.Combine(root, KvGenerationFile.FileName);
        if (File.Exists(markerPath)) _marker = File.ReadAllBytes(markerPath);
    }

    internal void AddWal(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        _wal.Add((path, stream, stream.Length));
    }

    internal bool ContainsManagedFile(string source)
    {
        string relative = Path.GetRelativePath(_root, source).Replace('\\', '/');
        return relative == "CURRENT" || relative == KvGenerationFile.FileName
            || relative.StartsWith(KvLsmPaths.ManifestPrefix, StringComparison.Ordinal) && !relative.Contains('/')
            || relative.StartsWith("sst/", StringComparison.Ordinal) || relative.StartsWith("wal/", StringComparison.Ordinal);
    }

    internal void CopyTo(string sourceRoot, string destinationRoot, List<string> paths, Action<string>? copied, Predicate<string>? include)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        void Write(string sourcePath, Action<FileStream> write)
        {
            string relative = Path.GetRelativePath(sourceRoot, sourcePath);
            if (include is not null && !include(relative)) return;
            string destination = Path.Combine(destinationRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write(output);
                output.Flush(flushToDisk: true);
            }
            paths.Add(relative);
            copied?.Invoke(relative.Replace('\\', '/'));
        }
        foreach (KvLsmFileMetadata file in _version.AllFiles)
        {
            string path = KvLsmPaths.SstPath(_root, file.FileNumber);
            Write(path, output =>
            {
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                input.CopyTo(output);
            });
        }
        foreach (var item in _wal)
        {
            Write(item.Path, output =>
            {
                item.Stream.Position = 0;
                byte[] buffer = new byte[64 * 1024];
                long remaining = item.Length;
                for (long block = 0, blocks = (remaining + buffer.Length - 1) / buffer.Length; block < blocks; block++)
                {
                    int read = (int)Math.Min(buffer.Length, remaining);
                    item.Stream.ReadExactly(buffer.AsSpan(0, read));
                    output.Write(buffer, 0, read);
                    remaining -= read;
                }
                if (remaining != 0) throw new EndOfStreamException("Pinned LSM WAL copy was incomplete.");
            });
        }
        Write(KvLsmPaths.ManifestPath(_root, _manifestNumber), output =>
        {
            KvManifestFile.WriteHeader(output);
            KvManifestFile.WriteRecord(output, _snapshot.Encode());
        });
        Write(KvLsmPaths.CurrentPath(_root), output => output.Write(Encoding.ASCII.GetBytes(KvLsmPaths.ManifestFileName(_manifestNumber) + "\n")));
        if (_marker is not null)
            Write(Path.Combine(_root, KvGenerationFile.FileName), output => output.Write(_marker));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            foreach (var item in _wal) item.Stream.Dispose();
        }
        finally
        {
            _version.Release();
            // Match ordinary snapshot lifetime: an engine closed while the backup was
            // pinned must close its remaining readers when the last pin is released.
            _releaseReaders();
        }
    }
}

internal sealed partial class KvLsmEngine
{
    internal KvLsmEngineBackup AcquireBackup()
    {
        using IDisposable pause = EnterMaintenancePause();
        lock (_writeLock)
        {
            ThrowIfUnwritableLocked();
            SyncWal();
            var pinned = _versions.AcquireBackup();
            KvLsmEngineBackup? backup = null;
            try
            {
                backup = new KvLsmEngineBackup(_root, pinned.Version, pinned.Snapshot, pinned.ManifestNumber, ReleaseUnreferencedReaders);
                foreach ((long number, string path) in EnumerateWalFiles(KvLsmPaths.WalDirectory(_root)))
                    if (number >= pinned.Snapshot.LogNumber!.Value && number <= _walNumber) backup.AddWal(path);
                return backup;
            }
            catch
            {
                if (backup is null) pinned.Version.Release();
                else backup.Dispose();
                throw;
            }
        }
    }

    internal static KvLsmBackupSet AcquireBackupsUnder(string directory)
    {
        string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        var result = new KvLsmBackupSet();
        try
        {
            foreach (KvLsmEngine engine in LiveEngines.Keys.OrderBy(static engine => engine._root, StringComparer.Ordinal))
                if (engine._root.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    result.Engines.Add(engine.AcquireBackup());
            return result;
        }
        catch { result.Dispose(); throw; }
    }
}
