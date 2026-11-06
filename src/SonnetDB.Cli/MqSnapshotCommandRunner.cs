using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SonnetMQ;

namespace SonnetDB.Cli;

internal sealed partial class CliApplication
{
    private const int MqMaxInventoryEntries = 100_000;
    private const long MqMaxInventoryBytes = 16L * 1024 * 1024 * 1024;

    private int RunMqSnapshot(IReadOnlyList<string> args)
    {
        if (args.Count is < 2 or > 16)
            throw new CliUsageException(BuildMqSnapshotHelp());
        string action = args[1].ToLowerInvariant();
        if (IsHelp(action))
        {
            _output.WriteLine(BuildMqSnapshotHelp());
            return ExitCodes.Success;
        }
        if (action is not ("snapshot" or "inspect" or "restore"))
            throw new CliUsageException(BuildMqSnapshotHelp());

        string? serverRoot = null;
        string? snapshot = null;
        string? output = null;
        bool offline = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var clock = Stopwatch.StartNew();
        for (int index = 2; index < args.Count; index++)
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(5) || !seen.Add(args[index]))
                throw new CliUsageException("MQ 参数重复或解析预算超限。");
            switch (args[index])
            {
                case "--server-root": serverRoot = ReadRequiredValue(args, ref index, "Server DataRoot"); break;
                case "--snapshot": snapshot = ReadRequiredValue(args, ref index, "实例 MQ 快照目录"); break;
                case "--output": output = ReadRequiredValue(args, ref index, "新快照目录"); break;
                case "--offline": offline = true; break;
                default: throw new CliUsageException($"未知 MQ 参数 '{args[index]}'。");
            }
        }
        if (action == "inspect")
        {
            if (snapshot is null || serverRoot is not null || output is not null || offline)
                throw new CliUsageException(BuildMqSnapshotHelp());
            snapshot = NormalizeMqPath(snapshot);
            var manifest = InspectMqSnapshot(snapshot);
            WriteMqReceipt(action, null, snapshot, manifest);
            return ExitCodes.Success;
        }
        if (!offline || serverRoot is null || action == "snapshot" && (output is null || snapshot is not null)
            || action == "restore" && (snapshot is null || output is not null))
            throw new CliUsageException(BuildMqSnapshotHelp());

        serverRoot = NormalizeMqPath(serverRoot);
        string mqDirectory = NormalizeMqPath(Path.Combine(serverRoot, ".system", "mq"));
        if (action == "snapshot")
        {
            output = NormalizeMqPath(output!);
            EnsureMqPathsSeparate(serverRoot, output);
            EnsureMqOutputMissing(output);
            if (!Directory.Exists(mqDirectory))
                throw new DirectoryNotFoundException("Server DataRoot 中不存在 .system/mq；拒绝创建空实例冒充备份源。");
            InspectMqInventory(mqDirectory, includeManifest: false);
            using var owner = SonnetMqStore.Open(new SonnetMqOptions
            {
                Path = mqDirectory,
                RetentionInterval = TimeSpan.Zero,
                TrimAcknowledgedMessages = false,
            });
            owner.CreateSnapshot(output);
            WriteMqReceipt(action, serverRoot, output, InspectMqSnapshot(output));
        }
        else
        {
            snapshot = NormalizeMqPath(snapshot!);
            EnsureMqPathsSeparate(snapshot, serverRoot);
            EnsureMqOutputMissing(serverRoot);
            _ = InspectMqSnapshot(snapshot);
            var manifest = SonnetMqStore.RestoreSnapshot(snapshot, mqDirectory, overwrite: false);
            WriteMqReceipt(action, serverRoot, snapshot, manifest);
        }
        return ExitCodes.Success;
    }

    private static SonnetMqInstanceSnapshotManifest InspectMqSnapshot(string directory)
    {
        InspectMqInventory(directory, includeManifest: true);
        string path = Path.Combine(directory, SonnetMqInstanceSnapshotManifest.FileName);
        if (new FileInfo(path).Length is < 1 or > 32 * 1024 * 1024)
            throw new InvalidDataException("MQ manifest 超过 32 MiB 或为空。");
        return SonnetMqStore.ReadSnapshotManifest(directory);
    }

    private static void InspectMqInventory(string directory, bool includeManifest)
    {
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException("MQ 源目录不存在。");
        var timer = Stopwatch.StartNew();
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((directory, 0));
        int entries = 0;
        long bytes = 0;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        while (pending.Count > 0)
        {
            var next = pending.Pop();
            if (next.Depth > 32 || timer.Elapsed > TimeSpan.FromSeconds(30))
                throw new IOException("MQ 文件清单超过深度或 30 秒预检预算。");
            foreach (string item in Directory.EnumerateFileSystemEntries(next.Path))
            {
                if (++entries > MqMaxInventoryEntries || timer.Elapsed > TimeSpan.FromSeconds(30))
                    throw new IOException("MQ 文件清单超过 100000 个目录项或预检预算。");
                FileAttributes attributes = File.GetAttributes(item);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("离线 MQ 管理不接受符号链接或目录联接。");
                if ((attributes & FileAttributes.Directory) != 0)
                    pending.Push((item, next.Depth + 1));
                else
                {
                    if (!includeManifest && string.Equals(item, Path.Combine(directory, "sonnetmq.lock"), comparison))
                        continue;
                    bytes = checked(bytes + new FileInfo(item).Length);
                    if (bytes > MqMaxInventoryBytes)
                        throw new IOException("离线 MQ 管理首片仅支持最多 16 GiB 文件清单。");
                }
            }
        }
    }

    private static string NormalizeMqPath(string path)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new CliUsageException("MQ 管理路径必须是绝对路径。");
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string root = Path.GetPathRoot(full)!;
        string[] segments = Path.GetRelativePath(root, full).Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length > 128 || string.Equals(root, full, StringComparison.Ordinal))
            throw new CliUsageException("MQ 管理路径深度超限或指向卷根目录。");
        string current = root;
        var timer = Stopwatch.StartNew();
        foreach (string segment in segments)
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(5))
                throw new IOException("MQ 路径检查超过 5 秒预算。");
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("离线 MQ 管理不接受符号链接或目录联接。");
        }
        return full;
    }

    private static void EnsureMqOutputMissing(string path)
    {
        if (Directory.Exists(path) || File.Exists(path))
            throw new IOException("离线 MQ 管理只接受不存在的新目标；不覆盖已有目录或文件。");
    }

    private static void EnsureMqPathsSeparate(string first, string second)
    {
        string left = Path.TrimEndingDirectorySeparator(first) + Path.DirectorySeparatorChar;
        string right = Path.TrimEndingDirectorySeparator(second) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (left.StartsWith(right, comparison) || right.StartsWith(left, comparison))
            throw new CliUsageException("MQ 源、快照和目标必须是互不嵌套的独立目录。");
    }

    private void WriteMqReceipt(string action, string? serverRoot, string snapshot, SonnetMqInstanceSnapshotManifest manifest)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("operation", action);
            writer.WriteString("storageScope", "instance-mq-only");
            writer.WriteString("cliVersion", GetVersion());
            writer.WriteString("serverRoot", serverRoot);
            writer.WriteString("mqDirectory", serverRoot is null ? null : Path.Combine(serverRoot, ".system", "mq"));
            writer.WriteString("snapshotDirectory", snapshot);
            writer.WriteString("manifestSha256", Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(
                Path.Combine(snapshot, SonnetMqInstanceSnapshotManifest.FileName)))));
            writer.WriteNumber("formatVersion", manifest.FormatVersion);
            writer.WriteString("createdUtc", manifest.CreatedUtc);
            writer.WriteNumber("fileCount", manifest.Files.Count);
            writer.WriteNumber("totalBytes", manifest.Files.Sum(static file => file.Length));
            writer.WriteEndObject();
        }
        _output.WriteLine(Encoding.UTF8.GetString(buffer.ToArray()));
    }

    private static string BuildMqSnapshotHelp()
        => "用法: sndb mq snapshot --server-root <绝对Server-DataRoot> --output <不存在的快照目录> --offline | inspect --snapshot <快照目录> | restore --snapshot <快照目录> --server-root <不存在的新Server-DataRoot> --offline；仅本机文件权限，必须先停止匹配版本Server。";
}
