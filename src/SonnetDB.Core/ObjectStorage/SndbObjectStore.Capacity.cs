using System.ComponentModel;
using System.Runtime.InteropServices;
using SonnetDB.Engine;

namespace SonnetDB.ObjectStorage;

public sealed partial class SndbObjectStore
{
    /// <summary>读取对象正文目录所在文件系统的容量，目录不可用时不回退为其它盘的余量。</summary>
    /// <returns>对象实际存储卷的总容量、可用字节与观测时间。</returns>
    public SndbStorageCapacityInfo GetStorageCapacity() => ReadStorageCapacity(_contentRoot);

    /// <summary>只读查询指定数据库的对象正文卷，不创建目录或打开对象元数据。</summary>
    /// <param name="tsdb">已打开的目标数据库。</param>
    /// <returns>正文实际存储卷的容量与观测时间；目录不可用时明确失败。</returns>
    public static SndbStorageCapacityInfo GetStorageCapacity(Tsdb tsdb)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        return ReadStorageCapacity(Path.Combine(tsdb.RootDirectory, "objects"));
    }

    private static SndbStorageCapacityInfo ReadStorageCapacity(string contentRoot)
    {
        if (!Directory.Exists(contentRoot))
            throw new SndbObjectStorageException("storage_capacity_unavailable", "Object storage directory is not available.");

        long total;
        long available;
        if (OperatingSystem.IsWindows())
        {
            // 直接询问目录所在卷；Windows DriveInfo 会退回盘符根，遗漏 junction/目录挂载。
            string directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(contentRoot)) + Path.DirectorySeparatorChar;
            if (GetDiskFreeSpaceEx(directory, out ulong availableBytes, out ulong totalBytes, out _) == 0)
            {
                int error = Marshal.GetLastWin32Error();
                throw new IOException("Object storage capacity is unavailable.", new Win32Exception(error));
            }
            if (totalBytes > long.MaxValue || availableBytes > long.MaxValue)
                throw new SndbObjectStorageException("storage_capacity_unavailable", "Object storage capacity exceeds the supported range.");
            total = (long)totalBytes;
            available = (long)availableBytes;
        }
        else
        {
            var drive = new DriveInfo(contentRoot);
            total = drive.TotalSize;
            available = drive.AvailableFreeSpace;
        }

        if (total <= 0 || available < 0 || available > total)
            throw new SndbObjectStorageException("storage_capacity_unavailable", "Object storage capacity is invalid.");
        return new SndbStorageCapacityInfo(total, available, DateTimeOffset.UtcNow);
    }

    // 与 DirectoryFsync 相同的 Safe-only、AOT 友好简单字符串/整数互操作，不启用 unsafe。
    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern int GetDiskFreeSpaceEx(string directory, out ulong availableBytes, out ulong totalBytes, out ulong freeBytes);
}
