# 2026-10-01 路线图并行实施记录

本轮从当前路线图中选择四个能够在仓内形成代码、测试和合同证据闭环的切片，使用三个并行智能体加主智能体实现。M20 scheduled、M35 真实模型质量/容量、M36 远程 parity、固定硬件和发布门禁继续保留为外部验证，不由本地测试冒充完成。

## 本轮交付

| 路线图 | 本地交付 | 验证与边界 |
|---|---|---|
| M43 CDC 源捕获 | `CdcDocumentSourceCaptureScheduler` 提供显式、有界、可取消的连续批次调度；保留单源、单实体、单分区 spool 合同。 | CDC 命名空间总计 69/69（其中调度器 4/4）。没有实现远程拓扑、多分区协调或自动后台线程。见 [CDC 调度证据](m43-cdc-capture-scheduler-20261001.md)。 |
| M42 SQL-002 | 关系表 `INSERT/UPDATE/DELETE`（含 `RETURNING`、`INSERT SELECT`、`ON CONFLICT`）在 mutation 进入事务缓冲或提交前计入累计行数/估算字节预算；预算模式禁用 DELETE 快速路径。 | `SqlMaterializationBudgetTests` 41/41，DML/INSERT SELECT 回归 20/20。预算是保守准入，不代表 CLR heap、首行延迟或固定硬件性能门禁。见 [M42 合同](../benchmarks/m42-sql-result-bounds.md)。 |
| M43 持久订阅 | 新增 `MaxDeliveryAttempts`、达到上限的可恢复异常和 `GetStatusAsync` 状态快照，持久化 pending、spool bytes、in-flight attempt、最早事件和 backlog age；旧状态格式可校验迁移。 | Streaming 定向 54/54；ACK 在 attempt 上限后仍可回收。只覆盖本地文件锁和至少一次投递，不代表远程 parity、分布式租约、DLQ 或 exactly-once。 |
| M35 RAG 恢复 | 真子进程在第二个 embedding 调用中被 `Process.Kill(true)` 终止；重开后复用已 durable 的第一个 chunk，只重新生成未完成 chunk，再发布完整 generation。 | 新增 CrashTests 1/1。只证明本机进程终止下的 staging/WAL 恢复，不代表掉电、真实模型质量或固定容量。见 [RAG 硬杀证据](m35-rag-hard-kill-20261001.md)。 |

## 统一验证

本轮源码没有新增第三方运行时依赖、unsafe 或数据库二进制格式变更；文件订阅状态 JSON 升为格式 2，并通过旧格式哈希校验后迁移到当前字段。新增公开类型均带中文 XML 文档，JSON 变更继续使用 source-generated context。已执行以下定向检查：

```powershell
dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj --no-restore --filter FullyQualifiedName~SonnetDB.Core.Tests.Cdc
dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj --no-restore --filter FullyQualifiedName~SonnetDB.Core.Tests.Streaming
dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj --no-restore --filter FullyQualifiedName~SonnetDB.Core.Tests.Sql.SqlMaterializationBudgetTests
dotnet test tests/SonnetDB.CrashTests/SonnetDB.CrashTests.csproj --no-build --filter FullyQualifiedName~M35_RagWriter_HardKillDuringEmbedding
```

完整 solution 构建、全量测试、NativeAOT、远程 parity、nightly 和固定目标机仍需在最终合并前后按仓库门禁执行；本记录只汇总本地闭环结果。
