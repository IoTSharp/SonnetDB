using System.Buffers;
using SonnetDB.Protocol;

namespace SonnetDB.Client;

public sealed partial class SndbFrameClient
{
    /// <summary>通过原生 HTTP/2 Frame 读取 KV，区分缺失键和空二进制值。</summary>
    /// <param name="db">目标数据库名。</param>
    /// <param name="keyspace">keyspace 名。</param>
    /// <param name="key">原始二进制键。</param>
    /// <param name="streamId">关联编号。</param>
    /// <param name="cancellationToken">调用方取消令牌。</param>
    /// <returns>独立拥有的值、版本和完整 UTC 过期时间；缺失键返回空引用。</returns>
    public async Task<KvDataFrameValue?> GetKvAsync(string db, string keyspace, ReadOnlyMemory<byte> key,
        uint streamId = 1, CancellationToken cancellationToken = default)
    {
        ValidateKvCall(db, keyspace, cancellationToken);
        var encoded = new ArrayBufferWriter<byte>();
        KvDataFrameCodec.EncodeGetRequest(encoded, streamId, db, keyspace, key.Span, _maxPayloadBytes);
        ReadOnlyMemory<byte> response = await SendKvAsync(KvFrameOp.Get, streamId, encoded, cancellationToken).ConfigureAwait(false);
        try { return KvDataFrameCodec.DecodeGetResponse(response.Span); }
        catch (Exception exception) when (exception is FrameFormatException or InvalidOperationException)
        {
            throw InvalidKvResponse(exception);
        }
    }

    /// <summary>通过原生 HTTP/2 Frame 原子读取并删除 KV，返回删除前记录及本次变更版本。</summary>
    /// <param name="db">目标数据库名。</param>
    /// <param name="keyspace">keyspace 名。</param>
    /// <param name="key">原始二进制键。</param>
    /// <param name="streamId">关联编号。</param>
    /// <param name="cancellationToken">调用方取消令牌。</param>
    /// <returns>独立拥有的旧值、旧版本及完整 UTC 过期时间，以及服务器确认的删除变更版本；缺失键的两个字段均为空引用。</returns>
    /// <remarks>请求开始发送后，取消、超时、响应超预算或回执不完整均可能发生在删除提交之后；保留未知结果，不自动重试或重放。</remarks>
    public async Task<KvDataFrameExchangeResult> GetAndDeleteKvAsync(string db, string keyspace, ReadOnlyMemory<byte> key,
        uint streamId = 1, CancellationToken cancellationToken = default)
    {
        ValidateKvCall(db, keyspace, cancellationToken);
        var encoded = new ArrayBufferWriter<byte>();
        KvDataFrameCodec.EncodeGetAndDeleteRequest(encoded, streamId, db, keyspace, key.Span, _maxPayloadBytes);
        ReadOnlyMemory<byte> response = await SendKvAsync(KvFrameOp.GetAndDelete, streamId, encoded, cancellationToken).ConfigureAwait(false);
        try { return KvDataFrameCodec.DecodeExchangeResponse(response.Span); }
        catch (Exception exception) when (exception is FrameFormatException or InvalidOperationException)
        {
            throw InvalidKvResponse(exception);
        }
    }

    /// <summary>通过原生 HTTP/2 Frame 写入 KV；未取得完整可信回执时保留未知结果，不自动重试。</summary>
    /// <param name="db">目标数据库名。</param>
    /// <param name="keyspace">keyspace 名。</param>
    /// <param name="key">原始二进制键。</param>
    /// <param name="value">原始二进制值，可为空。</param>
    /// <param name="expiresAtUtc">绝对过期时间，空表示永不过期；按 UTC ticks 无损传输。</param>
    /// <param name="streamId">关联编号。</param>
    /// <param name="cancellationToken">调用方取消令牌。</param>
    /// <returns>服务器确认的单调版本号。</returns>
    public async Task<long> PutKvAsync(string db, string keyspace, ReadOnlyMemory<byte> key,
        ReadOnlyMemory<byte> value, DateTimeOffset? expiresAtUtc = null, uint streamId = 1,
        CancellationToken cancellationToken = default)
    {
        ValidateKvCall(db, keyspace, cancellationToken);
        var encoded = new ArrayBufferWriter<byte>();
        KvDataFrameCodec.EncodePutRequest(encoded, streamId, db, keyspace, key.Span, value.Span, expiresAtUtc, _maxPayloadBytes);
        ReadOnlyMemory<byte> response = await SendKvAsync(KvFrameOp.Put, streamId, encoded, cancellationToken).ConfigureAwait(false);
        try { return KvDataFrameCodec.DecodePutResponse(response.Span); }
        catch (Exception exception) when (exception is FrameFormatException or InvalidOperationException)
        {
            throw InvalidKvResponse(exception);
        }
    }

    /// <summary>读取一页 KV 前缀结果；调用方以末条键作为下一次 afterKey，不自动预取或拼接全部结果。</summary>
    /// <param name="db">目标数据库名。</param>
    /// <param name="keyspace">keyspace 名。</param>
    /// <param name="prefix">原始二进制键前缀。</param>
    /// <param name="afterKey">排除性的起始键，空表示从前缀起点开始。</param>
    /// <param name="limit">显式单页条数，范围 1～10,000。</param>
    /// <param name="streamId">关联编号。</param>
    /// <param name="cancellationToken">调用方取消令牌。</param>
    /// <returns>独立拥有的单页记录，保持服务器字节顺序；不承诺跨请求快照。</returns>
    public async Task<IReadOnlyList<KvDataFrameEntry>> ScanKvPageAsync(string db, string keyspace,
        ReadOnlyMemory<byte> prefix = default, ReadOnlyMemory<byte> afterKey = default, int limit = 100,
        uint streamId = 1, CancellationToken cancellationToken = default)
    {
        ValidateKvCall(db, keyspace, cancellationToken);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, KvDataFrameCodec.MaxScanEntries);
        var encoded = new ArrayBufferWriter<byte>();
        KvDataFrameCodec.EncodeScanRequest(encoded, streamId, db, keyspace, prefix.Span, afterKey.Span, limit, _maxPayloadBytes);
        ReadOnlyMemory<byte> response = await SendKvAsync(KvFrameOp.Scan, streamId, encoded, cancellationToken).ConfigureAwait(false);
        try { return KvDataFrameCodec.DecodeScanResponse(response.Span, limit); }
        catch (Exception exception) when (exception is FrameFormatException or InvalidOperationException)
        {
            throw InvalidKvResponse(exception);
        }
    }

    private void ValidateKvCall(string db, string keyspace, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyspace);
    }

    private Task<ReadOnlyMemory<byte>> SendKvAsync(KvFrameOp op, uint streamId,
        ArrayBufferWriter<byte> encoded, CancellationToken cancellationToken)
        => SendUnaryAsync((byte)FrameService.Kv, (byte)op, streamId,
            encoded.WrittenMemory[FrameHeader.Size..], cancellationToken);

    private static SndbFrameException InvalidKvResponse(Exception exception)
        => new("frame_kv_terminal", "KV 响应 payload 不完整或不符合合同；结果可能未知。", exception);
}
