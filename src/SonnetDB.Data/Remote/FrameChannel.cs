using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using SonnetDB.Protocol;

namespace SonnetDB.Data.Remote;

/// <summary>
/// 客户端二进制帧传输通道（M28 P5b #241）。封装向 <c>POST /v1/frame</c> 发送请求帧、
/// 解析响应帧、并按 <see cref="SndbTransportProtocol"/> 与运行时惰性探测决定是否走帧。
/// 每个远程客户端持有一个实例，共享底层 <see cref="HttpClient"/>。
/// </summary>
/// <remarks>
/// <para>探测语义：<c>auto</c> 首次尝试帧端点——</para>
/// <list type="bullet">
///   <item>传输级失败（HTTP 非 2xx，如 415/404，或连接异常，或 200 体解析不出合法帧）
///     视为"服务端不懂帧"，缓存回落 REST，<see cref="TrySendAsync"/> 返回 <c>null</c>。</item>
///   <item>200 + 可解析帧（哪怕是带内错误帧）视为"服务端懂帧"，缓存走帧。带内错误帧交由
///     调用方经 <see cref="ThrowIfError"/> 转成 <see cref="SndbServerException"/>。</item>
/// </list>
/// <para>安全性：一元 POST 的传输级失败意味着服务端在处理前拒绝或从未收到请求，回落 REST
/// 不会重复应用写入；200 带内错误帧意味着操作已执行且应用级失败，直接上抛、绝不重试。</para>
/// <para><c>frame-http2</c> 强制走帧：帧端点传输级失败时直接抛错，不静默回落。</para>
/// </remarks>
internal sealed class FrameChannel
{
    private const string FrameContentType = "application/x-sonnetdb-frame";
    private const string FrameUrl = "v1/frame";

    private readonly HttpClient _http;
    private readonly SndbTransportProtocol _protocol;
    private volatile CapabilityState _state;

    public FrameChannel(HttpClient http, SndbTransportProtocol protocol)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _protocol = protocol;
        _state = protocol switch
        {
            SndbTransportProtocol.FrameHttp2 => CapabilityState.Frames,
            SndbTransportProtocol.Rest => CapabilityState.Rest,
            _ => CapabilityState.Unknown,
        };
    }

    /// <summary>当前是否应尝试帧传输（<c>rest</c> 或已探测为不支持时返回 false）。</summary>
    public bool ShouldTryFrames() => _state != CapabilityState.Rest;

    /// <summary>
    /// 发送一段请求帧缓冲，返回响应帧列表；服务端不支持帧（探测回落）时返回 <c>null</c>，
    /// 调用方据此回落 REST。<c>frame-http2</c> 强制模式下传输级失败会抛 <see cref="SndbServerException"/>。
    /// </summary>
    public async Task<IReadOnlyList<FrameMessage>?> TrySendAsync(
        ReadOnlyMemory<byte> requestFrames,
        CancellationToken cancellationToken)
    {
        if (_state == CapabilityState.Rest)
            return null;

        try
        {
            using var content = new ReadOnlyMemoryContent(requestFrames);
            content.Headers.ContentType = new MediaTypeHeaderValue(FrameContentType);
            using var response = await _http.PostAsync(FrameUrl, content, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return HandleTransportFailure(
                    $"帧端点返回 HTTP {(int)response.StatusCode}，服务端可能不支持帧协议。");

            byte[] body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            List<FrameMessage>? frames = TryParseFrames(body);
            if (frames is null || frames.Count == 0)
                return HandleTransportFailure("帧端点响应无法解析为帧，服务端可能不支持帧协议。");

            _state = CapabilityState.Frames;
            return frames;
        }
        catch (HttpRequestException ex)
        {
            return HandleTransportFailure($"帧端点连接失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 发送一段仅含单个请求帧的缓冲并返回其响应帧；先 <see cref="ThrowIfError"/>。
    /// 服务端不支持帧时返回 <c>null</c>。
    /// </summary>
    public async Task<FrameMessage?> SendUnaryAsync(
        ReadOnlyMemory<byte> requestFrame,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<FrameMessage>? frames = await TrySendAsync(requestFrame, cancellationToken).ConfigureAwait(false);
        if (frames is null)
            return null;

        FrameMessage first = frames[0];
        ThrowIfError(first.Header, first.Payload);
        return first;
    }

    /// <summary>带内错误帧（<see cref="FrameHeader.IsError"/>）转 <see cref="SndbServerException"/>。</summary>
    public static void ThrowIfError(in FrameHeader header, ReadOnlySpan<byte> payload)
    {
        if (!header.IsError)
            return;
        (string code, string message) = FrameCodec.ReadErrorPayload(payload);
        throw new SndbServerException(code, message, HttpStatusCode.OK);
    }

    private IReadOnlyList<FrameMessage>? HandleTransportFailure(string message)
    {
        if (_protocol == SndbTransportProtocol.FrameHttp2)
            throw new SndbServerException("frame_transport_error", message, HttpStatusCode.OK);

        _state = CapabilityState.Rest;
        return null;
    }

    private static List<FrameMessage>? TryParseFrames(byte[] body)
    {
        var frames = new List<FrameMessage>();
        var buffer = new ReadOnlySequence<byte>(body);
        try
        {
            while (FrameCodec.TryReadFrame(ref buffer, out FrameHeader header, out ReadOnlySequence<byte> payload))
                frames.Add(new FrameMessage(header, payload.ToArray()));
        }
        catch (FrameFormatException)
        {
            return null;
        }

        // 尾部有未成帧的残字节，视为非帧响应
        return buffer.Length == 0 ? frames : null;
    }

    private enum CapabilityState
    {
        Unknown,
        Frames,
        Rest,
    }
}

/// <summary>一个已解析的响应帧（帧头 + 已物化的 payload）。</summary>
/// <param name="Header">帧头。</param>
/// <param name="Payload">帧体字节（独立数组）。</param>
internal readonly record struct FrameMessage(FrameHeader Header, byte[] Payload);
