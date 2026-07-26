using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Engine;
using SonnetDB.Hosting;
using SonnetDB.Json;
using SonnetDB.ObjectStorage;

namespace SonnetDB.SemanticSearch;

/// <summary>
/// 持久化、调度并执行 Bucket 图片 embedding 与缩略图派生任务。
/// </summary>
internal sealed class ObjectSemanticProcessingService : BackgroundService
{
    internal const string ThumbnailBucket = "sonnetdb-semantic-thumbnails";
    private const string JobKeyspace = "__semantic_object_processing";
    private const string JobPrefix = "job:";
    private const int QueueCapacity = 256;
    private const int MaxAttempts = 5;
    private const int MaxDecodedPixels = 100_000_000;
    private static readonly TimeSpan RecoveryInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CompletedRetention = TimeSpan.FromDays(7);
    private readonly Channel<WorkItem> _queue = Channel.CreateBounded<WorkItem>(
        new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });
    private readonly ConcurrentDictionary<string, byte> _active = new(StringComparer.Ordinal);
    private readonly TsdbRegistry _registry;
    private readonly SemanticImageSearchService _semanticImages;
    private readonly SemanticSearchOptions _options;
    private readonly ILogger<ObjectSemanticProcessingService> _logger;

    public ObjectSemanticProcessingService(
        TsdbRegistry registry,
        SemanticImageSearchService semanticImages,
        IOptions<ServerOptions> options,
        ILogger<ObjectSemanticProcessingService> logger)
    {
        _registry = registry;
        _semanticImages = semanticImages;
        _options = options.Value.SemanticSearch;
        _logger = logger;
    }

    public ObjectProcessingStatusResponse? EnqueueIfEnabled(
        string database,
        Tsdb tsdb,
        SndbObjectInfo info,
        bool force = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(info);
        if (!IsImage(info.ContentType))
            return null;

        var objectStore = new SndbObjectStore(tsdb);
        var bucketOptions = objectStore.GetSemanticOptions(info.Bucket);
        if (!bucketOptions.AsyncIngestionEnabled && !bucketOptions.ThumbnailEnabled)
            return null;

        DateTimeOffset now = DateTimeOffset.UtcNow;
        string jobId = JobId(info.Bucket, info.Key, info.VersionId);
        string semanticImageId = SemanticImageId(info.Bucket, info.Key);
        string thumbnailKey = ThumbnailKey(info.Bucket, info.Key, info.VersionId);
        var existing = GetJob(tsdb, jobId);
        if (!force && IsAlreadyScheduledOrDerived(
                tsdb,
                objectStore,
                info,
                bucketOptions,
                semanticImageId,
                thumbnailKey,
                existing))
        {
            return null;
        }

        var job = new SemanticObjectProcessingJob(
            jobId,
            info.Bucket,
            info.Key,
            info.VersionId,
            info.ContentType,
            "upsert",
            bucketOptions.AsyncIngestionEnabled,
            bucketOptions.ThumbnailEnabled,
            bucketOptions.ThumbnailMaxWidth,
            bucketOptions.ThumbnailMaxHeight,
            bucketOptions.ThumbnailQuality,
            _options.Profile,
            "pending",
            Attempts: 0,
            Error: null,
            SemanticImageId: bucketOptions.AsyncIngestionEnabled ? semanticImageId : null,
            ThumbnailKey: null,
            now,
            now,
            NextAttemptUtc: null);
        PutJob(tsdb, job);
        _ = _queue.Writer.TryWrite(new WorkItem(database, job.Id));
        return ToResponse(database, job);
    }

    public ObjectProcessingStatusResponse? EnqueueDeletion(
        string database,
        Tsdb tsdb,
        SndbObjectInfo info)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(info);
        return EnqueueDeletion(
            database,
            tsdb,
            info.Bucket,
            info.Key,
            info.VersionId,
            info.ContentType);
    }

    /// <summary>
    /// 使用已删除对象的稳定身份排入语义索引和缩略图清理任务。
    /// </summary>
    public ObjectProcessingStatusResponse? EnqueueDeletion(
        string database,
        Tsdb tsdb,
        string bucket,
        string key,
        string versionId,
        string contentType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        if (!IsImage(contentType))
            return null;

        string jobId = JobId(bucket, key, versionId);
        string semanticImageId = SemanticImageId(bucket, key);
        string thumbnailKey = ThumbnailKey(bucket, key, versionId);
        var existing = GetJob(tsdb, jobId);
        bool upsertInFlight = existing is
        {
            Operation: "upsert",
            Status: "pending" or "processing" or "retry",
        };
        bool semanticExists = _semanticImages.IsStoredObjectVersionIndexed(
            tsdb,
            semanticImageId,
            bucket,
            key,
            versionId);
        var objectStore = new SndbObjectStore(tsdb);
        bool thumbnailExists = objectStore.GetBucket(ThumbnailBucket) is not null
            && objectStore.HeadObject(ThumbnailBucket, thumbnailKey) is not null;
        if (!upsertInFlight && !semanticExists && !thumbnailExists)
            return null;

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var job = new SemanticObjectProcessingJob(
            jobId,
            bucket,
            key,
            versionId,
            contentType,
            "delete",
            SemanticRequested: true,
            ThumbnailRequested: true,
            ThumbnailMaxWidth: 320,
            ThumbnailMaxHeight: 320,
            ThumbnailQuality: 80,
            _options.Profile,
            "pending",
            Attempts: 0,
            Error: null,
            SemanticImageId: semanticImageId,
            ThumbnailKey: thumbnailKey,
            now,
            now,
            NextAttemptUtc: null);
        PutJob(tsdb, job);
        _ = _queue.Writer.TryWrite(new WorkItem(database, job.Id));
        return ToResponse(database, job);
    }

    public ObjectBucketSemanticBackfillResponse EnqueueBucket(
        string database,
        Tsdb tsdb,
        string bucket)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        ArgumentNullException.ThrowIfNull(tsdb);
        var store = new SndbObjectStore(tsdb);
        _ = store.GetSemanticOptions(bucket);

        int scanned = 0;
        int queued = 0;
        string? continuationToken = null;
        do
        {
            var page = store.ListObjects(bucket, prefix: null, maxKeys: 10_000, continuationToken);
            foreach (var info in page.Objects)
            {
                scanned++;
                if (EnqueueIfEnabled(database, tsdb, info) is not null)
                    queued++;
            }
            continuationToken = page.NextContinuationToken;
        }
        while (continuationToken is not null);

        return new ObjectBucketSemanticBackfillResponse(bucket, scanned, queued, scanned - queued);
    }

    public ObjectProcessingStatusResponse? GetStatus(
        string database,
        Tsdb tsdb,
        string bucket,
        string key)
    {
        var info = new SndbObjectStore(tsdb).HeadObject(bucket, key);
        if (info is null)
            return null;
        var job = GetJob(tsdb, JobId(bucket, key, info.VersionId));
        return job is null ? null : ToResponse(database, job);
    }

    public SndbObjectReadResult? OpenThumbnail(Tsdb tsdb, string bucket, string key)
    {
        var store = new SndbObjectStore(tsdb);
        var info = store.HeadObject(bucket, key);
        if (info is null)
            return null;
        return store.GetBucket(ThumbnailBucket) is null
            ? null
            : store.OpenRead(ThumbnailBucket, ThumbnailKey(bucket, key, info.VersionId));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Task recovery = RecoverLoopAsync(stoppingToken);
        Task processing = ProcessLoopAsync(stoppingToken);
        await Task.WhenAll(recovery, processing).ConfigureAwait(false);
    }

    private async Task RecoverLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(RecoveryInterval);
        do
        {
            RecoverDueJobs();
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task ProcessLoopAsync(CancellationToken stoppingToken)
    {
        await foreach (var item in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            await ProcessAsync(item, stoppingToken).ConfigureAwait(false);
    }

    private void RecoverDueJobs()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (string database in _registry.ListDatabases())
        {
            if (!_registry.TryGet(database, out var tsdb))
                continue;

            try
            {
                foreach (var entry in tsdb.Keyspaces.Open(JobKeyspace).ScanPrefix(JobPrefix, limit: int.MaxValue))
                {
                    var job = JsonSerializer.Deserialize(
                        entry.Value.Span,
                        ServerJsonContext.Default.SemanticObjectProcessingJob);
                    if (job is null || !IsDue(job, now))
                        continue;
                    if (!_queue.Writer.TryWrite(new WorkItem(database, job.Id)))
                        break;
                }
            }
            catch (ObjectDisposedException)
            {
                // 数据库正被删除；下一轮只扫描仍注册的实例。
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to recover semantic object jobs for database {Database}.", database);
            }
        }
    }

    private async Task ProcessAsync(WorkItem item, CancellationToken stoppingToken)
    {
        string activeKey = item.Database + ":" + item.JobId;
        if (!_active.TryAdd(activeKey, 0))
            return;

        try
        {
            if (!_registry.TryGet(item.Database, out var tsdb))
                return;
            var job = GetJob(tsdb, item.JobId);
            if (job is null || !IsDue(job, DateTimeOffset.UtcNow))
                return;

            job = job with
            {
                Status = "processing",
                Attempts = job.Attempts + 1,
                Error = null,
                UpdatedUtc = DateTimeOffset.UtcNow,
                NextAttemptUtc = null,
            };
            PutJob(tsdb, job);

            try
            {
                job = await ProcessCoreAsync(item.Database, tsdb, job, stoppingToken).ConfigureAwait(false);
                PutJob(tsdb, job, completed: true);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                bool retry = job.Attempts < MaxAttempts;
                TimeSpan delay = TimeSpan.FromSeconds(Math.Min(60, 1 << Math.Min(job.Attempts, 6)));
                var failed = job with
                {
                    Status = retry ? "retry" : "failed",
                    Error = ex.GetBaseException().Message,
                    UpdatedUtc = DateTimeOffset.UtcNow,
                    NextAttemptUtc = retry ? DateTimeOffset.UtcNow.Add(delay) : null,
                };
                PutJob(tsdb, failed, completed: !retry);
                _logger.LogWarning(
                    ex,
                    "Semantic object processing failed for {Database}/{Bucket}/{Key} attempt {Attempt}.",
                    item.Database,
                    job.Bucket,
                    job.Key,
                    job.Attempts);
            }
        }
        finally
        {
            _active.TryRemove(activeKey, out _);
        }
    }

    private async Task<SemanticObjectProcessingJob> ProcessCoreAsync(
        string database,
        Tsdb tsdb,
        SemanticObjectProcessingJob job,
        CancellationToken cancellationToken)
    {
        var store = new SndbObjectStore(tsdb);
        if (string.Equals(job.Operation, "delete", StringComparison.Ordinal))
        {
            if (job.SemanticImageId is not null)
            {
                _ = await _semanticImages.DeleteStoredObjectVersionAsync(
                    database,
                    tsdb,
                    job.SemanticImageId,
                    job.Bucket,
                    job.Key,
                    job.VersionId,
                    cancellationToken).ConfigureAwait(false);
            }
            if (job.ThumbnailKey is not null
                && store.GetBucket(ThumbnailBucket) is not null
                && store.HeadObject(ThumbnailBucket, job.ThumbnailKey) is not null)
            {
                store.DeleteObject(ThumbnailBucket, job.ThumbnailKey);
            }

            return job with
            {
                Status = "completed",
                Error = null,
                UpdatedUtc = DateTimeOffset.UtcNow,
                NextAttemptUtc = null,
            };
        }

        var current = store.HeadObject(job.Bucket, job.Key);
        if (current is null || !string.Equals(current.VersionId, job.VersionId, StringComparison.Ordinal))
        {
            return job with
            {
                Status = "superseded",
                Error = null,
                UpdatedUtc = DateTimeOffset.UtcNow,
                NextAttemptUtc = null,
            };
        }

        var bucketOptions = store.GetSemanticOptions(job.Bucket);
        bool createSemantic = job.SemanticRequested && bucketOptions.AsyncIngestionEnabled;
        bool createThumbnail = job.ThumbnailRequested && bucketOptions.ThumbnailEnabled;
        if (!createSemantic && !createThumbnail)
        {
            return job with
            {
                Status = "cancelled",
                Error = null,
                UpdatedUtc = DateTimeOffset.UtcNow,
                NextAttemptUtc = null,
            };
        }

        if (current.SizeBytes > _options.MaxImageBytes)
            throw new InvalidDataException($"图片超过语义处理上限 {_options.MaxImageBytes} 字节。");

        var read = store.OpenRead(job.Bucket, job.Key, range: null, job.VersionId)
            ?? throw new InvalidDataException("待处理对象版本不存在。");
        byte[] image;
        await using (read.Content)
        {
            using var output = new MemoryStream(checked((int)read.Length));
            await read.Content.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            image = output.ToArray();
        }

        string? thumbnailKey = job.ThumbnailKey;
        if (createThumbnail
            && (thumbnailKey is null || store.HeadObject(ThumbnailBucket, thumbnailKey) is null))
        {
            byte[] thumbnail = await CreateThumbnailAsync(
                image,
                job.ThumbnailMaxWidth,
                job.ThumbnailMaxHeight,
                job.ThumbnailQuality,
                cancellationToken).ConfigureAwait(false);
            store.CreateBucket(ThumbnailBucket, "semantic-thumbnail");
            thumbnailKey = ThumbnailKey(job.Bucket, job.Key, job.VersionId);
            using var thumbnailContent = new MemoryStream(thumbnail, writable: false);
            await store.PutObjectAsync(
                ThumbnailBucket,
                thumbnailKey,
                thumbnailContent,
                "image/webp",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["source-bucket"] = job.Bucket,
                    ["source-key"] = job.Key,
                    ["source-version-id"] = job.VersionId,
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            job = job with { ThumbnailKey = thumbnailKey, UpdatedUtc = DateTimeOffset.UtcNow };
            PutJob(tsdb, job);
        }

        if (createSemantic)
        {
            string semanticImageId = job.SemanticImageId ?? SemanticImageId(job.Bucket, job.Key);
            await _semanticImages.IndexStoredObjectAsync(
                database,
                tsdb,
                semanticImageId,
                current,
                image,
                thumbnailKey is null ? null : ThumbnailBucket,
                thumbnailKey,
                cancellationToken).ConfigureAwait(false);
            job = job with { SemanticImageId = semanticImageId };
        }

        return job with
        {
            Status = "completed",
            Error = null,
            ThumbnailKey = thumbnailKey,
            UpdatedUtc = DateTimeOffset.UtcNow,
            NextAttemptUtc = null,
        };
    }

    private static async Task<byte[]> CreateThumbnailAsync(
        ReadOnlyMemory<byte> encodedImage,
        int maxWidth,
        int maxHeight,
        int quality,
        CancellationToken cancellationToken)
    {
        using Image image = Image.Load(encodedImage.Span);
        if ((long)image.Width * image.Height > MaxDecodedPixels)
            throw new InvalidDataException($"图片像素数不能超过 {MaxDecodedPixels}。");

        image.Mutate(operation => operation.AutoOrient());
        double scale = Math.Min(1d, Math.Min((double)maxWidth / image.Width, (double)maxHeight / image.Height));
        if (scale < 1d)
        {
            int width = Math.Max(1, (int)Math.Round(image.Width * scale));
            int height = Math.Max(1, (int)Math.Round(image.Height * scale));
            image.Mutate(operation => operation.Resize(new ResizeOptions
            {
                Size = new Size(width, height),
                Mode = ResizeMode.Stretch,
                Sampler = KnownResamplers.Lanczos3,
            }));
        }

        using var output = new MemoryStream();
        await image.SaveAsWebpAsync(
            output,
            new WebpEncoder { Quality = quality },
            cancellationToken).ConfigureAwait(false);
        return output.ToArray();
    }

    private static bool IsDue(SemanticObjectProcessingJob job, DateTimeOffset now)
        => job.Status is "pending" or "retry" or "processing"
            && (job.NextAttemptUtc is null || job.NextAttemptUtc <= now);

    private bool IsAlreadyScheduledOrDerived(
        Tsdb tsdb,
        SndbObjectStore objectStore,
        SndbObjectInfo info,
        SndbBucketSemanticOptionsInfo bucketOptions,
        string semanticImageId,
        string thumbnailKey,
        SemanticObjectProcessingJob? existing)
    {
        bool matchesCurrentOptions = existing is not null
            && existing.SemanticRequested == bucketOptions.AsyncIngestionEnabled
            && existing.ThumbnailRequested == bucketOptions.ThumbnailEnabled
            && existing.ThumbnailMaxWidth == bucketOptions.ThumbnailMaxWidth
            && existing.ThumbnailMaxHeight == bucketOptions.ThumbnailMaxHeight
            && existing.ThumbnailQuality == bucketOptions.ThumbnailQuality
            && string.Equals(existing.Profile, _options.Profile, StringComparison.Ordinal);
        if (matchesCurrentOptions && existing!.Status is "pending" or "processing" or "retry")
            return true;

        bool semanticCurrent = !bucketOptions.AsyncIngestionEnabled
            || _semanticImages.IsStoredObjectIndexed(tsdb, semanticImageId, info);
        bool thumbnailCurrent = !bucketOptions.ThumbnailEnabled
            || objectStore.GetBucket(ThumbnailBucket) is not null
            && objectStore.HeadObject(ThumbnailBucket, thumbnailKey) is not null;
        return semanticCurrent && thumbnailCurrent;
    }

    private static bool IsImage(string contentType)
        => contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    private static SemanticObjectProcessingJob? GetJob(Tsdb tsdb, string jobId)
    {
        byte[]? json = tsdb.Keyspaces.Open(JobKeyspace).Get(JobPrefix + jobId);
        return json is null
            ? null
            : JsonSerializer.Deserialize(json, ServerJsonContext.Default.SemanticObjectProcessingJob);
    }

    private static void PutJob(Tsdb tsdb, SemanticObjectProcessingJob job, bool completed = false)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            job,
            ServerJsonContext.Default.SemanticObjectProcessingJob);
        tsdb.Keyspaces.Open(JobKeyspace).Put(
            JobPrefix + job.Id,
            json,
            completed ? DateTimeOffset.UtcNow.Add(CompletedRetention) : null);
    }

    private static string JobId(string bucket, string key, string versionId)
        => Hash(bucket + "\n" + key + "\n" + versionId);

    private static string SemanticImageId(string bucket, string key)
        => "obj-" + Hash(bucket + "\n" + key);

    private static string ThumbnailKey(string bucket, string key, string versionId)
        => $"buckets/{Hash(bucket)}/objects/{Hash(key)}/{Uri.EscapeDataString(versionId)}.webp";

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static ObjectProcessingStatusResponse ToResponse(
        string database,
        SemanticObjectProcessingJob job) =>
        new(
            job.Id,
            job.Bucket,
            job.Key,
            job.VersionId,
            job.Operation,
            job.Status,
            job.SemanticRequested,
            job.ThumbnailRequested,
            job.Attempts,
            job.Error,
            job.SemanticImageId,
            job.ThumbnailKey is null
                ? null
                : $"/v1/db/{Uri.EscapeDataString(database)}/s3/{Uri.EscapeDataString(job.Bucket)}/{EscapeObjectKey(job.Key)}?thumbnail",
            job.CreatedUtc,
            job.UpdatedUtc,
            job.NextAttemptUtc);

    private static string EscapeObjectKey(string key)
        => string.Join('/', key.Split('/').Select(Uri.EscapeDataString));

    private readonly record struct WorkItem(string Database, string JobId);
}
