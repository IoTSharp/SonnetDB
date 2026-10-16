using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using SonnetDB.Protocol;

namespace SonnetDB.Client;

/// <summary>只使用 HTTP/2 二进制 Frame 的远程传输入口，不依赖本地数据库引擎。</summary>
public sealed class SndbFrameClient : IDisposable
{
    private const string ContentType = "application/x-sonnetdb-frame";
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Uri _endpoint;
    private readonly AuthenticationHeaderValue? _authorization;
    private readonly int _maxPayloadBytes;
    private readonly TimeSpan _timeout;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly CancellationToken _lifetimeToken;
    private int _disposed;

    /// <summary>创建无重定向、无协议回落、无自动业务重试的远程客户端。</summary>
    /// <param name="server">Server 基地址，可包含以斜杠结尾的部署路径。</param>
    /// <param name="token">既有认证 token；为空引用时不附认证头。</param>
    /// <param name="maxPayloadBytes">单个请求或响应的编码 payload 上限，默认 4 MiB。</param>
    /// <param name="timeout">从发送到响应完整结束的总期限，默认 30 秒，最多 10 分钟。</param>
    public SndbFrameClient(Uri server, string? token = null,
        int maxPayloadBytes = 4 * 1024 * 1024, TimeSpan? timeout = null)
        : this(CreateHttpClient(server, token, maxPayloadBytes, timeout), server,
            token, maxPayloadBytes, timeout, ownsHttp: true)
    { }

    internal SndbFrameClient(HttpClient http, Uri server, string? token = null,
        int maxPayloadBytes = 4 * 1024 * 1024, TimeSpan? timeout = null, bool ownsHttp = false)
    {
        Validate(server, token, maxPayloadBytes, timeout);
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _ownsHttp = ownsHttp;
        _endpoint = new Uri(server, "v1/frame");
        _authorization = token is null ? null : new AuthenticationHeaderValue("Bearer", token);
        _maxPayloadBytes = maxPayloadBytes;
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
        _lifetimeToken = _lifetimeCancellation.Token;
    }

    /// <summary>发送单请求/单响应 Frame，返回独立拥有的 payload；不支持多帧查询或订阅。</summary>
    /// <param name="service">服务编号。</param>
    /// <param name="operation">服务内操作码。</param>
    /// <param name="streamId">响应必须回显的关联编号。</param>
    /// <param name="payload">由对应模型 codec 编码的请求体。</param>
    /// <param name="cancellationToken">覆盖响应读取的调用方取消令牌。</param>
    /// <returns>独立拥有的响应 payload。</returns>
    public async Task<ReadOnlyMemory<byte>> SendUnaryAsync(byte service, byte operation,
        uint streamId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfGreaterThan(payload.Length, _maxPayloadBytes);
        if (service is (byte)FrameService.Sql or (byte)FrameService.Vector or (byte)FrameService.Graph
            || service == (byte)FrameService.Object && operation == (byte)ObjectFrameOp.Get
            || service == (byte)FrameService.Mq && operation is (byte)MqFrameOp.Subscribe or (byte)MqFrameOp.Unsubscribe)
            throw new NotSupportedException("此操作需要多帧或双工合同，不能通过单响应入口执行。");

        var buffer = new ArrayBufferWriter<byte>(checked(FrameHeader.Size + payload.Length));
        var header = new FrameHeader((uint)payload.Length, FrameHeader.CurrentVersion,
            service, operation, (byte)FrameFlags.None, streamId);
        FrameCodec.WriteFrame(buffer, header, payload.Span);
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new ReadOnlyMemoryContent(buffer.WrittenMemory),
        };
        request.Headers.Authorization = _authorization;
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(ContentType);
        using var deadline = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token, _lifetimeToken);
        linked.Token.ThrowIfCancellationRequested();

        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                .ConfigureAwait(false);
            if (response.Version != HttpVersion.Version20)
                throw new SndbFrameException("http_version", "Frame 响应未使用 HTTP/2，已拒绝协议回落。");
            if (!response.IsSuccessStatusCode)
                throw new SndbFrameException("http_error", $"Frame 端点返回 HTTP {(int)response.StatusCode}。");
            if (!string.Equals(response.Content.Headers.ContentType?.MediaType, ContentType, StringComparison.OrdinalIgnoreCase)
                || response.Content.Headers.ContentEncoding.Count != 0)
                throw new SndbFrameException("frame_content_type", "Frame 响应类型或内容编码不符合合同。");
            if (response.Content.Headers.ContentLength > FrameHeader.Size + (long)_maxPayloadBytes)
                throw new SndbFrameException("frame_budget", "Frame 响应超过配置的编码字节预算。");

            await using var body = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
            byte[] headerBytes = new byte[FrameHeader.Size];
            await body.ReadExactlyAsync(headerBytes, linked.Token).ConfigureAwait(false);
            if (!FrameHeader.TryRead(headerBytes, out var reply)
                || reply.Version != FrameHeader.CurrentVersion || reply.Service != service
                || reply.Op != operation || reply.StreamId != streamId
                || reply.Flags is not ((byte)FrameFlags.Response) and not ((byte)(FrameFlags.Response | FrameFlags.Error)))
                throw new SndbFrameException("frame_identity", "Frame 响应版本、标志或关联字段不匹配。");
            if (reply.PayloadLength > _maxPayloadBytes)
                throw new SndbFrameException("frame_budget", "Frame payload 超过配置的编码字节预算。");

            byte[] result = new byte[(int)reply.PayloadLength];
            await body.ReadExactlyAsync(result, linked.Token).ConfigureAwait(false);
            byte[] trailing = new byte[1];
            if (await body.ReadAsync(trailing, linked.Token).ConfigureAwait(false) != 0)
                throw new SndbFrameException("frame_trailing_data", "单响应操作包含额外帧或尾部数据。");
            if (reply.IsError)
            {
                var error = FrameCodec.ReadErrorPayload(result);
                throw new SndbFrameException(error.Code, error.Message);
            }
            return result;
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new SndbFrameCanceledException(cancellationToken, exception);
        }
        catch (OperationCanceledException exception) when (deadline.IsCancellationRequested)
        {
            throw new SndbFrameException("request_timeout", "Frame 请求总期限已到；结果可能未知。", exception);
        }
        catch (OperationCanceledException exception)
        {
            throw new SndbFrameException("frame_transport", "Frame 请求被客户端关闭或传输层中止；结果可能未知。", exception);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidOperationException or FrameFormatException)
        {
            throw new SndbFrameException("frame_transport", "Frame 请求未取得可信的完整响应；结果可能未知。", exception);
        }
    }

    /// <summary>释放客户端拥有的 HTTP 连接及在途请求。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try { _lifetimeCancellation.Cancel(); }
        finally
        {
            try { if (_ownsHttp) _http.Dispose(); }
            finally { _lifetimeCancellation.Dispose(); }
        }
    }

    private static void Validate(Uri server, string? token, int maxPayloadBytes, TimeSpan? timeout)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (!server.IsAbsoluteUri || server.Scheme is not ("http" or "https")
            || server.Query.Length != 0 || server.Fragment.Length != 0 || server.UserInfo.Length != 0
            || !server.AbsolutePath.EndsWith('/'))
            throw new ArgumentException("Server 必须为不含凭据/查询/片段、以斜杠结尾的 HTTP(S) 基地址。", nameof(server));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPayloadBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxPayloadBytes, (int)FrameHeader.MaxFramePayloadBytes);
        if (timeout is { } value && (value <= TimeSpan.Zero || value > TimeSpan.FromMinutes(10)))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        if (token is not null && (string.IsNullOrWhiteSpace(token) || token.Length > 16 * 1024
            || token.Any(char.IsWhiteSpace)))
            throw new ArgumentException("Token 必须为长度受限且不含空白的认证值。", nameof(token));
    }

    private static HttpClient CreateHttpClient(Uri server, string? token, int maxPayloadBytes, TimeSpan? timeout)
    {
        Validate(server, token, maxPayloadBytes, timeout);
        return new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }
}
