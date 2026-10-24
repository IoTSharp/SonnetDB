# Issue #343：约束、级联与索引最终状态一致性（2026-10-24）

## 固定范围

本切片从 `3266459acfd51dc2266c5124e57cec5a2c87ced6`（#342 合入后的 `origin/main`）开始，只验证嵌入式 Core SQL/TableManager 路径。Server/REST/Frame、真实进程强杀和部署恢复不从 Core 结果外推，仍需各自测试项目取得证据。

| 合同 | 现有/新增证据 | 失败阶段与最终断言 |
| --- | --- | --- |
| PRIMARY KEY、唯一索引、NOT NULL、CHECK | `SqlConstraintConsistencyTests.ConstraintAndIndexFailure_RollsBackWholeStatementAndLeavesBaseRowsAligned`；`SqlPrimaryKeyUpdateTests`、`SqlDecimalEnforcementTests` | 单语句批量失败与唯一键更新失败不发布部分行；基础扫描、唯一/二级索引点查在写入、删除和重开后相同 |
| 外键 `RESTRICT`/`CASCADE`/`SET NULL` | `SqlExecutorTableTests`、`SqlConstraintTriggerTests`；新增双 `SET NULL` 外键用例 | 非法孤儿/RESTRICT 删除失败保持前态；多级级联和 `SET NULL` 在提交后、重开后保留一致状态 |
| 同一子行的多个 `SET NULL` 动作 | `SqlConstraintConsistencyTests.SetNull_MultipleForeignKeysToSameParent_NullsEveryReferencingColumn`；显式事务配套用例 | 级联展开合并同一子行的多个置空列；不再因 touched 去重遗漏第二列，最终 FK 校验不产生孤儿 |
| 触发器组合 | `SqlConstraintTriggerTests`、`SqlTriggerAdvancedTests`、`SqlTriggerV2BaselineTests` | 已支持的立即/延迟触发器失败和提交回滚路径维持既有合同；本切片未新增未声明语法 |
| 事务/恢复 | `SqlTransactionAtomicityTests`（#342）及其 CrashTests 边界；新增显式事务级联用例 | 正常提交与前态/后态重开断言；故障强杀仍由 CrashTests 负责，不以 `Dispose` 代替 |

## 实现观察与修复

`ExpandCascadeDeletesLocked` 原来用每表 touched 主键集合去重所有级联动作。同一 child 行声明两个指向相同 parent 的 `ON DELETE SET NULL` 外键时，第一条 FK 生成置空 mutation，第二条被直接跳过；提交最终状态仍保留第二个已删除 parent 的键，随后 FK 校验失败（或在延迟 overlay 中暴露为孤儿）。

级联 mutation 现在带有仅限内存的内部标记，展开时按 `(table, primary-key)` 合并所有 SET NULL 列；延迟触发器/显式事务在已有 overlay 中重放合并后的 mutation，而不是只追加新增行。若同一行同时遇到生成的 SET NULL 与 CASCADE，删除动作优先。该标记不写入表行、WAL 或 schema 格式。

## 验证边界

- `git diff --check`：通过。
- `dotnet restore SonnetDB.slnx`、`dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/`、Core 定向测试与完整测试：**NOT_RUN**（当前环境 `dotnet: command not found`）。
- 具备 .NET 10 SDK 的合入环境必须先执行仓库要求的 restore/Format，再运行新增 `SqlConstraintConsistencyTests`、既有约束/触发器/索引回归和 #342 CrashTests；本记录不把本地未运行写成 PASS。
