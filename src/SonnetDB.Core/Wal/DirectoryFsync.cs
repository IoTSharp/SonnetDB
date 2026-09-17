using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace SonnetDB.Wal;

/// <summary>
/// 跨平台"目录 fsync"：把目录项（rename / create / delete）的元数据变更强制落盘，
/// 保证崩溃/掉电后原子改名与文件出现/消失的顺序可见性。
/// <list type="bullet">
///   <item><description>Linux/macOS：通过原生 <c>open</c> + <c>fdopendir</c> 验证并打开目录，再 <c>fsync</c>。</description></item>
///   <item><description>Windows：普通 <c>FileStream</c> 无法打开目录，改用 P/Invoke
///     <c>CreateFileW(FILE_FLAG_BACKUP_SEMANTICS)</c> 拿到目录句柄后 <c>FlushFileBuffers</c>（#189）。</description></item>
/// </list>
/// <para>目录 flush 只加强改名/删除的顺序保证。部分文件系统（例如 overlayfs、
/// 某些网络文件系统和 Windows 重定向器）明确不支持对目录句柄执行 fsync；这些平台
/// 会退化为文件内容已经 fsync 的旧行为，而不会让数据库因此无法打开或 Flush。</para>
/// </summary>
internal static class DirectoryFsync
{
    private const int UnixOpenReadOnly = 0;

    /// <summary>对 <paramref name="directory"/> 执行尽力而为的目录级 fsync。</summary>
    internal static void FlushBestEffort(string directory)
    {
        if (string.IsNullOrEmpty(directory))
            return;

        try
        {
            FlushRequired(directory);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (PlatformNotSupportedException)
        {
        }
    }

    /// <summary>
    /// 对目录项执行持久化刷新。调用方只有在本方法返回后，才可删除唯一恢复日志。
    /// 当底层文件系统明确不支持目录 fsync 时，本方法安全退化为 no-op；文件内容仍由
    /// 调用方通过 FileStream.Flush(true) 单独持久化。目录不存在、权限不足或其它真实 I/O
    /// 故障仍会抛出，以免把可恢复性错误静默为成功。
    /// </summary>
    internal static void FlushRequired(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Directory fsync target does not exist: '{directory}'.");

        try
        {
            if (OperatingSystem.IsWindows())
                FlushWindowsRequired(directory);
            else
                FlushUnixRequired(directory);
        }
        catch (IOException exception) when (IsDirectoryFsyncUnsupported(exception))
        {
            // EINVAL/ENOTSUP（Unix）以及 ERROR_INVALID_FUNCTION/ERROR_NOT_SUPPORTED
            // （Windows）表示该文件系统没有目录句柄 flush 能力，而不是数据文件写入失败。
            // 保留旧的 best-effort 语义，避免 overlayfs/NFS 上的能力回退。
        }
        catch (PlatformNotSupportedException)
        {
            // 新平台尚未提供目录句柄 fsync 时同样退化；文件内容仍已单独 flush。
        }
    }

    private static bool IsDirectoryFsyncUnsupported(IOException exception)
    {
        int error = exception.InnerException is Win32Exception native
            ? native.NativeErrorCode
            : exception.HResult & 0xFFFF;

        // POSIX: EINVAL (22), ENOTSUP/EOPNOTSUPP (95), ENOSYS (38).
        // Win32: ERROR_INVALID_FUNCTION (1), ERROR_INVALID_HANDLE (6),
        // ERROR_NOT_SUPPORTED (50), ERROR_INVALID_PARAMETER (87).
        return error is 1 or 6 or 22 or 38 or 50 or 87 or 95;
    }

    private static void FlushUnixRequired(string directory)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Directory fsync is supported on Windows, Linux, and macOS.");

        int descriptor = OpenUnixPath(directory, UnixOpenReadOnly);
        if (descriptor < 0)
        {
            int error = Marshal.GetLastPInvokeError();
            throw CreateUnixIOException("open", directory, error);
        }

        UnixDirectoryStreamHandle directoryStream = OpenUnixDirectoryStream(descriptor);
        if (directoryStream.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            directoryStream.Dispose();
            _ = CloseUnixDescriptor(descriptor);
            throw CreateUnixIOException("validate", directory, error);
        }

        using var handle = directoryStream;
        if (FsyncUnixDirectory(descriptor) != 0)
        {
            int error = Marshal.GetLastPInvokeError();
            throw CreateUnixIOException("fsync", directory, error);
        }
    }

    private static IOException CreateUnixIOException(string operation, string directory, int error)
    {
        var nativeError = new Win32Exception(error);
        return new IOException(
            $"Failed to {operation} directory '{directory}' for durable metadata publication "
            + $"(errno {error}: {nativeError.Message}).",
            nativeError);
    }

    [SupportedOSPlatform("windows")]
    private static void FlushWindowsRequired(string directory)
    {
        // FILE_FLAG_BACKUP_SEMANTICS 是拿到"目录"句柄的必要条件；只需元数据权限即可 FlushFileBuffers。
        using SafeFileHandle handle = CreateFileW(
            directory,
            dwDesiredAccess: GENERIC_WRITE,
            dwShareMode: FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            lpSecurityAttributes: IntPtr.Zero,
            dwCreationDisposition: OPEN_EXISTING,
            dwFlagsAndAttributes: FILE_FLAG_BACKUP_SEMANTICS,
            hTemplateFile: IntPtr.Zero);

        if (handle.IsInvalid)
            throw CreateWindowsIOException("open", directory);

        if (!FlushFileBuffers(handle))
            throw CreateWindowsIOException("flush", directory);
    }

    private static IOException CreateWindowsIOException(string operation, string directory)
    {
        int error = Marshal.GetLastWin32Error();
        return new IOException(
            $"Failed to {operation} directory '{directory}' for durable metadata publication: " +
            new Win32Exception(error).Message,
            error);
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenUnixPath(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags);

    [DllImport("libc", EntryPoint = "fdopendir", SetLastError = true)]
    private static extern UnixDirectoryStreamHandle OpenUnixDirectoryStream(int descriptor);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int FsyncUnixDirectory(int descriptor);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int CloseUnixDescriptor(int descriptor);

    [DllImport("libc", EntryPoint = "closedir", SetLastError = true)]
    private static extern int CloseUnixDirectoryStream(IntPtr directoryStream);

    private sealed class UnixDirectoryStreamHandle : SafeHandle
    {
        private UnixDirectoryStreamHandle()
            : base(IntPtr.Zero, ownsHandle: true)
        {
        }

        public override bool IsInvalid => handle == IntPtr.Zero || handle == new IntPtr(-1);

        protected override bool ReleaseHandle()
            => CloseUnixDirectoryStream(handle) == 0;
    }

    // ── Win32 P/Invoke（DllImport；签名简单，AOT/trim 友好，无需 AllowUnsafeBlocks）───────────

    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint FILE_SHARE_DELETE = 0x00000004;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(SafeFileHandle hFile);
}
