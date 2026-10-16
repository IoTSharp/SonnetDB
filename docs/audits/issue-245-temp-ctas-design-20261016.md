# #245 会话临时表与 CTAS：隔离准备及交接设计

日期：2026-10-16。状态：**准备完成，功能未实现**。源码基线为 `edd23c1d72c472c71ea0b8d4939df0ec5c2311e4`；本切片没有修改现有生产文件，等待 #248 → #247 的实际提交交接。以下合同是待实现、待验收的目标，不是当前能力说明。

## 原文与实际消费者

已读取 GitHub #245 和 #227 正文/评论、SqlSugar PR #1457。线上读取快照保存在 `artifacts/issue-supervision-20261016/issue245-work/upstream/`。SqlSugar head 为 `65e8226ce25a8e1cb9cca2590683f1fd6778cab3`，原始 provider 没有变化。

#245 要求会话临时关系表、CTAS/空 CTAS、两个连接同名隔离、关闭和失败清理、正确列元数据，以及调用方已有事务内的至少 10,000 行 Fastest 集合更新/合并。#227 负责持久关系表 CREATE/ALTER/DROP/INDEX 的完整事务迁移；不能因 #227 尚未完成而省略 #245 必需的临时对象事务基础。

真实 `SonnetDBFastBuilder.CreateTempAsync` 当前调用维护接口创建普通唯一工作表，并附加一个自增主键。成功和引擎 CHECK 失败的清理已实际观察。`BulkUpdate` 为 10 条多值 INSERT + 1 条 UPDATE FROM；`BulkMerge` 在此基础上加 1 条 INSERT SELECT。没有逐行查存在/逐行 UPDATE，但这些通过结果不代表原生临时表。

## 已运行证据

| 引擎/入口 | 控制通过 | 目标失败 | 解释 |
| --- | ---: | ---: | --- |
| edd23c1d 同源嵌入式 Native | 3 | 27 | 普通 DML 事务同步/异步和九种声明类型源结果通过；临时表、CTAS 在解析阶段拒绝 |
| 同源真实 REST Native | 3 | 27 | 真实 Kestrel HTTP/1.1，服务端返回 SQL 解析错误 |
| 同源真实 h2c Native | 3 | 27 | 真实 HTTP/2，15 个 Frame 请求为已支持查询；不能称临时表经 Frame 执行 |
| 同源嵌入式 SqlSugar | 6 | 8 | BulkCopy、无事务更新/合并同步/异步、CHECK 失败清理通过；已有事务均在普通工作表 DDL 失败 |
| 同源真实 REST SqlSugar | 6 | 8 | 相同结果；实际 HTTP/1.1 请求 |
| 同源真实 h2c SqlSugar | 6 | 8 | 相同结果；24 个 Frame 请求，事务仍走 HTTP/2 REST 会话端点 |
| 正式 4.0.0 Core/Data + 原 SqlSugar | 6 | 8 | 本次实际运行同一消费者，普通工作表与事务缺口一致 |
| 4.5.0-preview.ef266.5 Core/Data + 原 SqlSugar | 6 | 8 | 本次实际运行同一消费者，普通工作表与事务缺口一致 |

全部负基线进程保留非零退出码，未把预期失败改算成功。Native 后续隔离、清理、类型继承、取消和事务断言因语法拒绝尚未到达；只有源表类型控制真正验证了 INT、DECIMAL、TIME、DATETIME、BOOL、JSON、BLOB、DURATION、DATETIMEOFFSET 的空结果声明及非空值。

最终消费者构建 `consumer-build04` 为零 warning/error；源文件与实际运行 DLL 的 SHA 在冻结清单中。引擎和 Server 使用根 #249 最终同源的**提交前构建**，不是 edd23c1d 提交后的重建。Core SHA `8995B3C13DA64DF11FA363AF62726ECFA43D595565291FA3BD49F5F4F76D02AF`，Data SHA `BEED26E4C75B293CB1486124ACA17AD9713B8265C93DB623CB5098CAFD4B0369`，Server SHA `75D258D4049B5C270336D6F0895937A6A511ED97FD2C38BD71134A5361BCB56D`。

早期失败全部保留：`consumer-build01` 的探针 record 名与属性重名；`native-source01` 的夹具省略了引擎要求的 INSERT 列表；`sugar-source01` 因 Fastest 主动关闭日志事件，未采到内部命令。后者通过公开 Begin 回调重新开启 `IsEnableLogEvent` 观察，未替换 SQL、事务或执行结果。`remote01` 的中文日志被子进程默认编码错误解码；最终 `remote02` 显式 UTF-8 捕获并重跑，旧证据不覆盖。

本轮没有改生产代码，没有运行新的生产 AOT/NativeAOT 发布或故障恢复验收，没有执行提交门禁、提交、推送、外发 ORM 候选或关闭 Issue。正式发布能力不能由本准备或旧包控制推定。

## 拟实现的 SQL 合同

支持最小语法：

```sql
CREATE TEMP TABLE Staged(Id INT, Value INT, PRIMARY KEY(Id));
CREATE TEMP TABLE Staged AS SELECT Id, Value FROM Target WHERE 1=0;
CREATE TABLE Copied AS SELECT Id, Value FROM Target;
DROP TABLE Staged;
```

`TEMPORARY` 可作为 `TEMP` 同义词；不引入 SQL Server `#name`、ON COMMIT 变体、全局临时表或额外的 SqlSugar 能力接口。普通 CTAS 先只要求事务外执行；持久 CTAS 在已有事务内的发布/回滚归 #227。临时 CTAS、临时显式 CREATE/DROP 和其中 DML 必须参与已有 ADO 事务，不能把命令挪到另一个连接或先隐式提交。

CTAS 在查询完成且目标准备成功后一次发布；失败、超时、取消或预算耗尽不得留下可见的半张表。来源只读 SELECT，拒绝截断的预览结果，不可把 `Truncated` 部分结果悄悄当作完整表。需保留语句级快照/已有隔离级别；每行类型校验不能依靠首行取样。

投影输出名称按 `SelectExecutionResult.Columns` 顺序保存，含创建时拼写与转义双引号。类型取绑定后的 `ColumnInfo`/`ColumnSchema`，不由运行时值推断，所以空集和全 NULL 同样可创建。保留 DECIMAL(p,s)、TIME/DATETIME tick 精度、DURATION 有符号 tick、DATETIMEOFFSET 的 UTC tick 与 offset 语义，BLOB 原字节和 JSON 声明；避免把 DURATION/TIME 或 DATETIMEOFFSET/DATETIME 合并。未知表达式声明和未定类型 NULL 应明确要求 CAST，不静默选择 STRING/FLOAT。

CTAS 只复制投影的名称、值域类型及 DECIMAL 精度/scale，不继承主键、外键、索引、CHECK、默认值、自增、ROWVERSION 或源列备注；输出列可空。无主键行使用已经存在的关系表内部行标识能力，不把一个新的 SQL 自增列暴露给消费者。重复投影名或仅大小写不同的列名按 #211 拒绝，不能自动改名。

会话内未加引号按 OrdinalIgnoreCase 绑定，引号内按 Ordinal 绑定；执行器只使用绑定结果，不自行 lower-case。两连接同名临时表合法且完全隔离。第一版建议拒绝同一可见会话中临时名与持久表/measurement/视图/MV 名冲突，保留统一命名空间规则；不引入隐式遮蔽。实际实现前由根按 #211 合同核准此项，并补正向/反向碰撞测试。

## 会话与存储边界

现有 `EmbeddedConnectionImpl` 仅共享数据库引擎引用，未持有临时 SQL 会话；`SqlExecutor`/`TableManager` 都以数据库对象管理普通表。远程 `SqlTransactionSessions` 是短期事务上下文：当前活动上限 128，2 分钟租期，15 秒清扫，提交后进入终态。不能直接把这个事务实例当作连接临时表会话，否则 COMMIT 会错误删除临时表，或一个连接多次事务丢失所属对象。

需要独立、由连接生命周期拥有的 `SqlSessionContext`，其临时目录和表存储不可进入持久 catalog、普通备份和其他连接的 schema 列表。嵌入式 Close/Dispose 同步释放；Reader Dispose 只释放读取资源，保留会话。再次 Open 创建新会话。无会话的原始单次 SQL 请求不能保留跨请求临时对象，可拒绝或只提供该请求作用域，必须文档明确。

远程会话需不可猜测的句柄，并绑定数据库对象、数据库名称及授权身份；每次 SQL、metadata、DROP、关闭与事务请求验证所有权。不能以 HTTP TCP 连接、Frame channel、数据库名或客户端自行传入表名前缀替代授权会话。HTTP/1.1 与 h2c/Frame 应携带同一个逻辑会话；若第一版事务和临时表都路由 REST，必须保留请求级实际传输记录，不能称全 Frame。

Close/Dispose 应先回滚活动事务，再删除该会话临时状态。远程断网的释放不能保证立即到达服务端，所以需定义有界失联清理和租约续期；不能简单沿用 2 分钟事务终态租约使仍打开的正常空闲连接丢表。建议首次临时操作惰性创建连接会话、显式 close + 活跃会话心跳 + 失联租期，并给服务端会话数/表数/行数/字节配额。重启后临时对象不恢复、不能复活；独占 scratch 清理只删除可证明归属的目录，失败要记录和重试，不能影响持久表。

统一关系绑定应返回包含所属会话/管理器、表原名和稳定对象标识的句柄；JOIN、UPDATE FROM、INSERT SELECT、CTE/子查询和元数据必须使用同一解析结果。仅把 SQL 文本临时改写成唯一普通工作表不满足隔离、catalog、自动清理与权限合同。#248 的锁键和 #247 的过程内部执行也必须保留该会话；两个会话同名临时表不可互相争锁，临时表操作不能绕过目标持久表的锁/事务检查。

## 事务基础与 #227 分工

临时表创建/删除应有事务局部目录覆盖层；行写入复用既有 read-your-writes 和集合 DML 机制。事务创建后回滚删除新对象；事务删除已存在临时表后回滚恢复原对象及数据；事务提交保留临时表至连接关闭。对持久目标表的更新仍走原持久事务 journal，不能为了临时工作表分开提交。

当前 `SqlTransactionContext` 以 `schema.Name` 归集 mutation；实现必须使用包含作用域的对象身份，不能把两连接的 `Staged` 混在同一事务/锁空间。持久目标和临时源混合 UPDATE FROM 的来源应读到同一事务已导入的全部 10,000 行。临时目录、行变更与持久提交需整体预校验；不得在持久 journal 已完成后再执行可能失败且不可回滚的临时目录发布。

必要范围包括：事务内临时 CREATE/CTAS/DROP、临时行提交回滚、持久目标集合 UPDATE/INSERT、取消/关闭/执行失败清理。#227 继续负责普通持久表的多步 CREATE/ALTER/DROP/INDEX 迁移、结构失败恢复和其并发 DDL 边界。本项不重新实施已支持的普通 DML 事务，也不将“临时 DDL 例外放行”泛化成所有 DDL 可用。

## 版本、预算与回归

保持当前 schema v16、MV snapshot v4、SQL result v6。临时目录不得冒充持久目录的一个新增 schema flag；如后续设计确需持久格式变化，必须按 AGENTS 升版本、迁移/拒绝旧版并补 CHANGELOG，不能静默扩结构。空 CTAS 类型映射应复用现有声明类型协议，无须为本准备增加结果版本。

CTAS 物化、临时行数/内存/磁盘、源查询、绑定表达式深度及输出列数都应受现有 query resources 和会话累计配额约束。取消要进入扫描/构建/导入循环；每个阶段失败需保留目标未发布或回滚后的状态。预算/异常测试必须实际到达构建阶段，不能用解析失败冒充清理成功。

27 条 Native 目标探针已经写出，但其断言尚待实现后逐一到达。后续必须补真实执行阶段的预算失败、两连接并发、远程失联租期、服务重启无临时对象、持久 CTAS 原子发布恢复、#248 锁冲突及 #247 过程临时表共享上下文。它们是未完成事项，不能计入本次通过数。

## SqlSugar 最小候选

`scripts/validation/issue245-temp-ctas/sqlsugar-pr1457-native-ctas.patch` 仅替换 `CreateTempAsync`：从 DataTable 列名生成现有双引号方言，执行 `CREATE TEMP TABLE ... AS SELECT ... WHERE 1=0`，成功后登记临时名。保持现有批量 INSERT、UPDATE FROM、INSERT SELECT 和删除路径；没有修改 SqlSugar 公共核心、关闭事务或逐行回退。

补丁已在独立目录针对原文件实际 `git apply --check`/`apply`；结果与候选按 LF 规范化逐字符一致，原始 SHA 不同仅由 Git 应用后的行尾差异，两个 SHA 均保留。独立 FastBuilder 类使用实际 SqlSugar DLL 编译为零 warning/error，**这只是组件编译**。全 provider 严格编译被原有其他三个文件的四条 nullable 诊断阻断；原失败保留，没有压警告/降门禁或顺带修改上游代码，因此没有候选完整 provider/真实 Fastest 运行证据。

`sugar` 模式验证原有普通工作表路径；后续 `sugar-native` 模式会额外要求实际命令恰为一个临时 CTAS，且不得出现普通 CREATE TABLE。实现后还需整合真实 provider 编译、10,000 行各同步/异步提交回滚、复合键、部分列及 DECIMAL/TIME/DURATION/DTO 的类型消费，不由本补丁草稿推定可用。

## 下一次顺序

1. 根交付 #248、#247 的实际 SHA 后，先核本准备冻结清单，允许的快进/集成不覆盖旧证据。
2. 确立连接会话、统一关系句柄和临时事务 overlay，补生命周期/隔离测试；再接 AST/Parser/Executor/ADO/远程元数据。
3. 实现 CTAS 声明推断和原子物化，再跑全部 Native 探针和必要预算/恢复/并发用例。
4. 完成真实 SqlSugar provider 构建、运行 `sugar-native` 三协议消费者，核实际命令数量、事务和失败清理。
5. 根独立复审、AOT 分析、完整 restore/规定 Format、独立提交、批次推送及精确 CI 后，按用户最新要求判断已实现 issue 的关闭；正式发行与上游合入状态仍须据实记录。

原工作树、所有失败日志、数据库目录和产物保留。本准备不提交是因为当前授权仅限隔离准备，生产实现和根集成尚未开始。
