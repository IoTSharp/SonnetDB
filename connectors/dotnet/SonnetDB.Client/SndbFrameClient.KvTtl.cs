using System.Buffers;
using SonnetDB.Protocol;

namespace SonnetDB.Client;

public sealed partial class SndbFrameClient
{
    /// <summary>通过原生 HTTP/2 Frame 读取 KV 键的剩余 TTL。</summary>
    /// <param name="db">目标数据库名。</param>
    /// <param name="keyspace">keyspace 名。</param>
    /// <param name="key">原始二进制键。</param>
    /// <param name="streamId">关联编号。</param>
    /// <param name="cancellationToken">调用方取消令牌。</param>
    /// <returns>剩余毫秒数和绝对 UTC 过期时间；缺失键返回 -2，永不过期返回 -1。</returns>
    /// <remarks>请求取消、超时、断连或响应不完整时保留终态不确定性，客户端不会自动重试。</remarks>
    public async Task<KvDataFrameTtl> GetTimeToLiveKvAsync(
        string db,
        string keyspace,
        ReadOnlyMemory<byte> key,
        uint streamId = 1,
        CancellationToken cancellationToken = default)
    {
        ValidateKvCall(db, keyspace, cancellationToken);
        var encoded = new ArrayBufferWriter<byte>();
        KvDataFrameCodec.EncodeGetTimeToLiveRequest(
            encoded, streamId, db, keyspace, key.Span, _maxPayloadBytes);
        ReadOnlyMemory<byte> response = await SendKvAsync(
            KvFrameOp.GetTimeToLive, streamId, encoded, cancellationToken).ConfigureAwait(false);
        try
        {
            return KvDataFrameCodec.DecodeGetTimeToLiveResponse(response.Span);
        }
        catch (Exception exception) when (exception is FrameFormatException or InvalidOperationException)
        {
            throw InvalidKvResponse(exception);
        }
    }
}
