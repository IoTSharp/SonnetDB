using System.Net;
using System.Text;
using SonnetDB.Data.Graphs;
using SonnetDB.Graphs;
using Xunit;

namespace SonnetDB.Core.Tests.Graphs;

public sealed class GraphOperationsClientTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sonnetdb-graph-operations-client-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task EmbeddedOperations_ExposeSharedContractsAndPersistentApprovalAudit()
    {
        string connection = $"Data Source={_root};Mode=Embedded";
        using (var client = new SndbGraphClient(connection))
        {
            await client.CreateGraphAsync("plant");
            await client.UpsertVertexAsync(
                "plant",
                new GraphUpsertVertexRequest { Id = 1, RequestId = Guid.NewGuid(), Labels = [7] });

            GraphOperationsOverviewDto overview = await client.GetOperationsOverviewAsync("plant");
            Assert.Equal(1, overview.VertexCount);
            Assert.False(overview.Capabilities.SlowTraversalDiagnostics);
            Assert.Equal("not_available_embedded", overview.SlowTraversalSource);

            GraphVisualizationDto visualization = await client.GetVisualizationAsync("plant", limit: 10);
            Assert.False(visualization.Truncated);
            Assert.Equal(1, Assert.Single(visualization.Vertices).Id);

            GraphMaintenanceApprovalDto staged = await client.StageMaintenanceAsync(
                "plant",
                new GraphMaintenanceStageRequest { Action = GraphMaintenanceAction.Checkpoint });
            Assert.Equal("staged", staged.State);
            GraphMaintenanceApprovalDto completed = await client.ApproveMaintenanceAsync("plant", staged.ApprovalId);
            Assert.Equal("completed", completed.State);
            Assert.Contains(
                await client.ListMaintenanceAuditAsync("plant"),
                entry => entry.ApprovalId == staged.ApprovalId && entry.State == "completed");
        }

        using var reopened = new SndbGraphClient(connection);
        Assert.Contains(
            await reopened.ListMaintenanceAuditAsync("plant"),
            entry => entry.State == "completed");
        Assert.Equal(1, (await reopened.GetOperationsOverviewAsync("plant")).VertexCount);
    }

    [Fact]
    public async Task GraphClient_CancelledOperationsStopBeforeOpeningSnapshot()
    {
        using var client = new SndbGraphClient($"Data Source={_root}-cancel");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => client.ListGraphsAsync(cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (GraphExpansion _ in client.ExpandAsync(
                "missing",
                new GraphExpandRequest { VertexId = 1 },
                cancellation.Token))
            {
            }
        });
    }

    [Fact]
    public async Task GraphClient_PageAdaptersKeepResultsBoundedAndPreserveOrder()
    {
        using var client = new SndbGraphClient($"Data Source={_root}-pages");
        await client.CreateGraphAsync("plant");
        for (long id = 1; id <= 4; id++)
        {
            await client.UpsertVertexAsync(
                "plant",
                new GraphUpsertVertexRequest { Id = id, RequestId = Guid.NewGuid(), Labels = [7] });
        }
        for (long id = 1; id <= 3; id++)
        {
            await client.UpsertEdgeAsync(
                "plant",
                new GraphUpsertEdgeRequest
                {
                    Id = 100 + id,
                    RequestId = Guid.NewGuid(),
                    SourceId = 1,
                    TargetId = id + 1,
                    LabelId = 8,
                });
        }

        var pages = new List<IReadOnlyList<GraphExpansion>>();
        await foreach (IReadOnlyList<GraphExpansion> page in client.ExpandPagesAsync(
            "plant",
            new GraphExpandRequest { VertexId = 1, PageSize = 2 }))
        {
            pages.Add(page);
        }

        Assert.Equal(2, pages.Count);
        Assert.All(pages, page => Assert.InRange(page.Count, 1, 2));
        Assert.Equal([2L, 3L, 4L], pages.SelectMany(static page => page).Select(static item => item.NeighborId.Value).ToArray());
    }

    [Fact]
    public async Task GraphClient_ImportValidationReportsAllItemErrorsWithRequestId()
    {
        using var client = new SndbGraphClient($"Data Source={_root}-batch-errors");
        await client.CreateGraphAsync("plant");
        Guid requestId = Guid.NewGuid();

        SndbGraphBatchException exception = await Assert.ThrowsAsync<SndbGraphBatchException>(
            () => client.ImportAsync(
                "plant",
                new GraphImportRequest
                {
                    RequestId = requestId,
                    Vertices =
                    [
                        new GraphImportVertexDto { Id = 0 },
                        new GraphImportVertexDto { Id = 1 },
                        new GraphImportVertexDto { Id = 1 },
                    ],
                    Edges =
                    [
                        new GraphImportEdgeDto { Id = 10, SourceId = 0, TargetId = 1, LabelId = 2 },
                    ],
                }));

        Assert.Equal(requestId, exception.RequestId);
        Assert.True(exception.Errors.Count >= 3);
        Assert.Contains(exception.Errors, error => error.Code == "duplicate_id" && error.ElementId == 1);
        Assert.Contains(exception.Errors, error => error.Code == "invalid_item" && error.ElementKind == "edge");
    }

    [Fact]
    public async Task GraphClient_HeadersReceivedButBodyStalls_ConnectionDeadlineCancelsAndDisposes()
    {
        var body = new CancellationOnlyStream();
        using var http = new HttpClient(new ResponseHandler(HttpStatusCode.OK, body));
        using var client = new SndbGraphClient("Data Source=sonnetdb+http://localhost/graph-test;Protocol=rest;Timeout=1", http);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await using var rows = client.SeekVerticesAsync("plant", new GraphSeekRequest { LabelId = 7 }).GetAsyncEnumerator();
            await rows.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        });

        Assert.True(body.ReadStarted.Task.IsCompletedSuccessfully);
        Assert.True(body.ReadToken.IsCancellationRequested);
        Assert.True(body.Disposed);
    }

    [Fact]
    public async Task GraphClient_StreamingBodyCallerCancellation_CancelsAndDisposes()
    {
        var body = new CancellationOnlyStream();
        using var http = new HttpClient(new ResponseHandler(HttpStatusCode.OK, body));
        using var client = new SndbGraphClient("Data Source=sonnetdb+http://localhost/graph-test;Protocol=rest;Timeout=30", http);
        using var cancellation = new CancellationTokenSource();

        await using (var rows = client.SeekVerticesAsync("plant", new GraphSeekRequest { LabelId = 7 }, cancellation.Token).GetAsyncEnumerator())
        {
            Task<bool> next = rows.MoveNextAsync().AsTask();
            await body.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        Assert.True(body.ReadToken.IsCancellationRequested);
        Assert.True(body.Disposed);
    }

    [Fact]
    public async Task GraphClient_StreamingHttpFailure_DisposesResponseBeforeReturningError()
    {
        var body = new TrackingMemoryStream("{\"error\":\"forbidden\",\"message\":\"denied\"}");
        using var http = new HttpClient(new ResponseHandler(HttpStatusCode.Forbidden, body));
        using var client = new SndbGraphClient("Data Source=sonnetdb+http://localhost/graph-test;Protocol=rest;Timeout=5", http);

        Exception? error = await Record.ExceptionAsync(async () =>
        {
            await using var rows = client.SeekVerticesAsync("plant", new GraphSeekRequest { LabelId = 7 }).GetAsyncEnumerator();
            await rows.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        });

        Assert.NotNull(error);
        Assert.Equal("SndbServerException", error.GetType().Name);
        Assert.True(body.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GraphClient_StreamingCompletionOrEarlyExit_DisposesResponse(bool stopEarly)
    {
        var body = new TrackingMemoryStream("{\"id\":1,\"elementVersion\":1,\"labels\":[],\"properties\":[]}\n{\"id\":2,\"elementVersion\":1,\"labels\":[],\"properties\":[]}\n");
        using var http = new HttpClient(new ResponseHandler(HttpStatusCode.OK, body));
        using var client = new SndbGraphClient("Data Source=sonnetdb+http://localhost/graph-test;Protocol=rest;Timeout=5", http);
        int count = 0;
        await foreach (GraphVertex _ in client.SeekVerticesAsync("plant", new GraphSeekRequest { LabelId = 7 }))
        {
            count++;
            if (stopEarly) break;
        }

        Assert.Equal(stopEarly ? 1 : 2, count);
        Assert.True(body.Disposed);
    }

    [Fact]
    public async Task GraphClient_FilteredExpand_StreamingRequestUsesConfiguredHttp2Version()
    {
        const string responseBody = "{\"anchorId\":1,\"neighborId\":2,\"direction\":1,\"edge\":{\"id\":10,\"elementVersion\":1,\"sourceId\":1,\"targetId\":2,\"labelId\":8,\"properties\":[]}}\n";
        var handler = new VersionRecordingHandler(responseBody);
        using var http = new HttpClient(handler);
        using var client = new SndbGraphClient(
            "Data Source=sonnetdb+http://localhost/graph-test;Protocol=frame-http2;Timeout=5",
            http);

        var expansions = new List<GraphExpansion>();
        await foreach (GraphExpansion expansion in client.ExpandAsync(
            "plant",
            new GraphExpandRequest { VertexId = 1, TargetLabelId = 7 }))
        {
            expansions.Add(expansion);
        }

        GraphExpansion result = Assert.Single(expansions);
        Assert.Equal(2, result.NeighborId.Value);
        Assert.Equal(HttpVersion.Version20, handler.RequestVersion);
        Assert.Equal(HttpVersionPolicy.RequestVersionExact, handler.RequestVersionPolicy);
    }

    private sealed class ResponseHandler(HttpStatusCode status, Stream body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StreamContent(body) });
        }
    }

    private sealed class VersionRecordingHandler(string responseBody) : HttpMessageHandler
    {
        internal Version? RequestVersion { get; private set; }
        internal HttpVersionPolicy RequestVersionPolicy { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestVersion = request.Version;
            RequestVersionPolicy = request.VersionPolicy;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/x-ndjson"),
            });
        }
    }

    private sealed class TrackingMemoryStream(string content) : MemoryStream(Encoding.UTF8.GetBytes(content))
    {
        internal bool Disposed { get; private set; }
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class CancellationOnlyStream : Stream
    {
        internal TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken ReadToken { get; private set; }
        internal bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadToken = cancellationToken;
            ReadStarted.TrySetResult();
            var cancelled = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            using CancellationTokenRegistration registration = cancellationToken.Register(() => cancelled.TrySetCanceled(cancellationToken));
            return await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    public void Dispose()
    {
        string temporaryDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()))
            + Path.DirectorySeparatorChar;
        string[] ownedDirectories = [_root, _root + "-cancel", _root + "-pages", _root + "-batch-errors"];
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        foreach (string ownedDirectory in ownedDirectories)
        {
            deadline.Token.ThrowIfCancellationRequested();
            string fullPath = Path.GetFullPath(ownedDirectory);
            if (!fullPath.StartsWith(temporaryDirectory, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetDirectoryName(fullPath), Path.TrimEndingDirectorySeparator(temporaryDirectory), StringComparison.OrdinalIgnoreCase)
                || !string.Equals(fullPath, ownedDirectory, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Graph 测试目录不在当前 fixture 的临时目录边界内。");
            if (Directory.Exists(fullPath)) Directory.Delete(fullPath, recursive: true);
        }
    }
}
