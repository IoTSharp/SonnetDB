using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using SonnetDB.Data;
using SonnetDB.Data.ObjectStorage;
using SonnetDB.ObjectStorage;

namespace SonnetDB.Core.Tests.ObjectStorage;

public sealed class SndbObjectClientContractTests
{
    private const string Page = """
        {"bucket":"media","prefix":"dir/","maxKeys":2,"continuationToken":null,"nextContinuationToken":null,
         "isTruncated":false,"objects":[{"bucket":"media","key":"dir/a","versionId":"v1","contentType":"text/plain",
         "sizeBytes":1,"eTag":"tag","sha256":"hash","isDeleteMarker":false,"createdUtc":"2026-09-19T00:00:00Z",
         "updatedUtc":"2026-09-19T00:00:00Z","metadata":{},"tags":{}}],"delimiter":"/","commonPrefixes":["dir/sub/"]}
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Operations_PreCanceled_DoNotSendOrMutate(bool embedded)
    {
        string directory = Path.Combine(Path.GetTempPath(), "sndb-object-contract-" + Guid.NewGuid().ToString("N"));
        int calls = 0;
        try
        {
            using var client = embedded
                ? new SndbObjectStorageClient($"Data Source={directory};Mode=Embedded")
                : CreateClient(new Handler(_ => { calls++; throw new InvalidOperationException("Unexpected request."); }));
            await using var input = new MemoryStream([1, 2, 3]);
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            CancellationToken token = canceled.Token;
            Func<Task>[] operations =
            [
                () => client.ListBucketsAsync(token),
                () => client.CreateBucketAsync("media", cancellationToken: token),
                () => client.DeleteBucketAsync("media", token),
                () => client.GetSemanticOptionsAsync("media", token),
                () => client.SetSemanticOptionsAsync("media", true, true, cancellationToken: token),
                () => client.PutObjectAsync("media", "a", input, cancellationToken: token),
                () => client.ListObjectsAsync("media", cancellationToken: token),
                () => client.ListObjectVersionsAsync("media", cancellationToken: token),
                () => client.HeadObjectAsync("media", "a", token),
                () => client.OpenReadAsync("media", "a", cancellationToken: token),
                () => client.OpenThumbnailAsync("media", "a", token),
                () => client.CopyObjectAsync("media", "a", "media", "b", token),
                () => client.DeleteObjectAsync("media", "a", token),
                () => client.DeleteObjectsAsync("media", ["a"], token),
                () => client.SetObjectTagsAsync("media", "a", new Dictionary<string, string>(), token),
                () => client.GetPolicyAsync("media", token),
                () => client.SetPolicyAsync("media", null, token),
                () => client.GetLifecycleAsync("media", token),
                () => client.SetLifecycleAsync("media", null, null, null, token),
                () => client.ApplyLifecycleAsync("media", token),
                () => client.GetRetentionAsync("media", token),
                () => client.SetRetentionAsync("media", null, null, token),
                () => client.GetQuotaAsync("media", token),
                () => client.SetQuotaAsync("media", null, null, token),
                () => client.GetStatsAsync("media", token),
                () => client.ListAuditAsync("media", cancellationToken: token),
                () => client.GetLegalHoldAsync("media", "a", cancellationToken: token),
                () => client.SetLegalHoldAsync("media", "a", true, cancellationToken: token),
                () => client.InitiateMultipartUploadAsync("media", "a", cancellationToken: token),
                () => client.UploadPartAsync("media", "a", "upload", 1, input, token),
                () => client.CompleteMultipartUploadAsync("media", "a", "upload", [1], token),
                () => client.AbortMultipartUploadAsync("media", "a", "upload", token),
                () => client.CreatePresignedUrlAsync("media", "a", "GET", 1, token),
            ];
            foreach (var operation in operations)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(operation);
            Assert.Equal(0, calls);
            Assert.Equal(0, input.Position);
            if (embedded)
                Assert.Empty(await client.ListBucketsAsync());
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task ListBucketsAsync_BodyCanceled_PropagatesCancellationAndDisposes(HttpStatusCode status)
    {
        var stream = new BlockingStream();
        using var response = Response(status, new StreamOnlyContent(stream));
        using var client = CreateClient(new Handler(_ => Task.FromResult<HttpResponseMessage>(response)));
        using var cancellation = new CancellationTokenSource();
        Task operation = client.ListBucketsAsync(cancellation.Token);
        await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.True(response.WasDisposed);
        Assert.True(stream.WasDisposed);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task CreateBucketAsync_BodyExceedsConnectionTimeout_CancelsAndDisposes(HttpStatusCode status)
    {
        var stream = new BlockingStream();
        using var response = Response(status, new StreamOnlyContent(stream));
        using var client = CreateClient(new Handler(_ => Task.FromResult<HttpResponseMessage>(response)), timeout: 1);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.CreateBucketAsync("media").WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(response.WasDisposed);
        Assert.True(stream.WasDisposed);
    }

    [Fact]
    public async Task CreateBucketAsync_ErrorBodyIoFailure_PropagatesAndDisposes()
    {
        var failure = new IOException("broken response");
        using var response = Response(HttpStatusCode.BadRequest, new StreamOnlyContent(new FailingReadStream(failure)));
        using var client = CreateClient(new Handler(_ => Task.FromResult<HttpResponseMessage>(response)));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => client.CreateBucketAsync("media")));
        Assert.True(response.WasDisposed);
    }

    [Theory]
    [InlineData("{\"error\":\"object_conflict\",\"message\":\"Conflict\"}", "object_conflict")]
    [InlineData("<html>sensitive proxy details</html>", "http_error")]
    public async Task CreateBucketAsync_ErrorBody_UsesStableCodeWithoutRawBody(string body, string expectedCode)
    {
        using var response = Response(HttpStatusCode.Conflict, new StreamOnlyContent(new MemoryStream(Encoding.UTF8.GetBytes(body))));
        using var client = CreateClient(new Handler(_ => Task.FromResult<HttpResponseMessage>(response)));
        var error = await Assert.ThrowsAsync<SndbServerException>(() => client.CreateBucketAsync("media"));
        Assert.Equal(expectedCode, error.Error);
        Assert.DoesNotContain("sensitive proxy", error.Message, StringComparison.Ordinal);
        Assert.True(response.WasDisposed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task ListObjectsAsync_ValidPage_StreamsAndPreservesRequestProtocol(string? echoedToken)
    {
        var body = JsonNode.Parse(Page)!;
        body["continuationToken"] = echoedToken;
        using var response = Response(HttpStatusCode.OK, new StreamOnlyContent(new MemoryStream(Encoding.UTF8.GetBytes(body.ToJsonString()))));
        using var client = CreateClient(new Handler(request =>
        {
            Assert.Equal(HttpVersion.Version20, request.Version);
            Assert.Equal(HttpVersionPolicy.RequestVersionExact, request.VersionPolicy);
            Assert.Contains("prefix=dir%2F", request.RequestUri!.Query, StringComparison.Ordinal);
            return Task.FromResult<HttpResponseMessage>(response);
        }), protocol: "frame-http2");
        var page = await client.ListObjectsAsync("media", "/dir/", 2, null, "/", CancellationToken.None);
        Assert.Equal("dir/a", Assert.Single(page.Objects).Key);
        Assert.Equal("dir/sub/", Assert.Single(page.CommonPrefixes));
        Assert.True(response.WasDisposed);
    }

    [Theory]
    [InlineData("initiate", "{\"bucket\":\"other\",\"key\":\"a\",\"uploadId\":\"u\"}")]
    [InlineData("initiate", "{\"bucket\":\"media\",\"key\":\"a\",\"uploadId\":\"\"}")]
    [InlineData("part", "{\"partNumber\":2,\"sizeBytes\":1,\"eTag\":\"e\",\"sha256\":\"s\"}")]
    [InlineData("complete", "{\"bucket\":\"media\",\"key\":\"other\"}")]
    public async Task MultipartAsync_MismatchedResponse_RejectsWithoutRetry(string operation, string body)
    {
        int calls = 0;
        using var client = CreateClient(new Handler(_ =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }));
        await using var input = new MemoryStream([1]);
        Func<Task> action = operation switch
        {
            "initiate" => () => client.InitiateMultipartUploadAsync("media", "a"),
            "part" => () => client.UploadPartAsync("media", "a", "u", 1, input),
            _ => () => client.CompleteMultipartUploadAsync("media", "a", "u", [1]),
        };
        await Assert.ThrowsAsync<InvalidDataException>(action);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("bucket")]
    [InlineData("prefix")]
    [InlineData("delimiter")]
    [InlineData("limit")]
    [InlineData("overfull")]
    [InlineData("missing-token")]
    [InlineData("unexpected-token")]
    [InlineData("repeated-token")]
    [InlineData("request-token")]
    [InlineData("item-target")]
    [InlineData("item-prefix")]
    [InlineData("duplicate-key")]
    [InlineData("null-items")]
    [InlineData("null-prefixes")]
    [InlineData("bad-common-prefix")]
    public async Task ListObjectsAsync_InvalidPage_RejectsResponse(string problem)
    {
        var body = JsonNode.Parse(Page)!;
        string? token = null;
        switch (problem)
        {
            case "bucket": body["bucket"] = "other"; break;
            case "prefix": body["prefix"] = "other/"; break;
            case "delimiter": body["delimiter"] = null; break;
            case "limit": body["maxKeys"] = 3; break;
            case "overfull": body["commonPrefixes"]!.AsArray().Add("dir/zzz/"); break;
            case "missing-token": body["isTruncated"] = true; break;
            case "unexpected-token": body["nextContinuationToken"] = "next"; break;
            case "repeated-token":
                token = "same"; body["continuationToken"] = token;
                body["isTruncated"] = true; body["nextContinuationToken"] = token; break;
            case "request-token": token = "expected"; break;
            case "item-target": body["objects"]![0]!["bucket"] = "other"; break;
            case "item-prefix": body["objects"]![0]!["key"] = "other/a"; break;
            case "duplicate-key":
                body["commonPrefixes"] = new JsonArray();
                body["objects"]!.AsArray().Add(body["objects"]![0]!.DeepClone()); break;
            case "null-items": body["objects"] = null; break;
            case "null-prefixes": body["commonPrefixes"] = null; break;
            case "bad-common-prefix": body["commonPrefixes"]![0] = "dir/sub/deeper/"; break;
        }
        using var response = Response(HttpStatusCode.OK, new StringContent(body.ToJsonString()));
        using var client = CreateClient(new Handler(_ => Task.FromResult<HttpResponseMessage>(response)));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.ListObjectsAsync("media", "dir/", 2, token, "/", CancellationToken.None));
        Assert.True(response.WasDisposed);
    }

    [Fact]
    public async Task DeleteObjectsAsync_MutableInput_PreservesOriginalKeysAndPartialErrors()
    {
        string[] keys = ["a", "b"];
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = CreateClient(new Handler(async request =>
        {
            sent.SetResult();
            await release.Task;
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
            Assert.Equal("a", body["keys"]![0]!.GetValue<string>());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"bucket":"media","deleted":[{"key":"a","versionId":"v1","deleteMarker":true},
                    {"key":"b","versionId":"","deleteMarker":false,"errorCode":"object_retained","errorMessage":"Retained"}]}
                    """),
            };
        }));
        Task<SndbObjectDeleteManyResult> operation = client.DeleteObjectsAsync("media", keys);
        await sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        keys[0] = "changed";
        release.SetResult();
        var result = await operation;
        Assert.Equal("a", result.Deleted[0].Key);
        Assert.Equal("object_retained", result.Deleted[1].ErrorCode);
    }

    [Theory]
    [InlineData("{\"bucket\":\"other\",\"deleted\":[]}")]
    [InlineData("{\"bucket\":\"media\",\"deleted\":[]}")]
    [InlineData("{\"bucket\":\"media\",\"deleted\":[{\"key\":\"wrong\",\"versionId\":\"v1\",\"deleteMarker\":true}]}")]
    [InlineData("{\"bucket\":\"media\",\"deleted\":[{\"key\":\"a\",\"versionId\":\"\",\"deleteMarker\":false}]}")]
    public async Task DeleteObjectsAsync_InvalidBatchResponse_RejectsWithoutRetry(string body)
    {
        int calls = 0;
        using var client = CreateClient(new Handler(_ =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.DeleteObjectsAsync("media", ["a"]));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task PutObjectAsync_FrameFailure_DoesNotReplayAsRest()
    {
        int calls = 0;
        using var client = CreateClient(new Handler(request =>
        {
            calls++;
            Assert.Equal("/v1/frame", request.RequestUri!.AbsolutePath);
            throw new HttpRequestException("Connection lost after commit.");
        }), protocol: "auto");
        await using var input = new MemoryStream([1, 2, 3]);
        var error = await Assert.ThrowsAsync<SndbServerException>(() => client.PutObjectAsync("media", "a", input));
        Assert.Equal("frame_transport_error", error.Error);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("upload", false)]
    [InlineData("upload", true)]
    [InlineData("complete", false)]
    [InlineData("complete", true)]
    [InlineData("abort", false)]
    [InlineData("abort", true)]
    public async Task MultipartAsync_WrongEmbeddedTarget_RejectsBeforeMutation(string operation, bool wrongBucket)
    {
        string directory = Path.Combine(Path.GetTempPath(), "sndb-multipart-contract-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var client = new SndbObjectStorageClient($"Data Source={directory};Mode=Embedded");
            await client.CreateBucketAsync("media");
            var upload = await client.InitiateMultipartUploadAsync("media", "a");
            await using var original = new MemoryStream([1, 2, 3]);
            await client.UploadPartAsync("media", "a", upload.UploadId, 1, original);
            await using var replacement = new MemoryStream([9]);
            string bucket = wrongBucket ? "other" : "media";
            string key = wrongBucket ? "a" : "other";
            Func<Task> rejected = operation switch
            {
                "upload" => () => client.UploadPartAsync(bucket, key, upload.UploadId, 1, replacement),
                "complete" => () => client.CompleteMultipartUploadAsync(bucket, key, upload.UploadId, [1]),
                _ => () => client.AbortMultipartUploadAsync(bucket, key, upload.UploadId),
            };
            var error = await Assert.ThrowsAsync<SndbObjectStorageException>(rejected);
            Assert.Equal("multipart_not_found", error.Code);
            Assert.Equal(0, replacement.Position);
            await client.CompleteMultipartUploadAsync("media", "a", upload.UploadId, [1]);
            var read = (await client.OpenReadAsync("media", "a"))!;
            await using var content = read.Content;
            using var actual = new MemoryStream();
            await content.CopyToAsync(actual);
            Assert.Equal(new byte[] { 1, 2, 3 }, actual.ToArray());
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static SndbObjectStorageClient CreateClient(HttpMessageHandler handler, int timeout = 30, string protocol = "rest")
        => new($"Data Source=sonnetdb+http://object-contract.test/db;Protocol={protocol};Timeout={timeout}", new HttpClient(handler));

    private static TrackedResponse Response(HttpStatusCode status, HttpContent content) => new(status) { Content = content };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(request);
    }

    private sealed class TrackedResponse(HttpStatusCode status) : HttpResponseMessage(status)
    {
        public bool WasDisposed { get; private set; }
        protected override void Dispose(bool disposing)
        {
            WasDisposed |= disposing;
            base.Dispose(disposing);
        }
    }

    private sealed class StreamOnlyContent(Stream source) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => throw new InvalidOperationException("Response content must not be pre-buffered.");
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(source);
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => Task.FromResult(source);
    }

    private sealed class BlockingStream : MemoryStream
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool WasDisposed { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        protected override void Dispose(bool disposing) { WasDisposed |= disposing; base.Dispose(disposing); }
    }

    private sealed class FailingReadStream(IOException failure) : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(failure);
    }
}
