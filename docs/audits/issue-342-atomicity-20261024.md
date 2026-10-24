# Issue #342：关系事务原子性验证（2026-10-24）

## 本次切片

- 在 `tests/SonnetDB.Core.Tests/Sql/SqlTransactionAtomicityTests.cs` 增加四个确定性关系业务事务回归：两表 accounts/ledger 的多语句提交与独立重开、显式回滚与独立重开、失败语句仅回滚自身且事务可继续、提交前注入故障后双表恢复并连续两次重开保持前态。
- 复用既有 `TableManager.ApplyTransactionAfterTableTestHook`，不改变事务协议、WAL/journal 格式或生产代码。
- 真进程强杀握手与完成标记前后故障点仍由 `SonnetDB.CrashTests` 的既有 `crash_kill9_*trigger*` 剧本覆盖；本切片不把普通 `Dispose` 当成强杀证据。

## 验证边界

- 当前执行环境没有 .NET SDK（`dotnet: command not found`），因此尚未运行 restore、Core 定向测试、CrashTests 或仓库规定的 `dotnet format` 检查。
- 已执行 `git diff --check`（通过）；提交前仍需在具备 .NET SDK 的环境执行完整 restore/Format，并运行新增测试及相关 CrashTests。

