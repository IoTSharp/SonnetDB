# SonnetMQ 路线图

SonnetMQ 是 SonnetDB 生态内置的本地消息队列能力，目标是在单机、边缘网关和轻量私有化部署中提供可持久化、可确认、可追踪的队列语义。具体上层项目是否把它接成事件总线，由对应项目仓库维护。

## 设计原则

- **Kafka 的顺序日志**：Topic 以 append-only log 为核心，offset 单调递增，写路径优先顺序 I/O 与批量 flush。
- **RabbitMQ 的确认语义**：消费者组维护独立 ack offset，失败时可以从未确认位置重新拉取。
- **ZeroMQ 的轻量边界**：本地模式零第三方依赖，网络层保持薄协议，优先复用 ASP.NET Core 的高性能管道。
- **SonnetDB 的部署体验**：支持单目录与单文件模式，默认落在 SonnetDB `DataRoot/.system/mq`，便于备份、迁移和嵌入式运行。

## 第一阶段：本地模式 MVP

- 在 `src/SonnetDB.Core/Mq` 内提供 SonnetMQ 本地队列核心能力。
- 提供 `SonnetMqStore.Open`、`Publish`、`Pull`、`Ack`、`GetStats`。
- 文件格式采用 `sonnetmq.log` append-only record，消息和 ack 统一顺序记录。
- `SonnetDB` 服务宿主暴露 `/v1/db/{db}/mq/{topic}/publish|pull|ack|stats`。
- `SonnetDB.Data` 增加 `SndbMqClient`，上层应用可通过现有连接字符串调用。

## 第二阶段：吞吐与恢复优化

- 批量 publish/pull，减少 HTTP 和系统调用次数。
- 稀疏 offset index，避免大日志重启时全量扫描。
- Segment rolling：`topic/{partition}/{segment}.smqlog`，支持保留策略与 compact。
- Group commit：多个 publish 共享 flush/fsync。
- 内存中 topic ring buffer，减少 pull 热路径分配。

## 第三阶段：协议与生态

- 增加 Server-Sent Events 或长轮询订阅，降低消费者空轮询成本。
- 引入可选二进制帧协议，使用 `Span<byte>` / `PipeReader` / `PipeWriter` 处理请求。
- 提供通用 .NET 接入样例；上层项目专用 EventBus provider 在对应项目仓库规划。
- 提供迁移适配层：Kafka-like topic/offset API、RabbitMQ-like ack API。

## 第四阶段：可靠性与运维

- Topic 级保留策略：按大小、时间、已确认 offset 清理。
- 死信队列：消费者多次失败后转入 `<topic>.dlq`。
- 管理端展示 topic、lag、consumer group、磁盘占用与最近错误。
- 备份恢复与校验工具纳入 SonnetDB 统一维护命令。

## 验收清单

- 多消费者组互不影响 offset。
- 服务重启后消息和 ack 位置可恢复。
- 上层应用可用 `SonnetDB.Data` 发布和消费消息。
- ReadOnly token 不可 publish/ack，ReadWrite token 可 publish/ack/pull。
- 本地核心库不引入第三方运行时依赖，不使用 `unsafe`。
