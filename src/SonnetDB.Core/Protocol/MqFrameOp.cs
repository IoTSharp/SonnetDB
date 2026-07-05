namespace SonnetDB.Protocol;

/// <summary>
/// MQ service（<see cref="FrameService.Mq"/>）的 opcode。
/// </summary>
public enum MqFrameOp : byte
{
    /// <summary>发布单条消息。请求：db, topic, headers, payload；响应：offset。</summary>
    Publish = 1,

    /// <summary>批量发布。请求：db, topic, entries；响应：offsets。</summary>
    PublishBatch = 2,

    /// <summary>按消费组拉取。请求：db, topic, consumerGroup, maxCount；响应：messages。</summary>
    Pull = 3,

    /// <summary>确认消费。请求：db, topic, consumerGroup, offset；响应：nextOffset。</summary>
    Ack = 4,

    /// <summary>
    /// 订阅推送（#236，仅 <c>/v1/frame/stream</c> 双工端点）。请求：db, topic, consumerGroup, startMode, startOffset, batchMax；
    /// 响应（Response 位）：生效起始 offset；后续消息经带 <see cref="FrameFlags.Push"/> 位、同 streamId 的帧推送（布局同 pull 响应）。
    /// </summary>
    Subscribe = 5,

    /// <summary>取消订阅（#236，仅双工端点）。按 streamId 定位订阅；响应（Response 位）确认。</summary>
    Unsubscribe = 6,
}
