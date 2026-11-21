using System.Formats.Tar;
using System.IO.Compression;
using Microsoft.Extensions.Options;
using SonnetDB.Backup;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Engine;

namespace SonnetDB.Hosting;

/// <summary>
/// 处理目录、压缩包和 HTTP(S) 备份源的在线校验与新库恢复。
/// </summary>
internal sealed class BackupRestoreService
{
    private readonly TsdbRegistry _registry;
    private readonly ServerOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>创建在线恢复服务。</summary>
    public BackupRestoreService(
        TsdbRegistry registry,
        IOptions<ServerOptions> options,
        IHttpClientFactory httpClientFactory)
    {
        _registry = registry;
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>从目录、压缩包或 HTTP(S) 地址校验并恢复新数据库。</summary>
    public async Task<BackupRestoreResponse> RestoreAsync(
        string? path,
        string? url,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) == string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("必须且只能指定 path 或 url。");

        string? temporaryRoot = null;
        string sourcePath;
        if (!string.IsNullOrWhiteSpace(url))
        {
            temporaryRoot = CreateTemporaryRoot();
            string archivePath = Path.Combine(temporaryRoot, "source.package");
            await DownloadAsync(url, archivePath, cancellationToken).ConfigureAwait(false);
            sourcePath = ExtractOrUseDirectory(archivePath, temporaryRoot);
        }
        else
        {
            sourcePath = Path.GetFullPath(path!);
            if (File.Exists(sourcePath))
            {
                temporaryRoot = CreateTemporaryRoot();
                string copiedPackage = Path.Combine(temporaryRoot, Path.GetFileName(sourcePath));
                CopyFileWithLimit(sourcePath, copiedPackage);
                sourcePath = ExtractOrUseDirectory(copiedPackage, temporaryRoot);
            }
        }

        try
        {
            BackupVerificationResult verification = new BackupService().Verify(sourcePath);
            if (!verification.IsValid)
                throw new InvalidDataException($"备份校验失败：{string.Join("; ", verification.Errors.Take(8))}");

            Tsdb restored = _registry.RestoreAsNew(sourcePath, "restored", out string databaseName);
            _ = restored;
            BackupManifest manifest = new BackupService().ReadManifest(sourcePath);
            return new BackupRestoreResponse(
                databaseName,
                string.IsNullOrWhiteSpace(url) ? Path.GetFullPath(path!) : url!,
                true,
                verification.CheckedFiles,
                manifest.Consistency.TotalBytes,
                DateTimeOffset.UtcNow);
        }
        finally
        {
            if (temporaryRoot is not null)
                TryDeleteTemporaryRoot(temporaryRoot);
        }
    }

    /// <summary>下载 HTTP(S) 备份包并限制响应大小和运行时间。</summary>
    private async Task DownloadAsync(string url, string destination, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("备份 URL 必须是 HTTP 或 HTTPS 地址。", nameof(url));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.Backup.TransferTimeoutSeconds));
        using HttpClient client = _httpClientFactory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 0
            && response.Content.Headers.ContentLength > _options.Backup.MaxTransferBytes)
            throw new InvalidDataException("备份包超过配置的最大传输大小。");

        await using Stream source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, useAsync: true);
        await CopyStreamWithLimitAsync(source, target, _options.Backup.MaxTransferBytes, timeout.Token).ConfigureAwait(false);
    }

    /// <summary>复制本地包并执行同样的大小限制。</summary>
    private void CopyFileWithLimit(string source, string destination)
    {
        var info = new FileInfo(source);
        if (info.Length > _options.Backup.MaxTransferBytes)
            throw new InvalidDataException("备份包超过配置的最大传输大小。");
        File.Copy(source, destination, overwrite: false);
    }

    /// <summary>将 zip/tar 包解到受控临时目录，或直接返回已有目录。</summary>
    private static string ExtractOrUseDirectory(string packagePath, string temporaryRoot)
    {
        if (Directory.Exists(packagePath))
            return packagePath;

        string extractRoot = Path.Combine(temporaryRoot, "extracted");
        Directory.CreateDirectory(extractRoot);
        string extension = Path.GetExtension(packagePath).ToLowerInvariant();
        if (extension == ".zip")
            ZipFile.ExtractToDirectory(packagePath, extractRoot, overwriteFiles: false);
        else if (extension is ".tar" or ".gz" or ".tgz")
            TarFile.ExtractToDirectory(packagePath, extractRoot, overwriteFiles: false);
        else
            throw new InvalidDataException("备份包必须是 zip 或 tar 文件。");

        string? manifest = Directory
            .EnumerateFiles(extractRoot, BackupManifest.FileName, SearchOption.AllDirectories)
            .Take(2)
            .SingleOrDefault();
        if (manifest is null)
            throw new InvalidDataException("解包后未找到 sonnetdb.backup.json。");
        return Path.GetDirectoryName(manifest)!;
    }

    /// <summary>复制流并限制总字节数。</summary>
    private static async Task CopyStreamWithLimitAsync(
        Stream source,
        Stream destination,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[128 * 1024];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
        {
            total = checked(total + read);
            if (total > maximumBytes)
                throw new InvalidDataException("备份包超过配置的最大传输大小。");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string CreateTemporaryRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "sonnetdb-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDeleteTemporaryRoot(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
