# M39 SQL 存储过程与触发器生产加固验收

日期：2026-09-06。用户指定当前 Windows 工作站为固定验收机。

## 验收结论

关系表 SQL 存储过程与 AFTER ROW 触发器已完成本轮正确性、生命周期、诊断、远程预算和进程崩溃恢复加固。
#333 固定机器完整矩阵、#334 生命周期与顺序、#337 诊断治理均已有本地验收证据。
M39 整体仍保持进行中：高级语义须按下述准入结论继续评估，性能证据也没有覆盖生产混合负载和长期 SLO。
本次没有发布、部署或远程 CI 成功声明。

## 原实现短板与修复

| 短板 | 修复与可观察行为 |
|---|---|
| 多表独立 WAL 之间终止进程会留下源表/outbox 半提交 | 增加 `tables/transaction.sdbtxn`，先同步原值，再同步各表 WAL，最后同步完成标记；开放关系表之前撤销没有完整标记的事务 |
| 补偿或提交确认失败后仍可能继续操作不确定状态 | 表管理器和已取得的表句柄停止访问；例程返回 `routine_commit_unknown`，审计为 `unknown`，重开后核对幂等键 |
| 事务按主键线性扫描，逐次保存点复制已有全部变更，批量成本近似平方增长 | 主键索引定位、稳定链表顺序、增量撤销日志；保留重复 INSERT 拒绝、净变化归并和保存点语义 |
| 未定义 ROWVERSION 时，两个排队事务可能覆盖同一汇总行 | SQL 队列保留首次读取的规范行状态，提交时检测变化；返回 `table_concurrency_conflict`，重试整个业务事务 |
| 嵌套 CALL 重复累计同一结果；INSERT RETURNING 可绕过结果预算 | 嵌套返回只计一次，过程及触发器 RETURNING 均计入预算；SELECT 用剩余预算加一行探测，超限失败 |
| 过程或触发器执行完便记成功，外层回滚和被淘汰记录可能造成错误指标 | `pending/committed/rolled_back/failed/unknown/completed` 明确区分；每个事务调用只结算一次，累计失败数不依赖审计保留窗口 |
| 缺乏启停、重命名及显式顺序；每次派发重复扫描目录 | 持久化 ENABLE/DISABLE/RENAME、FOLLOWS/PRECEDES；一次发布不可变目录与派发表，每条 DML 固定入口快照 |
| 定义写入列和子查询依赖校验不完整 | CREATE/EXPLAIN 校验写入/返回列、赋值列及子查询源；DDL 走数据库 schema 锁，禁用定义继续保护依赖 |
| REST/Frame 不能一致配置批量触发器预算；放弃 batch 时审计未结算 | 两协议使用相同 Server 配置；异常和未完成 batch 在 finally 回滚并结算审计 |
| 备份逐文件复制期间仍可发生关系表写入，造成跨表不一致 | 备份从 checkpoint 到复制完成持有关系表提交/存储锁；覆盖源表/outbox 与例程目录恢复 |

## 固定机器与来源

| 项目 | 本次记录 |
|---|---|
| 主机 | DEVPER，用户明确选定的固定验收机 |
| CPU | Intel Core Ultra 9 185H，16 核、22 逻辑处理器 |
| 内存 | 66,540,240 KiB OS 可见内存 |
| 系统 | Windows 11 家庭版，10.0.26200，x64 |
| 数据卷 | 数据库在 C: 的 `%TEMP%`，源码/报告在 D:；两卷均位于 disk 0，NVMe PC SN8000S WD 2048GB，物理容量约 2.048 TB |
| 工具链 | .NET SDK 10.0.400 / runtime 10.0.11，PowerShell 7.6.5 |
| 代码基线 | `56cbcaed729914f1b1dcfa910439b208e0a96704`，最终改动尚未提交 |

原始报告及机器/源码/二进制校验值见 [manifest.json](evidence/m39-20260906/manifest.json)。
基线在改动前编译，Core DLL 与基线测试 DLL 的 SHA-256 一致；运行报告时源码编辑已开始，所以其 Git 字段如实带 `-dirty`。
最终基准使用的 Core DLL 与最后一轮 45 项例程测试使用的 Core DLL 哈希一致。
不能把基线与最终报告相同的 HEAD 字段解释成相同二进制，也不能把工作树证据当作已发布版本。

## 性能结果

完整矩阵均为 `1/100/10000` 行 × INSERT/UPDATE/DELETE × 三条路径，分别覆盖 27 个成功样本与 27 个失败回滚样本。
基线为一次独立进程；最终为三个顺序执行的独立进程，测量期间没有并行构建。
表格使用最终三次中位数，括号为最小值至最大值；这些是固定顺序的单次 DML 样本，不是稳定态 BenchmarkDotNet 分布，也不是请求 P95/P99。

| 10000 行 AFTER ROW | 原基线 ms | 最终中位数 ms（范围） | 延迟降低 | 原线程分配 → 最终中位数 MB | 分配降低 |
|---|---:|---:|---:|---:|---:|
| INSERT | 5087.04 | 349.52（344.85 至 413.75） | 93.13% | 7690.78 → 92.54 | 98.80% |
| UPDATE | 6604.07 | 432.62（418.47 至 500.09） | 93.45% | 6095.84 → 100.69 | 98.35% |
| DELETE | 7634.37 | 319.35（291.67 至 337.03） | 95.82% | 6089.52 → 93.97 | 98.46% |

MB 为十进制；分配是当前线程累计分配，不是峰值常驻内存。工作集和托管堆为停止计时后的采样，原始报告完整保留。

触发器三种 DML 的 WAL 逻辑字节仍分别为 860104、861156、681156，新增恢复日志分别为 340159、520159、520159 字节。
恢复日志与多表 WAL 的同步成本计入 DML 延迟；rowstore 差值在计时结束后的 checkpoint 采样。
无触发器单表仍沿用 `SyncWalOnEveryWrite=false`，因此路径差异同时包含同步策略和业务写入数量差异。

小批量及无触发器结果必须一并保留：

- 100 行 AFTER ROW INSERT/UPDATE/DELETE 从 7.65/6.36/6.97 ms 变为 7.53/6.91/9.11 ms；不能声称所有批量大小都提速。
- 10000 行 NoTrigger INSERT/UPDATE/DELETE 从 146.47/189.69/237.23 ms 变为 182.11/390.79/176.18 ms。
  UPDATE 三次范围为 262.37 至 396.37 ms，分配增加 6.35%；新增行状态验证有成本，但现有样本不能将全部时间变化归因于它。
  下述补充对照已检查预热/编译条件，原始结果继续保留；生产混合负载与 SLO 尚未验收。
- 单行样本混入首次调用/JIT 与新增日志同步；候选路径把逐行审计改成一条计数写入，业务语义不同，不能直接替代 outbox，也不能证明 statement trigger 已实现。

可复算数据：[comparison.json](evidence/m39-20260906/comparison.json)、[before.json](evidence/m39-20260906/before.json)、
[final-1.json](evidence/m39-20260906/final-1.json)、[final-2.json](evidence/m39-20260906/final-2.json)、[final-3.json](evidence/m39-20260906/final-3.json)。
同目录保留四份原始 Markdown。修复前主要大批量成本来自事务归并/保存点，不足以直接认定逐行 WAL 已是主要瓶颈。

为定位 NoTrigger UPDATE 差异，又从同一基线提交独立重建 Core，仅在任务独占的 probe 输出目录替换 Core DLL，保持相同最终基准实现、10000 行及持久化策略。
每个进程先运行 2 次预热，再采集 5 次，均建立独立数据库；原始 CSV、二进制哈希和 probe 源码见 [warm-comparison.json](evidence/m39-20260906/warm-comparison.json)。

| NoTrigger UPDATE 补充条件 | 基线中位数 ms | 最终中位数 ms | 基线范围 ms | 最终范围 ms |
|---|---:|---:|---|---|
| 默认分层编译，2 次预热 | 144.67 | 164.98 | 123.04 至 150.97 | 139.94 至 222.68 |
| `DOTNET_TieredCompilation=0`，2 次预热 | 130.98 | 140.06 | 112.85 至 180.28 | 119.53 至 159.51 |

后一组中位数差异约 6.94%，线程分配约增加 3.85%，没有重现原矩阵约两倍的差异。
该诊断表明编译/预热条件会显著影响原矩阵解释，但不证明全部差异只来自 JIT，也不以 5 个样本消除尾延迟或混合负载的剩余风险。
行状态验证用于避免丢失更新，仍保留；没有通过取消并发校验换取性能数字。

## 验证结果

| 验证 | 结果 | 边界 |
|---|---:|---|
| Core 全量 | 4217/4217 | SQL、关系表及其他共享存储/模型回归；最后一次触发器 RETURNING 补丁之前 |
| 最终例程与触发器 | 45/45 | 最后补丁之后，包含 29 项生产加固用例及已有 16 项 routine/baseline |
| REST/Frame 真服务 | 15/15 | 真实本地 Kestrel、读写权限、可配置预算、放弃 batch 的审计结算 |
| 真进程终止恢复 | 3/3 | 两表 WAL 之间、完成标记之前、完成确认之后；重复重开核对源表/outbox |
| 报告合同 | 1/1 | quick 合同校验三种 DML、路径、成功/回滚状态和独立 journal 字节 |
| 最终完整矩阵 | 3/3 | 总计 81 个成功、81 个精确回滚样本 |
| Core/Server Release 构建 | 0 警告、0 错误 | 启用 AOT/trim 分析与 warnings-as-errors；本次未运行 NativeAOT publish |

测试集互有包含，不能简单相加当作独立测试数。最后一个补丁仅补触发器 RETURNING 预算；Server、CrashTests 和报告合同的执行版本早于此补丁。
原始 TRX 保留在本机 `artifacts/m39-production/`，manifest 记录每份路径、起止时间、数量与 SHA-256。
新增 workflow 会归档 Core/Server/Crash/报告合同 TRX；本次没有触发远程 workflow。

## 使用与恢复合同

SQL 语法和例子见 [SQL reference](../sql-reference.md#sql-触发器)。推荐业务闭环为：定义关系表及 outbox → 创建过程/触发器 → EXPLAIN 校验 → CALL 或事务 DML → 检查最终审计 → 备份/恢复后核对业务主键。

- 默认预算保持 64 条语句、8 层深度、10000 行结果；10000 行、每行一个触发动作的请求应明确配置 `MaxRoutineStatements=10064`，并计入其他 body 和嵌套动作。
- `SonnetDBServer:SqlExecution` 对 REST/Frame 一致生效；调用方取消在进入持久化决定之前检查，进入后不强行打断提交。
- `table_concurrency_conflict` 表示提交失败，重新读取并重试整个事务。`routine_commit_unknown` 表示结果不确定，应关闭数据库、重开恢复并按幂等键核对，不能直接假定失败后重发。
- 恢复日志为独立 version 1，带 header/payload CRC，限制 128 MiB、1024 张表、每表 100 万个 row/index 操作。此上限不等于整个 SQL 执行的峰值内存上限；大事务仍需按业务分批。
- 恢复保存全部涉及的行与索引原值，校验 schema fingerprint 和 generation；损坏日志拒绝打开，不静默忽略。恢复重复执行仍得到相同状态。序列/自增预留允许留下间隙。
- 例程目录独立升级到 version 2，兼容读 version 1，记录启用状态与顺序；旧引擎拒绝 version 2。主数据文件及原 KV/WAL 格式未改变。升级前保留备份，不能直接降级打开已更新的例程目录。
- 审计最多保留 256 条，不记录参数值或行内容；STATS 的延迟分布只针对保留窗口。`ExportAudit(Stream, ...)` 是可落盘的脱敏快照，不是无损持久审计服务。pending 状态导出后需重新导出最终状态。
- 支持范围为单库关系表、ReadCommitted。物理断电/控制器故障、跨进程长事务、跨库事务、分布式 exactly-once、持久 CDC/outbox 消费器不由本次测试证明。

## M39 后续准入

| 条目 | 本次结论 | 仍需证据 |
|---|---|---|
| #333 | 固定机器基线与恢复矩阵已归档，另补同预热/编译条件的 UPDATE 对照 | 生产工作负载、稳定态分布与尾延迟独立验证 |
| #334 | 生命周期、位置顺序、依赖及备份恢复本地通过 | 版本发布时保留目录升级与降级拒绝说明 |
| #335 | 暂未准入；先消除平方复杂度已取得主要收益 | 同业务语义下证明逐行写放大为剩余主要瓶颈，再设计有界 transition tables、空集和失败合同 |
| #336 | 暂未准入；状态保护 journey 已能通过 AFTER 失败原子回滚 | 必须提供需要提交前改写 NEW 的业务案例，并验证生成列/约束/ROWVERSION 顺序 |
| #337 | 定义校验、过滤审计、窗口分布及快照导出本地通过 | 完整持久审计或订阅有独立需求时另立合同，不把快照冒充无损日志 |
| #338 | 暂未准入；当前保存点、取消、深度、提交错误已有合同 | deferred/constraint trigger 的真实用例、死锁及提交阶段执行设计；异步动作使用 durable outbox worker |
| #339 | 独立门禁，未实现 | Document/measurement 原生事件、幂等/重放、保留/compaction、备份与容量证据 |

## 复现

在固定工作站以 PowerShell 7 运行；外层执行器应限定每个测试/矩阵 5 分钟、Core 全量 10 分钟，并保留子进程身份与超时回收记录：

```powershell
dotnet test tests/SonnetDB.Core.Tests -c Release --filter 'FullyQualifiedName~SqlRoutineProductionTests|FullyQualifiedName~SqlRoutineTests|FullyQualifiedName~SqlTriggerV2BaselineTests'
dotnet test tests/SonnetDB.Tests -c Release --filter 'FullyQualifiedName~SqlFrameEndpointTests'
dotnet test tests/SonnetDB.CrashTests -c Release --filter 'FullyQualifiedName~betweenTriggerTableCommits|FullyQualifiedName~triggerCompletionBoundary'
dotnet test tests/SonnetDB.Benchmarks.Tests -c Release --filter 'FullyQualifiedName~TriggerEvidenceReportTests'
# 仅在以上 Core/CrashTests 通过后设置；每次单独启动进程，顺序运行三次并使用不同目录。
$env:M39_CRASH_EVIDENCE_VERIFIED = 'true'
dotnet tests/SonnetDB.Benchmarks/bin/Release/net10.0/SonnetDB.Benchmarks.dll --m39-trigger-evidence --output artifacts/m39-production/reproduction-1
Remove-Item Env:M39_CRASH_EVIDENCE_VERIFIED
```
