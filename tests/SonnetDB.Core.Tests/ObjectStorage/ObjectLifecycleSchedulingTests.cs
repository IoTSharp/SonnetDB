using System.Text;
using SonnetDB.Engine;
using SonnetDB.ObjectStorage;
using Xunit;

namespace SonnetDB.Core.Tests.ObjectStorage;

/// <summary>验证分页清理的版本语义、正常写入协同和取消后的资源释放。</summary>
public sealed class ObjectLifecycleSchedulingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-lifecycle-scheduling-" + Guid.NewGuid().ToString("N"));

    /// <summary>创建用例独占目录。</summary>
    public ObjectLifecycleSchedulingTests() => Directory.CreateDirectory(_root);

    /// <summary>只回收当前用例的数据。</summary>
    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>同一对象的多个版本跨页时，仍只按原 latest 指针分类并完整回收应过期版本。</summary>
    [Fact]
    public async Task LifecycleAcrossPagesKeepsVersionClassification()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var store = new SndbObjectStore(db);
        store.CreateBucket("images");
        for (int i = 0; i < 257; i++)
        {
            deadline.Token.ThrowIfCancellationRequested();
            string key = "capture/" + i.ToString("D5");
            await store.PutObjectAsync("images", key, new MemoryStream([1]), cancellationToken: deadline.Token);
            await store.PutObjectAsync("images", key, new MemoryStream([2]), cancellationToken: deadline.Token);
            store.DeleteObject("images", key);
        }
        store.SetLifecycle("images", null, 0, 0);
        SndbBucketLifecycleApplyResult result = store.ApplyLifecycle("images", deadline.Token);
        Assert.Equal(514, result.RemovedNoncurrentVersions);
        Assert.Equal(257, result.RemovedDeleteMarkers);
        Assert.Equal(0, result.ExpiredCurrentObjects);
        Assert.Empty(store.ListObjectVersions("images").Versions);
    }

    /// <summary>精确对象维护不能清理同名前缀下其它对象的历史版本。</summary>
    [Fact]
    public async Task ObjectLifecycleOnlyTouchesExactObject()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var store = new SndbObjectStore(db);
        store.CreateBucket("images");
        foreach (string key in new[] { "capture/a", "capture/a/b" })
        {
            await store.PutObjectAsync("images", key, new MemoryStream([1]), cancellationToken: deadline.Token);
            await store.PutObjectAsync("images", key, new MemoryStream([2]), cancellationToken: deadline.Token);
            store.DeleteObject("images", key);
        }
        store.SetLifecycle("images", null, 0, 0);
        SndbBucketLifecycleApplyResult result = store.ApplyObjectLifecycle("images", "capture/a", deadline.Token);
        Assert.Equal(2, result.RemovedNoncurrentVersions);
        Assert.Equal(1, result.RemovedDeleteMarkers);
        Assert.Empty(store.ListObjectVersions("images", "capture/a").Versions);
        Assert.Equal(3, store.ListObjectVersions("images", "capture/a/b").Versions.Count);
    }

    /// <summary>维护暂停在对象边界时正常 PUT 可继续，快照之后的新当前版本不会被过期规则误删。</summary>
    [Fact]
    public async Task ConcurrentPutContinuesAndProtectsNewCurrentVersion()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var store = new SndbObjectStore(db);
        store.CreateBucket("images");
        await store.PutObjectAsync("images", "alpha", new MemoryStream([1]), cancellationToken: deadline.Token);
        await store.PutObjectAsync("images", "beta", new MemoryStream([2]), cancellationToken: deadline.Token);
        store.SetLifecycle("images", 0, null, null);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        store.LifecycleKeyReadyTestHook = key =>
        {
            if (key != "alpha") return;
            ready.TrySetResult();
            release.Wait(deadline.Token);
        };
        Task<SndbBucketLifecycleApplyResult> cleanup = Task.Run(() => store.ApplyLifecycle("images", deadline.Token), deadline.Token);
        SndbObjectInfo latest;
        try
        {
            await ready.Task.WaitAsync(deadline.Token);
            latest = await store.PutObjectAsync("images", "alpha", new MemoryStream(Encoding.UTF8.GetBytes("new")), cancellationToken: deadline.Token);
        }
        finally { release.Set(); }
        SndbBucketLifecycleApplyResult result = await cleanup.WaitAsync(deadline.Token);
        Assert.Equal("beta", Assert.Single(result.ExpiredObjects).Key);
        Assert.Equal(latest.VersionId, store.HeadObject("images", "alpha")!.VersionId);
        Assert.Equal(2, store.ListObjectVersions("images", "alpha").Versions.Count);
    }

    /// <summary>取消终止后续对象处理并释放维护协调器，后续业务写入及下一轮清理都可继续。</summary>
    [Fact]
    public async Task CancellationReleasesMaintenanceRunnerWithoutDeletingLaterObjects()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var cancelled = new CancellationTokenSource();
        using var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var store = new SndbObjectStore(db);
        store.CreateBucket("images");
        await store.PutObjectAsync("images", "alpha", new MemoryStream([1]), cancellationToken: deadline.Token);
        await store.PutObjectAsync("images", "beta", new MemoryStream([2]), cancellationToken: deadline.Token);
        store.SetLifecycle("images", 0, null, null);
        store.LifecycleKeyProcessedTestHook = _ => cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => store.ApplyLifecycle("images", cancelled.Token));
        Assert.NotNull(store.HeadObject("images", "beta"));
        store.LifecycleKeyProcessedTestHook = null;
        await store.PutObjectAsync("images", "gamma", new MemoryStream([3]), cancellationToken: deadline.Token);
        Assert.Equal(2, store.ApplyLifecycle("images", deadline.Token).ExpiredCurrentObjects);
    }
}
