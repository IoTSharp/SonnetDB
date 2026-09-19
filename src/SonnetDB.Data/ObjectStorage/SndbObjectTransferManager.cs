using System.Security.Cryptography;
using System.Text.Json;
using SonnetDB.ObjectStorage;

namespace SonnetDB.Data.ObjectStorage;

/// <summary>对象传输的有界资源和恢复配置。</summary>
/// <param name="MultipartThresholdBytes">达到该正文大小后使用 multipart。</param>
/// <param name="PartSizeBytes">multipart 分片大小。</param>
/// <param name="MaxConcurrency">同时上传的分片数。</param>
/// <param name="MaxRetries">单个分片的最大安全重试次数。</param>
/// <param name="ResumeManifestPath">可选的本地恢复清单路径；清单写入采用临时文件替换。</param>
public sealed record SndbObjectTransferOptions(
    long MultipartThresholdBytes = 64L * 1024 * 1024,
    int PartSizeBytes = 8 * 1024 * 1024,
    int MaxConcurrency = 4,
    int MaxRetries = 2,
    string? ResumeManifestPath = null)
{
    /// <summary>校验传输边界。</summary>
    public void Validate()
    {
        if (MultipartThresholdBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(MultipartThresholdBytes));
        if (PartSizeBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(PartSizeBytes));
        if (MaxConcurrency is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(MaxConcurrency));
        if (MaxRetries is < 0 or > 8)
            throw new ArgumentOutOfRangeException(nameof(MaxRetries));
    }
}

/// <summary>对象传输进度。</summary>
public sealed record SndbObjectTransferProgress(
    long BytesTransferred,
    long TotalBytes,
    int CompletedParts,
    int TotalParts,
    string Operation);

/// <summary>对象传输结果及校验和。</summary>
public sealed record SndbObjectTransferResult(
    SndbObjectInfo Object,
    long BytesTransferred,
    string Sha256,
    bool Resumed,
    int Parts);

/// <summary>
/// 提供流式对象上传和下载。上传先落入受控临时文件，使 multipart 分片可以有界并发，
/// 分片失败只重试幂等的 UploadPart；complete 和普通 PUT 在发送后不自动重放。
/// </summary>
public sealed class SndbObjectTransferManager
{
    private readonly SndbObjectStorageClient _client;
    private readonly SndbObjectTransferOptions _options;

    /// <summary>创建对象传输管理器。</summary>
    public SndbObjectTransferManager(SndbObjectStorageClient client, SndbObjectTransferOptions? options = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _options = options ?? new();
        _options.Validate();
    }

    /// <summary>上传对象并按大小自动选择普通 PUT 或 multipart。</summary>
    public async Task<SndbObjectTransferResult> UploadAsync(
        string bucket,
        string key,
        Stream source,
        string? contentType = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        IReadOnlyDictionary<string, string>? tags = null,
        IProgress<SndbObjectTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();

        string tempDirectory = Path.Combine(Path.GetTempPath(), "sonnetdb-transfer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string sourcePath = Path.Combine(tempDirectory, "source.bin");
        try
        {
            (long length, string sha256) = await MaterializeAsync(source, sourcePath, progress, cancellationToken).ConfigureAwait(false);
            if (length < _options.MultipartThresholdBytes)
            {
                await using Stream input = OpenSource(sourcePath);
                SndbObjectInfo info = await _client.PutObjectAsync(bucket, key, input, contentType, metadata, tags, cancellationToken).ConfigureAwait(false);
                VerifyChecksum(info, sha256);
                Report(progress, length, length, 1, 1, "upload");
                return new SndbObjectTransferResult(info, length, sha256, false, 1);
            }

            return await UploadMultipartAsync(bucket, key, sourcePath, length, sha256, contentType, metadata, tags, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    /// <summary>以流式方式下载对象，并在结束时校验服务端返回的 SHA-256。</summary>
    public async Task<SndbObjectTransferResult> DownloadAsync(
        string bucket,
        string key,
        Stream destination,
        IProgress<SndbObjectTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        cancellationToken.ThrowIfCancellationRequested();
        SndbObjectReadResult read = await _client.OpenReadAsync(bucket, key, cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new SndbObjectStorageException("object_not_found", $"Object '{bucket}/{key}' was not found.");
        await using (read.Content.ConfigureAwait(false))
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[128 * 1024];
            long transferred = 0;
            int readCount;
            while ((readCount = await read.Content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await destination.WriteAsync(buffer.AsMemory(0, readCount), cancellationToken).ConfigureAwait(false);
                hash.AppendData(buffer, 0, readCount);
                transferred = checked(transferred + readCount);
                Report(progress, transferred, read.TotalLength, 1, 1, "download");
            }

            string sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            VerifyChecksum(read.Info, sha256);
            return new SndbObjectTransferResult(read.Info, transferred, sha256, false, 1);
        }
    }

    private async Task<SndbObjectTransferResult> UploadMultipartAsync(
        string bucket,
        string key,
        string sourcePath,
        long length,
        string sourceSha256,
        string? contentType,
        IReadOnlyDictionary<string, string>? metadata,
        IReadOnlyDictionary<string, string>? tags,
        IProgress<SndbObjectTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        int partCount = checked((int)((length + _options.PartSizeBytes - 1) / _options.PartSizeBytes));
        if (partCount > 10_000)
            throw new ArgumentOutOfRangeException(nameof(length), "对象分片数不能超过 10000。");
        var parts = new SndbMultipartPartInfo[partCount];
        bool resumed = false;
        SndbMultipartUploadInfo upload;
        TransferManifest? manifest = LoadManifest(bucket, key, length, sourceSha256);
        if (manifest is not null && manifest.PartSizeBytes == _options.PartSizeBytes)
        {
            upload = new SndbMultipartUploadInfo(
                manifest.Bucket,
                manifest.Key,
                manifest.UploadId,
                manifest.ContentType,
                DateTimeOffset.MinValue,
                DateTimeOffset.MaxValue,
                manifest.Metadata,
                manifest.Tags);
            foreach (TransferManifestPart part in manifest.Parts.Where(part => part.PartNumber >= 1 && part.PartNumber <= partCount))
                parts[part.PartNumber - 1] = new SndbMultipartPartInfo(part.PartNumber, part.SizeBytes, part.ETag, part.Sha256);
            resumed = true;
        }
        else
        {
            upload = await _client.InitiateMultipartUploadAsync(bucket, key, contentType, metadata, tags, cancellationToken).ConfigureAwait(false);
            SaveManifest(CreateManifest(upload, length, sourceSha256, parts));
        }
        using var gate = new SemaphoreSlim(_options.MaxConcurrency, _options.MaxConcurrency);
        try
        {
            var workers = Enumerable.Range(0, partCount).Select(async index =>
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    int partNumber = index + 1;
                    long offset = checked((long)index * _options.PartSizeBytes);
                    long size = Math.Min(_options.PartSizeBytes, length - offset);
                    if (parts[index] is null)
                    {
                        parts[index] = await UploadPartWithRetryAsync(upload, sourcePath, partNumber, offset, size, length, partCount, progress, cancellationToken).ConfigureAwait(false);
                        SaveManifest(CreateManifest(upload, length, sourceSha256, parts));
                    }
                    else
                    {
                        Report(progress, offset + size, length, partNumber, partCount, "resume");
                    }
                }
                finally
                {
                    gate.Release();
                }
            }).ToArray();
            await Task.WhenAll(workers).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            SndbObjectInfo info = await _client.CompleteMultipartUploadAsync(
                bucket,
                key,
                upload.UploadId,
                parts.Select(static part => part.PartNumber).ToArray(),
                cancellationToken).ConfigureAwait(false);
            VerifyChecksum(info, sourceSha256);
            DeleteManifest();
            return new SndbObjectTransferResult(info, length, sourceSha256, resumed, partCount);
        }
        catch
        {
            // 取消/失败时保留 manifest 只在调用方明确配置恢复路径时发生；否则清理服务端会话。
            if (string.IsNullOrWhiteSpace(_options.ResumeManifestPath))
            {
                try { await _client.AbortMultipartUploadAsync(bucket, key, upload.UploadId, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception exception) when (exception is IOException or HttpRequestException or SndbServerException) { }
            }
            throw;
        }
    }

    private async Task<SndbMultipartPartInfo> UploadPartWithRetryAsync(
        SndbMultipartUploadInfo upload,
        string sourcePath,
        int partNumber,
        long offset,
        long size,
        long totalLength,
        int totalParts,
        IProgress<SndbObjectTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (int attempt = 0; attempt <= _options.MaxRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using Stream input = OpenSource(sourcePath, offset, size);
                SndbMultipartPartInfo part = await _client.UploadPartAsync(upload.Bucket, upload.Key, upload.UploadId, partNumber, input, cancellationToken).ConfigureAwait(false);
                if (part.SizeBytes != size || string.IsNullOrWhiteSpace(part.Sha256))
                    throw new InvalidDataException("Multipart part response does not match the source extent.");
                Report(progress, offset + size, totalLength, partNumber, totalParts, "upload");
                return part;
            }
            catch (Exception exception) when (attempt < _options.MaxRetries && IsSafePartRetry(exception))
            {
                last = exception;
                await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
            }
        }

        throw last ?? new IOException($"Multipart part {partNumber} failed.");
    }

    private static async Task<(long Length, string Sha256)> MaterializeAsync(
        Stream source,
        string path,
        IProgress<SndbObjectTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[128 * 1024];
        long length = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            hash.AppendData(buffer, 0, read);
            length = checked(length + read);
            Report(progress, length, source.CanSeek ? source.Length : 0, 0, 0, "prepare");
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        return (length, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private TransferManifest? LoadManifest(string bucket, string key, long length, string sha256)
    {
        if (string.IsNullOrWhiteSpace(_options.ResumeManifestPath) || !File.Exists(_options.ResumeManifestPath))
            return null;
        try
        {
            byte[] bytes = File.ReadAllBytes(_options.ResumeManifestPath);
            TransferManifest? manifest = JsonSerializer.Deserialize(bytes, SndbObjectClientJsonContext.Default.TransferManifest);
            return manifest is not null
                && string.Equals(manifest.Bucket, bucket, StringComparison.Ordinal)
                && string.Equals(manifest.Key, key, StringComparison.Ordinal)
                && manifest.Length == length
                && string.Equals(manifest.Sha256, sha256, StringComparison.OrdinalIgnoreCase)
                ? manifest
                : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return null;
        }
    }

    private TransferManifest CreateManifest(SndbMultipartUploadInfo upload, long length, string sha256, SndbMultipartPartInfo[] parts)
        => new(
            upload.Bucket,
            upload.Key,
            upload.UploadId,
            length,
            sha256,
            _options.PartSizeBytes,
            upload.ContentType,
            new Dictionary<string, string>(upload.Metadata, StringComparer.Ordinal),
            new Dictionary<string, string>(upload.Tags, StringComparer.Ordinal),
            parts.Where(static part => part is not null)
                .Select(static part => new TransferManifestPart(part.PartNumber, part.SizeBytes, part.ETag, part.Sha256))
                .ToArray());

    private void SaveManifest(TransferManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(_options.ResumeManifestPath))
            return;
        string path = Path.GetFullPath(_options.ResumeManifestPath);
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string temporary = path + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, SndbObjectClientJsonContext.Default.TransferManifest);
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
        }
    }

    private static Stream OpenSource(string path, long offset = 0, long? length = null)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (offset > 0) stream.Seek(offset, SeekOrigin.Begin);
        return length.HasValue ? new FileSliceStream(stream, length.Value) : stream;
    }

    private static bool IsSafePartRetry(Exception exception)
        => exception is IOException or HttpRequestException or TimeoutException
            || exception is SndbServerException server
                && server.StatusCode is System.Net.HttpStatusCode.RequestTimeout
                    or System.Net.HttpStatusCode.TooManyRequests
                    or >= System.Net.HttpStatusCode.InternalServerError;

    private static void VerifyChecksum(SndbObjectInfo info, string expected)
    {
        if (!string.IsNullOrWhiteSpace(info.Sha256) && !string.Equals(info.Sha256, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("对象 SHA-256 与传输源不一致。");
    }

    private static void Report(IProgress<SndbObjectTransferProgress>? progress, long transferred, long total, int completedParts, int totalParts, string operation)
        => progress?.Report(new(transferred, total, completedParts, totalParts, operation));

    private void DeleteManifest()
    {
        if (!string.IsNullOrWhiteSpace(_options.ResumeManifestPath))
            File.Delete(_options.ResumeManifestPath);
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class FileSliceStream(FileStream inner, long remaining) : Stream
    {
        private long _remaining = remaining;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _remaining;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override int Read(Span<byte> buffer)
        {
            if (_remaining == 0) return 0;
            int count = inner.Read(buffer[..(int)Math.Min(buffer.Length, _remaining)]);
            _remaining -= count;
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            return Read(buffer.AsSpan(offset, count));
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining == 0) return 0;
            int count = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], cancellationToken).ConfigureAwait(false);
            _remaining -= count;
            return count;
        }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.FromException(new NotSupportedException());
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
