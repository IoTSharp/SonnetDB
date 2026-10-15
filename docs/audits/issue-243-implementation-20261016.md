# #243 无主键有界删除及 #253/#255 DML 基础

日期：2026-10-16。实施范围是补齐 #243 中无主键关系表的有界删除，所需稳定行身份与普通无主键写入同时覆盖 #253/#255 的引擎及 ADO 缺口。正式安装包及 Chloe/XCode 实际消费者仍单独验收。

## 基线与交付

- 隔离工作树：`C:\Users\mysti\.codex\worktrees\issue243-keyless-delete\SonnetDB`，detached HEAD `b99bf142af487a12b1b484b9b6a380b55359ffdc`。
- 开始实现前接收 #312 已完成候选中的 `TableStore`、`TableManager`、`TableSqlExecutor`、`SqlTransactionContext` 四个生产文件。原始候选差异保存在 `artifacts/issue-243/received-312.patch`；它们的已有主键更新能力不算作本任务新实现。根代理已另行提交 #312 为 `4c116ec2`。
- 生产十文件冻结清单：`artifacts/issue-243/production-frozen.json`。测试七文件冻结清单：`artifacts/issue-243/tests-frozen.json`。根代理按 SHA256 串行接收，并把共享生产基线交给 #226 接线。
- 本隔离树不执行 commit、push、Issue 评论或关闭。集成后的完整 solution restore/Format Check、逐任务提交及批次推送由根代理完成；不能把本报告局部通过称为完整 CI 或已发布。

## 行身份和 DML

无主键表的物理行键在现有 `r` 前缀之后使用 18 字节身份：`H`、版本 `1`、`Guid.NewGuid()` 的 16 字节编码。每次 INSERT 独立分配，重复业务值和 NULL 不归并；不需要高水位计数器。事务在入缓冲前分配身份，UPDATE/DELETE 沿用扫描得到的身份，批次准备阶段对存量和批内碰撞明确拒绝并保持原子性。既有业务主键编码及行值 payload 均不变。

SQL/ADO 的 `SELECT *`、`RETURNING *`、information_schema、`GetSchema("Columns")` 只显示声明列；表 schema 的 PrimaryKey 仍为空。底层 `TableRow.PrimaryKey` 在此模式承载不透明物理身份，不伪造业务主键。

实现覆盖：

- 原生 `Insert` / `InsertMany`、SQL INSERT、参数和批量重复行；WHERE UPDATE/DELETE、RETURNING、LIMIT 0/1/n/超过匹配数。
- 事务内插入/更新/删除及连续有界删除的 read-your-writes、commit/rollback、失败语句保存点；ROWVERSION、原始行状态并发检查。
- 普通/唯一索引、覆盖索引、NULL、CHECK、无主键子表的外键 RESTRICT/CASCADE/SET NULL；多表失败撤销保留原物理身份。
- 纯 keyless INSERT 在同一表锁内取得 generation 与 Serializable 首次读令牌。外部 TRUNCATE 后，ReadCommitted 拒绝过时代际，Serializable 返回既有 `table_serialization_conflict`，不能绕过首次读取检查。
- 正常重开、未完成/已完成的持久事务日志恢复、全库备份/恢复以及真实进程强制终止恢复。

LIMIT 只限制当前语句直接删除行数；候选扫描、内存物化和级联影响仍遵循各自已有预算。没有排序声明时不保证选中哪条重复行。原生按主键定位/Upsert、无主键 ON CONFLICT、无主键 JOIN/FROM UPDATE 明确拒绝；无主键表不是可被引用的外键主表。

## 兼容屏障和恢复

`tables.tblschema` 支持 v13，payload 与 v12 相同。新建无主键表、打开旧目录中的空无主键表、#226 的可恢复迁移可以显式使用 `SaveWithRecoveryBarrier`。该入口依次执行文件 flush-to-disk、原子替换和父目录 `DirectoryFsync.FlushRequired`，返回后才允许发布并写入新的物理身份。已有 v13 无主键目录开库时也先执行目录刷新。

CREATE 在保存前登记回滚责任，因此 rename 成功但目录刷新失败时仍会恢复原 schema；回滚使用相同屏障。所有目录保存以既有版本为下限，不因 DROP 最后一个无主键表或恢复旧 schema 而降回 v11/v12。`Load(Stream)` 改为 internal，供 #226 严格解析恢复快照；没有增加公共 API 或 JSON 反射边界。

旧引擎拒绝 v13；回退需要升级前的完整数据库副本。此次没有修改 `FileHeader`、已发布业务行值/主键编码或 DML journal 格式。目录刷新沿用仓库既有跨平台实现，其对明确不支持目录 fsync 的文件系统会按既有合同降级；实际强制终止测试不是断电测试。

## 验证证据

所有结果在本隔离树的 `artifacts/issue-243/`，忽略目录不提交：

| 检查 | 结果 | 证据 |
|---|---:|---|
| 修复后 Core 定向 | 104/104，0失败/跳过 | `core-keyless-reviewed.trx` |
| 其中新增无主键 Core 用例 | 26/26，已包含在104内 | `SqlKeylessDmlTests` / `SqlKeylessRecoveryTests` |
| 最终真实远程与有界删除 | 14/14，0失败/跳过 | `remote-keyless-final.trx` |
| 其中 keyless 嵌入式/REST/h2c × 同步/异步 | 6/6，已包含在14内 | `RemoteKeylessDmlTests`，观测请求协议 |
| 实际进程强制终止 | 3/3，0失败/跳过 | `crash-keyless-first.trx` |
| 最终 Core 存储/事务/DDL/索引/备份广回归 | 447/447，0失败/跳过，3分59秒 | `core-keyless-final.trx` |

真实 kill 测试在独立 worker 的“部分表完成”“全部表完成但 journal 未 complete”“journal complete 后”三个点等待父进程强制结束。恢复检查重复行的物理身份、覆盖索引、目录 v13、两张表值的同一提交决定，并在恢复后再次写入/删除及重开。worker 只在 `Child/Program.cs` 增加三条场景分发，逻辑在独立 `KeylessCrashScenario.cs`；与 #226 的独立场景可以合并。

较早的 `core-keyless-regression.trx` 415/415 在最后三项复审修补前执行，不绑定最终生产 SHA，不与最终结果累计。初轮两个失败均是新增测试使用既有不支持的 INFORMATION_SCHEMA COUNT 或期待错误异常类型，已修改测试表达真实合同；日志保留。独立审查发现并修复的生产问题为覆盖索引错误校验业务主键、v13 缺少目录持久化边界、纯插入 Serializable 跨 TRUNCATE 未记录首次读令牌。

最终447用例运行前已用修复后源码构建并通过104定向用例；随后生产/测试冻结SHA复核一致，447以 `--no-build --no-restore` 在同一构建产物执行。104与447存在重叠，不累计。`validation-final.json` 记录17个源/测试文件、构建产物及最终TRX的SHA256，生产与测试冻结清单均无漂移。最终运行进程已退出。

测试构建未报告编译警告或错误；未新跑 NativeAOT publish、完整 solution suite、远端 CI 或正式 ORM 消费者。JSON/source-generation、Safe-only 和核心零新运行时依赖边界保持。

## 根主树整合验证

根在 `4c116ec2` 上按冻结 SHA 接收十份生产文件及七份测试文件，保留前批 #312/#313/#314 和共享文档。Core、Server、Crash 三个 Release 构建均 0 warning / 0 error；Core 组合 **404/404**，Server 组合 **15/15** 加独立既有有界删除 **8/8**，实际进程终止组合 **5/5**（本项三种事务边界和既有主键重命名两边界），全部零失败/跳过。Core 根组合包含本项、主键更新、Tables、Serializable、触发器、自增、RETURNING 和安全 DDL，未重复子树已经完成的慢 OnlineCheckpoint 类；子树最终 447/447 包含该类与备份。两处结果有重叠，不累计为单次完整 suite。

根证据位于 `artifacts/issue-supervision-20261016/issue243/`。最终待提交树按仓库规定执行完整 restore 与 Format Check，收据实际均 exit 0 后才执行本次独立提交；SHA 以 Git log 为准。当前本项尚未推送、发包或关闭；首次可安装版本、XCode 最大行数/分批删除消费者验收仍未完成。

本项最终完整门禁通过后独立提交 `b6e674c431cb8841f33d4a14941537d965184366`，已随#226整批正常推送并回读main为 `285186b868a59d4b22e47279ea51deb38a3a1f45`。最终组合源码通过Core3313/3313、Server104/104、实际kill15/15，包含本项回归且与以上旧结果重叠，不累计。评论 `6203689607` 的正文、作者及open状态已回读验证；代码CI `38818030145` 正在执行。正式版本及XCode消费者条件仍未完成，Issue保持开放；见[本批线上回执](issue-supervision-priority-actions-20261016.json)。本段更新此前“尚未推送”的历史状态。

## CI 后续：更新无主键 schema 演进测试

`38818030145` 在精确源码 `285186b8` 上的三平台 NativeAOT 和 Format Check 成功，Ubuntu Core **7168/7168** 通过。Ubuntu 与 Windows rest 的 Server 都为 **1324/1326**：两条旧 `RemoteDdlEvolutionTests.EmptyKeylessTable_EmbeddedAndRemote_AddsKeyAndGeneratedColumns` 仍要求无主键 INSERT 抛错，实际写入已按本次新增合同成功。原 TRX 下载保存在 `artifacts/issue-supervision-20261016/ci-38818030145-ubuntu/` 和 `ci-38818030145-windows-rest/`，没有重跑或改写原失败结果。

本次仅修正这条过时测试：嵌入式/REST分别覆盖空表与已有无主键行，检查增加主键前没有伪造业务主键或隐藏列、迁移保留已有数据、自增高水连续、ROWVERSION回填及重连后的精确行集。没有修改生产代码。最终Release构建 **0 warning / 0 error**；完整Server项目 **1328/1328**、零失败/跳过，其中该类6项均通过，不与前述集合累计。

本地前两次完整运行同样保留：C盘空间耗尽时为1250通过/78失败；切到过深D临时目录后为1272通过/56失败，包含Windows目录持久化API打开长路径失败和后续派生任务失败。未改测试DLL或放宽断言，第三次只改为独占短 `D:\sndb-t\ci243-*` 临时目录后1328项全过。完整命令、退出码、日志和三份TRX位于 `artifacts/issue-supervision-20261016/issue243-ci-contract/`；`short-temp.txt`记录实际路径。此结果不声称修复了既有Windows长路径边界。

为恢复本机空间，#272两份已结束失败run的可再下载包缓存完整移到D盘，逐文件SHA256验证，原路径保留junction；所有内容仍在，回执在 `artifacts/issue-supervision-20261016/relocated-caches-272/receipt.json`。最终公开包、冻结源码与历史测试证据未清理。后续使用D盘私有缓存和短临时路径，完整restore及原Format Check实际通过后才能提交本次测试修正；远端剩余CI与发行/消费者验收另行回读。
