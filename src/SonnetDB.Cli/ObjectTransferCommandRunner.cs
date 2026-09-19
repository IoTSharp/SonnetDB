using SonnetDB.Data.ObjectStorage;

namespace SonnetDB.Cli;

internal sealed partial class CliApplication
{
    private int RunObjectCopy(IReadOnlyList<string> args)
    {
        var options = ParseObjectTransferOptions(args, "cp", requireDryRun: false);
        var source = ParseObjectLocation(options.Source!);
        var destination = ParseObjectLocation(options.Destination!);
        if (source.IsObject == destination.IsObject || source.IsDirectory || destination.IsDirectory)
            throw new CliUsageException("cp 要求一端为本地文件，另一端为 s3://bucket/key。");

        if (options.DryRun)
        {
            if (!source.IsObject && !File.Exists(source.Path))
                throw new FileNotFoundException($"本地文件不存在: {source.Path}");

            _output.WriteLine($"dry-run: {(source.IsObject ? "download" : "upload")} {source.Display} -> {destination.Display}，未传输任何内容。");
            return ExitCodes.Success;
        }

        using var client = new SndbObjectStorageClient(options.ConnectionString!);
        if (source.IsObject)
        {
            DownloadObjectAsync(client, source, destination.Path!).GetAwaiter().GetResult();
            _output.WriteLine($"已下载 {source.Display} -> {destination.Path}");
        }
        else
        {
            UploadFileAsync(client, source.Path!, destination).GetAwaiter().GetResult();
            _output.WriteLine($"已上传 {source.Path} -> {destination.Display}");
        }
        return ExitCodes.Success;
    }

    private int RunObjectSync(IReadOnlyList<string> args)
    {
        var options = ParseObjectTransferOptions(args, "sync", requireDryRun: true);
        var source = ParseObjectLocation(options.Source!);
        var destination = ParseObjectLocation(options.Destination!);
        if (source.IsObject == destination.IsObject || (!source.IsObject && !source.IsDirectory) || (!destination.IsObject && File.Exists(destination.Path)))
            throw new CliUsageException("sync 要求一端为目录，另一端为 s3://bucket/prefix。");

        int count;
        if (source.IsObject)
        {
            using var client = new SndbObjectStorageClient(options.ConnectionString!);
            count = WriteDownloadPlanAsync(client, source, destination.Path!).GetAwaiter().GetResult();
        }
        else
        {
            count = WriteUploadPlan(source.Path!, destination);
        }
        _output.WriteLine($"dry-run: {count} 个文件，未传输任何内容。");
        return ExitCodes.Success;
    }

    private async Task<int> WriteDownloadPlanAsync(SndbObjectStorageClient client, ObjectLocation source, string directory)
    {
        int count = 0;
        await foreach (var item in client.ListObjectsCursorAsync(source.Bucket!, source.KeyPrefix))
        {
            string relative = GetRelativeObjectKey(source.KeyPrefix!, item.Key);
            if (relative.Length == 0) continue;
            _output.WriteLine($"download {source.Bucket}/{item.Key} -> {GetSafeTargetPath(directory, relative)}");
            count++;
        }
        return count;
    }

    private int WriteUploadPlan(string directory, ObjectLocation destination)
    {
        int count = 0;
        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(directory, file).Replace(Path.DirectorySeparatorChar, '/');
            _output.WriteLine($"upload {file} -> {destination.Bucket}/{CombineObjectKey(destination.KeyPrefix!, relative)}");
            count++;
        }
        return count;
    }

    private static async Task UploadFileAsync(SndbObjectStorageClient client, string filePath, ObjectLocation destination)
    {
        await using var input = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await client.PutObjectAsync(destination.Bucket!, destination.KeyPrefix!, input, GetContentType(filePath)).ConfigureAwait(false);
    }

    private static async Task DownloadObjectAsync(SndbObjectStorageClient client, ObjectLocation source, string filePath)
    {
        var result = await client.OpenReadAsync(source.Bucket!, source.KeyPrefix!).ConfigureAwait(false)
            ?? throw new FileNotFoundException($"对象不存在: {source.Display}");
        string? parent = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
        await using var content = result.Content;
        await using var output = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await content.CopyToAsync(output).ConfigureAwait(false);
    }

    private static ObjectTransferOptions ParseObjectTransferOptions(IReadOnlyList<string> args, string command, bool requireDryRun)
    {
        string? connection = null;
        string? source = null;
        string? destination = null;
        bool dryRun = false;
        for (int index = 1; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--connection" or "-c": connection = ReadRequiredValue(args, ref index, "连接字符串"); break;
                case "--dry-run": dryRun = true; break;
                case "--help" or "-h": throw new CliUsageException(BuildObjectTransferHelp(command));
                case var value when !value.StartsWith("-", StringComparison.Ordinal) && source is null: source = value; break;
                case var value when !value.StartsWith("-", StringComparison.Ordinal) && destination is null: destination = value; break;
                default: throw new CliUsageException($"未知参数 '{args[index]}'。");
            }
        }
        if (string.IsNullOrWhiteSpace(connection) || string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(destination))
            throw new CliUsageException(BuildObjectTransferHelp(command));
        if (requireDryRun && !dryRun)
            throw new CliUsageException("sync 当前仅支持 --dry-run；实际传输由 Transfer Manager (#322) 负责。");
        return new ObjectTransferOptions(connection, source, destination, dryRun);
    }

    private static ObjectLocation ParseObjectLocation(string value)
    {
        if (!value.StartsWith("s3://", StringComparison.OrdinalIgnoreCase))
            return new ObjectLocation(false, null, null, value, Directory.Exists(value));
        string remainder = value[5..];
        int separator = remainder.IndexOf('/', StringComparison.Ordinal);
        if (separator <= 0) throw new CliUsageException("对象地址必须为 s3://bucket/key 或 s3://bucket/prefix。");
        string key = remainder[(separator + 1)..].TrimStart('/');
        if (key.Length == 0) throw new CliUsageException("对象地址必须包含 key 或 prefix。");
        return new ObjectLocation(true, remainder[..separator], key, null, false);
    }

    private static string GetRelativeObjectKey(string prefix, string key)
    {
        string prefixWithSlash = prefix.Trim('/') + "/";
        return key.StartsWith(prefixWithSlash, StringComparison.Ordinal) ? key[prefixWithSlash.Length..] : key;
    }

    private static string CombineObjectKey(string prefix, string relative) => prefix.TrimEnd('/') + "/" + relative;

    private static string GetSafeTargetPath(string directory, string relative)
    {
        string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        string target = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new CliUsageException("对象 key 不能写入同步目标目录之外。");
        return target;
    }

    private static string GetContentType(string filePath) => Path.GetExtension(filePath).ToLowerInvariant() switch
    {
        ".txt" => "text/plain",
        ".json" => "application/json",
        ".csv" => "text/csv",
        _ => "application/octet-stream",
    };

    private static string BuildObjectTransferHelp(string command) => command == "cp"
        ? "用法: sndb cp --connection \"<conn>\" <file-path|s3://bucket/key> <file-path|s3://bucket/key> [--dry-run]"
        : "用法: sndb sync --connection \"<conn>\" <directory|s3://bucket/prefix> <directory|s3://bucket/prefix> --dry-run";

    private readonly record struct ObjectTransferOptions(string? ConnectionString, string? Source, string? Destination, bool DryRun);
    private readonly record struct ObjectLocation(bool IsObject, string? Bucket, string? KeyPrefix, string? Path, bool IsDirectory)
    {
        public string Display => IsObject ? "s3://" + Bucket + "/" + KeyPrefix : Path ?? string.Empty;
    }
}
