# SonnetDB 与 CAP 集成

更新日期：2026-10-09（Asia/Shanghai）。归属 M43 生态适配。实现位于 [`extensions/SonnetDB.CAP`](../../extensions/SonnetDB.CAP/SonnetDB.CAP.csproj)，调用方式见 [PackageReadme](../../extensions/SonnetDB.CAP/PackageReadme.md)。

## 方案和依赖

按用户最新要求，存储与传输合并在一个项目和 NuGet 包 `SonnetDB.CAP`，注册入口保持独立：`UseSonnetDbDocumentStorage` 与 `UseSonnetDbTransport`。它们分别实现 CAP 的 `IDataStorage` / `IStorageInitializer` / `IMonitoringApi` 与 `ITransport` / `IConsumerClientFactory` / `IConsumerClient`，允许搭配其它 CAP 插件。

两条路径都通过 `src/SonnetDB.Data` 的 SDK 实现嵌入式和远程访问；不直接把 Core 当作远程客户端，不重复创建 HTTP 文档/MQ 客户端。NuGet 依赖为 `DotNetCore.CAP` 与 `SonnetDB`（Data 包），后者依赖 Core。因此本包包含 Data 的 ADO.NET Provider 程序集，但实际实现只调用 Document/MQ API，不依赖 EF Core、Dapper、Newtonsoft.Json 或 MongoDB Driver。

ADO.NET 存储与 EF Core 事务桥接不在本次交付中；不能因为 Data 包提供 ADO API 就将文档存储称为 SQL 存储。

## 上游接口依据

按 CAP [v10.0.2](https://github.com/dotnetcore/CAP/releases/tag/v10.0.2) 的源代码实现，源码提交 `e52b8508e54cdb7a9ce7f9fec03d9ea8ad2710fb`。只读参考下载位于忽略目录 `artifacts/cap-analysis-20261009/upstream`。

- `IDataStorage` 定义 Outbox/Inbox 保存、状态、重试、延迟调度、清理和存储锁；初始化和监控另有接口。
- `ITransport` / `IConsumerClientFactory` / `IConsumerClient` 定义发送、订阅、拉取、确认和拒绝。CAP 正常消费入口在 Inbox 保存及 enqueue 后确认，持久化失败时调用 Reject。
- 官方 MongoDB 插件参考了文档状态、索引、锁和事务设计，但实际依赖 MongoDB Driver、session、跨 collection transaction 等，不能直接连接 SonnetDB。
- 上游默认 `GroupConcurrent=0` 表示未显式限制，适配允许 0/1；broker 拉取逐条进行，业务 dispatcher 的并发单独管理。

## 原生文档存储

SonnetDB 具备 MongoDB-like 文档 CRUD、过滤、排序、投影、索引、原子 FindOneAndUpdate 和单 collection ordered BulkWrite，没有 MongoDB wire protocol、官方 Driver 直连或 MongoDB session/跨 collection 事务。参见 [文档能力矩阵](../document-store.md)、[差异目录](../document-mongodb-gap.json) 与 [迁移指南](../mongodb-migration.md)。

本实现使用一个 collection（默认 `cap`）保存 published、received 和 lock 文档。消息带格式版本、CAP 版本和状态 bucket；ID 分别以 `cap:p:`、`cap:r:`、`cap:lock:` 开头。业务记录使用 `business:` 前缀，查询同时校验内部 ID 范围，避免业务字段恰好包含 bucket 时混入消息。

- 初始化幂等创建 collection 和普通 `$.bucket` path 索引。新增 Core `EnsureIndex`、Data `EnsureIndexAsync` 与 Server `/documents/{collection}/indexes/ensure`；同名不同定义明确冲突，Server 使用数据库写权限与 source-generated DTO。
- 状态替换使用 ExpectedVersion，冲突/错误/未提交结果不能视为成功。重试保留 CAP 版本隔离和次数/时间约束；监控按 128 条游标扫描，页大小最多 1000、跳过最多 10000，不全量载入消息正文。
- 延迟与旧 Queued 消息通过单 collection 批事务传给 scheduler；callback 失败不提交状态。过期清理只删除到期 Succeeded 或重试已耗尽 Failed，保留待处理状态和可重试 Failed。
- 锁通过条件 FindOneAndUpdate 获取；获取仅允许过期锁，释放核对 owner，续锁核对 owner 和未到期。不是分布式 fencing token，时钟漂移与任意业务副作用仍由应用处理。

## 业务 Outbox 事务

`publisher.BeginSonnetDbDocumentTransaction()` 将业务文档操作和 CAP 消息暂存到同一 ordered BulkWrite。接口固定目标 collection 和业务 ID 前缀，提交成功后才调用 CAP Flush；回滚或冲突不会投递。

单批最多 1000 个操作，另受 canonical payload 16 MiB 和 WAL 预算限制。remote requestId 在提交开始后保持不变，未知结果重试必须重用原批次；开始提交后禁止新增操作。业务查询通过相同 Data 客户端与 `GetBusinessDocumentId` 读取。

这不提供跨 collection、关系表与文档、MQ 与文档、或外部副作用的原子性。独立业务集合与 Outbox 分别保存仍有崩溃窗口；未知 SQL/EF/MongoDB transaction 对象明确拒绝。跨 collection 事务需要独立引擎设计和验收，不在本包中假装实现。

## MQ 传输与消费组缺口

传输使用 source-generated 信封保存原始 CAP headers 和 body；topic/group 通过命名空间与 SHA-256 稳定映射。单条默认预算 1 MiB，可配置最多 16 MiB；超限返回 CAP failure，不发布消息。

现有 Ack 为累计 offset，缺少消费者分配租约，因此逐条持久 Inbox 后确认；Reject 保持 offset，不用有限 Nack 次数跳过未持久化消息。要求 ConsumerThreadCount=1、GroupConcurrent 0/1。同一适配连接内拒绝重复活动组；同组跨进程的单活动实例由部署保证。暂不支持通配符订阅。

补齐消费组持久登记：Core `EnsureConsumerGroup`、Data `EnsureConsumerGroupAsync` 和 Server `/mq/{topic}/ensure-group`。新组从最早保留 offset 开始，已有组不回退、不推进；复用现有 ACK WAL 保存 next offset，无二进制布局变更。CAP 拉取前登记，慢组即使没有 ACK 也参与回收边界，重启后继续保留待处理消息。新组无法找回已回收历史；废弃登记组可能长期阻止回收，需要运维处理。

Server MQ 是实例 `.system/mq` 边界，db 名称仍参与 topic 隔离。单数据库备份不能替代实例 MQ 备份。文档提交与 broker 发布分开，由 CAP 重试衔接；message-id 去重窗口不等同永久去重或业务 exactly-once。

## 验证与发布

`SonnetDB.CAP.Tests` 使用真实 SDK、嵌入式文件引擎和真实 Kestrel Server。覆盖消息状态与重试、初始化/索引冲突/权限、锁竞争/续期、调度失败回滚、终态清理、跨页监控、CAP 版本隔离、业务+Outbox 提交/回滚/冲突、逐条确认/重投、多消费组、停止释放、信封预算、文件重开/Server 重启及完整 CAP 发布/订阅。

发布脚本、artifact validator 和其合同测试将 `SonnetDB.CAP` 纳入八个 NuGet 包清单；候选包包含 README/XML 文档。所有自有 JSON source-generated，生产构建保留 trim/AOT 分析。CAP 上游 serializer/订阅发现有反射边界，分析器通过不证明整个 CAP 宿主 NativeAOT 可发布。

定向测试、候选包和格式门禁分别记录实际结果；正式发布需要明确版本号并通过现有平台发布门禁。强杀/掉电、跨进程消费竞争、固定硬件性能/长稳、Linux 平台和完整发行流水线保持独立验收，不能由本机测试升级为已完成。

本次 34 项专属测试、296 项 Core 回归、15 项 Server 回归通过，单包候选 `4.1.0` 已生成；完整发行因 Core 对 3.0.1 的 API 兼容检查失败而阻塞，GitHub CLI 凭据也已失效，未正式发布。覆盖率、失败记录及继续顺序见[本地验收报告](../audits/cap-integration-20261009.md)。
