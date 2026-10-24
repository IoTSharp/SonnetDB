---
title: SonnetDB MQ 重投运维：NACK、DLQ、Offset Reset 与 Message ID
categories: SonnetDB
tags: SonnetMQ,.NET
draft: false
---

# SonnetDB MQ 重投运维：NACK、DLQ、Offset Reset 与 Message ID

SonnetDB 的 SonnetMQ 提供了可追踪的 NACK、死信 Topic、消费者组 offset 重置和有限 message-id 去重合同，帮助运维人员处理毒消息、回放起点和重复发布风险，同时明确 retention 会改变可回放边界。

![SonnetDB MQ 重投运维：NACK、DLQ、Offset Reset 与 Message ID](https://img2024.cnblogs.com/blog/1725297/202610/1725297-20261023104507181-1200119925.png)

> AI 主题示意图，并非实测截图。

本文核查基线为源码提交 `faf4c9dda2892be69b906ff10d133fc417e571cd`、公开文档提交 `5424b66672e37f49a2a5633fdde0b54a9b1f0290`。SonnetDB.Core 4.0.0 是当前核实到的稳定 NuGet 包；更晚源码能力不能自动视为包含在该包中。下文展示源码/API 合同，未将固定硬件或长稳运行当作已完成的 MQ 质量证据。

## 1. NACK 只作用于当前消费位置

高层消费者处理失败时调用 `delivery.NackAsync(reason)`，底层客户端也提供 `NackAsync(topic, group, offset, reason)`。服务端要求 offset 必须正好等于消费者组当前待消费位置，不能跳过前面的消息。

```csharp
using System;
using SonnetDB.Data.Mq;

using var client = new SndbMqClient("Data Source=./demo-data;Mode=Embedded");
await using var consumer = client.Consumer("factory.events", "quality-workers")
    .ManualAck()
    .Build();

await foreach (SndbMqDelivery delivery in consumer.PullAsync())
{
    if (delivery.Headers.TryGetValue("validation-error", out string? reason))
    {
        SndbMqNackResult result = await delivery.NackAsync(reason);
        Console.WriteLine($"offset={delivery.Offset}, attempt={result.DeliveryAttempt}, "
            + $"deadLettered={result.DeadLettered}");
        continue;
    }

    Console.WriteLine($"processing offset={delivery.Offset}");
    await delivery.AckAsync();
}
```

代码用 `validation-error` 消息头表示应用校验失败，以展示 NACK 分支；实际使用时应将这个条件替换为自己的业务校验结果。成功处理后再 ACK，可避免把 ACK 请求的异常误当成业务处理失败并立即 NACK。

每次 NACK 都会记录投递次数和原因。低于配置的最大投递次数时，原消息会留在原 Topic 的当前消费位置，之后可再次拉取；达到 `SonnetMqOptions.MaxDeliveryAttempts` 时才会发布到 `<topic>.dlq`（默认后缀为 `.dlq`），并推进原 consumer group 的 offset。默认最大投递次数是 5；设置为 0 或负数时不会自动转入 DLQ。死信副本保留原 Topic、原 offset、投递次数和拒绝原因等消息头，供人工调查。

## 2. DLQ 是隔离结果，不是自动修复

达到 NACK 次数上限并进入 DLQ 后，原 Topic 的消费位点才会前进。应用应把 DLQ 当成隔离队列，先分析失败原因、修复处理器或准备补偿，再决定是否把消息复制到新的重放 Topic。不要把自动重试次数写成 exactly-once。

`MaxDeliveryAttempts <= 0` 时不会自动转入 DLQ，只记录 NACK；启用最大次数时必须保留非空 `DeadLetterTopicSuffix`。这两个配置是实例级合同，修改后应与当前队列目录和恢复记录一起审阅。

## 3. 按目标重置消费者组 offset

`ResetOffsetAsync` 支持四种目标：

| 模式 | 含义 |
|---|---|
| `Earliest` | 当前 retention 仍保留的最早 offset |
| `Latest` | Topic 当前末尾，也就是下一条新消息 |
| `Time` | 第一个时间戳大于等于指定 UTC ticks 的消息 |
| `Explicit` | 指定 offset，最终会裁剪到 retention 边界 |

```csharp
using System;
using SonnetDB.Data.Mq;

using var client = new SndbMqClient("Data Source=./demo-data;Mode=Embedded");
long replayFrom = await client.ResetOffsetAsync(
    "factory.events",
    "quality-workers",
    SndbMqOffsetResetMode.Explicit,
    value: 120);

Console.WriteLine($"consumer group will resume at {replayFrom}");
```

重置本身会写入队列日志并在重开后恢复。`Latest` 适合跳过旧积压，`Earliest` 适合从当前保留窗口开始核查，`Time` 适合按事件时间切段，`Explicit` 适合已经保存 offset 的人工回放。不要把“请求成功”解释成消息一定仍存在；如果目标早于 retention，服务端会把它裁剪到可用边界。

## 4. Message ID 去重是有界的

发布消息时可以设置 `message-id` 头。`MessageIdDeduplicationWindow` 是按消息条数计算的窗口，默认保留最近 100,000 条带 ID 消息，不是按时间保留。ID 还须非空且不超过 256 个 UTF-8 字节。命中窗口时返回原消息 offset，不追加新的日志记录；重开后可从持久消息尾部恢复有限索引。对应消息被 retention 清理后，旧 ID 不再命中。

```csharp
using System;
using System.Collections.Generic;
using System.Text;
using SonnetDB.Data.Mq;

using var client = new SndbMqClient("Data Source=./demo-data;Mode=Embedded");
var headers = new Dictionary<string, string>
{
    ["message-id"] = "order-20261022-0007"
};

long first = await client.PublishAsync(
    "factory.events",
    Encoding.UTF8.GetBytes("order-created"),
    headers);

long duplicate = await client.PublishAsync(
    "factory.events",
    Encoding.UTF8.GetBytes("order-created"),
    headers);

Console.WriteLine($"first={first}, duplicate={duplicate}");
```

在窗口仍保留时，`duplicate` 会复用 `first` 的 offset。窗口设置为 `0` 会关闭去重。此行为仅去重 SonnetMQ 中相同 header ID 的日志追加，不会去重消费者的数据库写入、HTTP 调用或其它业务副作用；这些仍需要业务自己的幂等键和结果记录。

## 5. 一套可审计的毒消息流程

可以按以下顺序处理一条持续失败的消息：

1. 在 Manual ACK 模式取得当前 delivery，记录 Topic、group、offset 和 message-id。
2. 处理失败时调用一次 NACK，保存 `DeliveryAttempt`、原因和返回的 `NextOffset`。
3. 达到最大次数后，核对 `<topic>.dlq` 中的原始头信息，确认原 Topic、原 offset 和拒绝原因。
4. 修复消费者后，在新的重放 Topic 或受控 group 上按明确 offset 回放；不要直接回写生产 Topic。
5. 如果需要从时间点重新读取，先记录 retention 边界，再用 `Time` 或 `Explicit` 重置并保存返回 offset。

这条流程把原始消费、隔离、修复和回放分成可检查的步骤，也避免把未知外部结果自动重放成重复业务操作。

## 6. 支持范围与恢复边界

- SonnetMQ 当前为 `partial` 能力，提供 NACK、有限次数 DLQ、四种 offset reset 和有界 message-id 去重。
- 没有 exactly-once、跨节点 rebalance 或跨模型业务事务承诺。
- retention 会裁剪可回放起点；DLQ 和原 Topic 的保留策略需要分别核对。
- 单库数据库备份不会自动覆盖实例级 `.system/mq`、consumer offset、DLQ 和队列日志；实例快照是独立恢复流程。
- 本文没有执行真实远程服务、固定硬件、断电或长期 soak 验证，示例只对应当前源码和文档合同。

源码与文档：[`SndbMqClient.cs`](https://github.com/IoTSharp/SonnetDB/blob/faf4c9dda2892be69b906ff10d133fc417e571cd/src/SonnetDB.Data/Mq/SndbMqClient.cs)、[`SonnetMqStore.cs`](https://github.com/IoTSharp/SonnetDB/blob/faf4c9dda2892be69b906ff10d133fc417e571cd/src/SonnetDB.Core/Mq/SonnetMqStore.cs)、[`SonnetMqOptions.cs`](https://github.com/IoTSharp/SonnetDB/blob/faf4c9dda2892be69b906ff10d133fc417e571cd/src/SonnetDB.Core/Mq/SonnetMqOptions.cs)、[`SonnetMQ 高层客户端`](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/docs/mq-high-level-client.md)、[`SonnetMQ 路线图`](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/docs/sonnetmq-roadmap.md)。
