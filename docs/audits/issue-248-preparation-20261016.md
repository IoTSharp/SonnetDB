# #248 NOWAIT：隔离准备检查点

日期：2026-10-16；源码基线：`aa4fbffba0caf7d400cb10e7ebb1d45cc99ff5c5`。工作树为 `D:/source/SonnetDB-worktrees/issue248-nowait`，分支为 `codex/issue248-nowait`。

本次只新增独立组件、组件测试和消费者/验收草稿；没有接入现有解析器、执行器、TableManager、TableStore、事务、Data 或 Server。**#248 尚未实现，保持 OPEN。** 按主监督分工，必须等 #250 → #249 完成共享源代码交接后再接入。提交、推送、根 HANDOFF/CHANGELOG 和线上回复由主监督负责；本准备不单独提交为完成项。

## 已取得的证据

证据根目录：`D:/source/SonnetDB/artifacts/issue-supervision-20261016/issue248-work`。命令参数、PID/启动时间、退出码、标准输出/错误、TRX 和二进制 SHA 由最终 `preparation-checkpoint01.json` 索引；首次失败没有删除或改写。

| 实际运行 | 结果 | 可以证明的范围 |
|---|---:|---|
| 独立 helper 首轮 | 13/13 | 首轮 helper，尚未覆盖随后期限和内存预算改动 |
| 独立 helper 最终 `helper-tests02` | 18/18，0 失败/跳过 | 仅行锁组件；没有调用 SQL 或 Data |
| 公开 4.0.0 embedded 消费者 | 10 控制通过、28 明确拒绝 | 实际公开 DLL 与原版 SqlSugar 的缺口 |
| 当前 aa4fbffb embedded 消费者 | 10 控制通过、28 明确拒绝 | 当前 Core/Data DLL 的缺口 |
| 当前 REST / frame-http2 配置消费者 | 每配置 10 控制通过、28 明确拒绝 | 真实服务建库、查询和事务生命周期；锁语句先被客户端 Data 守卫拒绝 |
| 真正 ADO 验收草稿 `acceptance-source-negative02` | 0/12，12 失败，0 跳过 | 各例在第一条锁语句得到 `SqlParseException`，证明当前还不能履行验收 |

`consumer-public-negative01` 首次缺少外部 SqlSugar 的 Newtonsoft.Json 运行库而失败；随后仅为外部消费者增加显式依赖引用并重跑。`acceptance-source-negative01` 首次因集合表达式选择 Span 断言重载跨 await 导致 CS4007；改成先等待并核单行后得到真实 12 项运行失败。两次原失败均保留。

远程消费者通过实际进程和端口启动 Server，网络日志确认 REST 为 HTTP/1.1、frame-http2 配置为 HTTP/2，普通控制查询确实出现 Frame 请求。**28 条锁定读没有到达远程 SQL 执行端点**：`SndbCommand.RejectUnsupportedLockingRead` 提前抛出异常。显式事务的创建/提交/回滚使用 HTTP JSON `/sql/transactions` 路径；不能把它描述为 Frame 二进制事务或 Server 已执行锁语句。没有做锁的正向远程验收、持久恢复或生产压力验证。

源码 aa4fbffb 的已运行程序集：Core SHA256 `3BCBF3DE4ED884098273F90938ADEBEB2F3CB064C8DE5D05933F72BEB806BCC6`，Data SHA256 `8F80F37FBEE32E755B9B77D103826D20EA33702E36AFC7B7DF3289239E9B8CEC`，归属由主监督 #251 根复审报告核验。公开 DLL 来源于此前只读冻结的 `issue251-work/public-02/stable-bin`，未修改其内容或证据。

## SqlSugar 原始源码归属

GitHub #248/#233 和 SqlSugar PR1457 已实际回读并保存 JSON。PR1457 的 `IoTSharp/SqlSugar` 原头为 `65e8226ce25a8e1cb9cca2590683f1fd6778cab3`。从此前 #250 的只读副本复制603文件后，逐文件以 Git blob 对照线上 recursive tree，603 全部一致，零缺失/差异。`sqlsugar-source-verification.json` 同时保存每文件 SHA256。

原始 `QueryableProvider` 在事务内把 `DbLockType.Wait` 输出为 `for update`，把 `DbLockType.Error` 输出为 `for update nowait`；SonnetDBQueryBuilder 追加该子句，Storageable 走同一路径。消费者验证实际生成的后缀，未修改上游选项、未把 Error 改成 Wait。原版外部构建成功但有13条上游 nullable 警告；这不是 Sonnet 产品的零警告构建证据。外部 SqlSugar 自带 Newtonsoft.Json 依赖，未添加到 Sonnet 核心运行时。

## 准备组件的合同

新增 `TableRowLockManager` 由调用者提供稳定表 token 和规范存储行身份。表身份按引用相等，行身份复制为十六进制字符串，不依赖用户提供数组后续修改。`TableExclusive` 与该表所有行冲突，用于未来 schema/主键迁移屏障。当前尚无 TableManager 创建或持有此组件。

申请集合去重，先检查全部冲突与容量，再一次性增加引用。NOWAIT 不排队，立即以 `sql_lock_not_available` 失败。失败不保留本次部分锁，也不释放已有事务锁。成功 lease 可在语句失败时释放本次引用，或 `Retain` 到 owner 生命周期。空集合不产生持锁资源。

普通等待通过异步通知，不在等待时持有组件 monitor；调用者仍必须保证未持有任何 TableManager/TableStore/schema monitor。队列只阻挡重叠资源，避免无关行全局排队；已有 owner 可以重入其锁。等待图检测环并拒绝最新申请，保留已有锁，要求调用方回滚/重试。取消保留原令牌，等待期限在唤醒后再次检查。owner 和 manager Dispose 释放资源并唤醒等待者。

默认限制为100,000个持锁资源、1,024个等待者，以及64 MiB的资源预算。预算按键字符串 UTF-16 字节加每项128字节保守开销计算，同时计入排队集合；不是精确 CLR 堆上限。创建 Resource 前的临时字节/字符串分配、owner 数量及调用者仍持有的 lease 对象不属于该计数，整库内存治理仍须结合既有查询预算和事务会话限制。预算拒绝不会部分取锁；取消/完成释放排队预算。不得据此宣称任意并发负载内存均已受完整保护。

异常类具有拟定的稳定 `NotAvailable`、`Deadlock`、`WaitTimeout`、`LimitExceeded` 代码和中文 public XML 文档。**现有 SqlErrorMapper 尚未注册这些异常**，所以组件代码暂不具备远程错误合同。

## 接入方案与必须保持的边界

1. 锁表归属于同一 `Tsdb` / `TableManager` 数据库实例，所有同目录 SndbConnection 共用。当前 SharedSndbRegistry 提供嵌入式共享，Tsdb 目录 `FileShare.None` 租约禁止另一个进程独立打开同一目录。不得做每连接锁表或只在 SQL 命令内加 monitor。
2. TableManager 分配稳定表 token，metadata/schema对象替换和 rename 保留 token，drop/recreate 使用新代际。行锁使用 `TableRow.PrimaryKey` 的规范存储身份，覆盖复合主键及 #243 无主键表的内部不透明行身份，不用投影值、列名或字符串化 SQL 作为身份。
3. 锁定读取只在显式事务可用。初始语法面为单个关系基表上的 `FOR UPDATE [NOWAIT]`，WHERE、投影、ORDER/LIMIT 必须锁定实际返回候选所对应的底层行。连接、聚合、DISTINCT、集合运算、派生表/CTE、图/文档等不能保留底层行身份的路径必须明确拒绝；SKIP LOCKED、FOR SHARE 不属于 #248。不得删除原拒绝测试后让复杂路径静默退化为 SELECT。
4. 在短暂读视图中选候选及其版本，释放 store/schema monitor 后申请锁，再进入一致的读视图复验表代际、行身份、版本和谓词；期间发生写入、删除、PK变化、排序/LIMIT候选变化时，释放本次 lease 并按同一剩余预算重选。未匹配行不建立 gap/predicate lock；不能在复验失败后返回旧结果。
5. writer 必须使用同一锁表。包括事务最终提交、autocommit DML、直接公开 TableStore.Insert/InsertMany/ApplyBatch/Upsert/Delete、级联写入、旧键删除/新键插入、TRUNCATE/drop/rename/列和主键迁移。先规划操作、在 manager/store monitor 外取得全部必要资源，再进入既有提交临界区复验并应用。级联扩展新增资源要退出并重试；不得在 `ApplyTransactionCore` 已持 manager 和所有 store monitor 时等待行锁。
6. #226 主键迁移需要表排他锁涵盖候选生成、身份重写、日志发布和恢复边界，兼容 #243 内部身份。不能用“更换 schema 对象”逃避现有锁；也不能扩大持久文件格式或预占版本来实现纯内存锁。
7. owner 与 `SqlTransactionContext` 生命周期一致。COMMIT、ROLLBACK、失败完成、事务 Dispose、连接 Close/Dispose、远程 session 到期或服务 Dispose 均释放。锁定语句失败只撤销本次 lease；事务此前成功锁保留。savepoint 需要明确定义：建议与已有成功语句锁一致保留到事务结束，回滚保存点只恢复数据变更，不能意外解锁其它语句。
8. 嵌入式同步/异步和远程 HTTP1/h2 传播同一稳定冲突码；不能把 NOWAIT 转换为普通 timeout 或乐观重试。SqlErrorMapper、HTTP错误和 Frame错误应沿用已有 source-generated DTO，禁止反射 JSON。普通等待读取按调用取消/请求预算工作，且 remote 会话信号量等待/租约过期能清理锁。
9. 保留既有 ReadCommitted 与乐观 Serializable 的隔离合同；行锁不等于范围锁，不能宣称自动解决全部 phantom。#233 所需普通行锁基础必须真实实现，但其 Chloe UpdLock、NoLock 及其它条件需单独核验，不能因 #248 完成直接关闭 #233。

## 共享实现后的验收顺序

当前 `AcceptanceTests.cs.txt` 是独立外部项目编译的12例草稿，未加入主测试树。这12例覆盖真实连接的同步/异步 NOWAIT、独立行/空匹配、COMMIT/ROLLBACK/Dispose释放、部分多行冲突和空匹配后插入；当前全部在首条锁语句失败，**尚未运行到后续断言**。

接入时先迁入正式测试并加入内部可控阶段钩子：候选读完、锁申请进入等待、取得锁准备复验。用事件确认等待已建立，再释放持有者或触发取消，避免凭短 sleep 猜测竞争。补普通 FOR UPDATE 等待、writer等待、取消精确令牌/命令超时、连接关闭、保存点、远程租约过期；确认失败前后既有锁与事务数据一致。

再用确定性钩子覆盖读取到加锁间更新谓词/排序字段、删除后重插同键、复合主键迁移、无主键身份、级联计划变化及DDL排他。锁后复验不能仅比较当前值，必须识别 ABA 的版本/代际变化；有界重试达到预算应明确失败而非返回过时行。

最后在真正 Server 分别跑 REST 和 HTTP/2，核对锁语句确实到达事务执行端点，并用原版 SqlSugar `TranLock(Wait/Error)` 与 Storageable 同步/异步验证。此后完成全量适当回归、Data/Server AOT分析、根规定 restore/Format，交主监督独立提交。正式匹配包发布后还需从实际包重跑；当前源码候选和外部源码消费者均不能代替正式交付、上游发布或关闭条件。

本检查点没有 commit/push/线上评论/关闭操作，没有修改 #251 冻结树、旧失败记录或 parity-results。
