using System.Buffers;
using System.Text;
using SonnetDB.IO;
using SonnetMQ;

namespace SonnetDB.Protocol;

/// <summary>
/// MQ service（<see cref="FrameService.Mq"/>）四个 opcode 的帧体编解码。
/// 解码结果中的字节字段是输入缓冲上的零拷贝视图，仅在缓冲存活期内有效
/// （服务端在 PipeReader AdvanceTo 之前处理完毕；store 的 Publish 内部自行拷贝）。
/// </summary>
public static class MqFrameCodec
{
    /// <summary>名字（db / topic / consumerGroup / header key）UTF-8 字节数上限。</summary>
    public const int MaxNameBytes = 512;

    /// <summary>单条消息 header 数量上限。</summary>
    public const int MaxHeaderCount = 1024;

    /// <summary>单条消息 headers（key+value UTF-8 字节）总量上限。</summary>
    public const int MaxHeadersBytes = 64 * 1024;

    private static readonly IReadOnlyDictionary<string, string> EmptyHeaders =
        new Dictionary<string, string>(0);

    // ────────────────────────────── publish (op=1) ──────────────────────────────

    /// <summary>
    /// 编码 publish 请求帧：db, topic, headers, payload。
    /// </summary>
    public static void EncodePublishRequest(
        IBufferWriter<byte> writer,
        uint streamId,
        string db,
        string topic,
        IReadOnlyDictionary<string, string>? headers,
        ReadOnlySpan<byte> payload)
    {
        int metaLength =
            SpanWriter.MeasureVarString(db) +
            SpanWriter.MeasureVarString(topic) +
            MeasureHeaders(headers) +
            SpanWriter.MeasureVarUInt32((uint)payload.Length);
        long payloadLength = (long)metaLength + payload.Length;
        ValidateFramePayloadLength(payloadLength);

        int bodyLength = payload.Length;
        WriteHeaderAndMeta(
            writer,
            new FrameHeader((uint)payloadLength, FrameHeader.CurrentVersion,
                (byte)FrameService.Mq, (byte)MqFrameOp.Publish, (byte)FrameFlags.None, streamId),
            metaLength,
            (ref SpanWriter meta) =>
            {
                meta.WriteVarString(db);
                meta.WriteVarString(topic);
                WriteHeaders(ref meta, headers);
                meta.WriteVarUInt32((uint)bodyLength);
            });
        if (!payload.IsEmpty)
            writer.Write(payload);
    }

    /// <summary>
    /// 解码 publish 请求帧体。
    /// </summary>
    public static MqPublishFrameRequest DecodePublishRequest(ReadOnlyMemory<byte> payload)
    {
        var reader = new SpanReader(payload.Span);
        string db = ReadName(ref reader, "db");
        string topic = ReadName(ref reader, "topic");
        IReadOnlyDictionary<string, string> headers = ReadHeaders(ref reader);
        ReadOnlyMemory<byte> body = ReadBody(ref reader, payload);
        return new MqPublishFrameRequest(db, topic, headers, body);
    }

    /// <summary>
    /// 编码 publish 响应帧：offset。
    /// </summary>
    public static void EncodePublishResponse(IBufferWriter<byte> writer, uint streamId, long offset)
        => EncodeVarUInt64Response(writer, (byte)MqFrameOp.Publish, streamId, (ulong)offset);

    /// <summary>
    /// 解码 publish 响应帧体。
    /// </summary>
    public static long DecodePublishResponse(ReadOnlySpan<byte> payload)
        => DecodeVarUInt64Response(payload);

    // ────────────────────────────── publish-batch (op=2) ──────────────────────────────

    /// <summary>
    /// 编码 publish-batch 请求帧：db, topic, entries。
    /// </summary>
    public static void EncodePublishBatchRequest(
        IBufferWriter<byte> writer,
        uint streamId,
        string db,
        string topic,
        IReadOnlyList<SonnetMqPublishEntry> entries)
    {
        int metaLength =
            SpanWriter.MeasureVarString(db) +
            SpanWriter.MeasureVarString(topic) +
            SpanWriter.MeasureVarUInt32((uint)entries.Count);
        long payloadLength = metaLength;
        for (int i = 0; i < entries.Count; i++)
        {
            SonnetMqPublishEntry entry = entries[i];
            payloadLength += MeasureHeaders(entry.Headers)
                + SpanWriter.MeasureVarUInt32((uint)entry.Payload.Length)
                + entry.Payload.Length;
        }

        ValidateFramePayloadLength(payloadLength);

        WriteHeaderAndMeta(
            writer,
            new FrameHeader((uint)payloadLength, FrameHeader.CurrentVersion,
                (byte)FrameService.Mq, (byte)MqFrameOp.PublishBatch, (byte)FrameFlags.None, streamId),
            metaLength,
            (ref SpanWriter meta) =>
            {
                meta.WriteVarString(db);
                meta.WriteVarString(topic);
                meta.WriteVarUInt32((uint)entries.Count);
            });

        for (int i = 0; i < entries.Count; i++)
        {
            SonnetMqPublishEntry entry = entries[i];
            int entryMetaLength = MeasureHeaders(entry.Headers)
                + SpanWriter.MeasureVarUInt32((uint)entry.Payload.Length);
            Span<byte> span = writer.GetSpan(entryMetaLength);
            var meta = new SpanWriter(span[..entryMetaLength]);
            WriteHeaders(ref meta, entry.Headers);
            meta.WriteVarUInt32((uint)entry.Payload.Length);
            writer.Advance(entryMetaLength);
            if (!entry.Payload.IsEmpty)
                writer.Write(entry.Payload.Span);
        }
    }

    /// <summary>
    /// 解码 publish-batch 请求帧体。
    /// </summary>
    public static MqPublishBatchFrameRequest DecodePublishBatchRequest(ReadOnlyMemory<byte> payload)
    {
        var reader = new SpanReader(payload.Span);
        string db = ReadName(ref reader, "db");
        string topic = ReadName(ref reader, "topic");
        uint count = reader.ReadVarUInt32();
        if (count == 0)
            throw new FrameFormatException("publish-batch 需包含至少 1 条消息。");
        if (count > (uint)reader.Remaining)
            throw new FrameFormatException($"publish-batch 消息数 {count} 超出帧体剩余长度。");

        var entries = new SonnetMqPublishEntry[count];
        for (int i = 0; i < entries.Length; i++)
        {
            IReadOnlyDictionary<string, string> headers = ReadHeaders(ref reader);
            ReadOnlyMemory<byte> body = ReadBody(ref reader, payload);
            entries[i] = new SonnetMqPublishEntry(body, headers.Count == 0 ? null : headers);
        }

        return new MqPublishBatchFrameRequest(db, topic, entries);
    }

    /// <summary>
    /// 编码 publish-batch 响应帧：count + offsets。
    /// </summary>
    public static void EncodePublishBatchResponse(IBufferWriter<byte> writer, uint streamId, IReadOnlyList<long> offsets)
    {
        int payloadLength = SpanWriter.MeasureVarUInt32((uint)offsets.Count);
        for (int i = 0; i < offsets.Count; i++)
            payloadLength += SpanWriter.MeasureVarUInt64((ulong)offsets[i]);

        WriteHeaderAndMeta(
            writer,
            new FrameHeader((uint)payloadLength, FrameHeader.CurrentVersion,
                (byte)FrameService.Mq, (byte)MqFrameOp.PublishBatch, (byte)FrameFlags.Response, streamId),
            payloadLength,
            (ref SpanWriter meta) =>
            {
                meta.WriteVarUInt32((uint)offsets.Count);
                for (int i = 0; i < offsets.Count; i++)
                    meta.WriteVarUInt64((ulong)offsets[i]);
            });
    }

    /// <summary>
    /// 解码 publish-batch 响应帧体。
    /// </summary>
    public static long[] DecodePublishBatchResponse(ReadOnlySpan<byte> payload)
    {
        var reader = new SpanReader(payload);
        uint count = reader.ReadVarUInt32();
        if (count > (uint)payload.Length)
            throw new FrameFormatException($"publish-batch 响应 offset 数 {count} 超出帧体长度。");
        var offsets = new long[count];
        for (int i = 0; i < offsets.Length; i++)
            offsets[i] = ReadOffset(ref reader);
        return offsets;
    }

    // ────────────────────────────── pull (op=3) ──────────────────────────────

    /// <summary>
    /// 编码 pull 请求帧：db, topic, consumerGroup, maxCount（0 表示服务端默认）。
    /// </summary>
    public static void EncodePullRequest(
        IBufferWriter<byte> writer,
        uint streamId,
        string db,
        string topic,
        string consumerGroup,
        int maxCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxCount);
        int payloadLength =
            SpanWriter.MeasureVarString(db) +
            SpanWriter.MeasureVarString(topic) +
            SpanWriter.MeasureVarString(consumerGroup) +
            SpanWriter.MeasureVarUInt32((uint)maxCount);

        WriteHeaderAndMeta(
            writer,
            new FrameHeader((uint)payloadLength, FrameHeader.CurrentVersion,
                (byte)FrameService.Mq, (byte)MqFrameOp.Pull, (byte)FrameFlags.None, streamId),
            payloadLength,
            (ref SpanWriter meta) =>
            {
                meta.WriteVarString(db);
                meta.WriteVarString(topic);
                meta.WriteVarString(consumerGroup);
                meta.WriteVarUInt32((uint)maxCount);
            });
    }

    /// <summary>
    /// 解码 pull 请求帧体。
    /// </summary>
    public static MqPullFrameRequest DecodePullRequest(ReadOnlyMemory<byte> payload)
    {
        var reader = new SpanReader(payload.Span);
        string db = ReadName(ref reader, "db");
        string topic = ReadName(ref reader, "topic");
        string consumerGroup = ReadName(ref reader, "consumerGroup");
        uint maxCount = reader.ReadVarUInt32();
        if (maxCount > int.MaxValue)
            throw new FrameFormatException($"pull maxCount {maxCount} 非法。");
        return new MqPullFrameRequest(db, topic, consumerGroup, (int)maxCount);
    }

    /// <summary>
    /// 编码 pull 响应帧：count + 每条 (offset, timestampUtcTicks, headers, payload)。
    /// </summary>
    public static void EncodePullResponse(IBufferWriter<byte> writer, uint streamId, IReadOnlyList<SonnetMqMessage> messages)
        => EncodeMessagesFrame(writer, streamId, messages, (byte)MqFrameOp.Pull, (byte)FrameFlags.Response);

    /// <summary>
    /// 编码推送帧（#236 订阅推送）：op=Subscribe、flags=Push，帧体布局与 pull 响应完全一致，
    /// 故 <see cref="DecodePullResponse"/> 可原样解码。
    /// </summary>
    public static void EncodePushFrame(IBufferWriter<byte> writer, uint streamId, IReadOnlyList<SonnetMqMessage> messages)
        => EncodeMessagesFrame(writer, streamId, messages, (byte)MqFrameOp.Subscribe, (byte)FrameFlags.Push);

    /// <summary>
    /// 消息序列帧编码核心：count + 每条 (offset, timestampUtcTicks, headers, payload)。op/flags 由调用方指定，
    /// pull 响应与订阅推送共用此实现。
    /// </summary>
    private static void EncodeMessagesFrame(IBufferWriter<byte> writer, uint streamId, IReadOnlyList<SonnetMqMessage> messages, byte op, byte flags)
    {
        long payloadLength = SpanWriter.MeasureVarUInt32((uint)messages.Count);
        for (int i = 0; i < messages.Count; i++)
        {
            SonnetMqMessage message = messages[i];
            payloadLength += SpanWriter.MeasureVarUInt64((ulong)message.Offset)
                + sizeof(long)
                + MeasureHeaders(message.Headers)
                + SpanWriter.MeasureVarUInt32((uint)message.Payload.Length)
                + message.Payload.Length;
        }

        ValidateFramePayloadLength(payloadLength);

        WriteHeaderAndMeta(
            writer,
            new FrameHeader((uint)payloadLength, FrameHeader.CurrentVersion,
                (byte)FrameService.Mq, op, flags, streamId),
            SpanWriter.MeasureVarUInt32((uint)messages.Count),
            (ref SpanWriter meta) => meta.WriteVarUInt32((uint)messages.Count));

        for (int i = 0; i < messages.Count; i++)
        {
            SonnetMqMessage message = messages[i];
            int metaLength = SpanWriter.MeasureVarUInt64((ulong)message.Offset)
                + sizeof(long)
                + MeasureHeaders(message.Headers)
                + SpanWriter.MeasureVarUInt32((uint)message.Payload.Length);
            Span<byte> span = writer.GetSpan(metaLength);
            var meta = new SpanWriter(span[..metaLength]);
            meta.WriteVarUInt64((ulong)message.Offset);
            meta.WriteInt64(message.TimestampUtc.UtcTicks);
            WriteHeaders(ref meta, message.Headers);
            meta.WriteVarUInt32((uint)message.Payload.Length);
            writer.Advance(metaLength);
            if (message.Payload.Length > 0)
                writer.Write(message.Payload);
        }
    }

    /// <summary>
    /// 解码 pull 响应帧体。帧内不携带 topic，由调用方传入以构造 <see cref="SonnetMqMessage"/>。
    /// </summary>
    public static SonnetMqMessage[] DecodePullResponse(ReadOnlyMemory<byte> payload, string topic)
    {
        var reader = new SpanReader(payload.Span);
        uint count = reader.ReadVarUInt32();
        if (count > (uint)payload.Length)
            throw new FrameFormatException($"pull 响应消息数 {count} 超出帧体长度。");

        var messages = new SonnetMqMessage[count];
        for (int i = 0; i < messages.Length; i++)
        {
            long offset = ReadOffset(ref reader);
            long ticks = reader.ReadInt64();
            IReadOnlyDictionary<string, string> headers = ReadHeaders(ref reader);
            ReadOnlyMemory<byte> body = ReadBody(ref reader, payload);
            messages[i] = new SonnetMqMessage(topic, offset, new DateTimeOffset(ticks, TimeSpan.Zero), headers, body.ToArray());
        }

        return messages;
    }

    // ────────────────────────────── ack (op=4) ──────────────────────────────

    /// <summary>
    /// 编码 ack 请求帧：db, topic, consumerGroup, offset。
    /// </summary>
    public static void EncodeAckRequest(
        IBufferWriter<byte> writer,
        uint streamId,
        string db,
        string topic,
        string consumerGroup,
        long offset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        int payloadLength =
            SpanWriter.MeasureVarString(db) +
            SpanWriter.MeasureVarString(topic) +
            SpanWriter.MeasureVarString(consumerGroup) +
            SpanWriter.MeasureVarUInt64((ulong)offset);

        WriteHeaderAndMeta(
            writer,
            new FrameHeader((uint)payloadLength, FrameHeader.CurrentVersion,
                (byte)FrameService.Mq, (byte)MqFrameOp.Ack, (byte)FrameFlags.None, streamId),
            payloadLength,
            (ref SpanWriter meta) =>
            {
                meta.WriteVarString(db);
                meta.WriteVarString(topic);
                meta.WriteVarString(consumerGroup);
                meta.WriteVarUInt64((ulong)offset);
            });
    }

    /// <summary>
    /// 解码 ack 请求帧体。
    /// </summary>
    public static MqAckFrameRequest DecodeAckRequest(ReadOnlyMemory<byte> payload)
    {
        var reader = new SpanReader(payload.Span);
        string db = ReadName(ref reader, "db");
        string topic = ReadName(ref reader, "topic");
        string consumerGroup = ReadName(ref reader, "consumerGroup");
        long offset = ReadOffset(ref reader);
        return new MqAckFrameRequest(db, topic, consumerGroup, offset);
    }

    /// <summary>
    /// 编码 ack 响应帧：nextOffset。
    /// </summary>
    public static void EncodeAckResponse(IBufferWriter<byte> writer, uint streamId, long nextOffset)
        => EncodeVarUInt64Response(writer, (byte)MqFrameOp.Ack, streamId, (ulong)nextOffset);

    /// <summary>
    /// 解码 ack 响应帧体。
    /// </summary>
    public static long DecodeAckResponse(ReadOnlySpan<byte> payload)
        => DecodeVarUInt64Response(payload);

    // ────────────────────────────── subscribe (op=5) ──────────────────────────────

    /// <summary>
    /// 编码 subscribe 请求帧：db, topic, consumerGroup（可空串）, startMode(u8), startOffset(varuint, 仅 mode=ExplicitOffset), batchMax(varuint, 0=服务端默认)。
    /// </summary>
    public static void EncodeSubscribeRequest(
        IBufferWriter<byte> writer,
        uint streamId,
        string db,
        string topic,
        string consumerGroup,
        MqSubscribeStartMode startMode,
        long startOffset,
        int batchMax)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(batchMax);
        int payloadLength =
            SpanWriter.MeasureVarString(db) +
            SpanWriter.MeasureVarString(consumerGroup) +
            SpanWriter.MeasureVarString(topic) +
            1 +
            SpanWriter.MeasureVarUInt64((ulong)startOffset) +
            SpanWriter.MeasureVarUInt32((uint)batchMax);

        WriteHeaderAndMeta(
            writer,
            new FrameHeader((uint)payloadLength, FrameHeader.CurrentVersion,
                (byte)FrameService.Mq, (byte)MqFrameOp.Subscribe, (byte)FrameFlags.None, streamId),
            payloadLength,
            (ref SpanWriter meta) =>
            {
                meta.WriteVarString(db);
                meta.WriteVarString(topic);
                meta.WriteVarString(consumerGroup);
                meta.WriteByte((byte)startMode);
                meta.WriteVarUInt64((ulong)startOffset);
                meta.WriteVarUInt32((uint)batchMax);
            });
    }

    /// <summary>
    /// 解码 subscribe 请求帧体。
    /// </summary>
    public static MqSubscribeFrameRequest DecodeSubscribeRequest(ReadOnlyMemory<byte> payload)
    {
        var reader = new SpanReader(payload.Span);
        string db = ReadName(ref reader, "db");
        string topic = ReadName(ref reader, "topic");
        string consumerGroup = ReadName(ref reader, "consumerGroup");
        byte modeRaw = reader.ReadByte();
        if (modeRaw > (byte)MqSubscribeStartMode.Latest)
            throw new FrameFormatException($"subscribe startMode {modeRaw} 非法。");
        ulong startOffset = reader.ReadVarUInt64();
        if (startOffset > long.MaxValue)
            throw new FrameFormatException($"subscribe startOffset {startOffset} 超出 long 范围。");
        uint batchMax = reader.ReadVarUInt32();
        if (batchMax > int.MaxValue)
            throw new FrameFormatException($"subscribe batchMax {batchMax} 非法。");
        return new MqSubscribeFrameRequest(db, topic, consumerGroup, (MqSubscribeStartMode)modeRaw, (long)startOffset, (int)batchMax);
    }

    /// <summary>
    /// 编码 subscribe 响应帧：生效起始 offset。
    /// </summary>
    public static void EncodeSubscribeResponse(IBufferWriter<byte> writer, uint streamId, long effectiveOffset)
        => EncodeVarUInt64Response(writer, (byte)MqFrameOp.Subscribe, streamId, (ulong)effectiveOffset);

    /// <summary>
    /// 解码 subscribe 响应帧体。
    /// </summary>
    public static long DecodeSubscribeResponse(ReadOnlySpan<byte> payload)
        => DecodeVarUInt64Response(payload);

    // ────────────────────────────── unsubscribe (op=6) ──────────────────────────────

    /// <summary>
    /// 编码 unsubscribe 请求帧（空体，按 streamId 定位订阅）。
    /// </summary>
    public static void EncodeUnsubscribeRequest(IBufferWriter<byte> writer, uint streamId)
    {
        WriteHeaderAndMeta(
            writer,
            new FrameHeader(0, FrameHeader.CurrentVersion,
                (byte)FrameService.Mq, (byte)MqFrameOp.Unsubscribe, (byte)FrameFlags.None, streamId),
            0,
            (ref SpanWriter _) => { });
    }

    /// <summary>
    /// 编码 unsubscribe 响应帧（空体，Response 位确认）。
    /// </summary>
    public static void EncodeUnsubscribeResponse(IBufferWriter<byte> writer, uint streamId)
    {
        WriteHeaderAndMeta(
            writer,
            new FrameHeader(0, FrameHeader.CurrentVersion,
                (byte)FrameService.Mq, (byte)MqFrameOp.Unsubscribe, (byte)FrameFlags.Response, streamId),
            0,
            (ref SpanWriter _) => { });
    }

    // ────────────────────────────── 内部辅助 ──────────────────────────────

    private delegate void MetaWriter(ref SpanWriter writer);

    private static void WriteHeaderAndMeta(IBufferWriter<byte> writer, in FrameHeader header, int metaLength, MetaWriter writeMeta)
    {
        Span<byte> span = writer.GetSpan(FrameHeader.Size + metaLength);
        header.Write(span);
        var meta = new SpanWriter(span.Slice(FrameHeader.Size, metaLength));
        writeMeta(ref meta);
        writer.Advance(FrameHeader.Size + metaLength);
    }

    private static void EncodeVarUInt64Response(IBufferWriter<byte> writer, byte op, uint streamId, ulong value)
    {
        int payloadLength = SpanWriter.MeasureVarUInt64(value);
        WriteHeaderAndMeta(
            writer,
            new FrameHeader((uint)payloadLength, FrameHeader.CurrentVersion,
                (byte)FrameService.Mq, op, (byte)FrameFlags.Response, streamId),
            payloadLength,
            (ref SpanWriter meta) => meta.WriteVarUInt64(value));
    }

    private static long DecodeVarUInt64Response(ReadOnlySpan<byte> payload)
    {
        var reader = new SpanReader(payload);
        ulong value = reader.ReadVarUInt64();
        if (value > long.MaxValue)
            throw new FrameFormatException($"offset {value} 超出 long 范围。");
        return (long)value;
    }

    private static void ValidateFramePayloadLength(long payloadLength)
    {
        if (payloadLength > FrameHeader.MaxFramePayloadBytes)
            throw new ArgumentException($"帧 payload 长度 {payloadLength} 超过上限 {FrameHeader.MaxFramePayloadBytes}。");
    }

    private static int MeasureHeaders(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0)
            return SpanWriter.MeasureVarUInt32(0);

        int total = SpanWriter.MeasureVarUInt32((uint)headers.Count);
        foreach (KeyValuePair<string, string> pair in headers)
            total += SpanWriter.MeasureVarString(pair.Key) + SpanWriter.MeasureVarString(pair.Value);
        return total;
    }

    private static void WriteHeaders(ref SpanWriter writer, IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0)
        {
            writer.WriteVarUInt32(0);
            return;
        }

        writer.WriteVarUInt32((uint)headers.Count);
        foreach (KeyValuePair<string, string> pair in headers)
        {
            writer.WriteVarString(pair.Key);
            writer.WriteVarString(pair.Value);
        }
    }

    private static IReadOnlyDictionary<string, string> ReadHeaders(ref SpanReader reader)
    {
        uint count = reader.ReadVarUInt32();
        if (count == 0)
            return EmptyHeaders;
        if (count > MaxHeaderCount)
            throw new FrameFormatException($"header 数 {count} 超过上限 {MaxHeaderCount}。");

        int startPosition = reader.Position;
        var headers = new Dictionary<string, string>((int)count, StringComparer.Ordinal);
        for (uint i = 0; i < count; i++)
        {
            string key = ReadName(ref reader, "header key");
            string value = ReadBoundedString(ref reader, MaxHeadersBytes, "header value");
            headers[key] = value;
            if (reader.Position - startPosition > MaxHeadersBytes)
                throw new FrameFormatException($"headers 总量超过上限 {MaxHeadersBytes} 字节。");
        }

        return headers;
    }

    private static string ReadName(ref SpanReader reader, string field)
        => ReadBoundedString(ref reader, MaxNameBytes, field);

    private static string ReadBoundedString(ref SpanReader reader, int maxBytes, string field)
    {
        uint length = reader.ReadVarUInt32();
        if (length > (uint)maxBytes)
            throw new FrameFormatException($"{field} 长度 {length} 超过上限 {maxBytes} 字节。");
        if (length == 0)
            return string.Empty;
        if (length > (uint)reader.Remaining)
            throw new FrameFormatException($"{field} 长度 {length} 超出帧体剩余长度。");
        return Encoding.UTF8.GetString(reader.ReadBytes((int)length));
    }

    private static ReadOnlyMemory<byte> ReadBody(ref SpanReader reader, ReadOnlyMemory<byte> payload)
    {
        uint length = reader.ReadVarUInt32();
        if (length > (uint)reader.Remaining)
            throw new FrameFormatException($"消息体长度 {length} 超出帧体剩余长度。");
        ReadOnlyMemory<byte> body = payload.Slice(reader.Position, (int)length);
        reader.Skip((int)length);
        return body;
    }

    private static long ReadOffset(ref SpanReader reader)
    {
        ulong value = reader.ReadVarUInt64();
        if (value > long.MaxValue)
            throw new FrameFormatException($"offset {value} 超出 long 范围。");
        return (long)value;
    }
}

/// <summary>publish 请求帧解码结果。<see cref="Payload"/> 为输入缓冲的零拷贝视图。</summary>
/// <param name="Db">数据库名。</param>
/// <param name="Topic">topic 名（未限定，服务端负责加 db 前缀）。</param>
/// <param name="Headers">消息头（可能为空字典）。</param>
/// <param name="Payload">消息体视图。</param>
public readonly record struct MqPublishFrameRequest(
    string Db,
    string Topic,
    IReadOnlyDictionary<string, string> Headers,
    ReadOnlyMemory<byte> Payload);

/// <summary>publish-batch 请求帧解码结果。条目 payload 为输入缓冲的零拷贝视图。</summary>
/// <param name="Db">数据库名。</param>
/// <param name="Topic">topic 名。</param>
/// <param name="Entries">批量条目（可直接传给 <c>SonnetMqStore.PublishMany</c>）。</param>
public readonly record struct MqPublishBatchFrameRequest(
    string Db,
    string Topic,
    IReadOnlyList<SonnetMqPublishEntry> Entries);

/// <summary>pull 请求帧解码结果。</summary>
/// <param name="Db">数据库名。</param>
/// <param name="Topic">topic 名。</param>
/// <param name="ConsumerGroup">消费组。</param>
/// <param name="MaxCount">最大拉取条数（0 表示服务端默认）。</param>
public readonly record struct MqPullFrameRequest(
    string Db,
    string Topic,
    string ConsumerGroup,
    int MaxCount);

/// <summary>ack 请求帧解码结果。</summary>
/// <param name="Db">数据库名。</param>
/// <param name="Topic">topic 名。</param>
/// <param name="ConsumerGroup">消费组。</param>
/// <param name="Offset">确认的 offset。</param>
public readonly record struct MqAckFrameRequest(
    string Db,
    string Topic,
    string ConsumerGroup,
    long Offset);

/// <summary>订阅起始位点模式（#236）。</summary>
public enum MqSubscribeStartMode : byte
{
    /// <summary>从消费组已提交位点开始（要求 <c>ConsumerGroup</c> 非空）。</summary>
    ConsumerGroup = 0,

    /// <summary>从显式 <c>StartOffset</c> 开始。</summary>
    ExplicitOffset = 1,

    /// <summary>从当前保留的最早消息开始。</summary>
    Earliest = 2,

    /// <summary>从当前末尾开始（仅推送订阅后到达的新消息）。</summary>
    Latest = 3,
}

/// <summary>subscribe 请求帧解码结果（#236）。</summary>
/// <param name="Db">数据库名。</param>
/// <param name="Topic">topic 名（未限定，服务端负责加 db 前缀）。</param>
/// <param name="ConsumerGroup">消费组（<see cref="MqSubscribeStartMode.ConsumerGroup"/> 模式必填，其余可空串）。</param>
/// <param name="StartMode">起始位点模式。</param>
/// <param name="StartOffset">显式起始 offset（仅 <see cref="MqSubscribeStartMode.ExplicitOffset"/> 有效）。</param>
/// <param name="BatchMax">单次推送最大条数（0 = 服务端默认）。</param>
public readonly record struct MqSubscribeFrameRequest(
    string Db,
    string Topic,
    string ConsumerGroup,
    MqSubscribeStartMode StartMode,
    long StartOffset,
    int BatchMax);
