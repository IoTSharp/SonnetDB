---
title: SonnetMQ 与流处理：Topic、ACK、DLQ 以及恢复边界
categories: SonnetDB,SonnetMQ,流处理
draft: false
---

SonnetMQ 是 SonnetDB 中的持久消息模型。它与 measurement 一样属于数据库的逻辑资源树，但物理日志位于实例级 `.system/mq`。理解这两个作用域，是设计备份和恢复流程的前提。

## Producer 与 Consumer

Producer 向 Topic 追加消息，Consumer 通过 prefetch、手动或自动 ACK 读取。手动确认适合需要在业务处理完成后再推进位点的场景；`nack` 可以进入重试或死信队列（DLQ）。客户端可以按消息 ID 做有限窗口的去重，也可以重置 offset 重新回放。

```text
publish db/factory/m/temperature
consume topic=alerts group=maintenance ack=manual
process -> ack
process failed -> nack -> retry/DLQ
```

当前合同是至少一次。它没有宣称 exactly-once、跨节点 rebalance、跨分区业务事务或 broker 集群。应用副作用必须使用幂等键和自己的事务边界处理。

## 持久订阅与窗口

流处理层提供本地文件订阅、tumbling/sliding/session 窗口、watermark、checkpoint、任务目录和有界 DLQ。提交位点与 ACK 的顺序是恢复关键：在收到 ACK 前崩溃，重开时允许重放；未知目标发布结果则 fail closed，交给人工恢复，不能自动猜测已成功。

CDC→stream 桥接会先写入 outbox/receipt，再确认 source。恢复时必须先恢复 pending bridge intent，核对 source event、target receipt 和 checkpoint，确认一致后才能继续消费。跨目录业务事务、远程 transport 和离线冲突解决仍是未闭合边界。

## 备份为什么要分层

数据库备份覆盖数据库目录中的模型与 catalog；实例级 `.system/mq` 的快照和任务状态需要单独的实例恢复流程。把单库备份写成“完整 MQ 灾备”会遗漏 offset、consumer、DLQ 和物理日志，因此发布文章和运维手册都必须分别说明 RPO/RTO 与非原子窗口。

参考：[`mq-high-level-client.md`](../mq-high-level-client.md)、[`streaming-subscription-contract.md`](../streaming-subscription-contract.md)、[`m43-subscription-dead-letter.md`](../m43-subscription-dead-letter.md)、[`m43-cdc-streaming-bridge.md`](../m43-cdc-streaming-bridge.md)。
