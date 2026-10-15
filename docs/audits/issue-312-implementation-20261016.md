# Issue #312 普通关系主键更新实施记录

日期：2026-10-16（Asia/Shanghai）。Issue：IoTSharp/SonnetDB #312，`[FreeSql][SQL] 支持关系表显式更新普通主键值`。

接收基线为 `89674db059928aa7f1bea1cfef8a13e97c43d648`。实施目录为 `C:\Users\mysti\.codex\worktrees\issue312-primary-key-update\SonnetDB` 的 detached HEAD 隔离树；未修改 `D:\source\SonnetDB`，未 commit、push、发包、评论或关闭 Issue。协调主智能体负责最终集成、完整提交门禁和线上状态核对。

## 已实现合同

- 普通单列、非 AUTO_INCREMENT 的 INT 主键允许单条 `UPDATE Items SET Id=101 WHERE Id=1`，受影响行数按命中行计，旧键消失、新键可查，未赋值列保持，ROWVERSION 沿用自动递增。
- `TableRowMutation.PrimaryKeyValues` 继续表示原始持久键，`NewValues` 包含新键。`TableStore` 校验源行并发基线、目标键及唯一约束，构造新主键的行与索引，删除旧主行/旧索引和发布新主行/新索引统一通过既有一个 KV WAL MutationBatch 完成。没有可见的中间删除/插入状态，也没有改变文件编码或版本。
- 事务缓冲的查找键跟随当前可见主键，归并后仍保存最初的持久键、ROWVERSION 和完整行状态。连续改键、改键后普通列更新/删除、事务新插入行改键/删除、提交/回滚、点查与扫描 overlay 一致。
- 多行改到未占用的新范围支持；目标键重复、NULL、CHECK、普通唯一索引冲突保持原行和索引。目标已存在时整句拒绝，因此交换键、向仍占用键范围顺延不支持。同一事务重新插入仍持久存在的旧键在提交时明确失败；提交成功之后旧键可重新插入。
- 含入站或出站外键的主键赋值在绑定后拒绝，提交时在 `TableManager` 写锁内重新验证当前 catalog，防止缓冲后新增外键导致约束失效。复合/非 INT 主键、联接 UPDATE 改键、AUTO_INCREMENT 与 ROWVERSION 显式赋值保持拒绝。通用生成列表达式不属于当前关系 `TableColumn` 合同，本次没有增加生成列能力。
- 多表事务 undo 记录同时覆盖旧主键、新主键以及两侧索引；原有恢复 journal 的 generation/schema fingerprint 验证与提交标记规则保留。

## 验证与证据

本地日志与 TRX 保存在忽略目录 `artifacts/issue-312/`。两测试项目均使用 `dotnet build ... -c Release -m:2`，避免无界占用主机。

- Core 和 Server 的 Release 构建已通过，均 0 warning / 0 error。Server 初次构建因隔离树缺两个 submodule 失败；按仓库 gitlink 初始化 `IoTSharp.CoAP.NET` (`797a8599`) 与 `MQTTnet.AspNetCore.Routing` (`9e0c8202`) 后通过，没有变更 gitlink。
- 新增 `SqlPrimaryKeyUpdateTests`、`TableStorePrimaryKeyUpdateTests` 和 `RemoteAdoHttp2TransportTests.PrimaryKeyUpdate.cs`。覆盖单/多行、普通/唯一索引、NULL/CHECK/重复失败、连续事务更新、删除、插入、旧键复用拒绝、最终 FK 校验、并发源行/目标键冲突、RETURNING、恢复及真实协议同步/异步参数。
- 真实传输与既有全表 UPDATE 回归：**10/10**，0 fail / 0 skip，见 `transport-final.trx` / `transport-final.log`。其中新增矩阵为 embedded / REST / h2c × sync / async 共 6 项；REST/h2c 使用真实动态端口 Kestrel，完全停止并重建 host 后核主键及索引，不把连接重新打开等同数据库重开。
- 存储测试验证一次改键只增加一个 WAL MutationBatch / 一次 WAL 同步，索引读快照跨写入仍返回完整旧行，随后新读返回新键。
- 恢复测试包括多表失败补偿，以及直接保存真实 journal 后分别在部分表写入/完成标记后调用 `CrashSimulationCloseWal` 并重开。这是进程内模拟关闭与持久恢复验证，不是实际进程强制终止测试。
- Core 最终新增用例 **26/26** 通过；`core-new-reviewed.trx` / `core-new-reviewed.log` 为 **27/27**，包含这 26 项与 1 项既有 SQL metrics 用例。扩大集合 `core-final.trx` 是 843 项：817 项既有全部通过，新增 26 项里 25 pass / 1 恢复测试期望排序错误；修正后上述最终定向重跑覆盖全部新增测试，未重跑整套扩大集合，不将原始失败文件称为全 PASS。
- 扩大集合约五分钟自然完成，两个既有在线检查点测试分别 116.920 秒和 107.871 秒且均通过；曾检查自有 testhost/父进程确认身份，准备回收时进程已自然退出，没有执行终止动作，不记录为死锁或测试中断。
- 首次定向 23 项为 22 pass / 1 夹具重复 Rollback 失败，提交失败本已终止事务，已删除多余清理调用。远程初次矩阵因夹具复用参数名错误为 2 pass / 4 fail，修正后增加异常来源和约束消息断言并取得上述最终 10/10；不累加中间运行数量。
- 两测试项目针对本次文件的 `dotnet format --verify-no-changes --no-restore --severity warn --include ...` 均通过，`git diff --check` 通过。未执行完整方案 restore/Format Check；协调主智能体必须在最终集成树每次 commit 前执行原强制门禁。

## 验收边界与后续集成

根已按冻结 SHA256 集成四份生产文件、三个独立测试文件和本报告，保留主树最新 HANDOFF/CHANGELOG 及已存在的 partial fixture。根 Core/Server Release 构建均零警告/错误；Core 组合 **67/67**、Server 组合 **18/18**，零失败/跳过，覆盖本项新增用例及 #313/#314 组合。证据位于主树 `artifacts/issue-supervision-20261016/issue312/`，与子树数量不累加。提交前完整 restore 与规定 Format Check 收据在同目录，两项实际 exit 0 后执行独立提交，最终 SHA 以 Git log 为准。本批尚未推送。

没有发布新的 NuGet/Server 安装包，尚无首次可安装版本；没有用对应发布包重跑 FreeSql 既有 `IUpdate.Set` 消费者入口。不能将本地 build/test 或提交视为 Issue 全部验收满足，#312 关闭仍需这些原始验收证据。没有执行完整测试 suite、远端 CI、NativeAOT publish、长稳或固定硬件性能验收；生产变更未涉及 JSON DTO/序列化或依赖。

下游 #226 / #243 应保留以下边界：

1. `TableStore.TableMutationOperation.RowKey` 表示旧/目标源行键，而 `NewRow.PrimaryKey` 可不同。任何批次应用、回滚、journal 捕获或重建都必须考虑两个键。
2. `MutationBuffer.Key` 表示当前可见键；`TableRowMutation.PrimaryKeyValues` 与首次 ExpectedRowState/ExpectedRowVersion 表示持久基线，不应被连续改键替换成尚未持久的新键。
3. FK 和当前 schema 必须在管理器最终提交锁下验证，DDL 应保持已有 schema/store 互斥及 generation/fingerprint 恢复合同。
4. 本次不改变主键 schema、无主键行身份或 format，不把这些后续工作并入 #312。

最终四个生产文件 SHA256：

| 文件 | SHA256 |
|---|---|
| `src/SonnetDB.Core/Sql/Execution/SqlTransactionContext.cs` | `B930E313B769E08187525B3E000285743BB9711D53FE330CF42C227BA77C5ECB` |
| `src/SonnetDB.Core/Sql/Execution/TableSqlExecutor.cs` | `BD9B27F4582DC4BA99981EC65DAFF405DC683CBE6F99AB06C2AC44F03FE41637` |
| `src/SonnetDB.Core/Tables/TableManager.cs` | `DEB1C1574D94DB1B901370240F02CE5736DE264D781DBD7FBCCF66E0E0F18FB6` |
| `src/SonnetDB.Core/Tables/TableStore.cs` | `BD95A9E4DF5C7651AD5A51BDA2C4F3FB6DD5E85E25A5E4EA7EEA894A5597C744` |
