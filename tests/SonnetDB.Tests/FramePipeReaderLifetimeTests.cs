using System.Buffers;
using System.IO.Pipelines;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SonnetDB.Auth;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Endpoints;
using SonnetDB.Hosting;
using SonnetDB.Protocol;
using SonnetDB.Sql.Execution;
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

    /// <summary>
    /// 验证单块 SQL 查询只由外层执行一次最终刷新，且 meta、rows、end 三帧完整同批送出。
    /// </summary>
    [Fact]
    public async Task Unary_SqlSingleChunk_FlushesCompleteResponseOnce()
    {
        using var dependencies = new HandlerDependencies();
        const string databaseName = "single-flush";
        Assert.True(dependencies.Registry.TryCreate(databaseName, out _));

        IReadOnlyList<byte[]> batches = await ExecuteSqlQueryAsync(
            dependencies,
            databaseName,
            "SELECT 1 AS value",
            streamId: 41);

        List<(FrameHeader Header, byte[] Payload)> frames = DecodeFrames(Assert.Single(batches));
        Assert.Equal(
            [SqlQueryChunkKind.Meta, SqlQueryChunkKind.Rows, SqlQueryChunkKind.End],
            frames.Select(static frame => SqlFrameCodec.PeekChunkKind(frame.Payload)).ToArray());
        Assert.Single(SqlFrameCodec.DecodeQueryRowsFrame(frames[1].Payload));
        (long rowCount, _) = SqlFrameCodec.DecodeQueryEndFrame(frames[2].Payload);
        Assert.Equal(1, rowCount);
        Assert.All(frames, static frame =>
        {
            Assert.Equal((byte)FrameService.Sql, frame.Header.Service);
            Assert.Equal(41u, frame.Header.StreamId);
            Assert.True(frame.Header.IsResponse);
            Assert.False(frame.Header.IsError);
        });
    }

    /// <summary>
    /// 验证跨两个 rows 块的 SQL 查询只在首块后中途刷新，第二块与 end 保持在最终刷新中。
    /// </summary>
    [Fact]
    public async Task Unary_SqlMultipleChunks_FlushesOnlyBeforeFollowingChunk()
    {
        using var dependencies = new HandlerDependencies();
        const string databaseName = "multiple-flushes";
        const string tableName = "frame_flush_rows";
        int rowCount = SqlFrameCodec.DefaultMaxChunkRows + 1;
        Assert.True(dependencies.Registry.TryCreate(databaseName, out var database));
        SqlExecutor.Execute(database, $"CREATE TABLE {tableName} (id INT, PRIMARY KEY (id))");

        var rows = new IReadOnlyList<object?>[rowCount];
        for (int i = 0; i < rows.Length; i++)
            rows[i] = new object?[] { (long)i };
        Assert.Equal(rowCount, database.Tables.Open(tableName).InsertMany(rows));

        IReadOnlyList<byte[]> batches = await ExecuteSqlQueryAsync(
            dependencies,
            databaseName,
            $"SELECT id FROM {tableName} ORDER BY id",
            streamId: 42);

        Assert.Equal(2, batches.Count);
        List<(FrameHeader Header, byte[] Payload)> firstBatch = DecodeFrames(batches[0]);
        List<(FrameHeader Header, byte[] Payload)> finalBatch = DecodeFrames(batches[1]);
        Assert.Equal(
            [SqlQueryChunkKind.Meta, SqlQueryChunkKind.Rows],
            firstBatch.Select(static frame => SqlFrameCodec.PeekChunkKind(frame.Payload)).ToArray());
        Assert.Equal(
            [SqlQueryChunkKind.Rows, SqlQueryChunkKind.End],
            finalBatch.Select(static frame => SqlFrameCodec.PeekChunkKind(frame.Payload)).ToArray());

        object?[][] firstRows = SqlFrameCodec.DecodeQueryRowsFrame(firstBatch[1].Payload);
        object?[][] finalRows = SqlFrameCodec.DecodeQueryRowsFrame(finalBatch[0].Payload);
        Assert.Equal(SqlFrameCodec.DefaultMaxChunkRows, firstRows.Length);
        Assert.Single(finalRows);
        Assert.Equal(0L, firstRows[0][0]);
        Assert.Equal((long)rowCount - 1, finalRows[0][0]);
        (long encodedRowCount, _) = SqlFrameCodec.DecodeQueryEndFrame(finalBatch[1].Payload);
        Assert.Equal(rowCount, encodedRowCount);
    }

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

    /// <summary>
    /// 创建直接调用帧处理器所需的最小 HTTP 上下文。
    /// </summary>
    private static DefaultHttpContext CreateContext(
        PipeReader requestReader,
        PipeWriter responseWriter,
        CancellationToken requestAborted,
        string protocol,
        IServiceProvider? requestServices = null,
        string? role = null)
    {
        var context = new DefaultHttpContext();
        context.Features.Set<IRequestBodyPipeFeature>(new RequestBodyPipeFeature(requestReader));
        context.Features.Set<IHttpResponseBodyFeature>(new ResponseBodyFeature(responseWriter));
        context.Request.ContentType = FrameContentType;
        context.Request.Protocol = protocol;
        context.RequestAborted = requestAborted;
        if (requestServices is not null)
            context.RequestServices = requestServices;
        if (role is not null)
            context.Items[BearerAuthMiddleware.RoleKey] = role;
        return context;
    }

    /// <summary>
    /// 执行一条 SQL 请求并在每次底层 flush 后立即取走该批字节，以便验证真实刷新边界。
    /// </summary>
    private static async Task<IReadOnlyList<byte[]>> ExecuteSqlQueryAsync(
        HandlerDependencies dependencies,
        string database,
        string sql,
        uint streamId)
    {
        var encodedRequest = new ArrayBufferWriter<byte>();
        SqlFrameCodec.EncodeQueryRequest(encodedRequest, streamId, database, sql);

        var requestPipe = new Pipe();
        var responsePipe = new Pipe(new PipeOptions(
            pauseWriterThreshold: 16 * 1024 * 1024,
            resumeWriterThreshold: 8 * 1024 * 1024));
        var batches = new List<byte[]>();

        // 每次刷新后立即消费完整缓冲，因此批次内容精确对应一次 FlushAsync。
        async ValueTask<FlushResult> RecordFlushAsync(CancellationToken cancellationToken)
        {
            FlushResult flush = await responsePipe.Writer.FlushAsync(cancellationToken);
            ReadResult read = await responsePipe.Reader.ReadAsync(cancellationToken);
            batches.Add(read.Buffer.ToArray());
            responsePipe.Reader.AdvanceTo(read.Buffer.End);
            return flush;
        }

        var responseWriter = new ControlledFlushPipeWriter(responsePipe.Writer, RecordFlushAsync);
        DefaultHttpContext context = CreateContext(
            requestPipe.Reader,
            responseWriter,
            CancellationToken.None,
            "HTTP/2",
            dependencies.Services,
            ServerRoles.Admin);

        await requestPipe.Writer.WriteAsync(encodedRequest.WrittenMemory);
        await requestPipe.Writer.CompleteAsync();
        try
        {
            await FrameEndpointHandler.HandleAsync(
                context,
                dependencies.Registry,
                dependencies.Grants,
                dependencies.MqStore,
                dependencies.Metrics);
        }
        finally
        {
            await requestPipe.Reader.CompleteAsync();
            await responsePipe.Writer.CompleteAsync();
            await responsePipe.Reader.CompleteAsync();
        }

        return batches;
    }

    /// <summary>
    /// 解码一个刷新批次中的全部完整帧，并拒绝残留的半帧字节。
    /// </summary>
    private static List<(FrameHeader Header, byte[] Payload)> DecodeFrames(byte[] batch)
    {
        var frames = new List<(FrameHeader, byte[])>();
        var buffer = new ReadOnlySequence<byte>(batch);
        while (FrameCodec.TryReadFrame(ref buffer, out FrameHeader header, out ReadOnlySequence<byte> payload))
            frames.Add((header, payload.ToArray()));
        Assert.Equal(0, buffer.Length);
        return frames;
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

        /// <summary>
        /// 创建帧处理器依赖，并注册 SQL 并发准入服务供直接调用测试使用。
        /// </summary>
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
            var services = new ServiceCollection();
            services.AddSingleton<IOptions<ServerOptions>>(Options.Create(new ServerOptions()));
            services.AddSingleton<SqlHttpRequestAdmission>();
            Services = services.BuildServiceProvider();
        }

        public TsdbRegistry Registry { get; }

        public GrantsStore Grants { get; }

        public SonnetMqStore MqStore { get; }

        public ServerMetrics Metrics { get; } = new();

        public ServiceProvider Services { get; }

        /// <summary>
        /// 释放服务、数据库注册表和消息存储，并尽力清理测试目录。
        /// </summary>
        public void Dispose()
        {
            Services.Dispose();
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
