using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace SonnetDB.Wal;

/// <summary>
/// 跨平台"目录 fsync"：把目录项（rename / create / delete）的元数据变更强制落盘，
/// 保证崩溃/掉电后原子改名与文件出现/消失的顺序可见性。
/// <list type="bullet">
///   <item><description>Linux/macOS：以只读方式打开目录并 <c>fsync</c>（<see cref="FileStream.Flush(bool)"/>）。</description></item>
///   <item><description>Windows：普通 <c>FileStream</c> 无法打开目录，改用 P/Invoke
///     <c>CreateFileW(FILE_FLAG_BACKUP_SEMANTICS)</c> 拿到目录句柄后 <c>FlushFileBuffers</c>（#189）。</description></item>
/// </list>
/// <para>尽力而为：句柄打开或 flush 失败不抛（吞掉 IO/权限异常），因为上层已对文件内容单独 fsync，
/// 目录 flush 只加强改名/删除的顺序保证，失败时退化为旧行为而非破坏正确性。</para>
/// </summary>
internal static class DirectoryFsync
{
    /// <summary>对 <paramref name="directory"/> 执行尽力而为的目录级 fsync。</summary>
    internal static void FlushBestEffort(string directory)
    {
        if (string.IsNullOrEmpty(directory))
            return;

        try
        {
            if (OperatingSystem.IsWindows())
                FlushWindows(directory);
            else
                FlushUnix(directory);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void FlushUnix(string directory)
    {
        using var fs = new FileStream(directory, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.Flush(flushToDisk: true);
    }

    [SupportedOSPlatform("windows")]
    private static void FlushWindows(string directory)
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
            return; // 打不开目录句柄：尽力而为，静默退化。

        _ = FlushFileBuffers(handle);
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
