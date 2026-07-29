# M39 SQL 触发器 V2 基线（#333）

本文档记录进入 `ALTER TRIGGER`、statement-level trigger 或 transition table 设计前的
可复现实验口径。它是容量和恢复证据，不代表任何尚未实现的 SQL 语法已经获得支持。

## 固定运行口径

- 代码基线：运行命令时的提交 SHA，运行时为 .NET 10（`global.json` 指定的 SDK）。
  本地工作区存在未提交改动时，报告中的 SHA 会追加 `-dirty`（Git 状态不可读时为
  `-status-unknown`），避免把临时测量误认成干净提交。
- 数据库：嵌入式 `Tsdb`，每个样本使用独立临时目录；后台 flush、compaction、自动 checkpoint 和过期清理关闭。
- WAL：`SyncWalOnEveryWrite=false`、`FlushWalToOsOnWrite=true`，以便比较逻辑写放大；这不是掉电耐久性结论。
- `wal_bytes` 是 DML 前后所有已打开关系表 active WAL 的逻辑长度差，包含尚未从缓冲区刷到文件的记录；它不是 fsync 或掉电耐久性指标。
- `rowstore_bytes` 是停止计时后执行 `CheckpointAll()` 生成的 `.SDBKVSNP`/`.SDBKVSEG` 文件增量，checkpoint I/O 不计入 `elapsed_ms`。
- 行数：`1`、`100`、`10,000`。
- 报告 schema：`m39-trigger-v2-baseline-v2`；`journeys`、`costMatrix`、`rollbackMatrix` 和 `crashEvidence` 均为机器可读数组。
- 本轮成本矩阵以批量 `INSERT` 作为写放大代表；`UPDATE`/`DELETE` 的功能合同在 golden journey 中覆盖，尚未纳入同规模计时，属于待补证据。
- 每档路径：
  - `NoTrigger`：仅向 `trigger_source` 执行批量 `INSERT`。
  - `V1RowTrigger`：当前 `AFTER INSERT FOR EACH ROW`，每行向 `trigger_audit` 写一条记录。
  - `CandidateStatementReference`：显式事务中向源表批量写入，再向汇总表写一条计数记录；这是客户端参考路径，**不是** statement trigger 或 transition table 实现。
- V1 的默认 `MaxRoutineStatements=64` 不被产品代码修改。为了让 100/10,000 行成本可观测，基准专用入口将该上限设为 `Rows+64`，并在结果中保留这一前提。

## 运行命令

短 smoke（输出制表符分隔的耗时、WAL、table-directory、工作集、托管堆和线程分配，并追加回滚样本）：

```powershell
dotnet run -c Release --project tests/SonnetDB.Benchmarks -- --m39-trigger-baseline-smoke
```

正式证据入口运行成功 DML 与失败回滚两张完整矩阵，并写出 source-generated JSON 与 Markdown：

```powershell
dotnet run -c Release --project tests/SonnetDB.Benchmarks -- `
  --m39-trigger-evidence --output artifacts/m39-trigger-v2
```

报告命令本身不启动 CrashTests；未设置 `M39_CRASH_EVIDENCE_VERIFIED=true` 时，报告会把
crash/replay 条目标为未运行的测试引用。只有在同一流程先运行 Core/CrashTests 并确认通过后，才可
在报告步骤设置该环境标记；workflow 已按此顺序执行并上传完整证据。

传入 `--quick` 只跑 1 行，用于验证报告管线；quick 报告不能作为 #333 的成本结论：

```powershell
dotnet run -c Release --project tests/SonnetDB.Benchmarks -- `
  --m39-trigger-evidence --quick --output "$env:TEMP\sndb-m39-trigger-v2-quick"
```

正式 BenchmarkDotNet 统计（3 次测量、无预热；可按需筛选）：

```powershell
dotnet run -c Release --project tests/SonnetDB.Benchmarks -- --filter '*TriggerBaseline*'
```

长测结果应保存 BenchmarkDotNet 的 `BenchmarkDotNet.Artifacts/results` 文件，并同时保存 smoke
表头和环境信息。普通 CI 不运行长测；建议由手动或 nightly workflow 上传 artifact。

## Smoke 结果示例

以下是 Windows x64 / .NET 10 Release 的一次 smoke（单次样本，不能替代正式统计，也不应被当作固定硬件容量声明）：

| 行数 | 路径 | 耗时 (ms) | WAL (bytes) | rowstore (bytes) | 托管堆 (bytes) | 线程分配 (bytes) |
|---:|---|---:|---:|---:|---:|---:|
| 1 | NoTrigger | 33.267 | 95 | 119 | 601,232 | 75,496 |
| 1 | V1RowTrigger | 13.400 | 190 | 238 | 751,600 | 147,688 |
| 1 | CandidateStatementReference | 0.652 | 190 | 238 | 744,888 | 150,048 |
| 100 | NoTrigger | 1.710 | 4,352 | 5,564 | 957,224 | 350,480 |
| 100 | V1RowTrigger | 10.481 | 8,704 | 11,128 | 2,295,800 | 1,640,376 |
| 100 | CandidateStatementReference | 3.920 | 4,447 | 5,683 | 1,376,008 | 734,368 |
| 10,000 | NoTrigger | 156.155 | 430,052 | 550,064 | 13,596,768 | 24,224,064 |
| 10,000 | V1RowTrigger | 7,350.413 | 860,104 | 1,100,128 | 27,828,848 | 7,671,602,024 |
| 10,000 | CandidateStatementReference | 3,390.818 | 430,147 | 550,183 | 23,845,920 | 3,224,734,392 |

该示例来自本机提交 `9699c3a32a41c3c45a78ed3b84f6e5b1f45a11eb-dirty` 的完整报告（2026-07-29）；`elapsed_ms`
只包含 DML 执行，WAL/文件/内存采样在计时停止后进行。
文件长度和工作集会受 SDK、文件系统和同机进程影响；上表只是一次本机 smoke，不能替代正式统计。
提交新结果时必须记录提交 SHA、SDK、OS、CPU、配置和完整原始输出。关键方向是：V1 每行动作产生
约 2 倍 WAL 和显著分配放大；候选参考路径的单条汇总写入不能证明原子 transition set，也不能用来
宣称 statement trigger 已实现。

回滚 smoke 对三个路径各跑 `1/100/10,000` 行。失败后源表行数必须为 `0`；V1 和候选路径保留
预置的 1 条审计基线行，无触发器路径保留 `0` 条。当前失败发生在 prepared batch 发布前，因此
`wal_bytes=0` 是预期结果，表示没有产生补偿 WAL；提交阶段注入和真实进程终止由独立测试覆盖。

## 功能与失败矩阵

基线测试还应覆盖三条 golden journey：

1. 审计 outbox：源表写入和审计行同一事务成功；触发动作失败时两者都回滚。
2. 派生汇总：多行 INSERT/UPDATE/DELETE 后汇总值与源表对拍；空影响集不产生伪动作。
3. 状态流转保护：非法状态迁移被拒绝，原始行和触发动作均不留下部分结果。

正式 JSON/Markdown 报告的 `journeys` 索引审计 outbox、派生汇总和状态流转保护三条 Core 测试；
`rollbackMatrix` 对 1/100/10,000 行分别记录无触发器、V1 行触发器和
客户端候选 statement 参考路径的失败耗时、WAL 增量、残留源行/审计行与稳定失败码。`crashEvidence`
列出 Core 失败注入、提交失败、真进程终止和重启 replay 的自动化测试名称；运行报告不会把未执行的
进程测试伪装成通过。

当前关系表使用独立 keyspace/WAL；跨表掉电原子性尚未得到证明，因此出现“原始表已提交、
outbox/summary 缺失”或反向 partial commit 时，应如实记录为 gap，而不是以 V1 触发器成功率掩盖。

## 准入判断

- 只有在固定机器上重复正式测量，确认逐行写放大是主要吞吐/分配瓶颈，并完成上述 crash/replay
  对拍后，才进入 #335（statement trigger / transition tables）设计。
- #334（生命周期和显式顺序）与 #337（诊断治理）可以基于真实 journey 的缺口独立排期；不得
  用候选参考路径代替产品合同。
- 在证据完成前，不实现 `BEFORE`、`FOR EACH STATEMENT`、transition table、Document/measurement
  trigger，也不宣称跨 keyspace exactly-once 或掉电原子性。
