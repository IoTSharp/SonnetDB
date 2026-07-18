using System.Buffers;
using System.IO.Pipelines;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using SonnetDB.Auth;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Endpoints;
using SonnetDB.Hosting;
using SonnetDB.Protocol;
using SonnetMQ;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>
/// Frame endpoints must release every acquired request-body read before cancellation unwinds.
/// A second read on an unadvanced <see cref="PipeReader"/> throws "Reading is already in progress".
/// </summary>
public sealed class FramePipeReaderLifetimeTests
{
    private const string FrameContentType = "application/x-sonnetdb-frame";

    [Fact]
    public async Task Unary_CancelDuringResponseFlush_ReleasesRequestRead()
    {
        using var dependencies = new HandlerDependencies();
        using var requestAborted = new CancellationTokenSource();
        var requestPipe = new Pipe();
        var responsePipe = new Pipe();
        var responseWriter = new ControlledFlushPipeWriter(
            responsePipe.Writer,
            cancellationToken =>
            {
                requestAborted.Cancel();
                return new ValueTask<FlushResult>(Task.FromCanceled<FlushResult>(cancellationToken));
            });
        var context = CreateContext(requestPipe.Reader, responseWriter, requestAborted.Token, "HTTP/1.1");

        await requestPipe.Writer.WriteAsync(EncodeMissingDatabasePullFrames(1));

        await FrameEndpointHandler.HandleAsync(
            context,
            dependencies.Registry,
            dependencies.Grants,
            dependencies.MqStore,
            dependencies.Metrics);

        await requestPipe.Writer.CompleteAsync();
        await AssertReaderReleasedAsync(requestPipe.Reader, expectBufferedData: false);
        await responsePipe.Writer.CompleteAsync();
    }

    [Fact]
    public async Task Stream_CancelWhileOutboundBackpressured_ReleasesRequestRead()
    {
        using var dependencies = new HandlerDependencies();
        using var requestAborted = new CancellationTokenSource();
        var requestPipe = new Pipe();
        var responsePipe = new Pipe();
        var flushStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var responseWriter = new ControlledFlushPipeWriter(
            responsePipe.Writer,
            cancellationToken =>
            {
                flushStarted.TrySetResult();
                return new ValueTask<FlushResult>(WaitForCancellationAsync(cancellationToken));
            });
        var context = CreateContext(requestPipe.Reader, responseWriter, requestAborted.Token, "HTTP/2");

        // The response writer consumes one frame and blocks in FlushAsync. The remaining frames fill
        // the bounded outbound channel, leaving RunReaderAsync canceled inside WriteAsync.
        await requestPipe.Writer.WriteAsync(EncodeMissingDatabasePullFrames(32));
        Task handlerTask = FrameStreamEndpointHandler.HandleAsync(
            context,
            dependencies.Registry,
            dependencies.Grants,
            dependencies.MqStore);

        await flushStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(50);
        Assert.False(handlerTask.IsCompleted);
        requestAborted.Cancel();
        await handlerTask.WaitAsync(TimeSpan.FromSeconds(5));

        await requestPipe.Writer.CompleteAsync();
        await AssertReaderReleasedAsync(requestPipe.Reader, expectBufferedData: true);
        await responsePipe.Writer.CompleteAsync();
    }

    [Fact]
    public async Task Stream_WriterIOException_CancelsReaderAndSubscriptionPump()
    {
        using var dependencies = new HandlerDependencies();
        Assert.True(dependencies.Registry.TryCreate("writer-fault", out _));

        using var requestAborted = new CancellationTokenSource();
        var requestPipe = new Pipe();
        var responsePipe = new Pipe();
        var flushStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var responseWriter = new ControlledFlushPipeWriter(
            responsePipe.Writer,
            async _ =>
            {
                flushStarted.TrySetResult();
                // Let the reader register the subscription pump and reach channel backpressure
                // before the independent writer task fails.
                await Task.Delay(50);
                throw new IOException("injected response flush failure");
            });
        var context = CreateContext(requestPipe.Reader, responseWriter, requestAborted.Token, "HTTP/2");
        context.Items[BearerAuthMiddleware.RoleKey] = ServerRoles.Admin;

        await requestPipe.Writer.WriteAsync(EncodeSubscribeThenMissingDatabasePullFrames(32));
        Task handlerTask = FrameStreamEndpointHandler.HandleAsync(
            context,
            dependencies.Registry,
            dependencies.Grants,
            dependencies.MqStore);

        await flushStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        IOException error = await Assert.ThrowsAsync<IOException>(
            () => handlerTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("injected response flush failure", error.Message);

        // A successful subscribe response proves that a real subscription (and its pump) was
        // created before the writer failed. HandleAsync only returns after that pump has exited.
        await responsePipe.Writer.CompleteAsync();
        ReadResult response = await responsePipe.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        ReadOnlySequence<byte> responseBuffer = response.Buffer;
        Assert.True(FrameCodec.TryReadFrame(ref responseBuffer, out FrameHeader header, out _));
        Assert.Equal((byte)MqFrameOp.Subscribe, header.Op);
        Assert.True(header.IsResponse);
        responsePipe.Reader.AdvanceTo(response.Buffer.End);
        await responsePipe.Reader.CompleteAsync();

        await requestPipe.Writer.CompleteAsync();
        await AssertReaderReleasedAsync(requestPipe.Reader, expectBufferedData: true);
    }

    private static DefaultHttpContext CreateContext(
        PipeReader requestReader,
        PipeWriter responseWriter,
        CancellationToken requestAborted,
        string protocol)
    {
        var context = new DefaultHttpContext();
        context.Features.Set<IRequestBodyPipeFeature>(new RequestBodyPipeFeature(requestReader));
        context.Features.Set<IHttpResponseBodyFeature>(new ResponseBodyFeature(responseWriter));
        context.Request.ContentType = FrameContentType;
        context.Request.Protocol = protocol;
        context.RequestAborted = requestAborted;
        return context;
    }

    private static byte[] EncodeMissingDatabasePullFrames(int count)
    {
        var writer = new ArrayBufferWriter<byte>();
        for (int i = 0; i < count; i++)
        {
            MqFrameCodec.EncodePullRequest(
                writer,
                checked((uint)(i + 1)),
                "missing-db",
                "topic",
                "consumer",
                maxCount: 1);
        }

        return writer.WrittenMemory.ToArray();
    }

    private static byte[] EncodeSubscribeThenMissingDatabasePullFrames(int pullCount)
    {
        var writer = new ArrayBufferWriter<byte>();
        MqFrameCodec.EncodeSubscribeRequest(
            writer,
            streamId: 1,
            db: "writer-fault",
            topic: "topic",
            consumerGroup: string.Empty,
            startMode: MqSubscribeStartMode.Latest,
            startOffset: 0,
            batchMax: 1);

        for (int i = 0; i < pullCount; i++)
        {
            MqFrameCodec.EncodePullRequest(
                writer,
                checked((uint)(i + 2)),
                "missing-db",
                "topic",
                "consumer",
                maxCount: 1);
        }

        return writer.WrittenMemory.ToArray();
    }

    private static async Task AssertReaderReleasedAsync(PipeReader reader, bool expectBufferedData)
    {
        ReadResult next = await reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        if (expectBufferedData)
            Assert.True(next.Buffer.Length > 0);
        else
            Assert.Equal(0, next.Buffer.Length);
        Assert.True(next.IsCompleted);
        reader.AdvanceTo(next.Buffer.End);
        await reader.CompleteAsync();
    }

    private static async Task<FlushResult> WaitForCancellationAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return default;
    }

    private sealed class RequestBodyPipeFeature(PipeReader reader) : IRequestBodyPipeFeature
    {
        public PipeReader Reader { get; } = reader;
    }

    private sealed class ResponseBodyFeature(PipeWriter writer) : IHttpResponseBodyFeature
    {
        public Stream Stream { get; } = Stream.Null;

        public PipeWriter Writer { get; } = writer;

        public void DisableBuffering()
        {
        }

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SendFileAsync(
            string path,
            long offset,
            long? count,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task CompleteAsync() => Task.CompletedTask;
    }

    private sealed class ControlledFlushPipeWriter(
        PipeWriter inner,
        Func<CancellationToken, ValueTask<FlushResult>> flushAsync) : PipeWriter
    {
        public override void Advance(int bytes) => inner.Advance(bytes);

        public override void CancelPendingFlush() => inner.CancelPendingFlush();

        public override void Complete(Exception? exception = null) => inner.Complete(exception);

        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
            => flushAsync(cancellationToken);

        public override Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint);

        public override Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint);
    }

    private sealed class HandlerDependencies : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "sonnetdb-frame-reader-lifetime-" + Guid.NewGuid().ToString("N"));

        public HandlerDependencies()
        {
            Directory.CreateDirectory(_root);
            Registry = new TsdbRegistry(Path.Combine(_root, "databases"));
            Grants = new GrantsStore(Path.Combine(_root, "system"));
            MqStore = SonnetMqStore.Open(new SonnetMqOptions
            {
                Path = Path.Combine(_root, "mq"),
                RetentionInterval = TimeSpan.Zero,
            });
        }

        public TsdbRegistry Registry { get; }

        public GrantsStore Grants { get; }

        public SonnetMqStore MqStore { get; }

        public ServerMetrics Metrics { get; } = new();

        public void Dispose()
        {
            MqStore.Dispose();
            Registry.Dispose();
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup for test files.
            }
        }
    }
}
