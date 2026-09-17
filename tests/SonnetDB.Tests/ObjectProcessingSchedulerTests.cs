using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SonnetDB.Configuration;
using SonnetDB.Engine;
using SonnetDB.Hosting;
using SonnetDB.Json;
using SonnetDB.ObjectStorage;
using SonnetDB.SemanticSearch;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>验证真实 KV 的派生任务恢复、原子调度与有界负载；不启动外部服务。</summary>
public sealed class ObjectProcessingSchedulerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SonnetDB.ObjectScheduler." + Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(90));
    private readonly TsdbRegistry _registry;
    private readonly Tsdb _db;

    /// <summary>为每个测试创建独占数据库目录与统一执行期限。</summary>
    public ObjectProcessingSchedulerTests()
    {
        _registry = new TsdbRegistry(_root);
        Assert.True(_registry.TryCreate("images", out _db));
    }

    /// <summary>到期页不能触碰大量历史终态及未来重试，实际候选数应随页大小变化。</summary>
    [Fact]
    public void ReadDue_ThousandsOfTerminalJobs_OnlyVisitsDuePage()
    {
        var store = new ObjectProcessingJobStore(_db);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        for (int index = 0; index < 4_096; index++)
        {
            _deadline.Token.ThrowIfCancellationRequested();
            Assert.True(store.TryWrite(null, Job($"done-{index:D6}", "completed", now), _deadline.Token));
        }
        for (int index = 0; index < 16; index++)
        {
            _deadline.Token.ThrowIfCancellationRequested();
            Assert.True(store.TryWrite(null, Job($"due-{index:D4}", "pending", now.AddMinutes(-1)), _deadline.Token));
            Assert.True(store.TryWrite(null, Job($"future-{index:D4}", "retry", now) with
                { NextAttemptUtc = now.AddHours(1) }, _deadline.Token));
        }
        int candidates = 0;
        store.DueCandidateVisitedForTest = () => candidates++;
        var due = store.ReadDue(now, 8, _deadline.Token);
        Assert.Equal(8, due.Count);
        Assert.InRange(candidates, 8, 9);
        Assert.All(due, entry => Assert.StartsWith("due-", Encoding.UTF8.GetString(entry.Value.Span)));
    }

    /// <summary>旧记录迁移每次只处理一页，数据库关闭重开后继续游标，完成后不会再次扫描。</summary>
    [Fact]
    public void MigrateLegacy_RestartAndCompletedCursor_ResumesWithoutRescanningHistory()
    {
        string path = Path.Combine(_root, "recovery-only");
        using (var db = Tsdb.Open(new TsdbOptions { RootDirectory = path }))
        {
            for (int index = 0; index < 11; index++)
            {
                _deadline.Token.ThrowIfCancellationRequested();
                SeedLegacy(db, Job($"{index:D4}", index == 8 ? "processing" : "completed", DateTimeOffset.UtcNow.AddHours(-1)));
            }
            var first = new ObjectProcessingJobStore(db).MigrateLegacyPage(4, _deadline.Token);
            Assert.Equal(4, first.Scanned);
            Assert.False(first.Complete);
        }
        using (var reopened = Tsdb.Open(new TsdbOptions { RootDirectory = path }))
        {
            var store = new ObjectProcessingJobStore(reopened);
            Assert.Equal(4, store.MigrateLegacyPage(4, _deadline.Token).Scanned);
            var last = store.MigrateLegacyPage(4, _deadline.Token);
            Assert.Equal(3, last.Scanned);
            Assert.Equal(1, last.Scheduled);
            Assert.True(last.Complete);
            store.LegacyCandidateVisitedForTest = () => throw new InvalidOperationException("完成后不可重新扫描历史。");
            Assert.Equal(0, store.MigrateLegacyPage(4, _deadline.Token).Scanned);
            Assert.Equal("0008", Encoding.UTF8.GetString(Assert.Single(store.ReadDue(DateTimeOffset.UtcNow, 4, _deadline.Token)).Value.Span));
        }
    }

    /// <summary>旧迁移提交失败时游标与 due 必须一起保持原状，下次整页恢复无丢失。</summary>
    [Fact]
    public void MigrateLegacy_CommitRejected_RetryRecoversWholePage()
    {
        SeedLegacy(_db, Job("legacy", "pending", DateTimeOffset.UtcNow.AddHours(-1)));
        var store = new ObjectProcessingJobStore(_db) { BeforeCommitForTest = () => throw new IOException("checkpoint budget rejected") };
        Assert.Throws<IOException>(() => store.MigrateLegacyPage(4, _deadline.Token));
        Assert.Empty(store.ReadDue(DateTimeOffset.UtcNow, 4, _deadline.Token));
        store.BeforeCommitForTest = null;
        var recovered = store.MigrateLegacyPage(4, _deadline.Token);
        Assert.True(recovered.Complete);
        Assert.Equal(1, recovered.Scheduled);
        Assert.Single(store.ReadDue(DateTimeOffset.UtcNow, 4, _deadline.Token));
    }

    /// <summary>已取消的迁移不推进游标；坏 JSON 留存审计，后续正常任务仍可恢复。</summary>
    [Fact]
    public void MigrateLegacy_CancellationAndCorruptRecord_ReportsDamageWithoutBlockingLaterJobs()
    {
        _db.Keyspaces.Open(ObjectProcessingJobStore.KeyspaceName).Put("job:000-bad", "{broken"u8);
        SeedLegacy(_db, Job("001-good", "pending", DateTimeOffset.UtcNow.AddMinutes(-1)));
        var store = new ObjectProcessingJobStore(_db);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => store.MigrateLegacyPage(4, cancelled.Token));
        var page = store.MigrateLegacyPage(4, _deadline.Token);
        Assert.True(page.Complete);
        Assert.Equal(1, page.Corrupt);
        Assert.Equal(1, page.Scheduled);
        Assert.NotNull(_db.Keyspaces.Open(ObjectProcessingJobStore.KeyspaceName).Get("job:000-bad"));
        Assert.Single(store.ReadDue(DateTimeOffset.UtcNow, 4, _deadline.Token));
    }

    /// <summary>租约未到期不能重复领取；后来删除任务替换旧任务后，旧 worker 不得回写完成。</summary>
    [Fact]
    public void TryWrite_LeaseAndReplacement_RejectsStaleCompletion()
    {
        var store = new ObjectProcessingJobStore(_db);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var job = Job("version", "pending", now.AddMinutes(-1));
        Assert.True(store.TryWrite(null, job, _deadline.Token));
        var pending = store.Read(job.Id, _deadline.Token);
        var leased = job with { Status = "processing", LeaseId = "lease-a", LeaseUntilUtc = now.AddMinutes(2) };
        Assert.True(store.TryWrite(pending, leased, _deadline.Token));
        var claimed = store.Read(job.Id, _deadline.Token);
        Assert.Empty(store.ReadDue(now, 4, _deadline.Token));
        Assert.Single(store.ReadDue(now.AddMinutes(3), 4, _deadline.Token));
        Assert.True(store.TryWrite(claimed, job with { Operation = "delete" }, _deadline.Token));
        Assert.False(store.TryWrite(claimed, leased with { Status = "completed" }, _deadline.Token));
        Assert.Equal("delete", store.Read(job.Id, _deadline.Token)!.Job.Operation);
        Assert.Single(store.ReadDue(now, 4, _deadline.Token));
    }

    /// <summary>终态写入遭拒必须保留原租约索引，重试成功后只移除 due 而保留审计。</summary>
    [Fact]
    public void TryWrite_TerminalCommitRejected_LeavesRecoverableLease()
    {
        var store = new ObjectProcessingJobStore(_db);
        var lease = Job("recover", "processing", DateTimeOffset.UtcNow) with
            { LeaseId = "a", LeaseUntilUtc = DateTimeOffset.UtcNow.AddSeconds(10) };
        Assert.True(store.TryWrite(null, lease, _deadline.Token));
        var before = store.Read(lease.Id, _deadline.Token);
        store.BeforeCommitForTest = () => throw new IOException("WAL unavailable");
        Assert.Throws<IOException>(() => store.TryWrite(before, lease with { Status = "completed" }, _deadline.Token));
        Assert.Equal("processing", store.Read(lease.Id, _deadline.Token)!.Job.Status);
        Assert.Single(store.ReadDue(DateTimeOffset.UtcNow.AddMinutes(1), 4, _deadline.Token));
        store.BeforeCommitForTest = null;
        Assert.True(store.TryWrite(before, lease with { Status = "completed", LeaseId = null, LeaseUntilUtc = null }, _deadline.Token));
        Assert.Empty(store.ReadDue(DateTimeOffset.UtcNow.AddMinutes(1), 4, _deadline.Token));
        Assert.Equal("completed", store.Read(lease.Id, _deadline.Token)!.Job.Status);
    }

    /// <summary>反复恢复不会重复填满内存队列；队列满载的持久任务在空位出现后继续处理。</summary>
    [Fact]
    public async Task RecoverDueJobs_FullQueueAndRepeatedRecovery_DeduplicatesAndEventuallyDrains()
    {
        using var fixture = CreateService(queueCapacity: 2);
        var store = new ObjectProcessingJobStore(_db);
        for (int index = 0; index < 3; index++)
            Assert.True(store.TryWrite(null, Job($"item-{index}", "pending", DateTimeOffset.UtcNow.AddMinutes(-1)), _deadline.Token));
        fixture.Service.RecoverDueJobs(_deadline.Token);
        fixture.Service.RecoverDueJobs(_deadline.Token);
        Assert.Equal(2, fixture.Service.ScheduledCountForTest);
        await fixture.Service.ProcessNextForTestAsync(_deadline.Token);
        fixture.Service.RecoverDueJobs(_deadline.Token);
        Assert.Equal(2, fixture.Service.ScheduledCountForTest);
        await fixture.Service.ProcessNextForTestAsync(_deadline.Token);
        await fixture.Service.ProcessNextForTestAsync(_deadline.Token);
        Assert.Equal(0, fixture.Service.ScheduledCountForTest);
        Assert.Empty(store.ReadDue(DateTimeOffset.UtcNow.AddHours(1), 4, _deadline.Token));
        Assert.Equal("superseded", store.Read("item-2", _deadline.Token)!.Job.Status);
    }

    /// <summary>终态提交失败不会抛出到消费循环；新进程在租约到期后恢复任务。</summary>
    [Fact]
    public async Task ProcessAsync_StateWriteFailure_RemainsRecoverableAfterRestart()
    {
        using var fixture = CreateService();
        var store = new ObjectProcessingJobStore(_db);
        var job = Job("recover-worker", "pending", DateTimeOffset.UtcNow.AddHours(-1));
        Assert.True(store.TryWrite(null, job, _deadline.Token));
        fixture.Service.RecoverDueJobs(_deadline.Token);
        int writes = 0;
        fixture.Service.BeforeJobCommitForTest = () =>
        {
            if (++writes == 2)
                throw new IOException("checkpoint pressure on terminal state");
        };
        await fixture.Service.ProcessNextForTestAsync(_deadline.Token);
        var leased = store.Read(job.Id, _deadline.Token)!;
        Assert.Equal("processing", leased.Job.Status);
        Assert.Equal(0, fixture.Service.ScheduledCountForTest);
        Assert.True(store.TryWrite(leased, leased.Job with { LeaseUntilUtc = DateTimeOffset.UtcNow.AddSeconds(-1) }, _deadline.Token));
        using var restarted = CreateService();
        restarted.Service.RecoverDueJobs(_deadline.Token);
        await restarted.Service.ProcessNextForTestAsync(_deadline.Token);
        Assert.Equal("superseded", store.Read(job.Id, _deadline.Token)!.Job.Status);
    }

    /// <summary>损坏图片首次解码后永久失败，不产生无限重试；正常图片仍生成可解码缩略图。</summary>
    [Fact]
    public async Task ProcessAsync_InvalidAndValidImages_FailsBadInputAndKeepsThumbnailAvailable()
    {
        using var fixture = CreateService();
        var objects = new SndbObjectStore(_db);
        objects.CreateBucket("captures");
        objects.SetSemanticOptions("captures", false, true, 32, 32, 80);
        using var badBytes = new MemoryStream([1, 2, 3]);
        var bad = await objects.PutObjectAsync("captures", "bad.png", badBytes, "image/png", cancellationToken: _deadline.Token);
        Assert.NotNull(fixture.Service.EnqueueIfEnabled("images", _db, bad));
        await fixture.Service.ProcessNextForTestAsync(_deadline.Token);
        var failure = fixture.Service.GetStatus("images", _db, "captures", "bad.png")!;
        Assert.Equal("failed", failure.Status);
        Assert.Equal(1, failure.Attempts);
        using var source = new Image<Rgba32>(64, 64);
        using var goodBytes = new MemoryStream();
        await source.SaveAsPngAsync(goodBytes, _deadline.Token);
        goodBytes.Position = 0;
        var good = await objects.PutObjectAsync("captures", "good.png", goodBytes, "image/png", cancellationToken: _deadline.Token);
        Assert.NotNull(fixture.Service.EnqueueIfEnabled("images", _db, good));
        await fixture.Service.ProcessNextForTestAsync(_deadline.Token);
        Assert.Equal("completed", fixture.Service.GetStatus("images", _db, "captures", "good.png")!.Status);
        var thumbnail = fixture.Service.OpenThumbnail(_db, "captures", "good.png");
        Assert.NotNull(thumbnail);
        await using (thumbnail.Content)
        using (var decoded = await Image.LoadAsync(thumbnail.Content, _deadline.Token))
            Assert.InRange(decoded.Width, 1, 32);
    }

    /// <summary>应用配置可直接绑定并收紧资源边界，不依赖部署环境变量。</summary>
    [Fact]
    public void Bind_ObjectProcessingConfiguration_IsBounded()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["SonnetDBServer:SemanticSearch:ObjectProcessing:QueueCapacity"] = "16",
            ["SonnetDBServer:SemanticSearch:ObjectProcessing:WorkerCount"] = "999",
            ["SonnetDBServer:SemanticSearch:ObjectProcessing:RecoveryBudgetMilliseconds"] = "0",
        }).Build();
        var settings = ServerOptionsBinder.Bind(configuration).SemanticSearch.ObjectProcessing.BoundedCopy();
        Assert.Equal(16, settings.QueueCapacity);
        Assert.Equal(4, settings.WorkerCount);
        Assert.Equal(10, settings.RecoveryBudgetMilliseconds);
        Assert.False(ObjectSemanticProcessingService.IsPermanentInputFailure(new IOException("disk corrupt")));
        Assert.True(ObjectSemanticProcessingService.IsStoragePressure(new IOException("checkpoint refused")));
    }

    /// <summary>构造可手动单步运行的后台服务，未启动线程、模型或监听端口。</summary>
    private ServiceFixture CreateService(int queueCapacity = 8)
    {
        var options = Options.Create(new ServerOptions
        {
            SemanticSearch = new SemanticSearchOptions
            {
                ObjectProcessing = new ObjectProcessingOptions { QueueCapacity = queueCapacity, RecoveryBudgetMilliseconds = 5_000 },
            },
        });
        var indexes = new USearchSemanticIndexRegistry(NullLogger<USearchSemanticIndexRegistry>.Instance);
        var images = new SemanticImageSearchService(options, new UnusedProvider(), indexes, NullLogger<SemanticImageSearchService>.Instance);
        return new ServiceFixture(new ObjectSemanticProcessingService(_registry, images, options,
            NullLogger<ObjectSemanticProcessingService>.Instance), images, indexes);
    }

    /// <summary>创建不需要模型和对象输入的调度测试任务。</summary>
    private static SemanticObjectProcessingJob Job(string id, string status, DateTimeOffset now)
        => new(id, "missing-bucket", id, "v1", "image/png", "upsert", false, true, 32, 32, 80,
            "test", status, 0, null, null, null, now, now, null);

    /// <summary>模拟旧版本只保存 job 正文而没有 due 索引的磁盘状态。</summary>
    private static void SeedLegacy(Tsdb db, SemanticObjectProcessingJob job)
        => db.Keyspaces.Open(ObjectProcessingJobStore.KeyspaceName).Put(ObjectProcessingJobStore.JobPrefix + job.Id,
            JsonSerializer.SerializeToUtf8Bytes(job, ServerJsonContext.Default.SemanticObjectProcessingJob));

    /// <summary>先关闭数据库全部后台资源，再仅删除本测试明确创建的唯一目录。</summary>
    public void Dispose()
    {
        _registry.Dispose();
        _deadline.Dispose();
        string path = Path.GetFullPath(_root);
        if (path.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(path).StartsWith("SonnetDB.ObjectScheduler.", StringComparison.Ordinal)
            && Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    /// <summary>集中回收测试实例持有的索引和服务资源。</summary>
    private sealed record ServiceFixture(ObjectSemanticProcessingService Service, SemanticImageSearchService Images,
        USearchSemanticIndexRegistry Indexes) : IDisposable
    {
        /// <summary>此测试从未启动 BackgroundService，直接释放本实例即可。</summary>
        public void Dispose() { Service.Dispose(); Images.Dispose(); Indexes.Dispose(); }
    }

    /// <summary>缩略图测试禁止意外调用 embedding，避免伪模型掩盖耦合。</summary>
    private sealed class UnusedProvider : IMultimodalEmbeddingProvider
    {
        /// <summary>声明未配置模型。</summary>
        public MultimodalEmbeddingProviderInfo Info => new("unused", "test", 1, false);
        /// <summary>缩略图路径不应调用文本模型。</summary>
        public ValueTask<float[]> EmbedTextAsync(string text, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("缩略图不应调用 embedding。");
        /// <summary>缩略图路径不应调用图片模型。</summary>
        public ValueTask<float[]> EmbedImageAsync(ReadOnlyMemory<byte> image, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("缩略图不应调用 embedding。");
    }
}
