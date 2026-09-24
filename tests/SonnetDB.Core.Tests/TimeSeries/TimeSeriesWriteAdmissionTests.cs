using SonnetDB.Data.TimeSeries;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
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
    public async Task WriteBatchAsync_MaxPendingBatches_HoldsAdmissionUntilAcceptedBatchCompletes()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var firstRequestReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task server = ServeTwoWritesAsync(listener, firstRequestReceived, releaseFirstRequest);

        try
        {
            using var client = new SndbTimeSeriesClient(
                $"Data Source=sonnetdb+http://127.0.0.1:{endpoint.Port}/test;Protocol=rest;Timeout=10");
            await using var writer = client.CreateWriter("cpu", new() { MaxPendingBatches = 1 });
            Task<SndbTimeSeriesWriteItemResult> first = writer.WriteAsync(Point(1));
            await firstRequestReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

            bool secondEnumerated = false;
            IEnumerable<SndbTimeSeriesPoint> Second()
            {
                secondEnumerated = true;
                yield return Point(2);
            }

            Task<SndbTimeSeriesWriteResult> second = writer.WriteBatchAsync(Second());
            Assert.False(secondEnumerated);
            Assert.False(second.IsCompleted);

            releaseFirstRequest.TrySetResult();
            Assert.True((await first.WaitAsync(TimeSpan.FromSeconds(5))).Succeeded);
            Assert.True((await second.WaitAsync(TimeSpan.FromSeconds(5))).IsSuccess);
            Assert.True(secondEnumerated);
            await server.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            releaseFirstRequest.TrySetResult();
            listener.Stop();
            try
            {
                await server.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
            {
            }
        }
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

    [Fact]
    public async Task DisposeAsync_AdmissionWaiter_ReportsWriterDisposedWithoutEnumeratingIt()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var firstRequestReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task server = ServeTwoWritesAsync(listener, firstRequestReceived, releaseFirstRequest);

        try
        {
            using var client = new SndbTimeSeriesClient(
                $"Data Source=sonnetdb+http://127.0.0.1:{endpoint.Port}/test;Protocol=rest;Timeout=10");
            var writer = client.CreateWriter("cpu", new() { MaxPendingBatches = 2 });
            Task<SndbTimeSeriesWriteItemResult> first = writer.WriteAsync(Point(1));
            await firstRequestReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var secondEnumerated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            IEnumerable<SndbTimeSeriesPoint> Second()
            {
                secondEnumerated.TrySetResult();
                yield return Point(2);
            }

            Task<SndbTimeSeriesWriteResult> second = writer.WriteBatchAsync(Second());
            await secondEnumerated.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var thirdEnumerated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            IEnumerable<SndbTimeSeriesPoint> Third()
            {
                thirdEnumerated.TrySetResult();
                yield return Point(3);
            }

            Task<SndbTimeSeriesWriteResult> third = writer.WriteBatchAsync(Third());

            Task dispose = writer.DisposeAsync().AsTask();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => third.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(thirdEnumerated.Task.IsCompleted);

            releaseFirstRequest.TrySetResult();
            await Task.WhenAll(first, second, dispose).WaitAsync(TimeSpan.FromSeconds(5));
            await server.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            releaseFirstRequest.TrySetResult();
            listener.Stop();
            try
            {
                await server.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
            {
            }
        }
    }

    private static async Task ServeTwoWritesAsync(
        TcpListener listener,
        TaskCompletionSource firstRequestReceived,
        TaskCompletionSource releaseFirstRequest)
    {
        for (int requestIndex = 0; requestIndex < 2; requestIndex++)
        {
            using TcpClient connection = await listener.AcceptTcpClientAsync();
            await using NetworkStream stream = connection.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            await ReadHttpRequestAsync(reader);
            if (requestIndex == 0)
            {
                firstRequestReceived.TrySetResult();
                await releaseFirstRequest.Task;
            }

            byte[] body = Encoding.UTF8.GetBytes("{\"writtenRows\":1}");
            byte[] response = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(response);
            await stream.WriteAsync(body);
            await stream.FlushAsync();
        }
    }

    private static async Task ReadHttpRequestAsync(StreamReader reader)
    {
        string? requestLine = await reader.ReadLineAsync();
        if (string.IsNullOrEmpty(requestLine))
            throw new InvalidDataException("缺少 HTTP 请求行。");

        int contentLength = 0;
        while (true)
        {
            string? header = await reader.ReadLineAsync();
            if (header is null)
                throw new InvalidDataException("HTTP 请求头不完整。");
            if (header.Length == 0)
                break;
            if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                contentLength = int.Parse(header["Content-Length:".Length..].Trim(), CultureInfo.InvariantCulture);
        }

        char[] body = new char[contentLength];
        int offset = 0;
        while (offset < body.Length)
        {
            int read = await reader.ReadAsync(body.AsMemory(offset));
            if (read == 0)
                throw new InvalidDataException("HTTP 请求正文不完整。");
            offset += read;
        }
    }

    private static SndbTimeSeriesPoint Point(int timestamp)
        => SndbTimeSeriesPoint.Create("cpu").Timestamp(timestamp).Field("value", (double)timestamp).Build();

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
