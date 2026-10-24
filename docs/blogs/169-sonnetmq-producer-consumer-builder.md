---
title: SonnetDB Producer 与 Consumer Builder：Prefetch、Manual ACK 与 Drain
categories: SonnetDB
tags: SonnetMQ,.NET
draft: false
---

# SonnetDB Producer 与 Consumer Builder：Prefetch、Manual ACK 与 Drain

SonnetDB 的 SonnetMQ 高层客户端把消息发布、有限预取、手动确认和停止排空组合成一套可读的 C# Builder API，适合在嵌入式目录或远程服务中实现有界的生产者和消费者流程。

![SonnetDB Producer 与 Consumer Builder：Prefetch、Manual ACK 与 Drain](assets/covers/169-sonnetmq-producer-consumer-builder.png)

> AI 主题示意图，并非实测截图。

本文以源码提交 `faf4c9dda2892be69b906ff10d133fc417e571cd` 和公开文档提交 `5424b66672e37f49a2a5633fdde0b54a9b1f0290` 为核查基线。SonnetDB.Core 4.0.0 是当前核实到的稳定 NuGet 包；源码更晚的能力不能自动视为已包含在该包中。示例说明 API 合同，不宣称固定硬件吞吐、长时间稳定性或 exactly-once 语义。

## 1. 创建一个有界 Producer

Producer 通过 `SndbMqClient.Producer(topic)` 创建。`MaxInFlight` 是同时进入底层客户端的发布操作上限，作用是把发送并发固定在一个可解释的范围内。

```csharp
using System.Collections.Generic;
using System.Text;
using SonnetDB.Data.Mq;

using var client = new SndbMqClient("Data Source=./demo-data;Mode=Embedded");
await using var producer = client
    .Producer("factory.events")
    .MaxInFlight(4)
    .Build();

long offset = await producer.PublishAsync(
    Encoding.UTF8.GetBytes("temperature=23.5"),
    new Dictionary<string, string> { ["message-id"] = "sensor-001-0001" });

await producer.DrainAsync();
Console.WriteLine($"published offset={offset}");
```

`PublishAsync` 返回消息 offset。`PublishManyAsync` 可以一次提交一组 `SndbMqPublishEntry`，返回值仍按输入顺序对应 offset。`MaxInFlight` 限制同时进入底层客户端的发布调用数，不是吞吐量承诺。

调用 `DrainAsync` 后，Producer 不再接收新的发布，并等待已经进入发布器的调用完成。传给 drain 的取消令牌只取消等待，不会中断或回滚已经开始的发布。应用退出时应先停止上游生产，再 drain，最后释放客户端。

如果在 drain 后继续调用 `PublishAsync`，Builder 会拒绝该调用。`MaxInFlight` 必须是正整数，不能用负数或零代替“暂停”。

## 2. 用 Prefetch 和 Manual ACK 消费

Consumer 需要同时指定 Topic 和 consumer group。`Prefetch(32)` 表示一次拉取并保留在当前枚举器中的消息上限，不是整个系统的全局缓存；同一个 Consumer 实例也只允许一个活动枚举器。

```csharp
using System.Text;
using SonnetDB.Data.Mq;

using var client = new SndbMqClient("Data Source=./demo-data;Mode=Embedded");
await using var consumer = client
    .Consumer("factory.events", "quality-workers")
    .Prefetch(32)
    .ManualAck()
    .Build();

await foreach (SndbMqDelivery delivery in consumer.PullAsync())
{
    Console.WriteLine(Encoding.UTF8.GetString(delivery.Payload));
    long nextOffset = await delivery.AckAsync();
    Console.WriteLine($"acked={delivery.Offset}, next={nextOffset}");
}
```

`Prefetch(32)` 限制当前枚举器一次拉取并持有的消息数；它不是进程全局缓存上限。同一个 Consumer 实例一次只允许一个活动枚举器。

Manual ACK 使用累计位点：确认 offset N 会推进到 N 之后，并确认此前的消息。因此，上例按顺序完成处理和 ACK。若应用并行处理预取消息，应只确认已经连续完成的 offset 前缀，不能因为较新的消息先完成就跳过较早的未完成消息。

## 3. 什么时候使用 AutoAck

`AutoAck()` 会在消息交给枚举器之前推进消费者组位点。它适合消息丢失可以接受、业务只做尽力而为处理的场景，例如临时观测或不需要重放的通知。它不适合必须在处理完成后才确认的写库、扣款或外部调用流程。

```csharp
using System;
using System.Text;
using SonnetDB.Data.Mq;

using var client = new SndbMqClient("Data Source=./demo-data;Mode=Embedded");
await using var bestEffort = client
    .Consumer("telemetry.preview", "dashboard")
    .Prefetch(8)
    .AutoAck()
    .Build();
```

AutoAck 与 Manual ACK 共享同一 Topic/consumer group 位点合同，区别只在确认发生的时刻。不要因为使用了异步枚举器，就把 AutoAck 理解成 exactly-once。

## 4. 停止轮询并排空预取消息

Consumer 的 `DrainAsync` 会停止新的轮询，但已预取的消息仍会由现有枚举器依次交付。这个调用本身不等待业务处理、ACK 或枚举器退出；应用还必须等待自己的消费任务结束。不要在预取消息尚未处理时先取消枚举令牌。

```csharp
using System;
using System.Text;
using SonnetDB.Data.Mq;

using var client = new SndbMqClient("Data Source=./demo-data;Mode=Embedded");
await using var consumer = client.Consumer("factory.events", "quality-workers")
    .Prefetch(16)
    .ManualAck()
    .Build();

Task consumeTask = ConsumeAsync();
Console.WriteLine("Press Enter to stop polling.");
await Console.In.ReadLineAsync();
await consumer.DrainAsync();
await consumeTask;

async Task ConsumeAsync()
{
    await foreach (SndbMqDelivery delivery in consumer.PullAsync())
    {
        Console.WriteLine(Encoding.UTF8.GetString(delivery.Payload));
        await delivery.AckAsync();
    }
}
```

调用 drain 后，消费任务会先处理并确认已预取的消息，再因枚举结束而返回。若改为取消枚举，当前读取会停止，未确认消息不会被自动确认；重新打开同一 consumer group 时仍可能再次收到它们。业务副作用仍需使用应用自己的幂等策略。

## 5. 嵌入式与远程入口的差异

同一套 Builder API 可以使用 `Mode=Embedded` 的本地目录，也可以连接远程服务。远程持续消费复用 HTTP/Frame 的 pull 和 ack 请求，实际空轮询间隔受 `PollInterval` 与服务端请求期限共同影响。远程入口没有因为使用 Builder 就获得跨节点 rebalance、跨分区事务或更强的提交保证。

## 6. 支持范围与验证边界

- SonnetMQ 高层 API 当前是 `partial` 能力，提供有界发布、有限预取、Manual/Auto ACK、NACK 和 drain 合同。
- 当前语义是至少一次；没有 exactly-once、跨节点 rebalance 或跨模型业务事务承诺。
- `MaxInFlight`、`Prefetch` 和 `PollInterval` 是局部资源边界，不是全局吞吐或延迟保证。
- 本文没有运行固定硬件、远程生产服务、长期 soak 或真实业务副作用验证；代码片段用于说明当前源码合同。

源码与文档：[`SndbMqHighLevel.cs`](https://github.com/IoTSharp/SonnetDB/blob/faf4c9dda2892be69b906ff10d133fc417e571cd/src/SonnetDB.Data/Mq/SndbMqHighLevel.cs)、[`SndbMqClient.cs`](https://github.com/IoTSharp/SonnetDB/blob/faf4c9dda2892be69b906ff10d133fc417e571cd/src/SonnetDB.Data/Mq/SndbMqClient.cs)、[`SonnetMQ 高层客户端`](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/docs/mq-high-level-client.md)、[`SonnetMQ 路线图`](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/docs/sonnetmq-roadmap.md)。
