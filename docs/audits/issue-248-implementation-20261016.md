# #248：FOR UPDATE NOWAIT 与原版 SqlSugar TranLock 验证

实现日期：2026-10-16；按用户合并授权于2026-10-17（Asia/Shanghai）恢复并完成交付收尾。定时监督仍暂停。任务分支：`codex/issue248-nowait`；实现基线：`edd23c1d72c472c71ea0b8d4939df0ec5c2311e4`。本文件记录子智能体交付；根监督智能体负责集成、最终提交门禁、提交推送与 GitHub 回复关闭。根准备将本交付集成到新 main `4d6e4f98`；独立 `50f598cc` CAST/Identity 修复及其它线上分支改动不在本交付中，也没有被覆盖。

## 已实现的合同

在显式关系事务中，单个关系基表的 `SELECT ... FOR UPDATE` 取得所选物理行的排他锁，`FOR UPDATE NOWAIT` 在当前不能取得全部目标锁时立即返回 `sql_lock_not_available`。不把 SqlSugar 的 `DbLockType.Error` 改成普通等待或乐观重试。查询支持现有的 WHERE、投影、ORDER BY、分页及本事务写入可见性；复合键、无主键表的隐藏物理身份和缓冲主键更新都有验收。

锁成功保留到 COMMIT、ROLLBACK、事务/连接释放或失败提交。保存点回滚保留已经取得的锁。多行 NOWAIT 申请原子失败，不留下本次新增锁，仍保留该事务前一语句已取得的锁。普通 SELECT 不加锁，空匹配不加间隙锁。当前轻事务继续缓冲写入；实际 COMMIT 遇到已有锁时等待，之后按原始行状态重新验证，不能覆盖并发变化。

普通等待可取消且有执行预算；等待环返回 `sql_lock_deadlock`，期限返回 `sql_lock_timeout`，资源保护返回 `sql_lock_limit_exceeded`。远程 SQL 错误的顶层 `error` 保留这些域错误码，NOWAIT/死锁返回 HTTP409。沿用既有 source-generated JSON DTO，没有新增反射序列化。

## 并发与持久化边界

- 锁资源由数据库实例内稳定表对象和复制的规范物理行键组成；rename 沿用表身份，drop/recreate 使用新身份。冷重开后首次 rename 也创建有效身份。
- 候选在 store 短锁内读取；离开 store/manager/schema 监视器后等待；重新进入短锁核 schema 与单调 sequence，再返回结果。候选变化、ABA、谓词/排序/LIMIT 变化重新选行，使用最初预算。
- `TableLockOperation` 在监视器内只尝试立即申请；竞争通过私有异常展开外层操作，归还本次临时租约，再在监视器外等待并重做尚未持久化的准备。AsyncLocal scope 仅在原线程、未释放时复用，避免后台任务继承过期操作。
- 多表写入先预演全部表的规范值、identity 和隐藏行身份并取得完整写集，再进行实际 identity 预留、约束核验、WAL/事务恢复日志及应用。后表竞争不会提前消耗前表 identity。级联写入遵循相同边界。
- 自增溢出的既有合同要求保留溢出前已经预留的号码；该终止性异常路径在取得 identity 资源后写高水位并立即抛出，不再申请其它行锁或重试行写入。
- 内部独立 `PrepareBatch`/`ApplyPreparedBatch` 不假称跨调用持续持锁。应用阶段先重新取得行锁，再校验 schema、sequence、generation；任何期间写入（包括值变回原样）均拒绝陈旧批次。主 `TableManager` 在两阶段之间持续持有外层操作及全部 store 监视器。
- DDL、truncate、identity 调整、主键迁移、索引维护使用保守的数据库排他资源。在线索引在有持有者时有界让出，释放后恢复。延迟触发器提交先取得数据库排他资源再回调，避免锁重试重复触发器副作用。
- 锁表和等待队列有100000个资源、1024个等待者及64MiB保守键内存预算；锁定读候选另有限行数/内存预算，并继续遵守查询治理预算。这不是无限规模扫描或实测堆内存声明。
- 锁只存在于当前进程内；重开后不恢复锁。既有 WAL、主键/无主键恢复和事务日志格式保持不变，没有升级持久化格式。

不支持的锁定组合明确拒绝：JOIN、聚合/窗口、DISTINCT、CTE/集合/派生表/子查询、用户函数和序列、非关系源，以及嵌套锁定读取。只读多结果批次在执行第一条前预检并拒绝锁定读取。`SKIP LOCKED`、`FOR SHARE`、间隙锁、幻读保护没有由本任务新增；#233 的其它 ORM 适配和完整交付仍需单独核验。

## 测试和真实消费者

最终执行记录根目录：`D:/source/SonnetDB/artifacts/issue-supervision-20261016/issue248-work`。各 JSON spec 保存精确参数，`logs/*.json` 保存 PID/起止时间/退出码，TRX 保存逐测试结果；不以日志中的一句 PASS 代替原始结果。

| 验证 | 结果与证据 |
| --- | --- |
| 最终扩大 Core | `core-regression-final01`：3986/3986，0失败/跳过；覆盖 SQL、Table、ADO、Outbox、Metadata，含74个锁组件/生命周期/并发用例 |
| 最终真实 Server | `remote-regression-final01`：342/342，0失败/跳过；含24个真实 REST/h2c 锁用例，覆盖等待/取消、事务及连接关闭、服务端租约过期和失败提交 |
| 显式 Data AOT/Trim 与 Core | `aot-data-final01`：Rebuild，显式开启 IsAotCompatible/EnableAotAnalyzer/EnableTrimAnalyzer，0警告/错误 |
| Server 原项目 AOT 分析 | `aot-server-final01`：按项目现有 IsAotCompatible/RDG 作用域 Rebuild，0警告/错误；未把宿主 AOT 全局属性传入 netstandard 生成器 |
| 完整 restore/Format | `solution-restore-final01` 2026-10-16 09:29:19Z→09:29:34Z exit0；规定原命令 `solution-format-final01` 09:30:36Z→09:32:12Z exit0。工作区加载警告原样保留；2026-10-17恢复时35份已测试源码及9个运行时SHA全部一致，因此沿用原结果，根在自己的最终待提交树上仍须重新门禁 |
| 最终原版 SqlSugar 消费者 | `consumer-positive-build-final01` 构建0警告/错误；`consumer-positive-embedded-final01`、`consumer-positive-remote-final01` 的 embedded/REST/h2c 各34/34，全部exit0；2026-10-17 00:42–00:43（Asia/Shanghai）运行。`final-consumer-bindings01.json` 核对实际消费者和Server与342个测试使用的 Core/Data 程序集相同 |

原版上游固定为 `IoTSharp/SqlSugar@65e8226ce25a8e1cb9cca2590683f1fd6778cab3`（PR1457 的来源）。603个源码文件已逐一核 Git blob 和 SHA256，未补丁修改。`Queryable<T>().TranLock(DbLockType.Wait)` 输出 `FOR UPDATE`，`Error` 输出 `FOR UPDATE NOWAIT`；`Storageable.TranLock` 使用同一公开路径。

正消费者逐协议覆盖34项：实际活动持有者的即时冲突、不同键/缺失键、提交/回滚释放、Wait 完成、Storageable 冲突后成功更新，同步和异步一致。每次运行打印4个实际加载程序集的 SHA256，冻结时回核其内容。它复用历史名为 `Remote-Negative.ps1` 的通用宿主启动器并传入正消费者 DLL；`ProbeMode` 未使用，不能据名称把正结果误写为负结果。

真实 REST/h2c 请求回执区分协议与路径：显式事务及锁定 SQL 走 HTTP JSON `/sql/transactions/...`，h2c 是 HTTP/2；普通只读控制出现 Frame 请求。没有把这些结果称为 Frame 二进制锁事务。

## 保留的失败与修复

准备报告 `issue-248-preparation-20261016.md`、`preparation-checkpoint01.json` 和原始负消费者/0-of-12验收仍是实现前的历史证据，未改写成成功。历史源路径不是独立源码快照；最终交付另复制冻结源。

- 初轮扩大回归 `core-regression01` 为3611/3620、9失败：取消异常合同3项、内部恢复批次缺作用域4项、自增溢出高水位1项、Outbox schema准入/锁顺序1项。修复后受影响整类232/232，再由最终3986/3986覆盖。
- 早期新增测试的 Parse 返回值误用、缺列名 INSERT、重复 MarkCompleted、无效 ALTER及删除结果类型等编译/夹具失败均保留。未降低生产约束来通过测试。
- 远程首次构建因新 worktree 未初始化固定 CoAP/MQTT 子模块而失败；初始化原固定提交后继续。随后测试的 DbTransaction 类型转换编译错误修正，原结果保留。
- 原上游 SqlSugar 自身构建有13条 nullable 警告，独立记录；不把它混算成 SonnetDB 或正消费者零警告构建。

## 交付与尚未声称的结果

最终 `implementation-checkpoint01.json` 索引交付源、冻结源副本、证据、测试和实际运行时文件；`implementation-frozen01/source` 与工作树逐文件相同。父智能体应在自己的新基线上保留独立修复并重新验收，按 AGENTS 规定对最终待提交树执行完整 restore 和原 Format 命令后才能提交。

本任务没有发布正式 NuGet/安装包，没有运行 NativeAOT publish 产物，没有修改 SqlSugar 上游，也没有据源码验证推断 Chloe/#233 的其它验收完成。GitHub回复和关闭按用户“已实现项提交、推送并关闭”的当前授权由父智能体处理，同时保留上述正式交付边界。
