# SonnetMQ 路线图

SonnetMQ 是 SonnetDB 生态内置的本地消息队列能力，目标是在单机、边缘网关和轻量私有化部署中提供可持久化、可确认、可追踪的队列语义。具体上层项目是否把它接成事件总线，由对应项目仓库维护。

状态图例：`✅` 已完成，`🟡` 局部完成/外部证据待补，`🚧` 进行中，`📋` 未开始，`⏳` 待验证，`❌` 失败/阻断，`⏸️` 暂停，`❓` 未知。

2026-11-11核查：本地队列、批量/组提交、实例快照及离线CLI已有实现；下一步优先明确ACK与OS flush/fsync边界，并完成实例MQ、数据库、身份配置和外部对象的组合恢复。默认OS flush不能被写成物理掉电已验，至少一次也不等于任意业务副作用exactly-once。全仓顺序见[根路线](../ROADMAP.md#当前推进顺序)，证据见[核心核查](audits/roadmap-audit-20261111/core.md)。

## 设计原则

- **Kafka 的顺序日志**：Topic 以 append-only log 为核心，offset 单调递增，写路径优先顺序 I/O 与批量 flush。
- **RabbitMQ 的确认语义**：消费者组维护独立 ack offset，失败时可以从未确认位置重新拉取。
- **ZeroMQ 的轻量边界**：本地模式零第三方依赖，网络层保持薄协议，优先复用 ASP.NET Core 的高性能管道。
- **SonnetDB 的部署体验**：支持单目录与单文件模式，默认落在 SonnetDB `DataRoot/.system/mq`，便于备份、迁移和嵌入式运行。

实例目录模式的离线 `sndb mq snapshot/inspect/restore`、生命周期 owner、范围与限制见 [实例 MQ 恢复手册](sonnetmq-instance-recovery.md)；单库备份及 WorkBuddy/生产验收分别保持边界。

## 第一阶段：本地模式 MVP ✅

- ✅ 在 `src/SonnetDB.Core/Mq` 内提供 SonnetMQ 本地队列核心能力。
- ✅ 提供 `SonnetMqStore.Open`、`Publish`、`Pull`、`Ack`、`GetStats`。
- ✅ 文件格式采用 append-only record，消息和 ack 统一顺序记录。
- ✅ `SonnetDB` 服务宿主暴露 `/v1/db/{db}/mq/{topic}/publish|pull|ack|stats`。
- ✅ `SonnetDB.Data` 增加 `SndbMqClient`，上层应用可通过现有连接字符串调用。

## 第二阶段：吞吐与恢复优化 🟡

- ✅ 批量 publish/pull，减少 HTTP 和系统调用次数。
- ✅ 稀疏 offset index，避免大日志重启时全量扫描。
- ✅ Segment rolling：topic 分段文件支持保留策略与 compact。
- ✅ Group commit：多个 publish 共享 flush/fsync。
- 📋 内存中 topic ring buffer，减少 pull 热路径分配。

## 第三阶段：协议与生态 🟡

- 🟡 增加 Server-Sent Events 或长轮询订阅，降低消费者空轮询成本；已有订阅/远程入口，完整推送语义仍待验。
- ✅ 引入可选二进制帧协议，使用安全的 span/帧编解码处理请求。
- ✅ 提供通用 .NET 接入样例；上层项目专用 EventBus provider 在对应项目仓库规划。
- 📋 提供迁移适配层：Kafka-like topic/offset API、RabbitMQ-like ack API。

## 第四阶段：可靠性与运维 🚧

- ✅ Topic 级保留策略：按大小、时间、已确认 offset 清理。
- ✅ 死信队列：消费者多次失败后转入 `<topic>.dlq`。
- 🟡 `message-id`有界去重、实例级日志/consumer offset快照与离线snapshot/inspect/restore已有实现；完整实例组合、跨进程/长期与物理掉电验收仍分项待补。
- 🚧 管理端展示 topic、lag、consumer group、磁盘占用与最近错误。
- ✅ 单库备份继续保持与 Server 实例 MQ 分离；实例快照恢复由 SonnetMQ 合同独立维护，完整实例门禁仍单列。

## 验收清单

- ✅ 多消费者组互不影响 offset。
- ✅ 服务重启后消息和 ack 位置可恢复。
- 🟡 快照恢复后消息头、consumer offset 和后续发布保持一致；实例级恢复证据仍待补。
- ✅ 上层应用可用 `SonnetDB.Data` 发布和消费消息。
- ✅ ReadOnly token 不可 publish/ack，ReadWrite token 可 publish/ack/pull。
- ✅ 本地核心库不引入第三方运行时依赖，不使用 `unsafe`。
