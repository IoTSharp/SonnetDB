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
}
