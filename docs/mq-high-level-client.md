# SonnetMQ 高层客户端

`SndbMqClient` 在嵌入式和远程模式共用一套高层 producer/consumer builder。高层入口只组合已有的 publish、pull 和 ack 合同，不改变日志格式。

## Producer

```csharp
using var client = new SndbMqClient("Data Source=./demo-data;Mode=Embedded");
await using var producer = client
    .Producer("events")
    .MaxInFlight(4)
    .Build();

long offset = await producer.PublishAsync(new byte[] { 1, 2, 3 });
await producer.DrainAsync();
```

`MaxInFlight` 限制同时进入底层客户端的发布操作数。调用 `DrainAsync` 后不会再接受新的发布，已经取得许可的操作完成后 drain 返回；取消 drain 只取消等待，不回滚已经发送的消息。

## Consumer

```csharp
await using var consumer = client
    .Consumer("events", "workers")
    .Prefetch(32)
    .ManualAck()
    .Build();

await foreach (var delivery in consumer.PullAsync(cancellationToken))
{
    await HandleAsync(delivery.Message, cancellationToken);
    await delivery.AckAsync(cancellationToken);
}
```

`PullAsync`、`PushAsync` 和 `ReadAllAsync` 都返回有界 `IAsyncEnumerable<SndbMqDelivery>`；`Prefetch` 是一次拉取并保留在当前枚举器中的消息上限。手动确认必须显式调用 `AckAsync`，确认会沿用消费者组的连续 offset 语义。`AutoAck()` 会在消息交给枚举器前确认，适用于调用方可以接受 at-most-once 交付的场景。一个 consumer 实例同时只允许一个活动枚举器。

`DrainAsync` 会停止新的轮询，已经预取的消息仍会在枚举器继续读取时交付。取消枚举会停止当前读取；未确认消息不会被自动确认。底层当前没有 nack、redelivery、最大投递次数或 DLQ，这些属于 #325；高层入口也不提供 exactly-once 或跨节点 rebalance 承诺。

远程模式使用现有 HTTP/Frame `pull` 和 `ack` 请求进行轮询，因此远程持续消费的延迟由 `PollInterval` 与服务端请求期限共同决定。固定硬件、容量和长期稳定性仍需独立现场证据。
