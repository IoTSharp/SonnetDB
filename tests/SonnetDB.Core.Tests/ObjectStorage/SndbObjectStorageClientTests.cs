using System.Net;
using System.Net.Http.Headers;
using System.Text;
using SonnetDB.Data.ObjectStorage;
using SonnetDB.ObjectStorage;

namespace SonnetDB.Core.Tests.ObjectStorage;

public sealed class SndbObjectStorageClientTests
{
    private const string ConnectionString =
        "Data Source=sonnetdb+http://object-client.test/testdb;Protocol=rest;Timeout=30";

    /// <summary>
    /// 验证 206 未声明 Content-Length 时使用 Content-Range 推导分段长度。
    /// </summary>
    [Fact]
    public async Task OpenReadAsync_RangeWithoutContentLength_DerivesLengthFromContentRange()
    {
        byte[] expected = [5, 6, 7, 8];
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = new StreamingContent(expected),
        };
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(5, 8, 20);
        using var client = CreateClient(new StubHandler(_ => response));

        var result = Assert.IsType<SndbObjectReadResult>(
            await client.OpenReadAsync("media", "video.bin", new SndbObjectRange(5, 4)));
        await using var content = result.Content;
        using var actual = new MemoryStream();
        await content.CopyToAsync(actual);

        Assert.Null(response.Content.Headers.ContentLength);
        Assert.Equal(5, result.Offset);
        Assert.Equal(4, result.Length);
        Assert.Equal(20, result.TotalLength);
        Assert.Equal(expected, actual.ToArray());
    }

    /// <summary>
    /// 验证服务端忽略 Range 并返回 200 时按完整响应处理，不沿用请求偏移或异常 Content-Range。
    /// </summary>
    [Fact]
    public async Task OpenReadAsync_RangeIgnoredWithOkResponse_UsesFullResponseSemantics()
    {
        byte[] expected = Enumerable.Range(0, 20).Select(static value => (byte)value).ToArray();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(expected),
        };
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(5, 8, 20);
        using var client = CreateClient(new StubHandler(_ => response));

        var result = Assert.IsType<SndbObjectReadResult>(
            await client.OpenReadAsync("media", "video.bin", new SndbObjectRange(5, 4)));
        await using var content = result.Content;
        using var actual = new MemoryStream();
        await content.CopyToAsync(actual);

        Assert.False(result.IsRange);
        Assert.Equal(0, result.Offset);
        Assert.Equal(expected.LongLength, result.Length);
        Assert.Equal(expected.LongLength, result.TotalLength);
        Assert.Equal(expected, actual.ToArray());
    }

    /// <summary>验证远程客户端不会把 suffix Range 错编码为从零开始的普通范围。</summary>
    [Fact]
    public async Task OpenReadAsync_SuffixRange_UsesSuffixHeader()
    {
        HttpRequestMessage? captured = null;
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent([7, 8, 9, 10])
        };
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(6, 9, 10);
        using var client = CreateClient(new StubHandler(request =>
        {
            captured = request;
            return response;
        }));

        var result = Assert.IsType<SndbObjectReadResult>(
            await client.OpenReadAsync("media", "video.bin", SndbObjectRange.FromSuffix(4)));
        await using (result.Content)
        {
            Assert.Equal("bytes=-4", captured?.Headers.Range?.ToString());
            Assert.Equal(6, result.Offset);
            Assert.Equal(4, result.Length);
            Assert.Equal(10, result.TotalLength);
        }
    }

    /// <summary>
    /// 验证创建响应内容流失败时及时释放 HTTP 响应。
    /// </summary>
    [Fact]
    public async Task OpenReadAsync_ReadStreamCreationFailure_DisposesResponse()
    {
        var expected = new IOException("simulated response stream failure");
        var response = new TrackingResponseMessage(HttpStatusCode.OK)
        {
            Content = new ThrowingStreamContent(expected),
        };
        using var client = CreateClient(new StubHandler(_ => response));

        IOException actual = await Assert.ThrowsAsync<IOException>(
            () => client.OpenReadAsync("media", "video.bin"));

        Assert.Same(expected, actual);
        Assert.True(response.IsDisposed);
    }

    /// <summary>
    /// 验证内容流释放失败时仍释放其所属 HTTP 响应。
    /// </summary>
    [Fact]
    public async Task OpenReadAsync_ContentDisposeFailure_StillDisposesResponse()
    {
        var expected = new IOException("simulated content dispose failure");
        var response = new TrackingResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamingContent([1, 2, 3], () => new ThrowingDisposeStream([1, 2, 3], expected)),
        };
        using var client = CreateClient(new StubHandler(_ => response));
        var result = Assert.IsType<SndbObjectReadResult>(
            await client.OpenReadAsync("media", "video.bin"));

        IOException actual = Assert.Throws<IOException>(() => result.Content.Dispose());

        Assert.Same(expected, actual);
        Assert.True(response.IsDisposed);
    }

    /// <summary>验证远程客户端能够读取并更新桶语义配置，且请求使用既有 semantic 端点。</summary>
    [Fact]
    public async Task SemanticOptionsAsync_RemoteRoundTripsThroughBucketEndpoint()
    {
        var requests = new List<(HttpMethod Method, string Path, string Body)>();
        using var client = CreateClient(new StubHandler(request =>
        {
            var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
            requests.Add((request.Method, request.RequestUri!.PathAndQuery, body));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"bucket\":\"media\",\"asyncIngestionEnabled\":true,\"thumbnailEnabled\":true,\"thumbnailMaxWidth\":320,\"thumbnailMaxHeight\":320,\"thumbnailQuality\":80,\"updatedUtc\":\"2026-07-27T00:00:00Z\"}",
                    Encoding.UTF8,
                    "application/json")
            };
        }));

        var current = await client.GetSemanticOptionsAsync("media");
        var updated = await client.SetSemanticOptionsAsync("media", true, true);

        Assert.True(current.AsyncIngestionEnabled);
        Assert.True(current.ThumbnailEnabled);
        Assert.True(updated.ThumbnailEnabled);
        Assert.Collection(
            requests,
            get =>
            {
                Assert.Equal(HttpMethod.Get, get.Method);
                Assert.Equal("/v1/db/testdb/s3/media?semantic", get.Path);
            },
            put =>
            {
                Assert.Equal(HttpMethod.Put, put.Method);
                Assert.Equal("/v1/db/testdb/s3/media?semantic", put.Path);
                Assert.Contains("\"thumbnailEnabled\":true", put.Body, StringComparison.Ordinal);
                Assert.Contains("\"asyncIngestionEnabled\":true", put.Body, StringComparison.Ordinal);
            });
    }

    /// <summary>验证嵌入式客户端直接复用对象桶语义配置，不依赖 HTTP 服务。</summary>
    [Fact]
    public async Task SemanticOptionsAsync_EmbeddedPersistsConfiguration()
    {
        string root = Path.Combine(Path.GetTempPath(), $"sonnetdb-semantic-{Guid.NewGuid():N}");
        try
        {
            using var client = new SndbObjectStorageClient($"Data Source={root};Mode=Embedded");
            await client.CreateBucketAsync("media");
            var updated = await client.SetSemanticOptionsAsync("media", true, true, 320, 320, 80);
            var current = await client.GetSemanticOptionsAsync("media");

            Assert.True(updated.AsyncIngestionEnabled);
            Assert.True(current.AsyncIngestionEnabled);
            Assert.True(current.ThumbnailEnabled);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// 使用指定响应处理器创建隔离的对象桶客户端。
    /// </summary>
    private static SndbObjectStorageClient CreateClient(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://object-client.test/"),
        };
        return new SndbObjectStorageClient(ConnectionString, httpClient);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        /// <summary>
        /// 返回测试预设的 HTTP 响应。
        /// </summary>
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }

    private sealed class StreamingContent : HttpContent
    {
        private readonly byte[] _content;
        private readonly Func<Stream> _streamFactory;

        /// <summary>
        /// 创建不声明 Content-Length 的测试内容。
        /// </summary>
        public StreamingContent(byte[] content, Func<Stream>? streamFactory = null)
        {
            _content = content;
            _streamFactory = streamFactory ?? (() => new MemoryStream(_content, writable: false));
        }

        /// <summary>
        /// 在需要序列化时写出测试内容。
        /// </summary>
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => stream.WriteAsync(_content).AsTask();

        /// <summary>
        /// 明确不预先声明内容长度。
        /// </summary>
        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        /// <summary>
        /// 创建测试响应内容流。
        /// </summary>
        protected override Task<Stream> CreateContentReadStreamAsync()
            => Task.FromResult(_streamFactory());

        /// <summary>
        /// 创建支持取消参数的测试响应内容流。
        /// </summary>
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
            => Task.FromResult(_streamFactory());
    }

    private sealed class ThrowingStreamContent(IOException failure) : HttpContent
    {
        /// <summary>
        /// 当前测试不会执行内容序列化。
        /// </summary>
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => Task.FromException(failure);

        /// <summary>
        /// 明确不预先声明内容长度。
        /// </summary>
        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        /// <summary>
        /// 模拟响应内容流创建失败。
        /// </summary>
        protected override Task<Stream> CreateContentReadStreamAsync()
            => Task.FromException<Stream>(failure);

        /// <summary>
        /// 模拟带取消参数的响应内容流创建失败。
        /// </summary>
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
            => Task.FromException<Stream>(failure);
    }

    private sealed class ThrowingDisposeStream(byte[] content, IOException failure)
        : MemoryStream(content, writable: false)
    {
        /// <summary>
        /// 模拟底层内容流释放失败。
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                throw failure;
            base.Dispose(disposing);
        }
    }

    private sealed class TrackingResponseMessage(HttpStatusCode statusCode) : HttpResponseMessage(statusCode)
    {
        public bool IsDisposed { get; private set; }

        /// <summary>
        /// 记录客户端是否尝试释放响应；测试替身不继续释放可能抛错的内容流。
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                IsDisposed = true;
        }
    }
}
