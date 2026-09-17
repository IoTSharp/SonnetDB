using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Engine;
using SonnetDB.Hosting;
using SonnetDB.ObjectStorage;

namespace SonnetDB.SemanticSearch;

/// <summary>
/// 持久化、调度并执行 Bucket 图片 embedding 与缩略图派生任务。
/// </summary>
internal sealed class ObjectSemanticProcessingService : BackgroundService
{
    internal const string ThumbnailBucket = "sonnetdb-semantic-thumbnails";
    private const int MaxDecodedPixels = 100_000_000;
    private readonly Channel<WorkItem> _queue;
    private readonly ConcurrentDictionary<WorkItem, byte> _scheduled = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _cooldowns = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _backfillsInProgress = new(StringComparer.OrdinalIgnoreCase);
    private readonly TsdbRegistry _registry;
    private readonly SemanticImageSearchService _semanticImages;
    private readonly SemanticSearchOptions _options;
    private readonly ObjectProcessingOptions _processingOptions;
    private readonly ILogger<ObjectSemanticProcessingService> _logger;
    private int _nextDatabase;

    /// <summary>用于验证领取与终态写入失败不会停止消费，生产环境不设置。</summary>
    internal Action? BeforeJobCommitForTest { get; set; }

    /// <summary>测试读取排队及执行中去重占位的数量。</summary>
    internal int ScheduledCountForTest => _scheduled.Count;

    /// <summary>测试单步消费，不启动后台线程或定时轮询。</summary>
    internal Task ProcessNextForTestAsync(CancellationToken cancellationToken)
        => _queue.Reader.TryRead(out var item) ? ProcessAsync(item, cancellationToken) : Task.CompletedTask;

    /// <summary>创建附带可选故障注入钩子的状态存储。</summary>
    private ObjectProcessingJobStore OpenJobStore(Tsdb tsdb)
        => new(tsdb) { BeforeCommitForTest = BeforeJobCommitForTest };

    /// <summary>冻结资源边界并创建真实可拒绝写入的有界内存队列。</summary>
    public ObjectSemanticProcessingService(
        TsdbRegistry registry,
        SemanticImageSearchService semanticImages,
        IOptions<ServerOptions> options,
        ILogger<ObjectSemanticProcessingService> logger)
    {
        _registry = registry;
        _semanticImages = semanticImages;
        _options = options.Value.SemanticSearch;
        _processingOptions = _options.ObjectProcessing.BoundedCopy();
        _logger = logger;
        _queue = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(_processingOptions.QueueCapacity)
        {
            // TryWrite 必须在满载时返回 false；DropWrite 会报告成功但丢弃任务并破坏去重状态。
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = _processingOptions.WorkerCount == 1,
            SingleWriter = false,
        });
    }

    /// <summary>先持久化派生请求，再尝试进入内存队列；满载由 due 恢复。</summary>
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
        var jobStore = new ObjectProcessingJobStore(tsdb);
        var previous = jobStore.Read(jobId, CancellationToken.None);
        var existing = previous?.Job;
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
        if (!jobStore.TryWrite(previous, job, CancellationToken.None))
            return GetStatus(database, tsdb, info.Bucket, info.Key);
        _ = TrySchedule(new WorkItem(database, job.Id));
        return ToResponse(database, job);
    }

    /// <summary>从对象元数据创建派生内容删除任务。</summary>
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
        var jobStore = new ObjectProcessingJobStore(tsdb);
        var previous = jobStore.Read(jobId, CancellationToken.None);
        var existing = previous?.Job;
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
        // 删除可能与旧 worker 的租约/终态写入竞争，限定三次 CAS 重试，避免漏掉派生清理。
        using var enqueueDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        bool written = false;
        for (int attempt = 0; attempt < 3 && !written; attempt++)
        {
            enqueueDeadline.Token.ThrowIfCancellationRequested();
            if (attempt > 0)
                previous = jobStore.Read(job.Id, enqueueDeadline.Token);
            written = jobStore.TryWrite(previous, job, enqueueDeadline.Token);
        }
        if (!written)
            throw new IOException("对象派生任务已并发变化，请重试删除派生内容请求。");
        _ = TrySchedule(new WorkItem(database, job.Id));
        return ToResponse(database, job);
    }

    /// <summary>对显式桶回填请求分批登记任务。</summary>
    public ObjectBucketSemanticBackfillResponse EnqueueBucket(
        string database,
        Tsdb tsdb,
        string bucket,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        ArgumentNullException.ThrowIfNull(tsdb);
        var store = new SndbObjectStore(tsdb);
        _ = store.GetSemanticOptions(bucket);

        var stateStore = OpenJobStore(tsdb);
        var existing = stateStore.ReadBackfill(bucket, cancellationToken);
        if (existing is null || existing.State.Completed)
        {
            var initial = new ObjectSemanticBackfillState(bucket, null, 0, 0, 0, false, DateTimeOffset.UtcNow);
            if (!stateStore.TryWriteBackfill(existing, initial, cancellationToken))
                existing = stateStore.ReadBackfill(bucket, cancellationToken);
            else
                existing = stateStore.ReadBackfill(bucket, cancellationToken);
        }

        // 只在请求内处理一页；后续页由恢复循环继续，避免大桶扫描阻塞 HTTP 请求。
        if (existing is not null && _backfillsInProgress.TryAdd(database + "\n" + bucket, 0))
        {
            try { existing = ProcessBackfillPage(database, tsdb, existing, cancellationToken); }
            finally { _backfillsInProgress.TryRemove(database + "\n" + bucket, out _); }
        }
        var result = existing?.State ?? new ObjectSemanticBackfillState(bucket, null, 0, 0, 0, false, DateTimeOffset.UtcNow);
        return new ObjectBucketSemanticBackfillResponse(bucket, result.ScannedObjects, result.QueuedObjects, result.SkippedObjects)
        {
            HasMore = !result.Completed,
            ContinuationToken = result.ContinuationToken,
            Completed = result.Completed,
        };
    }

    /// <summary>读取对象当前版本的派生任务审计状态。</summary>
    public ObjectProcessingStatusResponse? GetStatus(
        string database,
        Tsdb tsdb,
        string bucket,
        string key)
    {
        var info = new SndbObjectStore(tsdb).HeadObject(bucket, key);
        if (info is null)
            return null;
        var job = new ObjectProcessingJobStore(tsdb).Read(JobId(bucket, key, info.VersionId), CancellationToken.None)?.Job;
        return job is null ? null : ToResponse(database, job);
    }

    /// <summary>按原对象当前版本返回派生缩略图。</summary>
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

    /// <summary>启动有限并发 worker；后台生命周期由宿主取消，每轮恢复另有独立预算。</summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tasks = new Task[_processingOptions.WorkerCount + 1];
        tasks[0] = RecoverLoopAsync(stoppingToken);
        for (int index = 0; index < _processingOptions.WorkerCount; index++)
            tasks[index + 1] = ProcessLoopAsync(stoppingToken);
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>按固定间隔执行有界恢复；单库故障仅触发该库冷却。</summary>
    private async Task RecoverLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_processingOptions.RecoveryIntervalMilliseconds));
        do
        {
            RecoverDueJobs(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>逐个消费任务；任何读取或状态持久化失败都由处理方法隔离。</summary>
    private async Task ProcessLoopAsync(CancellationToken stoppingToken)
    {
        await foreach (var item in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            await ProcessAsync(item, stoppingToken).ConfigureAwait(false);
    }

    /// <summary>从入队到处理完成统一去重，满载时释放占位并保留持久 due 键。</summary>
    private bool TrySchedule(WorkItem item)
    {
        if (!_scheduled.TryAdd(item, 0))
            return true;
        if (_queue.Writer.TryWrite(item))
            return true;
        _scheduled.TryRemove(item, out _);
        return false;
    }

    /// <summary>轮转数据库，只读取到期页与一页旧记录，预算耗尽后下轮继续。</summary>
    internal void RecoverDueJobs(CancellationToken stoppingToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        budget.CancelAfter(_processingOptions.RecoveryBudgetMilliseconds);
        var databases = _registry.ListDatabases();
        int count = Math.Min(databases.Count, _processingOptions.DatabasesPerPass);
        for (int index = 0; index < count && !budget.IsCancellationRequested; index++)
        {
            string database = databases[_nextDatabase++ % databases.Count];
            if (_nextDatabase >= databases.Count)
                _nextDatabase = 0;
            if (IsCoolingDown(database) || !_registry.TryGet(database, out var tsdb))
                continue;
            try
            {
                var store = OpenJobStore(tsdb);
                foreach (var entry in store.ReadDue(DateTimeOffset.UtcNow, _processingOptions.RecoveryPageSize, budget.Token))
                {
                    budget.Token.ThrowIfCancellationRequested();
                    string id = Encoding.UTF8.GetString(entry.Value.Span);
                    ObjectProcessingJobStore.StoredJob? stored;
                    try { stored = store.Read(id, budget.Token); }
                    catch (System.Text.Json.JsonException exception)
                    {
                        _logger.LogError(exception, "派生任务正文损坏，保留正文并移除不可执行的索引：{Database}/{JobId}", database, id);
                        store.RemoveStaleDue(entry, budget.Token);
                        continue;
                    }
                    if (stored is null || ObjectProcessingJobStore.DueKey(stored.Job) != Encoding.UTF8.GetString(entry.Key.Span))
                    {
                        store.RemoveStaleDue(entry, budget.Token);
                        continue;
                    }
                    if (!TrySchedule(new WorkItem(database, id)))
                        break;
                }
                // 旧记录迁移完成标记持久化，之后仅一次主键读取，完全跳过 job: 历史区间。
                var migrated = store.MigrateLegacyPage(_processingOptions.LegacyMigrationPageSize, budget.Token);
                if (migrated.Corrupt > 0)
                    _logger.LogError("派生旧任务页包含 {Count} 条损坏正文，已保留审计且跳过执行：{Database}", migrated.Corrupt, database);
                if (migrated.Scanned > 0)
                    _logger.LogDebug("派生旧任务恢复：{Database} 扫描 {Scanned}，调度 {Scheduled}，完成 {Complete}",
                        database, migrated.Scanned, migrated.Scheduled, migrated.Complete);
                ProcessBackfillPageIfDue(database, tsdb, budget.Token);
            }
            catch (OperationCanceledException) when (budget.IsCancellationRequested)
            {
                // 本轮预算耗尽保持游标；宿主取消由外层定时器观察。
                return;
            }
            catch (ObjectDisposedException)
            {
                _cooldowns.TryRemove(database, out _);
            }
            catch (Exception exception)
            {
                CoolDown(database, exception);
            }
        }
    }

    /// <summary>恢复循环每轮最多推进一个 Bucket 回填页，页游标持久化后再释放占位。</summary>
    private void ProcessBackfillPageIfDue(string database, Tsdb tsdb, CancellationToken cancellationToken)
    {
        var store = OpenJobStore(tsdb);
        // 每轮最多检查固定数量 Bucket，避免维护循环在 Bucket 数量异常时失去时间上界。
        int inspected = 0;
        foreach (var bucket in new SndbObjectStore(tsdb).ListBuckets())
        {
            if (inspected++ >= 64)
                break;
            cancellationToken.ThrowIfCancellationRequested();
            var state = store.ReadBackfill(bucket.Name, cancellationToken);
            if (state is null || state.State.Completed)
                continue;
            string id = database + "\n" + bucket.Name;
            if (!_backfillsInProgress.TryAdd(id, 0))
                continue;
            try
            {
                ProcessBackfillPage(database, tsdb, state, cancellationToken);
            }
            finally
            {
                _backfillsInProgress.TryRemove(id, out _);
            }
            break;
        }
    }

    /// <summary>读取固定大小对象页并 CAS 推进游标；队列满载时仅保留持久任务，绝不等待。</summary>
    private ObjectProcessingJobStore.StoredBackfill ProcessBackfillPage(
        string database,
        Tsdb tsdb,
        ObjectProcessingJobStore.StoredBackfill current,
        CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_processingOptions.BackfillBudgetMilliseconds);
        var objectStore = new SndbObjectStore(tsdb);
        var page = objectStore.ListObjects(
            current.State.Bucket,
            prefix: null,
            maxKeys: _processingOptions.BackfillPageSize,
            continuationToken: current.State.ContinuationToken,
            delimiter: null,
            cancellationToken: budget.Token);
        int scanned = current.State.ScannedObjects;
        int queued = current.State.QueuedObjects;
        int skipped = current.State.SkippedObjects;
        foreach (var info in page.Objects)
        {
            budget.Token.ThrowIfCancellationRequested();
            scanned++;
            if (EnqueueIfEnabled(database, tsdb, info) is not null)
                queued++;
            else
                skipped++;
        }
        var next = current.State with
        {
            ContinuationToken = page.NextContinuationToken,
            ScannedObjects = scanned,
            QueuedObjects = queued,
            SkippedObjects = skipped,
            Completed = page.NextContinuationToken is null,
            UpdatedUtc = DateTimeOffset.UtcNow,
        };
        var jobStore = OpenJobStore(tsdb);
        if (!jobStore.TryWriteBackfill(current, next, budget.Token))
            return jobStore.ReadBackfill(next.Bucket, budget.Token) ?? current;
        return jobStore.ReadBackfill(next.Bucket, budget.Token)
            ?? new ObjectProcessingJobStore.StoredBackfill(next, current.Version);
    }

    /// <summary>以 CAS 领取持久租约并执行；状态提交失败保留到期租约供重启或冷却后恢复。</summary>
    private async Task ProcessAsync(WorkItem item, CancellationToken stoppingToken)
    {
        try
        {
            if (IsCoolingDown(item.Database) || !_registry.TryGet(item.Database, out var tsdb))
                return;
            var store = OpenJobStore(tsdb);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(_processingOptions.ProcessingTimeoutSeconds));
            var previous = store.Read(item.JobId, deadline.Token);
            if (previous is null || !ObjectProcessingJobStore.IsDue(previous.Job, DateTimeOffset.UtcNow))
                return;
            var job = previous.Job;
            if (job.Attempts >= _processingOptions.MaxAttempts)
            {
                _ = store.TryWrite(previous, job with
                {
                    Status = "failed", Error = "达到派生任务最大执行次数；可显式重试。",
                    UpdatedUtc = DateTimeOffset.UtcNow, NextAttemptUtc = null, LeaseId = null, LeaseUntilUtc = null,
                }, deadline.Token);
                return;
            }
            job = job with
            {
                Status = "processing", Attempts = job.Attempts + 1, Error = null,
                UpdatedUtc = DateTimeOffset.UtcNow, NextAttemptUtc = null,
                LeaseId = Guid.NewGuid().ToString("N"),
                LeaseUntilUtc = DateTimeOffset.UtcNow.AddSeconds(_processingOptions.ProcessingTimeoutSeconds + 30),
            };
            if (!store.TryWrite(previous, job, deadline.Token))
                return;

            SemanticObjectProcessingJob result;
            try
            {
                result = await ProcessCoreAsync(item.Database, tsdb, job, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // 执行已经取消后短时释放本租约，正常重启无需等待完整执行期限；写失败仍由到期租约恢复。
                try
                {
                    using var releaseBudget = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    var owned = store.Read(item.JobId, releaseBudget.Token);
                    if (owned?.Job.LeaseId == job.LeaseId)
                        _ = store.TryWrite(owned, job with
                        {
                            Status = "retry", LeaseId = null, LeaseUntilUtc = null,
                            NextAttemptUtc = DateTimeOffset.UtcNow, UpdatedUtc = DateTimeOffset.UtcNow,
                        }, releaseBudget.Token);
                }
                catch (Exception releaseFailure)
                {
                    _logger.LogWarning(releaseFailure, "停止时未能释放派生任务租约，将在到期后恢复：{JobId}", item.JobId);
                }
                throw;
            }
            catch (Exception exception)
            {
                bool storagePressure = IsStoragePressure(exception);
                bool retry = storagePressure || !IsPermanentInputFailure(exception) && job.Attempts < _processingOptions.MaxAttempts;
                int delaySeconds = storagePressure ? _processingOptions.StorageCooldownSeconds : Math.Min(60, 1 << Math.Min(job.Attempts, 6));
                result = job with
                {
                    Status = retry ? "retry" : "failed",
                    // 存储压力不表示图片输入坏了，不消耗输入失败次数；每次仍受执行期限和冷却约束。
                    Attempts = storagePressure ? Math.Max(0, job.Attempts - 1) : job.Attempts,
                    Error = exception.GetBaseException().Message,
                    UpdatedUtc = DateTimeOffset.UtcNow,
                    NextAttemptUtc = retry ? DateTimeOffset.UtcNow.AddSeconds(delaySeconds) : null,
                };
                if (storagePressure)
                    CoolDown(item.Database, exception);
                else
                    _logger.LogWarning(exception, "派生任务失败 {Database}/{JobId}，尝试 {Attempt}，重试 {Retry}",
                        item.Database, job.Id, job.Attempts, retry);
            }
            // 执行 deadline 可能已过期，状态提交使用独立有界预算且仍响应宿主取消。
            using var commitDeadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            commitDeadline.CancelAfter(TimeSpan.FromSeconds(5));
            var claimed = store.Read(item.JobId, commitDeadline.Token);
            if (claimed?.Job.LeaseId == job.LeaseId)
                _ = store.TryWrite(claimed, result with { LeaseId = null, LeaseUntilUtc = null }, commitDeadline.Token);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // 领取或终态提交失败也不能 fault 消费循环；持久 job/due 原子批次不会产生半条任务。
            CoolDown(item.Database, exception);
        }
        finally
        {
            _scheduled.TryRemove(item, out _);
        }
    }

    /// <summary>读取并顺手删除过期的数据库冷却记录。</summary>
    private bool IsCoolingDown(string database)
    {
        if (!_cooldowns.TryGetValue(database, out var until))
            return false;
        if (until > DateTimeOffset.UtcNow)
            return true;
        _cooldowns.TryRemove(database, out _);
        return false;
    }

    /// <summary>存储失败只暂停对应数据库的派生负载，不停止业务写入或缩略图功能。</summary>
    private void CoolDown(string database, Exception exception)
    {
        _cooldowns[database] = DateTimeOffset.UtcNow.AddSeconds(_processingOptions.StorageCooldownSeconds);
        _logger.LogWarning(exception, "派生后台任务暂停 {Seconds} 秒等待存储恢复：{Database}",
            _processingOptions.StorageCooldownSeconds, database);
    }

    /// <summary>检查点拒绝及 I/O 故障优先让出存储，不通过加大预算掩盖压力。</summary>
    internal static bool IsStoragePressure(Exception exception)
        => exception is IOException || exception is TimeoutException;

    /// <summary>输入尺寸、格式和解码内容错误没有重试价值；存储损坏不会归入输入错误。</summary>
    internal static bool IsPermanentInputFailure(Exception exception)
        => exception is PermanentObjectInputException or UnknownImageFormatException or InvalidImageContentException;

    /// <summary>根据对象当前版本与桶开关执行幂等缩略图、索引写入或清理。</summary>
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

        var current = store.GetBucket(job.Bucket) is null ? null : store.HeadObject(job.Bucket, job.Key);
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
            throw new PermanentObjectInputException($"图片超过语义处理上限 {_options.MaxImageBytes} 字节。");

        var read = store.OpenRead(job.Bucket, job.Key, range: null, job.VersionId)
            ?? throw new InvalidDataException("待处理对象版本不存在。");
        byte[] image;
        await using (read.Content)
        {
            using var output = new MemoryStream(checked((int)read.Length));
            await read.Content.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            image = output.ToArray();
        }

        // 键可从对象版本确定，状态提交中断后也能复用已经生成的缩略图。
        string? thumbnailKey = job.ThumbnailKey ?? (createThumbnail ? ThumbnailKey(job.Bucket, job.Key, job.VersionId) : null);
        if (createThumbnail
            && (store.GetBucket(ThumbnailBucket) is null || store.HeadObject(ThumbnailBucket, thumbnailKey!) is null))
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

    /// <summary>先识别尺寸再解码单帧，避免超大像素输入先分配整图内存。</summary>
    private static async Task<byte[]> CreateThumbnailAsync(
        ReadOnlyMemory<byte> encodedImage,
        int maxWidth,
        int maxHeight,
        int quality,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var imageInfo = Image.Identify(encodedImage.ToArray())
            ?? throw new PermanentObjectInputException("无法识别图片格式。");
        if ((long)imageInfo.Width * imageInfo.Height > MaxDecodedPixels)
            throw new PermanentObjectInputException($"图片像素数不能超过 {MaxDecodedPixels}。");
        using Image image = Image.Load(encodedImage.Span);
        cancellationToken.ThrowIfCancellationRequested();

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

    /// <summary>已排队或已生成当前版本时去重，保留显式 force 重跑能力。</summary>
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

    /// <summary>按内容类型判断是否需要图片派生。</summary>
    private static bool IsImage(string contentType)
        => contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    /// <summary>对象版本对应稳定任务身份。</summary>
    private static string JobId(string bucket, string key, string versionId)
        => Hash(bucket + "\n" + key + "\n" + versionId);

    /// <summary>同一对象跨版本使用稳定的语义图片身份。</summary>
    private static string SemanticImageId(string bucket, string key)
        => "obj-" + Hash(bucket + "\n" + key);

    /// <summary>缩略图键包含原对象版本，重复执行不产生新路径。</summary>
    private static string ThumbnailKey(string bucket, string key, string versionId)
        => $"buckets/{Hash(bucket)}/objects/{Hash(key)}/{Uri.EscapeDataString(versionId)}.webp";

    /// <summary>将对象标识映射为固定长度路径片段。</summary>
    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>生成兼容现有 API 的状态和缩略图地址。</summary>
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

    /// <summary>逐段编码对象路径并保留分隔符。</summary>
    private static string EscapeObjectKey(string key)
        => string.Join('/', key.Split('/').Select(Uri.EscapeDataString));

    private readonly record struct WorkItem(string Database, string JobId);

    /// <summary>明确由用户图片内容或尺寸导致的不可重试输入错误。</summary>
    private sealed class PermanentObjectInputException(string message) : Exception(message);
}
