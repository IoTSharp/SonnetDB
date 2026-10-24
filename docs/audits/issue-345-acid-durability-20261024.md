# Issue #345 ACID/D 持久性复验记录（2026-10-24）

## 范围与基线

- 工作分支：`codex/issue345-acid-durability`。
- 基线：`origin/main` `4a230bdac7e56c0fee118fcbdec0f60ceabbbcbe`（包含 #344 隔离验证及 #350 事务恢复日志 rename 修复）。
- 本切片只补提交确认后的真进程强杀验收和可追溯的持久性审计；没有重新实现已有 WAL、事务恢复或故障工具，也没有修改二进制格式。

## 提交与恢复路径核查

关系表多表事务在 `TableManager.ApplyTransactionCoreLocked` 中按以下顺序执行：

1. 每张参与表先准备 KV mutation，并捕获 before-image；影响超过一张表时，`TableTransactionJournal.Prepare` 先写临时恢复日志、`Flush(true)`，rename 到 `transaction.sdbtxn`，再同步父目录元数据（#350）。
2. 按稳定表顺序应用已准备 batch；每张表的 `SyncTransactionWal` 无条件同步 KV WAL，因此关系事务不依赖 `KvOptions.SyncWalOnEveryWrite` 的名称或默认值来推导提交确认。
3. 所有表 WAL 同步后写完成标记并 `Flush(true)`；完成标记成功后才从 `CommitTransaction` 返回成功。完成标记之前的异常走 before-image 回滚，完成标记之后的异常报告 `TableTransactionRecoveryException`（结果未知），要求关闭并重开恢复。
4. `TableManager` 构造阶段在开放关系表前读取未完成 journal，校验 schema/generation 指纹、重放 undo 并同步 WAL，随后写完成标记。重复重开只能看到完整提交或完整撤销，不会重复应用业务 mutation。

`TsdbOptions.SyncWalOnEveryWrite` 与 `FlushWalToOsOnWrite` 只控制时序 WAL；`KvOptions.SyncWalOnEveryWrite` 控制普通 KV mutation。关系多表提交的显式 `SyncTransactionWal` 是事务自己的强同步屏障。目录 fsync 在明确不支持的 overlayfs/NFS/重定向器上按代码退化为文件内容已同步的旧边界，不能宣称可抵御介质损坏。

## 本切片新增证据

- `tests/SonnetDB.CrashTests/CrashReliabilityTests.cs` 新增 `crash_kill9_afterCommitAcknowledgement_ReopenRetainsConfirmedTransfer`，覆盖默认与显式强持久化配置。子进程只有在 `COMMIT` API 返回后才写 ready 标记；独立父进程看到该标记后把业务操作标识和期望值写入父进程拥有的确认文件，随后立即 `Kill(entireProcessTree: true)`，不运行 `Dispose`，再独立重开两次核验 accounts/ledger 的完整转账状态。
- 既有 `crash_kill9_*trigger*` 剧本覆盖完成标记前、完成标记前暂停和完成标记后强杀；#342 Core 回归覆盖提交前故障恢复及重复重开；#350 的 `TableTransactionJournalTests` 覆盖 durable rename 发布后关闭/读取；`KvAtomicBatchTests` 覆盖 WAL 原子批次的同步错误、撕裂尾部丢弃和恢复链。
- 既有 `SqlConstraintTriggerTests` 的完成确认失败/完成前失败测试区分结果未知与可回滚错误；既有 KV WAL sync-failure 测试确认 I/O 异常不会被吞掉或报告为成功。

## 故障模型与边界

- **进程崩溃**：本切片使用独立 CrashTests 子进程和 `Process.Kill(entireProcessTree: true)`；ready/ack 在 COMMIT 返回后记录，普通 `Dispose` 不参与证据。
- **提交结果未知**：已有完成标记前/后故障和完成同步失败测试保留 UNKNOWN 语义；调用方不得自动重发非幂等业务。
- **系统崩溃/掉电**：未在本环境使用隔离 VM、块设备或真实断电工具验证未同步写入的丢失/重排；不能把进程 kill 的 PASS 外推为物理掉电 PASS。
- **介质损坏**：WAL/journal CRC、截断尾部和 schema/generation 不匹配会诊断失败；超出保证范围的数据损坏不被静默修复。

## 验证状态

- `git diff --check`：提交前执行并记录结果。
- `dotnet restore SonnetDB.slnx`：**NOT_RUN**（当前执行环境没有 `dotnet` 命令）。
- `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/`：**NOT_RUN**（同上；提交前必须在具备 .NET 10 SDK 的环境执行）。
- 新增 CrashTests、Core/Server 回归及真实系统/硬件故障实验：**NOT_RUN**（同上或当前环境不具备隔离故障设备）。
