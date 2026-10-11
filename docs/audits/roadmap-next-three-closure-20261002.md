# 2026-10-02 下一轮并行实现与验证

本轮在 `codex/roadmap-next-20261002` 上完成三个独立缺口：M42 文档直接 `ORDER BY` 物化预算、M43 持久订阅任务目录、M43 持久会话 COUNT 窗口。任务目录实现提交为本地 `48053e69`；本轮没有推送、发布、部署或合并到远程。

## 交付

- `DocumentSqlExecutor` 的预算路径允许单列结果名排序。排序输入在过滤后逐行调用 `SqlRowRetentionBudget.RetainForExecution`，完整输入扫描完成后才排序并应用 `OFFSET/LIMIT`；因此 `LIMIT 1` 仍受全量排序工作集行数/字节预算约束。TTL 使用固定查询时刻，过期候选只推进游标；多列排序、聚合、窗口、JOIN、去重和嵌套来源继续在读取前拒绝。
- `FileStreamingTaskCatalog` 保存任务定义和目录 revision，使用 source-generated JSON、SHA-256、原子替换、容量边界和单执行者 lease；注册/更新/删除使用 revision CAS，分页游标绑定 revision，路径必须位于目录根下。状态、暂停和恢复均通过真实 `FileStreamingSubscription` 打开并读取。
- `FileStreamingSessionWindowAggregator` 保存版本化会话成员、位点、watermark、最后批次身份和 SHA-256。相邻时间差等于 gap 时仍桥接；会话在 `end + gap + allowed lateness <= watermark` 时关闭；乱序可桥接未关闭会话，关闭后迟到按 Drop/Reject 处理，回收边界防止复活。批次完整校验并在单次提交后 ACK，重投按 delivery ID/hash 去重。

## 验证

| 检查 | 结果 |
| --- | --- |
| PowerShell | `C:\Program Files\PowerShell\7\pwsh.exe` 7.6.6 |
| Core build | `dotnet build tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj --no-restore -p:BuildInParallel=false -v:q`，0 warning / 0 error |
| Release build | `dotnet build SonnetDB.slnx -c Release --no-restore -p:BuildInParallel=false -v:minimal`，0 warning / 0 error |
| Native AOT | `dotnet publish src/SonnetDB/SonnetDB.csproj -c Release -r win-x64 --self-contained true -p:SonnetDbPublishAot=true`，成功；输出已清理。先行的 self-contained=false 配置被 SDK 以 NETSDK1102 拒绝，不计入成功证据。 |
| 定向回归 | 84 / 84 通过：Document ORDER BY 7、既有 Document/TTL 预算 66、任务目录 6、会话窗口 5 |
| Document 专项 | `DocumentOrderByMaterializationBudgetTests` 7/7；`DocumentSelectMaterializationBudgetTests` 与 `DocumentTtlSelectMaterializationBudgetTests` 合计 66/66 |
| 任务目录专项 | 6/6，覆盖重开、重复身份、revision/CAS、分页、根边界、坏 JSON fail closed、单执行者 lease、真实订阅读取/暂停/恢复 |
| 会话窗口专项 | 5/5，覆盖等号 gap、乱序桥接、Reject 全批原子性、关闭回收防复活、真实订阅提交后未 ACK 重开去重、合法 SHA 结构损坏拒绝 |
| 格式/差异 | 相关文件 `dotnet format ... --verify-no-changes --severity warn --exclude extensions/` 与 `git diff --check` 通过 |

## 边界

本轮没有执行远程 parity、固定硬件容量、掉电、长稳、真实业务副作用事务或生产门禁。任务目录和会话窗口是本地单执行者文件合同；不提供分布式租约、自动后台重发、远程调度或 exactly-once 业务事务。文档排序预算限制 SQL 结果物化累计行/估算字节，底层 KV 页、单值解析、整体 CLR heap 和远程请求仍有独立边界。
