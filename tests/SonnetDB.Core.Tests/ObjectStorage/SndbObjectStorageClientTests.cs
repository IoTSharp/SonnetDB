using System.Net;
using System.Net.Http.Headers;
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
