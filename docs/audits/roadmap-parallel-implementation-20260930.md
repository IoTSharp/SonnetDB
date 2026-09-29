# 2026-09-30 总里程碑并行实施记录

本轮依照 `docs/roadmap-total-milestone.md`，由 CDC、持久订阅、SQL 三个智能体并行实现，主智能体负责集成、证据汇总、资料草稿和路线图。交叉审查分别覆盖证据脚本、CDC 衔接和持久订阅恢复。

**范围：本地代码与文档切片；M42 / M43 整体仍未闭环。** 实施前基线 HEAD 为 `46dd3a5ec4a7d2e3ae7b07b139289f34c767345f`；本轮实现从该基线起提交到 `codex/m42-m43-local-slices-20260930`。基线 SHA 不能用作本轮新源码的 release attestation，也没有由本次本地验证推断新的线上 nightly、发布或外部收录结果。

## 实现范围

| 路线图 | 本轮交付 | 合同与保留边界 |
|---|---|---|
| M43 #386 | `CdcSnapshotReplica`：快照页序号恢复、准确总行数切换、连续增量原子物化与位点、有界读取 | 单源/实体/schema/分区；固定读视图和源捕获由调用方提供。[CDC 合同](../cdc-contract.md)、[专项记录](m43-cdc-snapshot-20260930.md)。 |
| M43 #392 | `FileStreamingSubscription`：文件事件与未确认批次、watermark、条件 checkpoint / spool ACK 协调、可取消背压 | 本机单执行者、至少一次；窗口聚合、远程投递、任务运维和业务副作用事务未交付。[订阅合同](../streaming-subscription-contract.md)、[专项记录](m43-streaming-persistent-subscription-2026-09-30.md)。 |
| M43 #390/#395 | 4 项真实 Child 进程强杀、重开与数据对账 | 覆盖快照页、物化后未 ACK、容量为 1 的尾批回收、订阅稳定投递 ID；不替代掉电或远程故障。 |
| M42 SQL-002 | opt-in `MaxMaterializedRows` / `MaxMaterializedBytes`，关系 SELECT 根调用累计计费与超限拒绝 | 支持只读关系查询与 EXPLAIN；DML/其它模型提前拒绝。累计保守估算不是 CLR heap 精确上限。[执行预算合同](../benchmarks/m42-sql-result-bounds.md)。 |
| M43 #397 | 八组发布证据清单、既有 verifier 调用、原始结果/摘要和 `PASS` / `NOT_READY` / `DEFERRED` 汇总 | 尚无组合 verifier 的质量/成本、安装、M41/M42 长稳项保持延期；不改变正式发布政策。[工具合同](../m43-release-evidence.md)。 |
| M43 #398/#401 | 中英文目录资料草稿、已有样例与基准复现索引 | [资料包](../ecosystem-directory-dossier.md)为 `DRAFT_NOT_SUBMITTED`；[复现索引](../ecosystem-reproduction-index.md)不表示命令已执行，外部提交为 `NOT_SUBMITTED`。 |

新增生产类型保持中文公开 XML 文档、Safe-only 和 source-generated JSON；没有新增第三方运行时依赖，没有改变已发布的数据库、CDC spool 或 Frame 二进制布局。接收端使用独立版本 1 状态格式。

## 交叉审查与修复

发布汇总脚本的四项问题已修复并加入合同回归：

- M19 必须同时绑定候选 SHA、`ci.repository`、`ci.runUrl` 和清单 run URL，其他仓库相同 SHA 的 fixture 不能成为目标仓库 `PASS`。
- 规范化现存路径、拒绝符号链接/目录联接/硬链接，以及覆盖或包含原始输入的输出目录；最终结果刷盘后原子替换。
- M19 文件须存在且匹配 verifier 保存的 SHA-256；汇总写出前复核所有保留文件，验证后修改或删除产生 `NOT_READY`。
- M40 先检查干净候选 HEAD，再从源码重新构建 evaluator；构建失败不能运行旧 bin，运行后再次核对工作树。M40 调度合同使用 mock 命令，仅验证 adapter 行为。

CDC 衔接示例在回放后续事件前回收已物化但未确认的前缀，覆盖最后一条事件、无后续批次且 spool 容量为 1 的恢复边界。真实强杀场景已核对恢复后能继续追加。

持久订阅重开使用内部带令牌的 spool 恢复路径，metadata 和帧扫描在读取/解析边界检查取消及操作超时，原公开 spool 构造签名保持不变。同步文件系统调用不能被抢占，取消是协作检查；确定性测试在扫描同步点取消并验证 lease 释放、原始事件保留和重开重试。

## 已执行验证

所有生成物留在本地 `artifacts/m43-local-20260930`，不提交 build artifacts。下表逐项记录实际执行，定向与集成结果存在重叠，不相加作为独立测试数。

| 检查 | 状态 | 原始结果 |
|---|---|---|
| CDC 定向 Release 回归 | PASS_LOCAL_ONLY | `cdc.trx`：44/44，0 失败、0 跳过；包括快照接收端 19 项。 |
| Streaming 定向 Release 回归 | PASS_LOCAL_ONLY | `streaming.trx`：51/51，0 失败、0 跳过；新文件入口 32 项。 |
| SQL 定向 Release /warnaserror | PASS_LOCAL_ONLY | `sql-budget-retest.trx`：181/181，0 失败、0 跳过；新增预算 36 项。 |
| SQL / CDC / Streaming 集成初轮 | PASS_LOCAL_ONLY | `integration.trx`：1746/1746，0 失败、0 跳过；随后恢复增强由下行专项覆盖。 |
| CDC / Streaming 恢复增强后完整回归 | PASS_LOCAL_ONLY | `cdc-streaming-final.trx`：99/99，0 失败、0 跳过；含新增尾批 ACK 1 项及恢复取消 3 项。 |
| 真实子进程强杀 | PASS_LOCAL_ONLY | `crash.trx`：4/4，0 失败、0 跳过；恢复增强后再验，最终结果覆盖最初 3 项的结果。 |
| 发布汇总脚本合同 | PASS_LOCAL_ONLY | `m43-script-contracts.log`：30 项合成合同通过；没有建立生产发布证据。 |
| 十四能力索引校验 | PASS_LOCAL_ONLY | `eng/validate-fourteen-capability-index.ps1` 校验 14 项入口、证据、成熟度和旅程映射通过。 |
| 整体 Release 构建 | PASS_LOCAL_ONLY | `release-build.binlog`：首次正常 restore 后构建通过；恢复增强后的最终 `dotnet build SonnetDB.slnx -c Release --no-restore /warnaserror` 再验，0 警告、0 错误；生产项目默认 AOT/trim 分析开启。 |
| 本地八组模板汇总 | DEFERRED | `release-evidence.json`：0 PASS、0 NOT_READY、8 DEFERRED；候选版本参数 `4.0.0` 不据此证明发布存在。 |
| NativeAOT 发布 / 跨架构执行 | NOT_READY | 本轮未执行；AOT 分析成功不能替代原生发布及运行。 |

SQL 首轮 `sql-budget.trx` 为 172/181；修正新测试夹具后最终 181/181，原始失败 TRX 保留。CDC 首轮两项异常精确类型断言失败，修正派生异常断言和公开 API 预取消检查后完整回归通过。恢复扫描新测试首轮编译触发 xUnit1031，未进入测试；改用确定同步点和异步清理后最终 99/99。CrashTests 首次 `--no-restore` 零输出退出且没有 TRX，不计成功；正常 restore 后构建真实 Child 并执行强杀测试。详细说明保留在专项记录中。

主要复现命令：

```powershell
dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj -c Release --no-restore /warnaserror `
  --filter 'FullyQualifiedName~.Sql.|FullyQualifiedName~.Cdc.|FullyQualifiedName~.Streaming.' `
  --logger 'trx;LogFileName=integration.trx' --results-directory artifacts/m43-local-20260930
dotnet test tests/SonnetDB.CrashTests/SonnetDB.CrashTests.csproj -c Release --no-restore /warnaserror `
  --filter FullyQualifiedName~M43_ --logger 'trx;LogFileName=crash.trx' `
  --results-directory artifacts/m43-local-20260930
dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj -c Release --no-restore /warnaserror `
  --filter 'FullyQualifiedName~.Cdc.|FullyQualifiedName~.Streaming.' `
  --logger 'trx;LogFileName=cdc-streaming-final.trx' --results-directory artifacts/m43-local-20260930
pwsh -NoProfile -File eng/test-m43-release-evidence.ps1
pwsh -NoProfile -File eng/validate-fourteen-capability-index.ps1
dotnet build SonnetDB.slnx -c Release /warnaserror
```

## 仍待闭环

- CDC 自动源捕获、固定读视图接线、多分区/远程拓扑、冲突/schema 演进、离线同步及客户端入口。
- Streaming 持久窗口、任务列表/暂停/重试运维、真实 change-feed、远程 parity、实际掉电与更多提交阶段故障注入。
- SQL DML 与其它模型预算、端到端流式执行、首行/heap/冷启动和固定双架构性能。
- 十四能力真实 golden journey 与组合入口的权限、取消、重启和结果对账。
- 固定目标硬件、七天 scheduled nightly、真实模型质量/成本、干净安装、NativeAOT/跨架构、外部数据库对拍和 168 小时报告。
- 真实 release 绑定、目录提交/维护反馈、公开案例和 M43 最终验收。

本轮保留十三项 `partial`、Graph `beta` 和 M43 进行中状态；已有功能和本地测试不能回写为上述现场门禁完成。
