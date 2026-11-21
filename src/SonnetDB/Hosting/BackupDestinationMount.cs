using System.Diagnostics;
using SonnetDB.Configuration;

namespace SonnetDB.Hosting;

/// <summary>在备份开始或发布前确认 Linux 目标仍在当前挂载命名空间中。</summary>
internal static class BackupDestinationMount
{
    private const int MaximumMountEntries = 65_536;
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(5);

    /// <summary>按配置核对目标根已存在且为独立挂载点，无法确认时拒绝写入。</summary>
    public static void EnsureMounted(BackupRuntimeOptions options, CancellationToken cancellationToken)
    {
        if (!options.RequireDestinationMount)
            return;
        if (!OperatingSystem.IsLinux())
            throw new InvalidOperationException("备份卷挂载检查仅支持 Linux。");

        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(options.DestinationRoot)
            || !Path.IsPathFullyQualified(options.DestinationRoot))
        {
            throw new InvalidOperationException("启用备份卷挂载检查时必须指定绝对目标路径。");
        }

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.DestinationRoot));
        if (root == "/" || !Directory.Exists(root)
            || !ContainsMount(File.ReadLines("/proc/self/mountinfo"), root, cancellationToken))
        {
            throw new IOException("备份根目录不是当前 Linux 挂载命名空间中的独立挂载点。");
        }
    }

    /// <summary>按内核 mountinfo 字段语义精确匹配挂载点，禁止把上级挂载当作目标卷。</summary>
    internal static bool ContainsMount(
        IEnumerable<string> mountEntries,
        string target,
        CancellationToken cancellationToken)
    {
        string encodedTarget = target
            .Replace(@"\", @"\134", StringComparison.Ordinal)
            .Replace(" ", @"\040", StringComparison.Ordinal)
            .Replace("\t", @"\011", StringComparison.Ordinal)
            .Replace("\n", @"\012", StringComparison.Ordinal);
        Stopwatch elapsed = Stopwatch.StartNew();
        int count = 0;
        foreach (string entry in mountEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++count > MaximumMountEntries || elapsed.Elapsed > ScanTimeout)
                throw new IOException("备份卷挂载检查超过读取预算。");
            string[] fields = entry.Split(' ', 6);
            if (fields.Length == 6 && string.Equals(fields[4], encodedTarget, StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
