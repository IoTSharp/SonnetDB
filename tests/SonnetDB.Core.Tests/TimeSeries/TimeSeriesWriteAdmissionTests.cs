using SonnetDB.Data.TimeSeries;
using Xunit;

namespace SonnetDB.Core.Tests.TimeSeries;

public sealed class TimeSeriesWriteAdmissionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sonnetdb-write-admission-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(0)]
    [InlineData(65537)]
    public void CreateWriter_InvalidBatchLimit_RejectsOptions(int limit)
    {
        using var client = new SndbTimeSeriesClient($"Data Source={_root}");
        Assert.Throws<ArgumentOutOfRangeException>(() => client.CreateWriter("cpu", new() { MaxBatchPoints = limit }));
    }

    [Fact]
    public async Task WriteBatchAsync_InfiniteInput_RejectsAfterBoundedProbeWithoutWriting()
    {
        using var client = new SndbTimeSeriesClient($"Data Source={_root}");
        await using var writer = client.CreateWriter("cpu", new() { MaxBatchPoints = 3 });
        int observed = 0;
        bool disposed = false;

        IEnumerable<SndbTimeSeriesPoint> Points()
        {
            try
            {
                while (true)
                {
                    observed++;
                    yield return Point(observed);
                }
            }
            finally { disposed = true; }
        }

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => writer.WriteBatchAsync(Points()));
        Assert.Equal(4, observed);
        Assert.True(disposed);
        Assert.Equal(0, client.Embedded!.Catalog.Count);

        // 被拒批次没有污染 writer，后续正常批次仍可完成。
        Assert.True((await writer.WriteAsync(Point(10))).Succeeded);
    }

    [Fact]
    public async Task WriteBatchAsync_ExactLimit_PreservesPerItemResultsAcrossChunks()
    {
        using var client = new SndbTimeSeriesClient($"Data Source={_root}");
        await using var writer = client.CreateWriter("cpu", new() { MaxBatchPoints = 3, BatchSize = 1 });
        SndbTimeSeriesWriteResult result = await writer.WriteBatchAsync(
            [Point(1), SndbTimeSeriesPoint.Create("other").Timestamp(2).Field("value", 2d).Build(), Point(3)]);

        Assert.Equal(2, result.SucceededCount);
        Assert.Equal(new[] { 0, 1, 2 }, result.Items.Select(item => item.Index));
        Assert.Equal("invalid_point", result.Items[1].ErrorCode);
    }

    [Fact]
    public async Task WriteBatchAsync_CanceledBeforeEnumeration_DoesNotReadInput()
    {
        using var client = new SndbTimeSeriesClient($"Data Source={_root}");
        await using var writer = client.CreateWriter("cpu");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        bool enumerated = false;
        IEnumerable<SndbTimeSeriesPoint> Points()
        {
            enumerated = true;
            yield return Point(1);
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.WriteBatchAsync(Points(), cancellation.Token));
        Assert.False(enumerated);
        Assert.Equal(0, client.Embedded!.Catalog.Count);
    }

    [Fact]
    public async Task WriteBatchAsync_CanceledDuringEnumeration_DisposesInputWithoutWriting()
    {
        using var client = new SndbTimeSeriesClient($"Data Source={_root}");
        await using var writer = client.CreateWriter("cpu");
        using var cancellation = new CancellationTokenSource();
        bool disposed = false;
        IEnumerable<SndbTimeSeriesPoint> Points()
        {
            try
            {
                yield return Point(1);
                cancellation.Cancel();
                yield return Point(2);
                throw new InvalidOperationException("取消后不应继续枚举。");
            }
            finally { disposed = true; }
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.WriteBatchAsync(Points(), cancellation.Token));
        Assert.True(disposed);
        Assert.Equal(0, client.Embedded!.Catalog.Count);
    }

    [Fact]
    public async Task WriteBatchAsync_EnumerationThrows_PropagatesWithoutPartialWrite()
    {
        using var client = new SndbTimeSeriesClient($"Data Source={_root}");
        await using var writer = client.CreateWriter("cpu");
        IEnumerable<SndbTimeSeriesPoint> Points()
        {
            yield return Point(1);
            throw new IOException("input failed");
        }

        await Assert.ThrowsAsync<IOException>(() => writer.WriteBatchAsync(Points()));
        Assert.Equal(0, client.Embedded!.Catalog.Count);
    }

    [Fact]
    public async Task WriteBatchAsync_EmptyInput_StillChecksCancellationAndLifetime()
    {
        using var client = new SndbTimeSeriesClient($"Data Source={_root}");
        await using var writer = client.CreateWriter("cpu");
        Assert.Empty((await writer.WriteBatchAsync([])).Items);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.WriteBatchAsync([], new CancellationToken(true)));
        await writer.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => writer.WriteBatchAsync([]));
    }

    [Fact]
    public async Task WriteBatchAsync_AdmissionBusy_CancelsWaitingProducerBeforeEnumeration()
    {
        using var client = new SndbTimeSeriesClient($"Data Source={_root}");
        await using var writer = client.CreateWriter("cpu", new() { MaxPendingBatches = 1 });
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool secondEnumerated = false;
        IEnumerable<SndbTimeSeriesPoint> First()
        {
            entered.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("测试生产者未被释放。");
            yield return Point(1);
        }
        IEnumerable<SndbTimeSeriesPoint> Second()
        {
            secondEnumerated = true;
            yield return Point(2);
        }

        Task<SndbTimeSeriesWriteResult> first = Task.Run(() => writer.WriteBatchAsync(First()));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var cancellation = new CancellationTokenSource();
            Task<SndbTimeSeriesWriteResult> second = writer.WriteBatchAsync(Second(), cancellation.Token);
            Assert.False(second.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
            Assert.False(secondEnumerated);
        }
        finally { release.Set(); }
        Assert.True((await first.WaitAsync(TimeSpan.FromSeconds(10))).IsSuccess);
    }

    [Fact]
    public async Task DisposeAsync_AdmissionWaiterBehindBlockedInput_RejectsWaiterWithoutEnumeratingIt()
    {
        using var client = new SndbTimeSeriesClient($"Data Source={_root}");
        await using var writer = client.CreateWriter("cpu", new() { MaxPendingBatches = 1 });
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool secondEnumerated = false;
        IEnumerable<SndbTimeSeriesPoint> First()
        {
            entered.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("测试生产者未被释放。");
            yield return Point(1);
        }
        IEnumerable<SndbTimeSeriesPoint> Second()
        {
            secondEnumerated = true;
            yield return Point(2);
        }

        Task<SndbTimeSeriesWriteResult> first = Task.Run(() => writer.WriteBatchAsync(First()));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Task<SndbTimeSeriesWriteResult> second = writer.WriteBatchAsync(Second());
            Assert.False(second.IsCompleted);
            await writer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => second.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(secondEnumerated);
            Assert.Equal(0, client.Embedded!.Catalog.Count);
        }
        finally { release.Set(); }
        await Assert.ThrowsAsync<ObjectDisposedException>(() => first.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static SndbTimeSeriesPoint Point(int timestamp)
        => SndbTimeSeriesPoint.Create("cpu").Timestamp(timestamp).Field("value", (double)timestamp).Build();

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
