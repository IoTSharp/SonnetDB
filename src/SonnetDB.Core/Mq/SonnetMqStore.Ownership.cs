namespace SonnetMQ;

public sealed partial class SonnetMqStore
{
    internal const string LifecycleLockFileName = "sonnetmq.lock";
    private static readonly object DirectoryOwnersSync = new();
    private static readonly HashSet<string> DirectoryOwners = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private FileStream? _directoryLease;
    private string? _directoryOwnerPath;

    private void AcquireDirectoryOwnership()
    {
        string directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(RootDirectory));
        lock (DirectoryOwnersSync)
        {
            if (!DirectoryOwners.Add(directory))
                throw new IOException("SonnetMQ 实例目录已由当前进程中的另一个 owner 打开；离线维护前必须停止该 owner。");
        }
        FileStream? lease = null;
        try
        {
            Directory.CreateDirectory(directory);
            lease = new FileStream(Path.Combine(directory, LifecycleLockFileName), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None, bufferSize: 1, FileOptions.None);
            if (!OperatingSystem.IsMacOS())
                lease.Lock(0, 1);
            _directoryLease = lease;
            _directoryOwnerPath = directory;
        }
        catch
        {
            lease?.Dispose();
            lock (DirectoryOwnersSync) DirectoryOwners.Remove(directory);
            throw;
        }
    }

    private void ReleaseDirectoryOwnership()
    {
        FileStream? lease = Interlocked.Exchange(ref _directoryLease, null);
        string? directory = Interlocked.Exchange(ref _directoryOwnerPath, null);
        try { lease?.Dispose(); }
        finally
        {
            if (directory is not null)
                lock (DirectoryOwnersSync) DirectoryOwners.Remove(directory);
        }
    }
}
