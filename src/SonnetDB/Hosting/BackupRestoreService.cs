using System.Formats.Tar;
using System.IO.Compression;
using Microsoft.Extensions.Options;
using SonnetDB.Backup;
using SonnetDB.Configuration;
using SonnetDB.Contracts;

namespace SonnetDB.Hosting;

/// <summary>验证在线恢复来源，并在新名称下注册恢复后的数据库。</summary>
internal sealed class BackupRestoreService(
    TsdbRegistry registry,
    IOptions<ServerOptions> options,
    ILogger<BackupRestoreService> logger) : IDisposable
{
    private const int MaximumEntries = 250_000;
    private const int MaximumPathDepth = 64;
    private const long MaximumManifestBytes = 64L * 1024 * 1024;
    private const int BufferSize = 128 * 1024;
    private readonly BackupRuntimeOptions _options = options.Value.Backup;
    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
    });
    private int _activeRestore;

    /// <summary>从备份根下的目录、压缩包或已允许的 URL 恢复新库。</summary>
    public Task<BackupRestoreResponse> RestoreAsync(string? path, string? url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) == string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("必须且只能指定 path 或 url。");

        return RunAsync(async (root, token) =>
        {
            if (url is not null && !string.IsNullOrWhiteSpace(url))
            {
                Uri uri = GetTrustedUrl(url);
                string package = Path.Combine(root, "source.package");
                await DownloadAsync(uri, package, token).ConfigureAwait(false);
                return (await ExtractAsync(package, root, token).ConfigureAwait(false), uri.GetLeftPart(UriPartial.Authority));
            }

            string source = ResolveLocalSource(path!);
            if (Directory.Exists(source))
                return (source, source);

            return (await ExtractAsync(source, root, token).ConfigureAwait(false), source);
        }, cancellationToken);
    }

    /// <summary>读取上传文件并在服务独占的暂存目录中恢复新库。</summary>
    public Task<BackupRestoreResponse> RestoreUploadAsync(
        Stream source,
        Func<CancellationToken, Task> validateEnd,
        CancellationToken cancellationToken)
        => RunAsync(async (root, token) =>
        {
            string package = Path.Combine(root, "source.package");
            await SavePackageAsync(source, package, token).ConfigureAwait(false);
            await validateEnd(token).ConfigureAwait(false);
            return (await ExtractAsync(package, root, token).ConfigureAwait(false), "upload");
        }, cancellationToken);

    /// <summary>为每次尝试建立独占暂存与截止时间，失败或取消后回收暂存。</summary>
    private async Task<BackupRestoreResponse> RunAsync(
        Func<string, CancellationToken, Task<(string Directory, string Description)>> prepareSource,
        CancellationToken cancellationToken)
    {
        if (!_options.EnableRestore)
            throw new InvalidOperationException("在线恢复未启用。");
        if (Interlocked.CompareExchange(ref _activeRestore, 1, 0) != 0)
            throw new InvalidOperationException("已有在线恢复正在运行。");

        DirectoryInfo? temporaryRoot = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.TransferTimeoutSeconds));
            CancellationToken token = timeout.Token;
            temporaryRoot = Directory.CreateTempSubdirectory("sonnetdb-backup-");
            (string source, string description) = await prepareSource(temporaryRoot.FullName, token).ConfigureAwait(false);
            ValidateSourceTree(source, token);
            var manifestFile = new FileInfo(Path.Combine(source, BackupManifest.FileName));
            if (manifestFile.Length > MaximumManifestBytes)
                throw new InvalidDataException("备份 manifest 超过在线恢复大小上限。");
            var backupService = new BackupService();
            BackupManifest manifest = backupService.ReadManifest(source);
            token.ThrowIfCancellationRequested();

            // 注册表在发布新库前执行原有的完整 manifest 校验及离线恢复预检。
            _ = registry.RestoreAsNew(source, "restored", out string databaseName);
            return new BackupRestoreResponse(
                databaseName,
                description,
                true,
                manifest.Files.Count,
                manifest.Consistency.TotalBytes,
                DateTimeOffset.UtcNow);
        }
        finally
        {
            if (temporaryRoot is not null)
            {
                try
                {
                    temporaryRoot.Delete(recursive: true);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(exception, "无法清理在线恢复暂存目录 {TemporaryRoot}。", temporaryRoot.FullName);
                }
            }
            Volatile.Write(ref _activeRestore, 0);
        }
    }

    /// <summary>按协议、主机和有效端口精确匹配指定的下载来源。</summary>
    private Uri GetTrustedUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length != 0
            || uri.Fragment.Length != 0)
        {
            throw new ArgumentException("备份 URL 必须是无用户信息和片段的 HTTP(S) 地址。", nameof(url));
        }

        string origin = uri.GetLeftPart(UriPartial.Authority);
        if (!_options.AllowedRestoreUrlOrigins.Any(allowed =>
                Uri.TryCreate(allowed, UriKind.Absolute, out Uri? trusted)
                && trusted.AbsolutePath == "/"
                && trusted.UserInfo.Length == 0
                && trusted.Query.Length == 0
                && trusted.Fragment.Length == 0
                && string.Equals(origin, trusted.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("备份 URL 来源未在服务器配置中允许。", nameof(url));
        }
        return uri;
    }

    /// <summary>将服务器路径限定于备份根目录，逐级拒绝软链接和重分析点。</summary>
    private string ResolveLocalSource(string path)
    {
        string root = Path.GetFullPath(_options.DestinationRoot!);
        string source = Path.GetFullPath(path, root);
        string relative = Path.GetRelativePath(root, source);
        if (relative is "." or ".." || Path.IsPathRooted(relative)
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException("恢复路径必须位于服务器 Backup.DestinationRoot 下。", nameof(path));
        }

        FileSystemInfo? current = new FileInfo(source);
        for (int depth = 0; current is not null; depth++)
        {
            if (depth > MaximumPathDepth)
                throw new ArgumentException("恢复路径层级过深。", nameof(path));
            RejectLink(current);
            current = Directory.GetParent(current.FullName);
        }
        return source;
    }

    /// <summary>下载已允许的备份包，响应必须直接返回成功和有界内容。</summary>
    private async Task DownloadAsync(Uri uri, string destination, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _http.GetAsync(
            uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidDataException($"备份下载未成功，HTTP 状态码 {(int)response.StatusCode}。");
        if (response.Content.Headers.ContentLength > _options.MaxTransferBytes)
            throw new InvalidDataException("备份包超过配置的最大传输大小。");

        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await SavePackageAsync(source, destination, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>将传输流写入暂存包，并在未知总长度时仍强制字节上限。</summary>
    private async Task SavePackageAsync(Stream source, string destination, CancellationToken cancellationToken)
    {
        await using var target = new FileStream(
            destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
        await CopyBoundedAsync(source, target, _options.MaxTransferBytes, new byte[BufferSize], cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>按内容识别 ZIP、TAR、gzip 包装的 TAR，并查找唯一的备份 manifest。</summary>
    private async Task<string> ExtractAsync(string package, string temporaryRoot, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(package, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        if (input.Length > _options.MaxTransferBytes)
            throw new InvalidDataException("备份包超过配置的最大传输大小。");

        byte[] header = new byte[4];
        await input.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        input.Position = 0;
        var destination = new ArchiveDestination(Path.Combine(temporaryRoot, "extracted"), _options.MaxTransferBytes);
        try
        {
            if (header.AsSpan().SequenceEqual("PK\u0003\u0004"u8) || header.AsSpan().SequenceEqual("PK\u0005\u0006"u8))
            {
                await ExtractZipAsync(input, destination, cancellationToken).ConfigureAwait(false);
            }
            else if (header[0] == 0x1f && header[1] == 0x8b)
            {
                await using var gzip = new GZipStream(input, CompressionMode.Decompress, leaveOpen: true);
                await ExtractTarAsync(gzip, destination, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await ExtractTarAsync(input, destination, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("备份包格式无效。", exception);
        }

        return destination.ManifestDirectory
            ?? throw new InvalidDataException("解包后未找到 sonnetdb.backup.json。");
    }

    /// <summary>解包 ZIP 的普通文件和目录，拒绝所有链接及特殊条目。</summary>
    private static async Task ExtractZipAsync(
        Stream input,
        ArchiveDestination destination,
        CancellationToken cancellationToken)
    {
        using var zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        if (zip.Entries.Count > MaximumEntries)
            throw new InvalidDataException("备份包的条目数超过上限。");
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int unixFileType = (entry.ExternalAttributes >> 16) & 0xF000;
            bool directory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
            if ((entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0
                || unixFileType != 0 && unixFileType != (directory ? 0x4000 : 0x8000))
            {
                throw new InvalidDataException("备份包不允许链接或特殊文件。");
            }
            if (directory)
            {
                destination.AddDirectory(entry.FullName);
                continue;
            }

            await using Stream data = entry.Open();
            await destination.AddFileAsync(entry.FullName, data, entry.Length, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>顺序读取 TAR 的普通文件和目录，并对解包总量和条目数施加上限。</summary>
    private static async Task ExtractTarAsync(
        Stream input,
        ArchiveDestination destination,
        CancellationToken cancellationToken)
    {
        await using var reader = new TarReader(input, leaveOpen: true);
        for (int count = 0; count <= MaximumEntries; count++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TarEntry? entry = await reader.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false);
            if (entry is null)
                return;
            if (count == MaximumEntries)
                throw new InvalidDataException("备份包的条目数超过上限。");
            if (entry.EntryType == TarEntryType.Directory)
            {
                destination.AddDirectory(entry.Name);
                continue;
            }
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                throw new InvalidDataException("备份包不允许链接或特殊文件。");

            await destination.AddFileAsync(entry.Name, entry.DataStream ?? Stream.Null, entry.Length, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>在读取 manifest 前确认输入树不包含任何链接，限制枚举深度与条目数。</summary>
    private static void ValidateSourceTree(string root, CancellationToken cancellationToken)
    {
        var pending = new Stack<(DirectoryInfo Directory, int Depth)>();
        pending.Push((new DirectoryInfo(root), 0));
        int entries = 0;
        while (pending.TryPop(out var item))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectLink(item.Directory);
            foreach (FileSystemInfo entry in item.Directory.EnumerateFileSystemInfos())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++entries > MaximumEntries || item.Depth >= MaximumPathDepth)
                    throw new InvalidDataException("备份目录条目数或层级超过上限。");
                RejectLink(entry);
                if ((entry.Attributes & FileAttributes.Directory) != 0)
                    pending.Push((new DirectoryInfo(entry.FullName), item.Depth + 1));
            }
        }
    }

    /// <summary>拒绝文件或目录上的软链接、连接点及其它重分析点。</summary>
    private static void RejectLink(FileSystemInfo entry)
    {
        FileAttributes attributes = File.GetAttributes(entry.FullName);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("恢复来源路径不允许链接。");
    }

    /// <summary>有界读取传输或解包流，实际写入总量同时受取消与字节上限约束。</summary>
    private static async Task<long> CopyBoundedAsync(
        Stream source,
        Stream destination,
        long maxBytes,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        long total = 0;
        while (total <= maxBytes)
        {
            int requested = (int)Math.Min(buffer.Length, maxBytes - total + 1);
            int read = await source.ReadAsync(buffer.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return total;
            if (read > maxBytes - total)
                break;
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            total += read;
        }
        throw new InvalidDataException("备份包传输或解包数据超过配置的字节上限。");
    }

    /// <summary>关闭在线恢复下载连接。</summary>
    public void Dispose() => _http.Dispose();

    /// <summary>隔离包内路径，保存普通文件并累计实际解包字节数。</summary>
    private sealed class ArchiveDestination(string root, long maximumBytes)
    {
        private readonly byte[] _buffer = new byte[BufferSize];
        private long _remaining = maximumBytes;

        /// <summary>包含唯一 manifest 的目录。</summary>
        public string? ManifestDirectory { get; private set; }

        /// <summary>创建经过路径校验的归档目录。</summary>
        public void AddDirectory(string name) => Directory.CreateDirectory(ResolvePath(name, isDirectory: true));

        /// <summary>保存普通文件并核对声明长度与实际内容。</summary>
        public async Task AddFileAsync(string name, Stream source, long length, CancellationToken cancellationToken)
        {
            string target = ResolvePath(name, isDirectory: false);
            if (length < 0 || length > _remaining)
                throw new InvalidDataException("备份包解包数据超过配置的字节上限。");
            bool isManifest = Path.GetFileName(target).Equals(BackupManifest.FileName, StringComparison.OrdinalIgnoreCase);
            if (isManifest && length > MaximumManifestBytes)
                throw new InvalidDataException("备份 manifest 超过在线恢复大小上限。");
            string directory = Path.GetDirectoryName(target)!;
            Directory.CreateDirectory(directory);
            await using var output = new FileStream(
                target, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
            long copied = await CopyBoundedAsync(source, output, length, _buffer, cancellationToken).ConfigureAwait(false);
            if (copied != length)
                throw new InvalidDataException("备份包条目大小与实际内容不一致。");
            _remaining -= copied;

            if (isManifest)
            {
                if (ManifestDirectory is not null)
                    throw new InvalidDataException("备份包只能包含一个 manifest。");
                ManifestDirectory = directory;
            }
        }

        /// <summary>限制归档路径为可移植的相对路径，逐段拒绝根路径、设备名和父级引用。</summary>
        private string ResolvePath(string name, bool isDirectory)
        {
            string normalized = name.Replace('\\', '/');
            if (normalized.StartsWith("./", StringComparison.Ordinal))
                normalized = normalized[2..];
            if (isDirectory)
            {
                if (normalized == "." || normalized.Length == 0 && name == "./")
                    return root;
                normalized = normalized.TrimEnd('/');
            }
            string[] parts = normalized.Split('/');
            if (parts.Length > MaximumPathDepth || parts.Any(IsInvalidPart))
                throw new InvalidDataException("备份包包含无效的相对路径。");
            return Path.Combine(root, Path.Join(parts));
        }

        /// <summary>校验归档文件名片段，排除路径别名和系统保留名称。</summary>
        private static bool IsInvalidPart(string part)
        {
            if (part.Length is 0 or > 255 || part.EndsWith('.') || part.EndsWith(' ')
                || part.Any(character => char.IsControl(character) || "<>:\"|?*".Contains(character)))
            {
                return true;
            }

            string stem = part.Split('.', 2)[0].ToUpperInvariant();
            return stem is "CON" or "PRN" or "AUX" or "NUL"
                || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal)
                    || stem.StartsWith("LPT", StringComparison.Ordinal))
                    && "123456789¹²³".Contains(stem[3]);
        }
    }
}
