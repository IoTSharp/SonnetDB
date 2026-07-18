using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.ComponentModel;
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
/// <para>尽力而为：句柄打开或 flush 失败不抛（吞掉 IO/权限异常），因为上层已对文件内容单独 fsync，
/// 目录 flush 只加强改名/删除的顺序保证，失败时退化为旧行为而非破坏正确性。</para>
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
    }

    /// <summary>
    /// 对目录项执行必须成功的持久化刷新。调用方只有在本方法返回后，才可删除唯一恢复日志。
    /// </summary>
    internal static void FlushRequired(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Directory fsync target does not exist: '{directory}'.");

        if (OperatingSystem.IsWindows())
            FlushWindowsRequired(directory);
        else
            FlushUnixRequired(directory);
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
