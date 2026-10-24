Warning: truncated output (original token count: 192904)
Total output lines: 2637

# Changelog

本项目所有重要变更将记录在此文件中。
格式遵循 [Keep a Changelog 1.1.0](https://keepachangelog.com/zh-CN/1.1.0/)，版本号遵循 [SemVer 2.0.0](https://semver.org/lang/zh-CN/)。

本文件记录已发生的实现或文档变更；`[Unreleased]` 表示已经实现但尚未归入发布版本的变更，不单独证明已合入远程分支或发布。未来工作放在 [ROADMAP.md](ROADMAP.md)，从本文件移出的旧 Planned 草案保存在[历史计划归档](docs/audits/historical-plans-from-changelog.md)。历史条目中的阶段状态、测试数量和性能数字只适用于其当时声明的范围，不能直接当作当前版本或生产门禁的结论；2026-09-05 的[核查记录](docs/audits/changelog-verification-20260905.md)列出已确认的问题和仍缺失的证据。

## [Unreleased]

### Changed
- GH-Issue #349：固定关系表 `VECTOR(N)`/`GEOPOINT` 继续为 measurement `FIELD` 专用类型；`DataTypes.SupportsRelationalColumn=false` 与显式标量/JSON 替代模型保持不变。新增跨 Core、REST、Frame 和 ADO 的稳定错误码 `sql_relational_type_unsupported` 及可行动提示，避免 FreeSql/其它 ORM 解析本地化文本；边界与证据见 `docs/audits/issue-349-relational-specialized-types-20261024.md`。
- GH-Issue #348：补充 ADO `GetSchema` 的 schema/table 结构化身份回归，覆盖同名表、大小写限制、索引、跨 schema 外键和重开后的嵌入式/REST 路径；文档明确默认 `main` 及 Views/DocumentCollections 的当前单 schema 边界。HTTP/2 frame 与正式 FreeSql 包仍按环境和发布边界单独验收。
- GH-Issue #347：补齐 `FOR UPDATE SKIP LOCKED` 的分页语义：先按稳定顺序逐行尝试锁定、跳过竞争行，再对可锁定行应用 `OFFSET`/`LIMIT`，只把返回页的临时锁保留到事务结束；新增复合主键、空/竞争页、嵌入式与远程 ADO 回归。同步修正 SQL/ADO 锁定读文档，明确 `FOR SHARE` 仍以 `sql_locking_read_unsupported` 拒绝。
- GH-Issue #345：补充关系事务 ACID/D 持久性审计与默认/显式强持久化配置下的 COMMIT 返回后真进程强杀回归；确认多表提交无条件同步关系 WAL、完成标记后才返回成功，并明确进程崩溃与物理掉电/介质损坏边界。详见 `docs/audits/issue-345-acid-durability-20261024.md`。
- GH-Issue #344：补齐 ACID/I 的确定性隔离验收：Core 跨表读写与只读依赖、保存点回滚后的首次读保留，嵌入式 ADO 默认/显式 `ReadCommitted` 双连接可见性及同步/异步 `Serializable` 冲突整笔重试，远程 REST/Frame HTTP/2 同步 ADO 合同与隔离级别协商；不重复实现既有 Serializable 引擎，详见 `docs/audits/issue-344-acid-isolation-20261024.md`。
- GH-Issue #343：修复同一子行上多个 `ON DELETE SET NULL` 外键级联动作被去重而只置空一列的问题；级联展开现在合并所有受影响列，并在显式事务 overlay 中重放合并后的 mutation。新增嵌入式关系约束/索引失败回滚、级联重开与事务提交回归；未扩展未支持的 Server/强杀证据边界。
- GH-Issue #342：新增关系表多语句、多表业务事务原子性回归，覆盖完整提交、显式回滚、语句失败后的继续执行，以及提交前故障注入后的双表恢复与重复重开；未修改事务或存储格式。
- GH-Issue #340：统一关系表事务隔离级别文档；明确默认/`Unspecified` 为 `ReadCommitted`、ADO.NET 显式 `Serializable` 仅适用于关系基表并采用表级乐观提交校验，补充嵌入式/远程会话协商、SQL 文本 `BEGIN`/`/sql/batch` 仅支持默认隔离级别及冲突后整笔重试要求。同步修正文档中的版本证据边界；不据此推断已发布 NuGet/Server 的最低版本。
- FreeSql/SonnetDB 关系 provider 收口：关系表 `FOR UPDATE SKIP LOCKED` 已接入 parser、逐行锁竞争跳过、序列复验和锁清理；ADO `Tables`、`Columns`、`Indexes`、`ForeignKeys`、`Views` 与 `INFORMATION_SCHEMA` 保留真实 schema 身份，Server/Remote schema DTO 同步接线。Core/ADO 定向 92/92、远程 REST 65/65 通过；HTTP/2 frame 宿主未建立精确 HTTP/2，相关 32 项保留 FAIL/NOT_RUN 边界。FreeSql provider 230/230 通过，正式包与正式发布另验。

- 首次安装向导增加前后端输入校验，并将管理员与首枚 Token 合并写入，避免非法字段或重复 Token 导致用户残留；Compose 增加 `/healthz/live` 存活检查并补充启动、状态查看和日志排查文档。

- GH-Issue #227：将受限关系表 `CREATE/ALTER/DROP TABLE` 与 `CREATE/DROP INDEX` 接入嵌入式 ADO.NET 事务私有 workspace；提交使用 journal v2、目录永久 floor v19 与成对 schema/table 身份，回滚和第二步 DDL 失败不改动持久表，已有数据、索引、重开恢复均有真实 ADO 回归。远程 DDL 仍为 NOT_RUN，正式包与上游 ORM 交付另验。

- GH-Issue #232 Core：关系表使用独立的 schema/table 成对身份，默认 schema 为 `main`；支持两段限定名、逐段大小写与引号合同、非 `main` 无歧义存储路径、目录格式 v18（v1-v17 旧表归入 `main`），并让 SQL CRUD、索引、外键、事务与重开恢复保留该身份。Data/Server/远程元数据接线仍待后续切片。

- GH-Issue #245：补真实固定SqlSugar Fastest原版/最小原生TEMP CTAS候选，三协议候选各10/10，覆盖一万行集合写、已有事务提交/回滚、BulkMerge、隔离及失败清理；保存原版普通工作表控制、Windows长路径失败和观测工具修正回执。正式包及上游发布另验。

- 修复 Windows 原生目录刷盘的长路径：在 CreateFileW 前规范化绝对/相对路径并使用扩展本地或 UNC 前缀，保留真实 I/O 失败与原持久化要求；覆盖超过260字符的实际 WAL 目录，恢复长 TEMP 根下 SqlSugar Fastest 原生 CTAS。

- 保存 #247 SqlSugar、#242 XCode、#233 Chloe 的固定上游消费者探针、最小 provider 补丁和真实三协议审计；原版失败、正式 4.0 包缺口及 HTTP/2 REST 事务边界分别保留，不把候选验证写成上游或稳定包已发布。

- 精度兼容：声明为旧 DATETIME 的 CURRENT_DATETIME()/CURRENT_UTC_DATETIME() 明确返回毫秒对齐值并保留 Local/UTC Kind，恢复已有 VALUES/动态 DEFAULT/ALTER DEFAULT；这是两个时钟函数的精度合同收窄。完整时钟使用 CURRENT_UTC_DATETIME_OFFSET() 后显式 CAST DATETIME2；其他日期 CAST/运算不截断。

- GH-Issue #236 Core 切片：新增 UTC 100ns DATETIME2、原生 DateTime 参数、完整 ticks 的行/主键/索引/覆盖载荷与比较/分组/溢写、关系声明预检及无损迁移。旧 DATETIME 布局保留且亚毫秒写入明确拒绝；measurement time 的既有 Unix 毫秒参数合同在已绑定上下文保留。表目录永久 floor v17、物化视图声明 v5，旧格式仍读，旧引擎不支持新目录且降级须恢复升级前备份；FileHeader 未改。Data/Protocol/Server 与正式消费者仍在后续片处理。

- GH-Issue #242：原生关系 REPLACE 收集主键及全部非 NULL 唯一冲突，以同一事务完成真实 DELETE→INSERT、默认值/identity/ROWVERSION、触发器及外键删除动作、最终唯一索引发布；影响数为新插入行数，显式事务保留有限等待的库级写门禁。Core134/134为定向证据，真实远程/XCode与正式发行另验。

- GH-Issue #245：提供显式 SqlSessionContext 的会话 TEMP/COW/CTAS Core、临时对象备份排除，以及独立 v1 创建日志的持久 CTAS 原子发布和恢复；FileHeader 未改。根提前修复裸 CAST Decimal 的未知 precision/scale 回归，CTAS 默认值域不影响公开元数据。ADO 嵌入式连接拥有并回收会话，远程独立 connection-session header 贯穿 SQL、Frame、过程、metadata 和事务，按数据库及认证身份绑定；空闲 5 分钟租约、有限准入、失败清理保留 owner/配额并有界重试。关闭先回滚事务，过期不自动重建或重发业务 SQL；过程只访问连接预建 TEMP，过程体 TEMP DDL 及正式包消费者另验。

- GH-Issue #247：标准 StoredProcedure 命令与 IN/OUT/INOUT/ReturnValue、完整多结果及 Reader 成功关闭回写接入 ADO/REST/实际 HTTP2 Procedure=3；在调用保存点内校验输出声明与响应预算，发送后未知结果不重试。原生存储过程兼容断言保留，旧拒绝 setter 测试更新为真实过程执行及未知类型拒绝。固定 SqlSugar 候选验收与稳定包/上游发行单独记录。

- RAG CLI 支持由调用方传入取消令牌并保留原总期限；真实请求收到后触发取消的回归验证一次请求、审计终态、未发布与持久续跑，另验输入前取消无数据库/审计/HTTP副作用。
- 合入 PR #320 的 Microsoft.AspNetCore.SpaProxy 10.0.13；CAP 历史 timeout 保留 UNKNOWN，固定子模块后本机 CapJourneyTests 10/10 仅作定向证据。
- 合入 PR #319 的 Microsoft.AspNetCore.Mvc.Testing 10.0.13。
- 合入 PR #318 的 Microsoft.AspNetCore.Identity.UI 10.0.13。
- 合入 PR #317 的 Microsoft.AspNetCore.Identity.EntityFrameworkCore 10.0.13；并保留 #327 的并行预热修复。
- 合入 PR #316 的 Microsoft.AspNetCore.Diagnostics.EntityFrameworkCore 10.0.13。
- 合入 PR #315 的 github/codeql-action 4.38.3，保留原扫描范围和真实门禁。

- 合入 PR #327 的 Microsoft.Extensions.Caching.Abstractions 10.0.13；修复其真实 CI 暴露的关系表并行预热竞态，短锁保护共享行锁身份目录，保持不同表并行打开、行锁身份隔离及重命名后的稳定性。
- 合入 PR #326 的 Microsoft.Extensions.AI 10.11.0；历史 PR CI 的 Windows Core 超时保留为待最终候选复核的独立失败证据。
- 合入 PR #324 的 Microsoft.EntityFrameworkCore 与 Microsoft.EntityFrameworkCore.Relational 10.0.13，为后续 Design/Tools 10.0.13 更新消除已观测的 NU1605 降级。
- 合入 PR #325 的 Microsoft.EntityFrameworkCore.Design 与 Microsoft.EntityFrameworkCore.Tools 10.0.13；与 #324 的 Relational 10.0.13 配套，保留完整依赖版本一致性。
- 合入 PR #321 的 Microsoft.Data.Sqlite 10.0.13。

- 人工统一整合现有 M49、演示/Studio 配置与 #248 交付；保存 #247/#245 未实施的准备探针、监督暂停/关闭回执和旧树差异审计。草稿不计作功能完成，定时监督继续暂停。
- Online Studio：设置入口改为独立连接/观测偏好页；观测展示按授权数据库读取的九模型目录、存储/WAL 元信息、只读 SQL 自检及慢查询形状，明确未采集/失败状态，普通用户不请求实例级 Prometheus 数据。
- 在项目门面与中英文 README 公布用户授权的受限演示入口和凭据，说明正常注册测试账号及数据长期保留、不参与共享演示账号的周期恢复。
- IoTSharp Online 交付补丁：允许邮箱式用户名称，并对含 PASSWORD 的 SQL 诊断样本/SSE 使用脱敏形状；账号隔离与凭据保留、解析及诊断回归通过。该本地交付补丁未提交至上游，不代表新的正式 Release。
- Identity MVC 样例显式保留既有 `LockoutEnd` 的 `DATETIME` 映射，修复 Provider 默认使用原生 `DATETIMEOFFSET` 后模型与已有迁移不一致的问题；JOIN 列裁剪递归保留 CAST 操作数，CAST 内函数继续遵守谓词下推限制。保持原 UTC 毫秒布局、迁移快照及模型差异断言。

- GH-Issue #249：支持逐行列/表达式 JSON_VALUE path、动态数组下标和 JSON_OBJECT 类型安全对象谓词；对象保留精确 DECIMAL、整数、布尔、NULL 和转义，动态 JSON_VALUE 沿用静态 Int64 优先否则 Double 的取值合同，静态/参数 JSON 索引保持。补文档/文件/向量/时序/JOIN 的精确对象接线、预算/取消释放、真实 SqlSugar 同步异步三协议和重开验证；不改变目录或结果版本，正式包及上游发行仍需另验。

- 修复原生 DATETIMEOFFSET 引入后的 EF Provider 映射兼容：默认 DTO 属性使用原生 UTC ticks，显式旧 DATETIME 布局通过查询列 CAST 保持参数/函数类型一致且继续保留旧毫秒存储精度；补齐 ADO DataTypes 的 DATETIMEOFFSET 目录项和跨协议旧 DATETIME 参数拒绝/显式兼容回归。不改变引擎混合类型比较守卫或持久格式，既有列迁移须显式执行。
- M49-F01 关系 SQL 写入首片：新增独立 `Sql/Execute=2`、显式 payload v1、4 MiB 累计输入预算与精确参数/类型化 NULL；新 `SonnetDB.Client.ExecuteSqlAsync` 仅使用实际 HTTP/2，Server 支持关系表 CREATE/DROP TABLE 和无 RETURNING 的 INSERT/UPDATE/DELETE，复用 Write 权限、admission、AST 参数绑定及语句原子性。响应缺失/畸形、取消或断连保留未知提交且不重试；事务/多结果/其它模型显式拒绝。旧混合 Data 的 ADO/REST/session 迁移待完整 Frame 合同，未自动改接。见[协议与边界](docs/sql-execute-frame.md)及[验收](docs/audits/m49/f01-20261017/README.md)，F01/M49 继续进行中。

- M49-D01：将 Frame 信封/opcode 和 SpanReader/SpanWriter 直接迁入零引擎依赖的 SonnetDB.Protocol，新增只依赖它的原生 .NET HTTP/2 单响应远程入口；真实 h2c、取消/关闭、预算、干净包消费者和依赖隔离已验证。托管 API 类型归属改变，消费者须重新编译；九模型 typed API、多帧 SQL/参数/事务、Core 有界执行和正式发行门禁仍待完成。换机入口见 docs/handoffs/m49-next-computer.md。

- M49 首个实现切片：Native SQL result 改为持有 reader/command 并按需逐行读取，移除 ABI 层完整结果副本；EOF 后拒绝旧行，失败终态保留，提前释放/连接关闭清理活动结果。唯一 AOT 导出工程迁到 `embedded/native`，同步 CMake、solution 和独立 Native 测试入口。仍依赖混合 Data/Core 物化路径，D01/R01/R02/R03 尚未完成；不宣称端到端有界、新远程 SDK 或全平台交付。

- **连接器/嵌入式双产品线需求与规划修订（2026-10-16）**：按用户答复更新[确认需求](docs/design/connector-embedded-split-requirements-20261016.md)和[M49](docs/design/m49-connector-analytics-bounded-batches.md)，远程原生HTTP/2二进制Frame，嵌入式.NET托管/非.NET共用AOT，BASIC仅远程，Java8兼容，先64位，不保留旧兼容层。首批只做九模型数据访问、允许分批正式发行，共15包；当前首片进度与实际验证见[实施矩阵](docs/design/m49-implementation-matrix.md)，其它平台API后续。

- GH-Issue #250：补齐持久真实主键名称和 `information_schema.table_constraints`、实际 DESC 索引编码与 INCLUDE 类型化载荷、`ALTER TABLE ... AUTO_INCREMENT = n` 原生下一值维护。约束改名/删除、索引覆盖读取/事务/迁移/在线恢复、真实三协议维护及终止恢复已补验；含主键目录和新索引选项使用永久 v16 屏障，在线新选项断点 v2，旧引擎拒绝且降级须恢复升级前副本，未改变 FileHeader。正式包、SqlSugar 上游交付与标准 CodeFirst 的 #227 事务 DDL 条件仍待完成。

- 表目录临时文件占用回归将同步保存放在专用线程，避免单核/双核测试进程中的线程池饥饿延迟文件锁释放；保留实际 Windows 文件锁、75ms 释放条件及生产重试次数和间隔，旧源码失败与修正后验证分别留档。

- GH-Issue #251：新增独立 DATETIMEOFFSET 声明、UTC 100ns ticks 持久化及通用 ADO.NET 原生读取；默认 DateTimeOffset 参数使用新类型，显式 DbType.DateTime/DateTime2 保留旧 DATETIME 毫秒合同。混合比较在扫描和副作用前明确拒绝。表目录永久提升至 v15、原生物化声明快照 v4、REST/Frame 结果 v6；旧格式可读，旧引擎拒绝新目录，降级须使用升级前副本。附迁移强杀、真实 Chloe 三协议及旧端兼容证据，正式包与 Chloe 上游交付仍待完成。

- 递归 CTE 取消回归改为在实际递归候选行触发，校验直接取消或稳定错误码包装携带同一令牌、资源完全释放及后续查询可用；消除 10ms 定时器与不同轮询位置造成的测试竞态，不修改生产取消行为。

- 新增 M48 单机日志与事件分析路线图与独立详细设计：明确 20 项 R1 必选工作包、4 项条件候选、事件/确认/查询/生命周期/告警/恢复合同、阶段依赖和固定硬件/168 小时验收；归档 ELK 能力分析与官方依据。此项仅交付规划文档，未实现日志平台、不新增第十模型、不自动扩大既有 4.5 发布必选范围。

- 将 `llms.txt` 与 `llms-full.txt` 的 AI/Agent 上下文从 3.1 基线完整更新到 4.5 系列：按 2026-10-16 核实公开稳定版、NuGet/Workbench 两条预览与当前源码边界，补齐十四项能力、SQL/ADO/EF、MCP、持久 RAG、多模态、CDC/流处理、管理界面、恢复和资源合同；增加可执行示例及任务文档索引，修正原内联主键示例，不把规划或局部验收写成正式发行。

- 对象回填预算恢复测试按真实单页合同续读合法持久前缀，逐页校验计数、游标和去重；增加目标取消点之前先结束一页的确定性回归，消除默认 250ms 页预算与第 3 条取消钩子的竞态，不修改生产预算或恢复逻辑。

- GH-Issue #253：补齐既有无主键DML的Chloe原保护与最小候选、重复/NULL/批量/事务/RETURNING及三协议独立重开验收；保留两个公开版本的写入拒绝，等待正式发行及上游交付，不重复243引擎实现。

- GH-Issue #255：补齐既有无主键DML的公开负基线和真实XCode三协议/重开/双向跨库验收，提供每次查询保留NULL的最小外部候选，并保持自定义抽取器数据源与原分页状态；正式发行及XCode上游交付仍待完成，不改Sonnet生产。

- GH-Issue #254：补齐既有聚合CAST的公开包、源码补验及真实Chloe三协议消费证据，保留旧公开包CONCAT声明失败与最小翻译候选；正式发行和上游交付继续跟踪，不重复引擎实现。

- GH-Issue #256：SQL LIMIT/OFFSET及绑定/执行支持完整非负Int64，实际物化、Top-N和外排仍受资源预算限制。**公共API二进制兼容变更**：PaginationSpec.Offset/Fetch属性改为long/long?，旧int构造器保留，但直接使用这些属性的已编译消费者须重新编译；原Int32 SQL输入兼容，不改变存储或协议格式。附真实XCode分页及三协议/重开验收，正式发行与上游交付继续跟踪。

- GH-Issue #258：补齐既有 uuid() 的公开包逐行/每表达式位置、RETURNING、事务和独立重开验收，以及 Chloe Guid.NewGuid 单行翻译候选的真实三协议证据；正式发行及上游交付条件继续保留。

- Streaming 死信恢复测试补齐检查点自身超时先于锁重试结束的既有分支，继续严格核验异常内部信息、恢复拒绝与原spool保留，不修改生产超时合同。

- GH-Issue #262：日期标量回归按已交付合同显式检查 DATE_ADD 的原生 DateTime 类型、UTC Kind 与加日结果，替换旧隐式 DateTimeOffset 期望；保留原 CI 失败证据。

- GH-Issue #263：补齐公开预览包原始SByte/UInt16/UInt32及枚举/NULL参数、Chloe DbParam/Session/SqlQuery、批量/回滚/重开的三协议消费证据；明确公开旧包UInt64额外控制限制，正式发行和Chloe正式交付仍待完成。
- GH-Issue #264：补齐公开预览包DECIMAL精度/失败原子性/跨进程恢复及固定Chloe源码DDL候选的嵌入式、REST、HTTP/2消费证据；附可复现工具和独立候选补丁，正式发行及Chloe上游交付仍待完成。
- GH-Issue #270：补齐公开 `.5` 参数化字面 Contains/StartsWith/EndsWith 的原复现、SQLite对照、真实REST/h2c和官方BookStore作者HTTP列表/Count/分页验收；附隔离消费者及来源/哈希收据，不修改生产或应用业务。
- GH-Issue #271：补齐公开 `4.5.0-preview.ef266.5` BLOB相等/相关Any原复现、边界/索引/外键/回滚/跨进程恢复、真实REST/h2c及固定ABP Passkey Store/账户HTTP验收；客户端公开包与源码Server来源分别保留，不新增生产实现。
- GH-Issue #272：补齐固定公开 NuGet `4.5.0-preview.ef266.5` 的原始命名外键复现与五阶段迁移验收；SonnetDB/SQLite共12个独立进程验证实际目录、约束移除/替换、回退及重开，附可复现消费者与持久收据，不新增生产实现。
- GH-Issue #243：更新 schema 演进回归的旧无主键写入拒绝断言，分别验证空表及已写入表增加主键后，自增高水位、ROWVERSION、ADO 元数据和重连读取保持正确；没有修改生产实现。
- 表目录 `tables.tblschema` 读取兼容 v1～v13；无主键关系表启用内部持久行身份或执行可恢复主键迁移前保存 v13 兼容屏障，v13 沿用 v12 payload，保存、回滚和移除无主键表后均不降级。主键迁移另有带版本和 CRC 的独立恢复日志。尚未升阶的目录按长度/注释属性写 v12，否则写 v11。旧引擎拒绝不支持的目录版本，回退须使用升级前副本；未修改 `FileHeader`、既有显式主键编码或行载荷。
- Windows CI 按 Core 与其余项目分片运行完整测试集合，保留每任务 60 分钟、Test 步骤 50 分钟和单项挂起 10 分钟上限；两片均完整构建原方案，发布就绪门禁要求两片任务、分片选择步骤及独立结果产物齐全且成功。

### Fixed
- 修复 Parity crash reliability 回归：keyless DML 断言当前 v16 目录下限，并将 DDL journal prepared/committed 与 recovery 子进程场景接入 CrashTests Child 分发；完整 CrashTests 82/82 通过。

- 修复旧 `DATETIME` 写入 `DateTimeOffset` 的参数类型边界：未声明参数在亚毫秒值下 fail-closed，EF/Identity 显式 `DbType.DateTimeOffset` 兼容路径保留；修复 Reader 关闭异常测试的重复注入异常。Core 7890/7890、EF Core 295/295 及 Identity 定向回归通过。

### Added

- GH-Issue #247 原生切片：过程支持 IN/OUT/INOUT、SET/RETURN、类型化命名调用与完整结果快照；完成校验回调在提交前执行，输出/响应校验失败回滚本次保存点。例程目录升级 v5，兼容读 v1–v4 的 IN 定义，旧引擎拒绝新格式；FileHeader 未改，降级须恢复升级前例程目录。旧 Text/CALL 的 IN 转换及默认最后结果形态保留，ADO/远程/SqlSugar 与正式包仍待后续验证。

- GH-Issue #248：显式事务的单关系基表支持真实 `FOR UPDATE [NOWAIT]` 行锁；写入与 COMMIT、表维护共享锁资源，支持等待/取消/超时、死锁与预算拒绝、锁后版本及排序谓词复验、保存点和连接/会话结束释放。普通 SELECT 保持不加锁；轻事务写入仍在 COMMIT 复验，不承诺 gap/phantom 保护，也不宣称 #233 的全部 ORM 条件完成。
- GH-Issue #252：新增原生 DURATION/TimeSpan，保留完整有符号 Int64 ticks、100ns、NULL、批量/事务/重开与真实 Chloe 候选；TIME/显式 DbType.Time 保持一天内合同。关系目录启用永久 v14 屏障及可恢复迁移，含时长物化快照写 v3（其他仍 v2），SQL Frame/REST 协商 v5；旧引擎拒绝升级目录，回退须使用升级前副本，未修改 FileHeader。正式包与 Chloe 上游交付仍待完成。

- GH-Issue #259：新增 bit_and_uint64 / bit_or_uint64，覆盖完整 UInt64 值域并以 DECIMAL(20,0) 精确返回；补参数/列/NULL、失败原子性、三协议与真实 Chloe 候选验收，正式发行与适配交付继续跟踪。

- GH-Issue #260：新增 parse_uint64(text)，以有界ASCII整数语法解析完整UInt64值域并精确返回DECIMAL(20,0)；拒绝负号、小数、指数、溢出和超预算输入，保留NULL、空结果声明、语句原子性及真实三协议/Chloe候选证据，不新增存储格式。

- GH-Issue #312：关系表支持普通单列非自增 INT 主键的显式 UPDATE；旧键、新键和普通/唯一索引以单个 WAL 批次原子发布，连续事务改键保留原始并发基线及新旧键恢复信息。目标键占用、NULL、约束失败完整回滚；含外键、复合/非 INT 主键及联接改键继续明确拒绝，首次可安装版本与 FreeSql 发布包验收另计。
- GH-Issue #313：已声明 measurement INT/FLOAT FIELD 支持单列、TAG 等值与 time 范围的原子 UPDATE，每次最多 256 个逻辑样本；稀疏目标、NULL、类型不匹配与不支持谓词在提交前拒绝。复用内部 KV 替换层的查询、摘要回退与恢复，数值替换记录使用 v2（类型标记及 8 字节数值），向量 v1 原字节继续兼容；不修改 `FileHeader`、时序原段或 WAL 格式，旧引擎拒绝数值 v2，回退须使用更新前副本。可安装版本与 FreeSql 消费者验收仍待完成。
- GH-Issue #226 / M43：支持已有数据的主键列重命名、有效类型转换和单列/复合主键定义迁移；重建主行与普通/唯一索引，按原父行映射更新受支持的入站外键，保持默认值、CHECK、自增高水位和 ROWVERSION。完整预检后通过独立 undo 日志恢复一致旧表或新表，持久决定不确定时要求重开；迁移前已缓冲的事务在 schema 变化后以 `table_serialization_conflict` 拒绝读取旧缓冲行、合并或提交。范围、资源上限及恢复边界见[迁移合同](docs/sql-primary-key-migration.md)，正式包与 FreeSql/XCode 消费者验收另计。
- GH-Issue #231 / M43：新增单 schema 的持久 Int64 `CREATE/DROP SEQUENCE` 与原子 `nextval`，支持关系表 INSERT 表达式、批量和直接序列 DEFAULT；同步 WAL 后返回，回滚耗号，缺失/耗尽/恢复有稳定诊断。备份冻结覆盖检查点和完整复制，持久化结果不确定时停止发号；查询形状和默认值边界见 [序列合同](docs/sql-sequences.md)。
- GH-Issue #237 / M43：新增持久 `COMMENT ON TABLE/COLUMN ... IS '...'|NULL`，保留 Unicode、空串、引号、换行和重命名后的注释，通过 information_schema、嵌入式/远程 `GetSchema` 提供读取。
- GH-Issue #240 / M43：关系表支持 `STRING(n)` / `VARCHAR(n)`，按 UTF-16 代码单元计算，所有写入口拒绝超长值并保持批次原子性；ALTER 扩大/缩短先校验现有行及默认值，支持取消，结果与 ADO/远程目录返回实际声明长度。
- GH-Issue #243 / M43：有主键、复合主键及无主键关系表均支持原子 `DELETE ... WHERE ... LIMIT n [RETURNING ...]`，接受非负 Int64 字面量/整型参数；无主键表以不透明 `H/v1/GUID128` 物理身份保留重复行，支持普通 INSERT、WHERE UPDATE/DELETE、事务、索引、外键子表级联、触发器和恢复，SQL 元数据不增加隐藏列或业务主键。上限针对直接删除行，候选扫描和级联影响另计；无主键按实体主键定位、UPSERT/ON CONFLICT 和 JOIN/FROM UPDATE 明确拒绝。正式包与 XCode 消费者验收另计。
- GH-Issue #224 / M45-C01：measurement TAG/time 分组复用已有标量求值器处理 HAVING 和复合聚合投影，支持分组键、投影别名与多键排序，并在筛选/排序后分页；残差/Geo WHERE 与显式物化预算的既有拒绝边界保留。
- GH-Issue #229：ADO 多条 SELECT 在执行前按 SQL 词法边界完整预检，冻结参数，并通过 `NextResult` / `NextResultAsync` 顺序切换结果和元数据，释放前一结果后才执行下一条；经典 schema 对未声明长度的 STRING 返回 `ColumnSize=-1`，支持 DataSet/DataTable 装载而不伪造声明长度。沿用现有嵌入式、REST 和 Frame 通道，不新增批次快照或写入协议。
- GH-Issue #257：关系表 UPDATE 可省略 WHERE 更新全部行，复用既有表达式、RETURNING、ROWVERSION、约束、事务、取消和物化预算；其它模型仍须声明 WHERE。
- 保存博客园系列143–157及159、最终主题封面、发布台账与筛选公开证据，增加项目识别/封面门禁和逐篇伴随闪存的独立记录；不改变数据库运行时或默认每日发布上限。
- CAP #265 与当前主线集成，并将 NuGet 候选提升清单和有界发布循环同步为含 `SonnetDB.CAP` 的八包合同；增加缺少 CAP 包的拒绝检查，保留默认完整 CAP 宿主的上游 NativeAOT 失败边界。分支与 PR 核查见[集成记录](docs/audits/branch-integration-20261015.md)。
- CAP #265：保留默认序列化/订阅管线的完整 NativeAOT 宿主实测失败边界，46 条上游 DotNetCore.CAP 10.0.2 诊断未压制；普通 CLR 回归通过不代表原生或正式消费者验收，适配器仍为私有未合入检查点。
- 增加默认只验证的NuGet候选提升入口：绑定成功Publish预检run、已合入main的源码SHA、版本与七包规范化SHA256清单，复用完整release-readiness门禁；人工明确确认后直接推送已下载的原包，不重打包，不自动创建tag/Release/镜像。

- GH-Issue #266 JSON 片段切片：`json_set_value` 增加可选第四布尔参数，显式解析完整 JSON 片段以替换对象/数组，三参数及 `FALSE` 保留原标量合同和容器拒绝边界。EF 复用标准修改命令和 JSON metadata writer，为数组、Guid、日期、二进制、转换器属性及唯一 `OwnsOne` 子树生成参数化更新；精确路径保留目标外成员，数组原地修改、回滚、失败原子性与重开分别验证。片段/源各8MiB、共享100000输入节点、结果8MiB/100000节点/64层及既有取消/协作式预算保持；`OwnsMany` 元素路径、JSON `ExecuteUpdate`、公开包和固定消费者仍另计。

- GH-Issue #266 嵌套标量切片：单属性 `SaveChanges` 可沿唯一 `OwnsOne` ownership 链更新嵌套标量，从每层模型 JSON 名称重建固定路径并保留未知兄弟和数字词法；路径限制保持 64 片段/1024 字符。集合/数组路径、子树与 JSON `ExecuteUpdate` 仍不支持；公开包与完整消费者验收另计。

- GH-Issue #266 EF 桥接切片：根 `OwnsOne.ToJson` 的单个直接标量属性 `SaveChanges` 通过 `json_set_value` 更新，从模型构造精确属性路径并复用 EF 标量参数、事务和影响行数并发检查；保留其它成员和数字原文，支持已有对象缺失末端属性及 JSON null。嵌套 Owned、数组/子树、转换器/其它类型、非有限浮点和 JSON `ExecuteUpdate` 仍拒绝；ComplexProperty/多属性的既有整根替换行为不变，正式包、官方 BookStore/Passkey 与 EF NativeAOT 不在本切片验收范围。

- GH-Issue #266 Core 切片：新增三参数 `json_set_value`，替换非根标量或添加现有对象的末端属性，保留数字原文、成员顺序及未知成员；拒绝重复键、缺失 parent、越界与容器目标。输入/输出各限 8 MiB UTF-8，树限 100000 节点/64 层，默认字符串转义按固定块写出，独立编码 scratch 限 5120 字节；单次求值保留协作式 10 秒预算并读取 SQL 取消/deadline。EF 桥接、非根 Owned、ExecuteUpdate、子树更新、官方 BookStore/Passkey 与正式包消费仍须单独验收。

- Web 图表依赖更新至 ECharts/ZRender 6.1.0（原 PR #299），修复嵌入式轨迹页在不超过 1100px 视口下筛选行收缩为零、加载按钮被地图画布遮挡的问题；保留筛选内容高度并允许容器滚动。真实前端正常点击、提示、缩放尺寸、空态恢复及销毁验证通过，模拟 API 与正式 Server 接受分开。

- GH-Issue #261：原生零行 reader 保留普通/物化视图的声明列名、SQL/CLR 类型、可空性及已知 DECIMAL 精度/小数位；未知声明保持未知，schema 查询不执行视图 UDF。物化快照 v2 在 CRC 内保存代际声明，兼容读 v1 行并要求显式 REFRESH 后访问完整元数据；Frame v3 显式协商完整声明，保留旧编码，仅对执行前版本拒绝回退一次 v2。


- 关系基表轻事务新增保守的表级乐观 `Serializable` 隔离：保留首次读取的 store/schema/generation/sequence，提交前按固定顺序锁定实际读、写及外键依赖并校验；冲突返回 `table_serialization_conflict`，终止整个事务并要求从头重试。嵌入式与 REST/h2c NDJSON 事务会话均显式传递隔离级别并拒绝未确认的 Serializable 降级；默认 ReadCommitted 行为保持（Issue #225）。


- PR #296 / GH-Issue #246：修正派生 SELECT 加载时的名称优先级，外层显式列别名高于 CTE 已解析的输出名，CTE 输出名高于内部投影别名；保留完整双引号点号名称、空结果类型以及原 CTE ORDER BY/LIMIT/OFFSET 合同。
- GH-Issue #246：关系 UPDATE JOIN/FROM 接受参数化派生 VALUES/SELECT（含 UNION ALL）与按位置列别名；目标及来源共享轻事务 overlay，连续批次保留最初并发基线。声明校验覆盖空来源；固定右侧哈希构建保留首匹配顺序并支持单条万行集合更新，VALUES 行列/字节、取消和解析预算有界。正式包及完整 CI 验收独立。


- EF Core JSON #266：固定对象属性路径的字符串、布尔、受支持整数及浮点标量投影/谓词复用 json_value，属性名按 JSON path 与 SQL literal 两层转义并保留路径预算；修复共享 JSON 求值器将 Int64 先提升为 Double 导致的大整数精度丢失。Complex/Owned × embedded/REST/h2c 六条真实路径通过；数组路径、转换器/其它标量、非根 Owned 更新和 ExecuteUpdate 继续明确拒绝，公开包与官方 BookStore 验收独立。
- EF Core JSON #266：支持 ToJson 整容器 UTF-8 物化、文本参数/SQL literal 与 EF 序列化的根替换，真实嵌入式/REST/h2c 读写、回滚、并发和正常重开通过；非根 Owned 更新、路径查询与批量更新仍明确拒绝，官方 BookStore/公开包消费者验收独立。
- EF Core 10：为 ToJson 的 JsonTypePlaceholder 提供默认 JSON 容器模型和迁移 DDL 生成；JSON 聚合查询/写入在实现前明确拒绝，普通 STRING/raw JSON 字符串行为保留（Issue #266，建模切片，未完成数据库与消费者验收）。
- GH-Issue #258：新增零参数 `uuid()`，逐次返回标准 D 格式非空 GUID 字符串，保留逐行 SELECT/UPDATE 与 RETURNING/事务语义及空结果 String 元数据；明确拒绝 UUID 列 DEFAULT，正式发行和 Chloe Provider 集成仍独立验收。

- GH-Issue #264：持续执行显式 DECIMAL/NUMERIC(p,s) 写入约束，拒绝需要舍入或超精度的输入，SQL/核心存储/轻事务和跨表批次在生成列及行索引写入前预检；裸 Decimal 保留完整 CLR 值域及既有浮点兼容。表 catalog `tables.tblschema` 升级 v11（flags bit4 保存显式来源），旧引擎拒绝新 catalog；当前 reader 读取旧 v9/v10 的非默认 p/s 后执行后续写入约束，旧 38/28 来源不明时保持不约束，历史非法行不在读取时改写。此 catalog 格式/旧写入行为变化必须与旧数据副本的回退边界一起发布；正式包和 Chloe 验收独立。

- GH-Issue #244：关系表 INSERT VALUES 支持已有无行标量表达式、参数 CAST、函数、CASE 与算术；Decimal 目标和显式 DECIMAL CAST 保留原数值文本，CASE/COALESCE 保持短路及单次函数求值。全部行先准备再预留自增号/写入，RETURNING、ON CONFLICT、默认值和触发器/事务保存点沿用现有合同；本地与实际 REST/h2c 验证、正式发布和外部 ORM 验收分列。
- GH-Issue #254：关系聚合投影/HAVING 支持 CAST 与既有标量组合，SUM/AVG/MIN/MAX/COUNT 的结果转换保留 NULL、声明类型、Decimal 精度和聚合/转换阶段溢出合同；REST 按已有 Float64 元数据读取数值，避免整值浮点结果变成 Int64；嵌入式与真实 REST/h2c 回归、正式发包及 Chloe 验收分列。
- GH-Issue #222：在已有关系表 OVER 上新增 `rank()`、`dense_rank()`、`ntile(n)`，复用多键分区及 NULL/升降序排序，保留并列排名、正 Int64 桶数参数和空结果 Int64 元数据；measurement、已有 row_number/聚合合同和正式 FreeSql Provider 交付分开验收。

- GH-Issue #220：新增原生 `lpad` / `rpad`，默认空格或循环自定义填充、UTF-16 宽度、较小宽度保留原串及 NULL 传播；拒绝空填充、非法宽度与损坏 UTF-16 输出（包括代理对截断），单次结果限定 1,048,576 个 UTF-16 单元，空关系与全 NULL 结果保留 STRING 声明元数据。正式包发布与 FreeSql #2234 适配另行验收。
- GH-Issue #219：新增原生 `shift_left` / `shift_right` 标量函数，固定有符号 Int64/64 位、0..63 计数、左移丢出位/右移保留符号和 NULL 传播，拒绝非整数及越界参数；空关系结果保留 Int64 声明元数据。正式包发布与 FreeSql #2234 的 CLR 位宽/掩码适配另行验收。

- GH-Issue #221：新增弧度制 `sin` / `cos` / `tan` / `asin` / `acos` / `atan` / `atan2(y,x)` 标量函数，明确可空 Float64、有限数值与反三角函数定义域合同，并保留空结果类型元数据；正式发布和 FreeSql Provider 验收仍独立。

- GH-Issue #223：新增关系表双参数 `string_agg`/`group_concat` 基础聚合，支持 NULL/参数分隔符、GroupBy/JOIN/关联单列子查询与 nullable String 元数据，逐行一次求值并限制共享拼接预算；可选聚合语法、measurement、正式包与 FreeSql 集成独立验收。
- M47-P08 公开发布 Windows x64 Workbench Web R1 预览 4.5.0-preview.1.12（Release 409754336）：P07完整12/12、8/8及实际maikebing审批通过；匿名四附件/290项库存和Chrome/Edge公开包首跑、正常停止/非空进程审计通过，Studio/VS Code及正式4.5发布仍独立验收。
- M47-P08 显式恢复按冻结原Release ID读取草稿，并核tag/ID一致；真实可见性回归与94合同通过，原零写入拒绝保留，恢复发行与公开首跑继续验收。
- M47-P08 修复stdin二进制附件上传的Content-Length，并提供精确原Release/空资产清单/独立授权hash限定的草稿恢复；原失败和tag/Release ID保留，94合同通过，实际恢复与匿名首跑待验。
- M47-P07 候选12真实放行完成：原十二同源workflow及八项验收全部通过，prepare/P07原artifact和三归档digest、18原文、10+5工具库存独立核验；P08四附件及精确请求已冻结，按用户决定允许maikebing发起并实际审批，变更工具真实prepare/P07复验12/12、8/8通过，未公开发布/匿名首跑。
- M47-P07 候选12实物冻结：干净P04/290项库存、四阶段37记录、五类拒绝及17回执/244身份审计通过；243依赖原文与Community/微软分发输入审查通过。冻结新四附件和18份无损原文，真实远端prepare/P07整体放行仍待验，P08未发布。
- M47-P07 继任候选12：恢复已发布 4.0 的构造/解构/表工厂及废弃 WAL 回收入口，启用已发布 4.0.0 包兼容基线；修正 P07 实物 manifest 依赖库存路径。候选11的真实构建、许可审查和发布预检失败保持，源码修复须新候选及完整同源验收。
- M47-P07 继任候选11：精确升级 Mapbox JSON lint 2.0.3，使用自有 Vue 指令替换发出的 vdirs 代码并核最终 chunk 库存；入包微软原条款、Community 资格来源和显式启动同意，P07 收集绑定实际依赖库存。469 项 Web、86 项发布工具及七项许可合同通过；新包实测与十二同源门禁尚待执行，原候选10不改。
- M47-P08：实现发布逐写ID日志、匿名四附件/ZIP回读与真实首跑入口，配置maikebing审批/禁止自批及管理员绕过；五项许可原通知准备、86项Node与补件/脱敏合同通过。候选10及原P07保持NOT_READY，许可/八项同源门禁未闭环，未公开发布或执行公开包首跑；[报告](docs/design/m47-unified-management-workbench/preview-1-p08-publication.md)与机器记录分列证据。
- M47-P07 完成候选10真实prepare/P07原证据收集与未放行评审：run38116860908/38117134746成功、7/8验收通过；原发布门禁4/12通过、8项缺同源码运行与11项运行时许可条件仍阻断，机器记录绑定真实artifact/hash/tooling，产品NOT_READY，P08未执行。
- M47-P07：新增同候选原门禁/安装收据与许可评审收集入口，精确冻结原证据和工具SHA；修正Windows分发checkout换行漂移。初审4/12工作流、许可NOT_READY，P08仍须独立授权。
- **M47-P06预览说明与独立分发（2026-10-11）**：候选10包外说明、限制/回退/反馈及四附件hash补记；新增默认只读prepare的手动入口、同source/run/attempt/库存核验、P07原完整verifier及P08人工环境授权、显式四附件上传和未知效果只读对账。35项本地合成合同、实际279ZIP库存及身份校验通过，原候选/三资产不变；[P06报告](docs/design/m47-unified-management-workbench/preview-1-p06-distribution.md)分列证据。P07/P08未验，NOT_READY，未tag/公开Release。
- **M47-P05候选10实物验收（2026-10-11）**：来源54eefc81 / 4.5.0-preview.1.10，干净CI38104431383、279项库存/hash及NativeAOT版本独立通过；实际Chrome/Edge密码登录、R1查询/1000行导出/单行审批、撤权/unknown/错配拒绝、正常Ctrl+C重开与61文件同候选快照回退通过。验收工具等待真实写响应后独立回读，原过早回读FAIL保留；[报告](docs/design/m47-unified-management-workbench/preview-1-p05-installed-journey.md)及[不可变候选10](docs/design/m47-unified-management-workbench/preview-1-candidate-10.json)分列实物/夹具/故障注入与清理边界。P06～P08待验，NOT_READY；未公开发布。
- **M47-P05实物错配修复（2026-10-11）**：候选09原包首次设置已实测；发现包内candidate编号滞后及缺运行时Server/Web身份核验，保留原包/失败收据，补candidate/version检查、source-generated身份端点、编译Web挂载前核验和Preview请求版本/SHA约束。继任候选10须新SHA干净CI重包；[P05报告](docs/design/m47-unified-management-workbench/preview-1-p05-installed-journey.md)分列实际包、工具错误与待验门禁，NOT_READY。
- **M47-P04独立预览组包器（2026-10-11）**：新增手动、只读构建CI与来源/版本/profile绑定的win-x64 NativeAOT Server＋同源Web组包器，精确库存/ZIP哈希、实际版本、工具依赖和许可证清单；24项本地合同通过。候选09真实NativeAOT/ZIP/版本/依赖闭包独立PASS；候选05～08原CI失败保留，[P04报告](docs/design/m47-unified-management-workbench/preview-1-p04-packaging.md)分列证据；P05～P08未执行，NOT_READY。
- **M47-P03 Preview入口与安全结果（2026-10-10）**：显式preview-1外壳、路由/动作白名单、真实HTTP角色与数据库grant核权、一次关系表单行参数化审批、撤权/取消/身份切换清载荷、unknown不重放和有界SQL/schema/导出。依[P03范围修订R1](docs/design/m47-unified-management-workbench/preview-1-p03-scope-revision.md)明确延期七模型专用读取；[本地验证](docs/design/m47-unified-management-workbench/preview-1-p03-controls.md)不替代发行物/远端门禁，产品仍NOT_READY，候选03在代码提交后冻结。
- **M47-P01 首版范围与候选冻结（2026-10-10）**：记录已提交候选 `c40fa670e3a170c5fac8cefa181d618631862270`、保留版本 `4.5.0-preview.1.1`、win-x64 同源环回 Web＋Server、九模型受限读取和关系表单行审批插入白名单；新增[范围/入口控制](docs/design/m47-unified-management-workbench/preview-1-scope.md)、[机器清单](docs/design/m47-unified-management-workbench/preview-1-candidate-01.json)及[独立分发合同](docs/design/m47-unified-management-workbench/preview-1-distribution.md)。核实 Publish 的 Studio/全套资产默认行为，冻结不经 v* 的独立 prerelease 和精确附件集合，保留原12个workflow/五个Workbench job与artifact门禁。仅完成范围合同，入口限制/打包分发实现及候选真实验收归P02～P07，产品仍NOT_READY，未构建/打标签/发布。
- 记录 WB83 preflight helper6秒与Host命令20秒的独立边界、首次ports helper等待关联及PID重复调用缺口，冻结保持原权限/预算的JS逐调用观察合同（WB84；尚未实施）。
- **M47 WB83 VS Code 独立真实窗口检查点**：复用既有stop/history/elapsed合同，冻结新窗口并取得preflight helper_deadline失败及四条真实stop诊断；原cleanup失败与未知边界保留，未验收Query/history/Code旅程。

- **M47 WB82 VS Code stop阶段诊断（2026-10-09）**：在既有三脚本增加最多384 attempt与已接受candidate/helper关联、固定JS/PS阶段及退出/结果类别；非法/缺失/getter/观察异常归unknown，原权限/异常/校验/三门禁保持。新增13项模拟测试，唯一文件88/88，0实际Host/产品build，原失败原因保持未知。

- **M47 WB81 VS Code stop诊断合同（2026-10-09）**：追溯原stop-verification回调拒绝与缺失的helper关联，冻结三脚本固定阶段/结果观察及模拟验收范围；原FAIL、终止权限与现有测试保持，未增加实际运行或产品功能。

- **M47 WB80 本机Native恢复证据（2026-10-09）**：增加独立证据入口，唯一真实Studio旅程通过A/B选择、双桌面、B被动恢复/真实查询和两次正常关闭；严格清理零fallback。复用旧Release，未新增恢复功能或产品build；原失败、Server优雅关停及三宿主/安装/AOT等边界分列。

- **M47 WB78 连接库安全字段观察（2026-10-09）**：Native诊断在原有受管JSON读取中记录固定字段类别与未知数量，最多两对象、各32字段、8条记录与100ms协作时限；不保存未知名字/值、不放宽disk拒绝或进程权限。14项新合同及60项回归通过，合成终态保留失败；真实宿主另验。

- **M47 WB77 Native 接续窗口（2026-10-09）**：记录唯一新根移交、新有界合同与独立证据入口；旧WB76预算发布失败保留，task-local wrapper修复及真实旅程分别验收，不改变生产身份、恢复、正常关闭或回收合同。

- **M47 WB76 新 Native 窗口入口（2026-10-09）**：增加固定证据目录及对应标签，单列 .62 文件前置与实际运行时观察；仅 runner metadata，保留原查询、缓存、身份、预算及正常关闭／回收合同。源码逆投影证明其它字节不变，真实旅程结果另见 WB76 报告，不把已获取的最新代码或既有源码测试当作恢复通过。

- **M43 CAP 适配（2026-10-09）**：新增统一 `SonnetDB.CAP` 项目，通过 Data SDK 提供本地/远程 Document Store 存储与 MQ 传输；包含消息状态/重试/调度/监控、条件存储锁和单 collection 业务+Outbox 原子批事务，不依赖 EF Core。补齐幂等文档索引和 MQ 消费组持久登记，避免未确认慢组消息在重启回收时丢失；复用已有 ACK WAL，无布局变更。新增真实本地/Server/CAP 流程测试及 NuGet 发布清单，候选包与正式发布分列，详见[集成合同](docs/design/cap-integration.md)。

- **M43 CAP 首条事务启动顺序（2026-10-12，PR #265）**：在已启动的 CAP 后台初始化完成后提交业务与 Outbox 并通知 dispatcher，避免 .NET 10 异步宿主启动期间首条嵌入式消息只进入延后重试；可控启动屏障、等待取消、启动任务异常与未启动宿主的嵌入式/远程回归通过，保留 Scheduled Outbox 合同、原发送超时与完整发布门禁。

- **M47 WB75 本地集成窗口（2026-10-08）**：记录隐藏.git元数据修复和真实不提交预演、固定15路径/owned HANDOFF与完整最终树门禁；已有Native CIM六源和38/14/41验收继承，旧WB74失败不升级。最终本地提交结果以WB75收据为准，提交后暂停。

- **M47 WB74 owned 本地集成守卫（2026-10-08）**：绑定 shared HANDOFF 完整冻结前缀、明确 owned 工作段与 private blob/index，只允许未暂存尾追加；拒绝中间插入和提交内容变化。沿用已验收 Native CIM 六源，不重跑源测试或真实旅程，原 WB73 集成失败及退出限界保留。

- **M47 WB73 Native CIM 本地验收（2026-10-08）**：验收已有阶段观测，PS micro/Node micro 各1/1、修复后 PS14/14、Node41/41。首次 full 的空结果 mock 失败及原 WB72 singleton/过期失败保留；仅夹具 bare-return 窄修，0actual/产品构建，最终 owned 集成以本片收据为准。

- **M47 WB72 Native CIM 阶段观测（2026-10-08）**：仅观察现有 snapshot 查询，四阶段/160 槽/有界结果数、单调耗时与 100ms 记账停止上限，同 envelope 安全投影；原 cache/预算/权限/运行时行为保持。静态独审与修复后 PS micro 1/1 通过，原 singleton 微试语义失败保留；完整验收未运行，过期窗口关闭，0 actual/产品构建。

- **M47 WB71 Native运行时/快照合同（2026-10-08）**：新增绑定原.53 prerequisite与实际.62 CDP HTTP version的合同报告，区分coarseCDP标签、ownership snapshot与逐查询时序缺口；没有运行时选择绑定/根因或恢复完成的新声明。0sourcechange/actual/产品build/Node重测，后续阶段耗时观察尚未实现。

- **M47 WB68 Native选库普通异库准备（2026-10-08）**：真实runner显式启用单次独立B控件准备，仅在首次A已active且新鲜守卫成立时执行，ack/DOM/disk完成后重读原A守卫；默认同值/unknown拒绝、原A→B恢复旅程与预算保持。新增17项、本地Node60/60与micro1/1通过，0actual/产品build，不声明真实数据库恢复已验收。

- **M47 WB67 Native helper统计保留（2026-10-08）**：保留同helper success/error的既有CIM查询数、缓存PID数与内部耗时至当前record/compact，非法或缺失为unknown，不新增采集或动作权限；新增15项、本地Node31/31及纯PS输出夹具通过。原业务结果/错误、预算/身份/回收门禁及真实恢复失败边界保持。

- **M47 WB66 Native helper预算合同（2026-10-08）**：新增实际协议/调用链、墙钟与CIM/cache/fresh回收预算核查报告，明确20.19s拒绝与160数量上限、内外计时和既有统计丢弃边界；独立28项复核通过。仅文档，0actual/test/产品build，不升级原恢复/退出/cleanup失败。

- **M47 WB65 Studio普通选库前置（2026-10-08）**：在原A/B点击前有界核普通DOM异库与活动身份一致，目标已活动或unknown明确拒绝且0点击/0ack轮询；保留原七fresh PUT谓词及完整恢复/正常退出门禁。最终43项本地合同（原32必要夹具适配+新增11），0actual/0build/0安装；旧失败与三宿主边界保持。

- **M47 WB64 Studio真实选库失败检查点（2026-10-08）**：新增独立runner metadata准入并复用9个runtime/source SHA完成唯一实际观察；A PUT200 seq6/request43未越过pre-click双barrier6/57，DOM匹配A、30次轮询7658ms，后续恢复阶段未执行。原normalExit/cleanupProven均false与helper预算失败保留，根精确归属回收另证；0新build/0定向测试，不计三宿主恢复完成。

- **M47 WB63 Studio选库失败观察（2026-10-08）**：点击前同步保留请求/响应双barrier，失败时投影既有候选的七项固定谓词及普通DOM计数/身份布尔；保存原轮询调用数与实际耗时，不导出任意文本/SQL/凭据，观察故障继续抛出原异常对象。32项本地合同通过，0actual/0build；原真实恢复失败、第二桌面与B查询未验收的结论保持。

- **M47 WB62 Studio seed失败观察（2026-10-08）**：新增仅seed失败的有界脱敏流式观察，保留原HTTP错误和现有权限/退出门禁；23项合同与源码复核通过。唯一真实运行越过修正seed门禁，但A选库fresh ack验收失败（实际A PUT200已保存，失败barrier未留存），第二桌面与B查询未执行；回收通过不能计正常退出或恢复完成。

- **M47 WB-61 Studio 数据库恢复验收入口（2026-10-07）**：复用实际 NativeWebApp/WebView2 runner，新增普通 A→B 选库、公开 PUT/磁盘对拍、两次桌面启动恢复及 B 查询的独立场景和 14 项合同测试；双请求/响应序号阻止旧 ack，候选进程完整验证后才取得回收权限。唯一实际运行在夹具建表 HTTP400 处失败，选库/重启/查询未执行；已修正夹具原生 STRING/主键语法但未再实跑，不能计数据库恢复或三宿主完成。

- **木垒现场只读验收（2026-10-08）**：归档当前 ARM64 配置/挂载、六个索引定义、真实倒序分页及 48/48 读取放大比、REST 预览边界和三图对象完整读取/SHA-256 的 5 个已完成子项；证据绑定现场 `6a8d82d2`，不替代 M41/M42/M36 的整体性能、容量、恢复或长稳门禁。见[现场报告](docs/audits/mulei-field-readonly-verification-20261008.md)。

- **ASP.NET Core MVC + Identity 样例**：以 `SonnetDB.EntityFrameworkCore` 替换 SQL Server Provider，保留默认 Identity 配置、上下文、控制器和视图；提供 SonnetDB 专属初始迁移和独立 MVC / Identity 集成测试，覆盖账户流程、TOTP、并发与关联存储。Provider 补齐事务提交阶段的 EF 保存异常及字符串 `MaxLength` 检查约束，核心允许现有长度函数用于 CHECK。

- **开源中国文章补发账本（2026-10-07）**：保存 142 篇渠道复核稿、逐篇博客/动弹回执、4.0.0 新闻审核状态与每日续发进度；通用发布和对账脚本迁入全局 `publish-oschina` 技能，项目只保留稿件、账本与接续说明。动弹失败独立记录，已有 ID 或未知请求阻止重复创建。

- **M47 WB-42 真实VS Code查询诊断（2026-10-07）**：新增独立Host入口和隔离真实Server/Code runner，复用现有当前语句/选区/EXPLAIN命令；最终syntax/TypeScript和既有Node20/20通过。第二实跑当前语句两行、选区一行及原列/SQL/database生成payload与独立真实reference一致；EXPLAIN断言及process audit失败、history未运行，完整旅程未通过。首preflight parent-chain超时有据修为有界snapshot lookup，原两失败/缺证据保持；根两单PIDfallback、端口/282对象runtime回收另列，不能计正常退出/UI/分页/三宿主完成。完整门禁和八文件本地提交见M47记录。

- **M47 WB-41 SQL原生文件诊断（2026-10-07）**：新增显式sql-dialogs薄入口，复用Ctrl+O/Ctrl+S与生产bridge/picker；四phase按真实OS ack、DTO、普通DOM与磁盘bytes/hash独立验收，保有界等待/48文件/终态门禁。Syntax通过；首actual真实Open POST后桌面工具未看到picker且激活失败，60秒无ack超时，余三phase未运行，不能称对话框或正常退出通过。六失败终态与9归属fallback/四端口/三runtime回收、根失败checkpoint/独立复核保留；生产/旧默认生命周期不改，最终八文件完整门禁和本地提交见M47记录，整体三宿主仍未闭环。

- **M47 WB-39 原生烟测终态与普通关闭（2026-10-07）**：诊断入口请求普通可见Studio、以有界.NET主窗刷新及fresh任务身份执行CloseMainWindow；新增schema2进程身份/父链去重与独立normal-exit/cleanup/detail/result写入，保512KiB/凭据门禁并为结果保留3秒，失败不得PASS。16纯内存故障注入与语法检查通过；第二真实run本机宽窗退出0/signalnull、四端口/旧新Server身份释放、零fallback与五terminal保存通过。首轮根monitor超时/缺终态与WB-38原失败保留，生产不改、构建产物hash复用单列；最终完整门禁/十文件本地提交见M47记录，默认窄窗/对话框/安装/AOT/三宿主整体另验。

- **M47 WB-38 Studio原生诊断入口（2026-10-07）**：新增实际Studio/WebView2 CDP附着runner与PowerShell7进程身份helper；本机宽窗native bootstrap、Managed Local Health/Stop/Start及旧/新Server归属已有真实局部证据。三次实跑全部保留失败：默认窗Health隐藏、正常关闭窗口句柄未发现、终态process-events超512KiB落盘中断等仍待接续；不称完整生命周期通过。Web build、Server/Studio Release和根回收审计通过，生产未改；最终完整restore/原级别format及本地八文件提交见M47验证记录，安装/对话框/AOT/三宿主整体另验。

- **M47 WB-37 Object真实Web证据（2026-10-07）**：新增隔离runner薄入口和三正常旅程；接续首run3/3补100/51 continuation/151原DTO/当前51截断结果、206 Range4096与正常Download8192、一次text审批PUT200/管理员独立Get与history1、旧审批REVOKE403/管理员404/批准值保持和READ重授同tokenSchema200六tabs锁存。Blob不可观察body明确null；三JSON335381字节/manifest核验，初始三失败/耗尽budget与未知reset保留。生产/共享runner不改，完整独立复核、最终restore/原级别format与八文件本地提交见M47验证记录；Multipart/恢复/Server预算/三宿主/发行物另验。

- **M47 WB-35 Graph真实Web证据（2026-10-07）**：新增复用隔离runner的Graph薄入口和三旅程，普通READ核实际Canvas250/10/1000、typed vertex、独立snapshot JSON导出10/1000及原身份history；普通WRITE一次审批取得单vertex Upsert sequence/isDuplicate终态与管理员Get/version+1；旧审批REVOKE403清画布/overview/编辑器/可见import草稿并在READ重授/同tokenSchema200保持锁存。真实第三轮3/3、Node27/27、全Web327/327、Graph fixture16/16/build通过，前两轮正常控件定位/数字提交失败记录保留；三JSON811899字节/manifest核验。Graph Beta、完整Int64/edge/import/维护及三宿主继续另验，恰满顶点预算的导出截断候选缺口留下一片；最终完整restore/原级别format为提交前置，实际见M47验证记录。

- **M47 WB-34 MQ真实Web证据（2026-10-07）**：新增复用隔离runner的MQ薄入口与三旅程；普通READ fromOffset0/100的100/51当前JSONL/原身份history、WRITE正常Publish201/Ack200一次审批与管理员对拍、旧Publisher审批REVOKE403清载荷/草稿/审批且READ重授/同tokenSchema200仍锁存。真实首轮3/3、Node22/22、全Web327/327、MQ fixture16/16/build通过，三JSON195806字节/manifest核验；生产/API/Server/共享runner不改，真实冒号Topic400、无实际文件导入、实例恢复/Server预算/三宿主未闭环边界单列。独立复核和最终完整restore/原级别format为本地提交前置，实际结果见M47验证记录。

- **M47 WB-32 Vector真实Web证据（2026-10-07）**：新增复用隔离runner的Vector入口与三条真实旅程，READ身份raw L2 Top-K20/100、原名metadata、当前JSON/CSV与history逐项对拍；Search撤权403清外层载荷，重授READ/同tokenSchema200仍锁存。独立SQL403仅锁当前Measurement子页，外层旧Vector命中保留、子页重挂载可能丢锁的缺口明确留待下一片。真实首轮3/3、全Web321/321、Vector fixture10/10、Web/Server Release构建通过；成功三JSON/manifest独立落盘，最终完整restore/format为本地提交前置。不计Profile、显式恢复、Recall、Server预算或三宿主整体完成。

- **M47 WB-31 Measurement真实Web旅程（2026-10-06）**：补100/500/61当前窗口与JSON/CSV、普通WRITE两点逐INSERT终态、真实撤权审批/monitor锁存四旅程PASS；共享真实runner分开身份遍历与有界日志，保留完整父链与安全双错，Node10/10、全Web321/321、Document真实3/3通过。成功证据独立复核通过，最终完整restore/format作为本地提交放行条件，实际门禁/提交见M47验证记录；根预算时区错误及失败记录保留；不计完整写终态/Server预算或三宿主完成。

- **M47 WB-30 Measurement 客户端隔离（2026-10-06）**：现有点读取、目标监控、文件导入及审批统一精确401/403锁存与脱敏，清载荷/草稿且readonly程序入口禁写；连接/auth/原名/Schema同步代际隔离迟返、同结构Schema刷新与旧finally，原审批一次消费，已派取消记原身份unknown。点与监控先截断至500行，auto最多12轮/60秒，导入准入1000语句/10批/60秒；正常100+1停止后新审批兼容。Node16/16、全Web311/311、Chrome fixture20/20、旧Measurement/Vector子页8/8与build通过，既有Kestrel SQL预览兼容3/3单列；新UI真实Server权限、完整写终态/Int64、Server预算及三宿主/发行物另验。

- **M47 WB-29 KV 真实 Web 旅程（2026-10-06）**：新增复用隔离真实runner的KV入口与三条Chrome→Kestrel旅程，验证Scan100/opaque cursor尾51、Get原值/安全版本与当前已加载JSONL逐行对拍；普通数据库WRITE身份经三次正常审批取得NX成功、NX未应用影响0及交换实际版本/原身份历史，管理员Get独立核验。实际REVOKE拒绝旧导入审批403，重授READ/同tokenSchema刷新仍锁存不重放。真实首轮3/3、Node17/17、全Web300/300、Chrome fixture12/12和build通过，三份成功JSON与SHA256 manifest独立落盘。生产组件/Server/共享runner不改；完整atomic/Int64/TTL/CAS、瞬时结果表展示、显式恢复、Server预算与三宿主/发行物另验。

- **M47 WB-28 FullText 真实 Web 旅程（2026-10-06）**：新增复用隔离真实runner的FullText入口和三条Chrome→Kestrel旅程，验证Top-K20/100、精确文档/Analyzer、当前结果导出与原身份历史，数据库Admin一次批准同步重建完整终态，以及实际撤权403后重授READ/同token刷新仍锁存不重放。三份成功响应/下载/history证据与SHA256 manifest独立落盘且拒写凭据；真实3/3、Node15/15、全Web300/300、Chrome fixture8/8与build通过。生产组件/Server不改；首run错误WRITE授权失败、显式恢复、typed全文分页/服务端预算和三宿主/发行物边界见M47验证记录。

- **M47 WB-27 Relation 真实 Web 事务与权限（2026-10-06）**：新增本机隔离Kestrel/Vite/Chrome分页/当前结果导出、两条审批插入完整终态、COMMIT唯一键冲突与实际撤权403锁存旅程。返回编辑保留暂存并提供重新预览/显式丢弃，隐藏审批不可确认；完整成功事务使用COMMIT权威影响数，避免两行误报四行，失败或缺失COMMIT不把暂存计为持久写入。抽出共享真实runner并保留Document兼容入口。Relation真实3/3、Document真实回归3/3、Node20/20、全Web300/300、Chrome fixture12/12与build通过；失败记录、显式恢复、完整权限/SQL矩阵、Server预算、三宿主与发行物边界见M47验证记录。

- **M47 WB-26 Document 真实 Web 权限与输出预算（2026-10-06）**：新增隔离本机 Kestrel/Vite/Chrome 旅程，使用普通用户的真实撤权、拒写、重新授 READ 与显式 Find100 恢复，并核对实际响应、下载与历史。修正 Distinct 的真实请求兼容：空IDs省略，显式IDs保留；请求最多1000项，小于1000的预览用哨兵证明截断，恰1000项满窗明确完整性未知，保留真实响应终态且不虚构分页。真实Web→Kestrel3/3、Node21/21、全Web295/295、Chrome fixture11/11与build通过；其它宿主、登录UI/readonly props、安装、AOT、扫描/物化/字节预算及发布证据分开，范围和失败记录见 M47 验证记录。

- **M47 WB-25 Object 写终态与一次审批消费（2026-10-06）**：补 Object 写执行器的审批快照一次消费、最多1000项批次和60秒客户端新操作启动窗口；冻结写入输入并核验对象/桶/Multipart/语义响应的目标与必要字段，批删逐项匹配批准 key，明确失败与传输/缺失/损坏终态的 unknown 分开记录并保留已确认影响数量。DELETE/删桶/Multipart abort 校验 HTTP 204，单对象 DELETE 校验 delete-marker、version-id、ETag；已证明的 Multipart 写终态不被伴随刷新拒绝推翻。Node32/32、全Web292/292、Vite build、新 Chrome fixture5/5、既有 Object 浏览器4/4、真实 Kestrel Object4/4+Multipart2/2通过；真实新UI权限、完整传输/资源预算、三宿主、安装与发布仍独立验收。

- **M47 WB-24 Object 读取隔离与有界预览（2026-10-06）**：复用既有桶浏览、版本、治理、Multipart 和图片语义；补六态、readonly 程序/按钮门禁及全读取路径401/403载荷/URL/草稿/审批清理与锁存。实际连接/API/认证、桶、prefix/token、选中key/version和请求代际隔离迟返、ABA与卸载；审批及native/Web文件选择器绑定原身份。列表先截断再映射，每页及累计最多1000，拒绝错误目标/不前进token且超返不复用跳项游标；Range先校验安全整数和真实206声明区间/版本，再slice至请求/声明长度/4096字节后读取格式化。专属Node20/20、全Web280/280、Chrome15/15、既有浏览器4/4、TypeScript/Vite、独立复核和既有真实Kestrel兼容4/4通过；写终态/一次消费、完整语义/Multipart与服务端预算、新UI真实权限、真实OS对话框及三宿主仍独立验收。

- **M47 WB-23 Graph 权限与有界画布（2026-10-06）**：Graph Beta 工作台消费能力矩阵并锁存 401/403，清理画布、诊断、元素、导入与维护审批载荷；只读保留浏览、元素读取与导出，写入/导入/维护入口均受门禁。请求绑定数据库、Graph、端点、认证和连接代际，隔离迟返、ABA、卸载并支持分派前取消；画布客户端按 10～1000 总元素预算先截断并保留边端点，Inspector 限制 32 项/4096 字符。元素数字 ID/版本超出安全整数时拒绝读取或写审批，维护 staged/paused/applying 不记完成，未知终态不重放。专属 Node **27/27**、全 Web **260/260**、Graph Chrome **16/16**、既有 Graph 浏览器 **3/3**、TypeScript/Vite 与真实 Kestrel Graph 兼容 **4/4** 通过；fixture/UI 与真实服务证据分开，完整 Int64 字符串、真实新 UI 权限/恢复及三宿主仍待独立验收。

- **M47 WB-22 MQ 权限与有界预览（2026-10-06）**：复用发布、Ack、JSONL、真实 offset 分页和监控；补六态、readonly、401/403 载荷/草稿/审批清理与锁存，实际 Topic/连接/认证同步代际隔离迟返和 fallback ABA。审批一次消费，按每项原 Topic/消费者组验证终态，缺失或传输异常记 unknown 且不重放；不安全整数 offset 禁止 Ack/分页/Seek。Browse 每窗最多1000条、先截断后映射，Inspector最多格式化4096原始字节与有界header，完整已加载JSONL保留。Seek最多25窗/60秒；自动监控最多12轮/60秒且只取消自身请求。专属Node22/22、全Web240/240、TypeScript/Vite、Chrome夹具16/16、既有MQ浏览器3/3和独立复核通过；既有真实Kestrel兼容2/2单列。完整Base64解码/传输/总堆预算、读取恢复、新UI真实Server权限、三宿主/安装/AOT/发布仍独立验收。

- **M47 WB-21 KV 权限与有界预览（2026-10-06）**：复用游标、JSONL、NX/XX、交换/删除与精确版本；新增六态和只读门禁，Scan/Stats/Get/Write 的401/403锁存并清值、统计、游标、结果、写草稿与审批。同身份刷新、空数据库/profile/端点/keyspace往返不解锁；快照与同步代际隔离迟返/ABA，真实Axios分派前取消取得本地adapter证据。扫描累计/Get最多1000项，先截断再映射，超返不复用跳过未保留项的cursor；Inspector最多格式化4096原始字节，完整原值仅用于已加载记录的round-trip导出，不预填截断写草稿。unknown历史保留未知且不重放。专属Node17/17、既有KV12/12、全Web218/218、TypeScript/Vite、Chrome夹具12/12与独立复核通过；既有真实Kestrel兼容6/6单列。完整Base64解码/传输与总堆预算、atomic全响应、真实新客户端权限/恢复及三宿主/安装/AOT/发布仍独立验收。

- **M47 WB-20 Vector Workbench 兼容切片（2026-10-06）**：保留数据库/measurement/column 原名和旧 key，原始检索冻结连接、实际 API、认证、Schema 与查询参数，隔离迟返/ABA/卸载。401/403 清命中、metadata、解析向量和结果，保留用户输入；空 Schema 往返与同身份刷新保持权限锁存。严格有限数值/已知维度，Top-K 1～100 先截断后显示/导出与历史；距离标 Distance。缺索引 Profile 时拒绝隐式文本 embedding，数据子页复用 Measurement 并传递门禁/代际重建。专属 Node17/17、全Web201/201、TypeScript/Vite、Chrome夹具10/10、既有导入回归1/1与独立复核通过；既有真实Kestrel HTTP兼容3/3单列。真实权限/读取恢复、Profile/模型质量、服务端资源预算及三宿主/安装/AOT/发布仍独立验收。

- **M47 WB-19 FullText Workbench 兼容切片（2026-10-06）**：复用检索、Analyzer、导入与索引维护审批，绑定数据库/集合/索引原名和连接/认证代际，隔离迟返与卸载。HTTP401/403清命中/文档/Token/结果和写草稿，保留检索输入；只读保留查询/Analyzer/导出并阻止写入。Top-K沿用1～100，先截断后读取文档和导出，历史记录预览完整性；重建/导入一次审批且后续批次绑定原目标，重建校验真实同步终态，未知终态不重放。专属Node15/15、全Web184/184、TypeScript/Vite、Chrome夹具8/8及独立复核通过；既有真实Kestrel HTTP兼容4/4单列。真实Server新客户端权限/写终态旅程、安全读取恢复、服务端资源预算及三宿主/安装/AOT/发布继续独立验收，详情见 M47 验证记录。

- **M47 WB-18 Document 读取恢复与高级预览（2026-10-06）**：本地 401/403 锁存后提供显式全新 Find100 验证；失败、会话/资源 ABA、Schema 刷新、外部无权限和卸载隔离迟返，旧草稿与审批不恢复或重放。权限及恢复错误正文不进入页面/历史。Aggregate 保留用户 pipeline 并追加末尾 1001 项输出上限；Distinct 按 1～1000 所选上限多取一个哨兵值，先截断再格式化/导出，历史记录实际预览数与完整性，清除旧 Find 游标。专属 Node 18/18、全 Web 169/169、TypeScript/Vite 与独立复核通过；浏览器结果见 M47 验证记录。既有真实 Kestrel HTTP 回归 3/3 作为独立兼容证据，新恢复/1001 请求的真实 Server 旅程、扫描/中间物化/字节预算、Advanced 子页及三宿主/安装/AOT/发布仍单列。

- **M47 WB-17 Relation Table Workbench 迁移（2026-10-06）**：复用既有分页、行编辑、设计器与审批，补数据库/表原名身份和六态；SELECT 预览最多 200 行，连接/端点/会话/Schema 变化及卸载隔离迟返。HTTP 403 清载荷并锁存权限态；只读保留浏览、结果导出和纯 DDL，写子工作台受限。审批展示值及前后差异、绑定原上下文且仅确认一次；缺终态、损坏响应、传输异常及 HTTP 408/5xx 记 unknown，不重放旧审批。实际 SQL API/Axios 异步发送取消已取本地 adapter 证据；专属 Node 15/15、全 Web 161/161、TypeScript/Vite、Chrome 夹具 12/12 与独立复核通过；修复满页分页按钮被 Copilot 悬浮入口遮挡。真实 Server 权限、物化/字节预算、完整三宿主、安装、AOT 和发布继续独立验收。

- **M47 WB-16 VS Code→Web 导航与认证证据（2026-10-06）**：登录返回统一校验内部管理路由并保留目标数据库/资源；显式 `SONNETDB_WEB_BASE_PATH` 联动 Vite 资产、路由、初始 API、本地连接及 SSE，默认根部署保持兼容。资源树按真实条目匹配活动分组，修复 `table:` 索引 id 错误展开 Tables 的问题。专属 Node 9/9、全 Web 146/146、扩展 Node 20/20、根/代理两次 TypeScript/Vite 构建、浏览器两部署各17/17及独立复核通过；本机真实 Extension Host 调用13节点命令并在外部开启边界捕获链接。九模型/index/backup、原名/冒号、跨库同名MQ、导航字段白名单及草稿不执行已取本地证据；Measurement/Relation既有只读预览单列。真实Server认证/权限、实际代理部署、远程SSE、同origin跨部署存储隔离、VSIX与发布仍独立验收。

- **M47 WB-15 Studio 客户端宿主合同（2026-10-06）**：现有 Web 工作区消费 WB-13 的宿主/profile/端点/数据库原名身份与生命周期；完整合同确认后才开放 Managed Local 操作，external/unknown 保留 Health 并拒绝 Start/Stop。初始化、状态、保存和目录对话框隔离迟返与 A→B→A，端点变化同步认证上下文；连接保存串行、只消费匹配的宿主身份且不产生回授 PUT。专属 Node 10/10、全 Web 137/137、Studio 三类定向 40/40、TypeScript/Vite、StudioNative 浏览器夹具 8/8与独立复核通过。真实 Server 权限、WebView2/干净 Windows、安装升级卸载及三宿主发布继续独立验收。

- **M47 三宿主会话交接与后续队列（2026-10-06）**：WB-12/13/14分别保存本地验证与独立提交；HANDOFF顶部集中记录真实哈希、风险边界与唯一写入归属，路线图区分本地切片和完整验收，队列列出WB-15～WB-18。用户确认的新会话已创建，同一个每30分钟workbench heartbeat转移并回读核实；旧会话最后交接提交后停止写入，三宿主整体研发继续，未push/发布/部署。

- **M47 WB-12 Document Workbench 迁移（2026-10-06）**：保留 database/collection 原名和旧入口，补五区锚点与六态、无权限载荷清理、只读写门禁；查询/文件picker丢弃旧目标响应，审批绑定原连接/数据库/集合，导入冻结模式与项目。Find预览最多1000文档；同身份Schema刷新保留停止进度与逐项错误，101项导入停止、旧文件失败和桥接握手竞态均有回归。专属Node10/10、全Web127/127、TypeScript/Vite、Document浏览器7/7与独立复核通过；403后显式恢复、Aggregate/Distinct预算、真实Server权限及三宿主整体旅程仍待补。

- **M47 WB-14 VS Code Workbench 资源入口（2026-10-06）**：新增 `sonnetdb.openWorkbench` 命令与 Explorer 上下文入口，将九模型、index/backup 投影到保留 database、原名/大小写与 legacy key 的资源身份；MQ database + Topic / instance `.system/mq`、Graph Beta 不变。链接保留部署子路径，只含 database/model/tool/node，不携带凭据或 SQL、不自动执行。TypeScript、Node20/20与明确本机 Code.exe 的真实 Extension Host 注册 smoke通过；浏览器最终认证/权限/回选、VSIX和三宿主全旅程仍待独立证据。

- **M47 WB-13 Studio 宿主合同（2026-10-06）**：既有 `/connections` bridge 返回 `studio-desktop`、canonical profile/端点与数据库原名身份，连接库拒绝相对、非 HTTP(S)、凭据、query/fragment URL，保留部署子路径；既有 `/server/status` 增加进程归属、生命周期与 `canStop` 派生字段，外部实例不能由 Studio 停止。source-generated JSON、无 Token 落盘和 GUID 临时保存回收保持；Release 定向 34/34 与独立差异复核通过。Web 客户端消费、干净 Windows/WebView2、安装和三宿主发布仍待独立验收。

- **博客园系列核查与自动发布队列（2026-10-05）**：新增 `docs/blogs/publishing-state.json`、事实复核记录和 135–142 八篇当前能力文章，覆盖九模型、SQL 名称大小写、KV/文档、SonnetMQ/流处理、typed MCP/RAG、语义图片检索、M47 工作台和性能证据边界；旧 001–134 底稿起初在缺少博客园 URL/postId 时暂停发布，再按下述公开记录回填，避免重复发布。Codex 项目自动化 `sonnetdb` 已配置为 Asia/Shanghai 每日 11:00 串行发布最多两篇，只有收到明确 URL 或 postId 才回填 `published`。发布状态依据实际博客园记录；数据库生产发布门禁仍按独立证据验收。

- **博客园即时发布（2026-10-05）**：135《SonnetDB 当前能力全景：九种原生模型与一套数据库目录》已发布到[博客园](https://www.cnblogs.com/IoTSharp/p/23202291)，136《SonnetDB SQL 名称大小写合同：原名、双引号与安全迁移》已发布到[博客园](https://www.cnblogs.com/IoTSharp/p/23202334)；状态与审计事件已回填，发布计划新增 ✅/🕒/❓ 标记列。

- **博客园历史对账与后续选题（2026-10-05）**：公开账号 9 页记录确认 001–075 的历史发布链接，075 为历史原稿；另登记 10 篇多模型专题，076–134 保留待核对。新增 143–202 共 60 个选题、14 个系列及事实来源，自动化在队列不足时先核实写稿，按每日两篇总额度发布并同步标题链接、表情与事件；未完成正文的选题不进入发布队列。

- **M47 管理工作台设计评审包（2026-10-05）**：新增完整导航、M29 五区外壳、共享对话框/提示、页面任务规范与静态 HTML 原型，规划覆盖七一级、30 个全局页面、九模型及 166 个任务页签（模型任务 60 个）；明确数据库索引/备份节点、MQ 数据库逻辑命名空间与实例共享日志、Graph Beta、三宿主和旧入口兼容边界。九模型统一数据库资源树，物理存储范围不直接决定导航。按用户要求先评审全菜单/导航与共享框架，再逐页细化，用户确认原型后才进入生产实现。本条记录设计文档与原型交付，不代表生产前端、真实 Server、安装、Extension Host 或三面发布证据通过。见[设计评审包](docs/design/m47-unified-management-workbench/README.md)。
- **M47 WB-01 原型交互闭环（2026-10-05）**：设计原型补齐工作区页签 active 可见性、内存草稿标记与关闭后保留、对话框焦点恢复、箭头/Home/End 键盘导航、窄屏动作门禁和错误/规划状态的“检查并重试”边界；桌面与 390px Playwright 证据仅覆盖静态原型，不代表生产前端或真实服务能力。
- **M47 WB-02 首批页面状态合同（2026-10-05）**：为 SQL、KV、SonnetMQ、对象 Bucket、Graph Beta 五页补充 capabilities、状态矩阵、空态、专用字段和主动作，并把合同注入对应任务页；其余页面和真实宿主接线仍待后续批次。
- **M47 WB-02B 下一批模型状态合同（2026-10-05）**：为时序 Measurement、关系表、JSON 文档、向量索引和全文索引补充 capabilities、六态 stateMatrix、专用字段与边界说明，并把页级合同注入全部对应任务页；新增 Node 静态合同测试 2/2。保留 SQL 名称、TAG/FIELD、JSON 属性键、显式向量 Profile、hash fallback 与全文任务证据边界；仅为 REVIEW_DRAFT 原型设计，不代表真实 Server、权限、质量、三宿主或生产迁移验收。
- **M47 WB-02C 全局页面状态合同（2026-10-05）**：为数据库目录、连接管理、SQL Notebook、执行历史和性能指标补充 capabilities、六态 stateMatrix、规范专用字段与任务注入；连接测试明确拆分 URL/健康、认证、数据库/对象权限和能力检查，Notebook 保持全规划且不自动执行，历史恢复不重放，指标示例不等于采样或质量/成本证据。Node 静态合同测试 3/3；仅为 REVIEW_DRAFT 原型设计，不代表真实 Server、权限、三宿主或生产迁移验收。
- **M47 WB-02D 观测与数据流状态合同（2026-10-05）**：为实时事件、慢查询、告警规则、运行时诊断和 Modbus TCP 补充 capabilities、六态 stateMatrix、规范专用字段与任务注入；明确视图暂停不暂停服务器、慢查询恢复不重跑、告警与诊断仍规划且不发通知/伪造健康、Modbus Runtime/Pending/Audit 与审批边界。Node 静态合同测试 3/3；仅为 REVIEW_DRAFT 原型设计，不代表真实 Server、权限、现场写入、三宿主或生产迁移验收。
- **M47 WB-02E 工作台与数据流状态合同（2026-10-05）**：为实例概览、最近工作区、导入任务、对象传输和任务与位点补充 capabilities、六态 stateMatrix、规范专用字段与任务注入；保留 MQ `scope=database` / `persistenceScope=instance` / `.system/mq` 单库备份边界、工作区恢复不重放、无持久 resume 需重新选文件、服务器 checksum/complete/abort/取消终态，以及 generation/revision/offset/object version 分离。Node 静态合同测试 3/3、WB-02B/C/D 联合 8/8；仅为 REVIEW_DRAFT 原型设计，不代表真实 Server、权限、现场写入、三宿主或生产迁移验收。
- **M47 WB-02F AI 与 MCP 状态合同（2026-10-05）**：为 AI Connect、Copilot/Provider、RAG 管理和工具权限与外发补充 capabilities、六态 stateMatrix、规范专用字段与任务注入；保留 typed Streamable HTTP 与规划中的 stdio bridge/配置向导边界、真实模型质量/成本与 usage 独立证据、RAG profile/generation/revision 续跑合同、默认只读工具、权限交集、显式数据外发和凭据不外发。Node 静态合同测试 3/3、WB-02B/C/D/E 联合 14/14；仅为 REVIEW_DRAFT 原型设计，不代表真实 MCP/tools/list、Provider、模型质量、权限、三宿主或生产迁移验收。
- **M47 WB-02G 治理页面状态合同（2026-10-05）**：为用户、数据库授权、访问 Token、审批与审计、数据库备份与恢复补充 capabilities、六态 stateMatrix、规范专用字段与任务注入；保持控制平面与数据库权限分开、MQ database grant/实例共享 Store、Token 一次性明文、审批服务器终态/错误不重试、本地草稿与服务端审计分离、单库备份不覆盖共享 `.system/mq`。Node 静态合同测试 3/3、WB-02B–F 联合 17/17；仅为 REVIEW_DRAFT 原型设计，不代表真实权限、Token、审批执行、灾备或生产迁移验收。
- **M47 WB-02H 设置与发布页面状态合同（2026-10-05）**：为偏好、实例配置、Studio 宿主、能力/发布矩阵和关于帮助补充 capabilities、六态 stateMatrix、专用字段与任务注入；保留宿主范围与服务端预算边界、敏感配置脱敏、Web 不伪造 Start/Stop、干净 Windows/Extension Host 安装证据、manifest/签名未就绪不显示 PASS，以及真实发行物版本和诊断摘要预览。Node 静态合同测试 3/3、WB-02B–H 联合回归 20/20；仅为 REVIEW_DRAFT 原型设计，不代表真实 Server、三宿主、安装、发布或生产迁移验收。
- **M47 WB-03C Explorer 兼容证据（2026-10-05）**：新增静态 Node 合同测试覆盖资源归组、旧 key、原名大小写/冒号、index/backup 特殊资源及 MQ `database + topic` 外层选择上下文；测试使用 `--experimental-vm-modules`，5/5 通过。记录了 index-only 首选 key 缺口与旧 MQ key 需保留数据库上下文的边界，未修改 Explorer 生产源码。
- **M47 WB-03 资源与能力合同（2026-10-05）**：新增 Web `management-core` 的资源身份、逻辑/物理持久化边界、Graph Beta 标记及纯内存能力注册表；保留原始名称、大小写、冒号和现有 Explorer key，明确 MQ 的 database + topic、实例 `.system/mq` 与单库备份排除边界，并强制通用工厂的 MQ `name === topic` 且 topic 必须显式提供。Node 合同测试 6/6、TypeScript 检查通过。本条仅覆盖静态/内存合同，不代表真实 Server、权限、存储迁移、AOT、三宿主或发布门禁。
- **M47 WB-04 迁移前导航兼容预检（2026-10-05）**：新增只读静态测试覆盖 `/admin`、`/admin/app`、Studio/databases/trajectory-map legacy redirects、现有路由与管理员边界、7 项基础导航、5 项管理员导航及 secondaryNavigation 管理员条件、setup/auth guard 和 trajectory query→SQL 映射；5/5 通过。生产壳与路由未修改，当前仍等待 `REVIEW_DRAFT` 用户确认，不代表七模块生产迁移或三宿主验收。
- **M47 WB-04B Explorer→SQL 深链接兼容预检（2026-10-05）**：新增只读静态测试覆盖九模型 `tool/model/node` 深链接、database selection、index/backup legacy fallback、Open-in-SQL allowlist、KV/MQ/Bucket 排除及 route-only 不自动执行；5/5 通过。未修改生产源码，静态证据不代表 Vue 运行时、真实 Server 或生产迁移验收。
- **M47 设计基线确认并启动 WB-05（2026-10-05）**：用户确认外壳布局、七个一级/二级导航、九模型数据库逻辑资源树、MQ `database + Topic` 与实例 `.system/mq` 边界、Graph Beta、共享流程和六态语义；设计包状态更新为 `CONFIRMED_BASELINE`。WB-05 结果/草稿/历史/审批迁移按两个有界切片启动，逐项验证，不能视为全量生产、真实 Server、三宿主或发布验收。
- **M47 WB-05 结果/草稿/历史/审批工作流迁移（2026-10-05）**：结果预览统一 10,000 行预算并保留截断/未知终态，SQL 草稿支持关闭后恢复/丢弃且不自动执行；历史新增 `unknown`/完整性字段并对文本、JSON/JSONL 敏感值脱敏；SQL 取消、传输中断和缺少终态不再记录为成功；审批绑定连接、端点、数据库与草稿指纹，失效时要求重新预览/审批，未知结果不自动重试。WB-05 窄测 9/9、Web 回归 94/94、SQL 回归 11/11、共享工作台 Playwright 3/3 通过；仍不代表真实 Server、三宿主、AOT、安装、发布或全量页面迁移验收。
- **M47 WB-06 生产外壳与七模块导航迁移（2026-10-05）**：生产壳采用唯一的概览、工作台、观测、数据流、AI 与 MCP、治理、设置模块 descriptor，设置置于 rail footer；新增模块兼容 aliases，保留旧 route/guard/trajectory query 与九模型 Explorer/MQ 数据库上下文。flows/govern 保持管理员边界，flows→Modbus、settings→About、ai→RAG 仅为当前实现落点。迁移、兼容与 Explorer 路由测试 13/13，管理 Explorer 5/5，TypeScript 与独立复核通过；不代表真实 Vue runtime、Server、三宿主、AOT、安装、发布或全量页面迁移验收。
- **M47 WB-07 Explorer 资源身份页签投影（2026-10-05）**：Explorer 的九模型、index 与 backup 节点统一投影 `ResourceDescriptor`；对象页签携带 database/resource/legacyKey 三元身份，保留既有 `tool/model/node` 路由与 route-only 行为。MQ 明确 `database + Topic`、`scope=database`、`persistenceScope=instance`、`.system/mq` 与单库备份排除；Graph 保持 Beta。WB-07 自有测试 5/5、Explorer/路由/壳联合回归 18/18、全 Web Node 回归 102/102、TypeScript/Vite build 和独立复核通过；直接浏览器旧 query 的 `node` 选择仍留待后续切片，不代表真实 Server、权限、三宿主或发布门禁。
- **M47 WB-08 旧深链接对象回选（2026-10-05）**：新增 `model/node`（及 tool-only）到数据库本地 Explorer legacy key 的精确投影，覆盖九模型、index、backup、大小写/冒号与未知/缺失回退；SQL 工作台在 schema/management 就绪后按 route token 一次性回选，route-only 不执行 SQL，watcher 顺序避免默认首项页签与目标页签重复。自有测试 5/5、定向回归 20/20、全 Web Node 107/107、TypeScript/Vite build 和独立复核通过；URL 无 database 参数仍使用当前 active/default DB，真实 Server、权限、三宿主与发布门禁不在本切片内。
- **M47 WB-09 深链接数据库上下文（2026-10-05）**：新生成 Explorer 深链接附带可选 `database` query，同时保留旧 `tool/model/node` 形状；SQL 工作台等待数据库列表后，仅对有效数据库选择并回投影对象，缺失/未知数据库回退当前 active/default，database-only 与 token reset 恢复路径可重试，route-only 不执行 SQL。自有测试 4/4、Explorer/路由兼容 10/10、全 Web Node 111/111、TypeScript/Vite build 和独立复核通过；同一旧 URL 下用户手动切库不会被强制切回，但手动切库后只修改该 URL 的 model/node 仍留待后续边界。
- **M47 WB-10 手动切库后的深链接投影（2026-10-05）**：修复同一旧 `database` URL 下用户手动切换数据库后只改变 `model/node` 仍被旧上下文阻断的问题；新 `database` query 仍优先选择有效库，旧 token 清空后在当前库解析并安全回退，route-only 不自动执行。定向 5/5、Explorer/路由/管理兼容 15/15、全 Web Node 112/112、TypeScript、Vite build、独立复核与 `git diff --check` 通过；代码提交 `ee44d7bb34b0b9a95ae3e7914b82a65021bf97f3`。证据仍不覆盖真实 Server、权限、三宿主、AOT、安装、发布或全量页面迁移。
- **M47 WB-11 Measurement Workbench（2026-10-05）**：按已确认五区基线迁移 Measurement 页面状态与动作合同，保留现有 measurement 路由、旧深链接、database、原始名称/大小写和旧 key；补齐 normal/empty/error/permission/readonly/longContent 六态、查询/刷新/CSV/JSON 导出、Measurement/关系表监控，以及经 `WriteApprovalPanel` 的写入/删除/导入审批。无权限错误会清理点值、Schema、监控和旧请求并禁用导出，跨数据库同名切换不会让旧响应回写。专属测试 5/5、全 Web Node 117/117、TypeScript、Vite、Measurement Playwright 8/8、`git diff --check` 通过；代码提交 `390ff526`。真实 Server、三宿主、安装、发布和全量九模型验收仍待补。
- **M45-C01 measurement TAG/time 分组首批（2026-10-04）**：支持 `GROUP BY` TAG 与 `time(duration)` 复合键的裸聚合投影，按时间桶与 TAG 原值稳定排序；稀疏 FIELD 的 COUNT 为 0、其它空聚合为 NULL，Int64 SUM 精确累加且溢出拒绝。FIELD、未知/重复分组键、残差/Geo/HAVING、复合投影与 TAG/多键排序提前拒绝，EXPLAIN 共用校验和名称绑定。覆盖多 TAG/多 series、空输入、flush/reopen、大小写及精度回归；未完成整项 M45-C01、资源预算或持续计算。见[合同](docs/benchmarks/m45-measurement-tag-grouping.md)。
- SQL 复合倒序范围查询在完整条件、同向排序及有限 `OFFSET/LIMIT` 下使用有序索引早停；保留完整时间并列组、稳定主键排序、取消与读取预算边界。分页遇到字节预算短页会继续续读，达到有界页数仍明确失败，不截断结果。

- 关系表 ONLINE 索引在覆盖层超过在线恢复门槛、尚未达到日常自动检查点预算时，按需只调度一次检查点，防止反复返回等待而无法推进；保留 100,000 项门槛、禁用自动检查点配置和既有失败退避，不对已排队或冻结视图反复强制重排。

- 关系表管理器的全部表共享物理请求读取并发、队列及一个维护读取槽，补齐逐表独立预算会放大整体并发的缺口。关闭逐表收集错误并释放共享所有权，现有快照由 state 租约保护，延迟 checkpoint 由 keyspace 生命周期租约保护；普通 KV 管理器仍独立共享其自身预算，指标汇总多个管理器。

- 关系表冷开索引恢复只实例化主键、已发布及在线待构建索引所需列，跳过无关 JSON、文本和 blob 正文，保留完整布局、定长值域、唯一冲突和 CRC 校验；稳定分页复用不可变原始值数组，减少重复复制。文件格式及普通行读取不变，磁盘键元数据驻留和索引逐项校验仍保留。

- 新增每份管理器预算的物理 KV 请求读服务端并发、队列容量与有限等待配置，以及活跃、排队和拒绝指标。默认并发 8、排队 64、等待 5 秒；过载通过 `kv_read_overloaded` 诊断，未开始响应的 REST 请求返回 503 和一秒重试提示。checkpoint 独立保留一个维护读槽和 120 秒等待时限，保存中途失败时删除未发布的部分 state 文件；CRC 校验前归还 I/O 许可。

- **M42 measurement 排序预算（2026-10-03）**：显式累计物化准入扩展到直接 raw SELECT 多键排序，候选、辅助键和分页输出共用根预算，支持未投影标量字段；默认路径与整体 heap/首行、固定硬件门禁独立保留。见[合同](docs/benchmarks/m42-measurement-order-bounds.md)。
- **M43 多分区与任务接线（2026-10-03）**：新增 `CdcStreamingBridgeTopology` 有界公平推进和 `FileStreamingTaskRunner` 目录身份校验/自动投递入口；真实 `--topology` 样例核对文档源、未 ACK 窗口去重及独立位点重开。远程拓扑、业务事务和发布门禁继续待补。见[合同](docs/m43-local-task-topology.md)。

- **M42 表值函数物化准入（2026-10-03）**：直接 measurement `knn`、文档 `vector_search` 与文件 `json_each/json_table` 在候选及标量结果保留前使用累计行/估算字节预算；复杂来源和同名注册回调预检拒绝，默认路径保持兼容。文件预算模式逐记录读取，候选替换、排序快照与结果可能重复计费；新增 `--budgets` 真实文件/双模型向量/重开入口。见[合同](docs/benchmarks/m42-table-function-result-bounds.md)与[并行记录](docs/audits/roadmap-parallel-next-20261003.md)。
- **M42 直接 Graph SQL 物化准入**：直接节点/边与原生固定一跳 GRAPH_TABLE 的标量 WHERE/投影/OFFSET/LIMIT 使用分页候选和累计行/估算字节预算；EXPLAIN/ANALYZE 共用根预算，复杂路径预检拒绝。默认查询、图快照和文件格式保持兼容，整体 heap/首行及远程门禁仍待验证。见[合同](docs/benchmarks/m42-graph-result-bounds.md)。
- **M43 有界自动订阅投递**：新增显式驱动器，串行回调、成功后 ACK、失败退避与持久次数上限，默认耗尽保留或显式条件 DLQ；超时停止，回调未收敛前保留同对象租约。见[合同](docs/m43-streaming-dispatcher.md)。
- **M43 CDC→流恢复桥接**：新增单分区 outbox、目标接收凭证和身份/摘要核对，交付证明先于源 ACK；未知且事件已回收的发布明确拒绝推进，保留人工恢复边界。样例新增真实文档 change feed→桥接→自动投递→持久窗口入口；无业务副作用 exactly-once 声明。见[合同](docs/m43-cdc-streaming-bridge.md)及[验证报告](docs/audits/roadmap-parallel-next-20261002.md)。
- **M42 Document ORDER BY 物化预算（2026-10-02）**：直接文档 raw SELECT 支持单列结果名 `ORDER BY`；阻塞排序先完整扫描并逐行计入 `SqlRowRetentionBudget`，再应用排序与 `LIMIT/OFFSET`，因此 `LIMIT 1` 不能绕过全量排序工作集预算。TTL、冷开、取消和 `EXPLAIN ANALYZE` 保持固定查询时刻与既有未支持边界；聚合、JOIN、多列排序和嵌套来源仍在执行前拒绝。见本轮[验证报告](docs/audits/roadmap-next-three-closure-20261002.md)。
- **M43 持久会话 COUNT 窗口（2026-10-02）**：新增独立版本化文件聚合器，支持等号仍桥接的不活跃 gap、乱序桥接、watermark/迟到策略、全批原子拒绝、关闭后回收防复活、状态容量/分页/操作时限及提交后 ACK 去重；状态采用 source-generated JSON、SHA-256 和单执行者 lease。真实文件订阅重开、远端传输、掉电和生产门禁仍待补。
- **M43 持久订阅任务目录（2026-10-02）**：新增有界任务定义目录，保存稳定身份、相对路径、订阅定义和目录 revision，支持 CAS 注册/更新/删除、revision 绑定分页、容量边界、坏 JSON/hash fail closed，并通过真实 `FileStreamingSubscription` 提供状态、暂停和恢复入口。目录不伪造运行状态，不提供分布式租约、自动调度或业务副作用事务。见[任务目录合同](docs/m43-streaming-task-catalog.md)。
- **M42 TTL 文档 SQL 物化准入（2026-10-02）**：显式预算 raw SELECT 在固定查询时刻逐文档判断 TTL，过期候选推进游标但不计结果保留、不执行过期删除；冷开与预算 EXPLAIN 跳过 TTL 全量回收，TTL 时间计算避免 Int64 相加溢出。派生索引初始化、阻塞查询、整体 heap 及远程预算仍有独立边界，见[合同](docs/benchmarks/m42-document-ttl-result-bounds.md)。
- **M43 持久滑动窗口（2026-10-02）**：新增显式 size/slide 的 COUNT、精确 decimal 与字符串分组窗口，每事件最多 128 个重叠成员；复用提交后 ACK 与重投去重，按成员处理关闭窗口和整批拒绝。独立 v4 状态保留 v1～v3 读取与原字段哈希，见[合同](docs/m43-sliding-window-contract.md)。
- **M43 DLQ 本地重放与删除（2026-10-02）**：持久重放身份及领取次数支持重开续处理，条件删除按独立目录 revision、原投递身份和重放回执回收容量，死信序号不复用；旧 v1 严格校验后迁移 v2。业务处理与删除不构成跨目录事务，见[运维合同](docs/m43-streaming-dlq-operations.md)。
- **M43 滑动窗口与 DLQ 组合恢复验收（2026-10-02）**：真实本地文件旅程覆盖隔离、同一 ReplayId 重开、目标窗口提交后未 ACK 去重和条件删除；并行切片的构建、98 项定向回归、5815 项 Core 回归和强杀恢复证据见[核查报告](docs/audits/roadmap-parallel-20261002.md)，远程及生产门禁仍独立待验。
- **M42 文档 SQL 物化准入（2026-10-01 第四批）**：直接无 TTL 集合 raw SELECT 按文档 ID 逐条读取，在过滤、OFFSET/LIMIT 和投影后、保留结果前共享累计行/估算字节预算；排序、聚合、搜索、JOIN 和嵌套文档来源在扫描前拒绝。底层 KV 快照和单值解析仍有独立工作集，不承诺整体 heap 上限或远程预算选项。见[合同](docs/benchmarks/m42-document-result-bounds.md)。
- **M43 持久分组窗口（2026-10-01 第四批）**：显式顶层 JSON string 键按原值分组，支持 COUNT 与精确 decimal 数值聚合、键数/结果数/字节边界、组合游标分页和关闭窗口回收；分组使用独立 v3 状态，旧 COUNT v1/数值 v2 保持原字段与 SHA-256 校验。见[合同](docs/m43-grouped-windows.md)。
- **M43 持久 DLQ（2026-10-01 第四批）**：耗尽批次可经 delivery ID、revision 和 attempt 条件显式隔离，先保存完整事件与原因再推进确认，重开协调中断提交；有界摘要与事件读取，容量满保留原批次。独立 DLQ 状态不改变订阅 v1~v3；窗口不自动跳过隔离后的位点差距。见[合同](docs/m43-subscription-dead-letter.md)。
- **M43 数值窗口（2026-10-01 第三批）**：持久 UTC 滚动窗口新增显式 JSON 数值字段选择与精确 decimal SUM/MIN/MAX/AVG；坏值、不可精确表示和总和溢出整批拒绝，保留已有 COUNT 状态读取与提交后未 ACK 重投去重。见[数值合同](docs/m43-numeric-windows.md)。
- **M43 持久订阅运维（2026-10-01 第三批）**：新增持久暂停/恢复与耗尽批次的受控投递计数重置，通过状态 revision、delivery ID 和 attempt 条件拒绝陈旧命令；暂停期间允许发布与 ACK，旧 v1/v2 状态严格校验后迁移到 v3，旧实现不能重开 v3 文件。读取和容量等待设取消及五分钟上限，不承诺 DLQ 或分布式运维。见[运维合同](docs/m43-subscription-operations.md)。
- **M42 measurement 物化准入（2026-10-01 第三批）**：直接 raw SELECT 的跨字段时间轴合并改用惰性前沿，过滤和分页后的结果在入列表前共享累计行/估算字节预算；opt-in 模式在扫描前拒绝尚未纳入预算的排序、聚合、JOIN 等路径，默认调用保持原合同。存储快照与页解码不属于整体 heap 硬上限。见[查询合同](docs/benchmarks/m42-measurement-result-bounds.md)。
- **M43 #386 多分区本地 CDC 拓扑（2026-10-01）**：新增 `CdcLocalReplicaTopology`，以精确源/实体/分区身份绑定最多 64 条独立路由，轮转推进快照及增量，限制次数、时间、行数和字节数，并从各分区持久位点重开；一路失败保留其它分区提交，失败路由不提前 ACK。仍不提供跨分区事务、远程传输、冲突解决或 schema 演进。见[合同](docs/m43-local-cdc-topology.md)。
- **M43 #391~#395 持久窗口切片（2026-10-01）**：新增 `FileStreamingWindowAggregator`，保存固定 UTC 滚动 COUNT、已应用订阅位点和最后批次身份，先提交窗口再 ACK；重投核对内容后跳过重复计数，支持显式 watermark、迟到丢弃/拒绝、容量边界、有界结果分页和关闭窗口回收。使用独立 source-generated JSON 状态，不变更现有订阅格式；数值聚合、任务运维及远程执行继续待补。见[合同](docs/m43-persistent-windows.md)。
- **M43 #396 组合恢复样例（2026-10-01）**：新增可运行 `SonnetDB.CdcStreamingJourney`，真实两文档集合经过多分区 CDC 快照中断/增量续传、源副本对账、固定副本导出、持久订阅及 ACK 前窗口恢复，核对最终计数与检查点；设置三十秒总时限和专属临时目录清理。范围限于本地有序关闭重开，不替代十四能力总验收或持续 CDC 到流的事务桥接。见[复现入口](samples/SonnetDB.CdcStreamingJourney/README.md)。
- Measurement schema-on-write 增加数据库总 measurement 数、单 measurement 列数和单次写入新增列数上限；支持 `Disabled`、`CreateOnly`、`CreateAndEvolve` 全局及按 measurement 覆盖。新增稳定内容 revision、进程内有界审计事件、扩列/类型提升/拒绝指标，并向 REST、SDK、MCP 与管理后台提供缓存刷新依据。关系表未知列自动 DDL 的独立 opt-in 边界见 [设计文档](docs/design/relational-auto-ddl-opt-in.md)，当前默认仍严格拒绝。
- **M42 SQL-002 单表预览早停**：显式 REST 预览的可惰性关系表 SELECT 在执行阶段按行数和估算字节预算保留前缀，并准确报告截断；默认 16 MiB 字节预算可配置。复杂阻塞查询、其它模型及 DML RETURNING 保留现有结果合同；物化估算不等于 CLR heap 硬上限或端到端流式执行。
- **M43 #385~#390 本地真实 CDC 源切片（2026-09-30～2026-10-01）**：新增 `CdcSourceReadView` 从文档集合的稳定 KV 读快照生成固定行集，并以 `OpenExisting` 校验及恢复创建结果未知时的持久 descriptor；`CdcDocumentSourceCapture` 将单集合持久 change feed 按 spool 高水位转换为版本化事件，`CdcDocumentSourceCaptureScheduler` 在调用方任务中按批次/事件/间隔有界推进，`CdcLocalReplicaPump` 每次有界推进一页快照或一批增量并协调接收端提交与 spool ACK。文档主数据、change feed 和序号由集合写入路径在同一 KV batch 提交。范围限于单源/单实体/单分区；多分区、远程拓扑、冲突和生产门禁仍待补。
- **M43 #386 本地快照/增量接收端（2026-09-30）**：新增有界 `CdcSnapshotReplica`，提供固定快照分页恢复、完整计数切换、同源/schema/分区连续增量的原子物化与位点恢复，复用 CDC spool 保留复制期间的增量；接收端本身不调度源捕获，也不提供多分区切换、冲突或远程复制拓扑。
- **M43 #392 持久订阅（2026-09-30～2026-10-01）**：新增 `FileStreamingSubscription`，复用 CDC spool 与条件 checkpoint 保存事件、watermark、发布完成标记及未确认批次；重开协调中断的 ACK，恢复扫描协作检查取消/操作超时，支持有界容量、可取消背压、稳定投递 ID、至少一次重投、投递次数上限和 backlog 运维快照；状态格式 1 可校验迁移到格式 2。窗口聚合、远程运维和分布式边界仍待补。
- **M43 #390/#395 本地强杀恢复（2026-09-30）**：新增 5 项真实子进程 hard-kill/reopen 场景，覆盖文档源捕获续传与源/副本对账、部分快照页续传、物化已提交但 spool 未确认、容量为 1 的尾批回收，以及订阅未确认批次稳定 ID 重投和确认后再重开；不替代真机掉电、远程拓扑或长期 gate。
- **M42 SQL-002 关系查询/DML 物化准入（2026-09-30～2026-10-01）**：`SqlExecutionOptions` 新增 opt-in 累计物化行数/估算字节上限，关系 SELECT、CTE/子查询、集合、阻塞阶段、EXPLAIN 以及关系表 INSERT/UPDATE/DELETE 共用根预算；DML 在 mutation 进入事务缓冲或提交前计费，超限整条语句拒绝，支持 RETURNING、INSERT SELECT 和 ON CONFLICT。其它模型、用户回调和不支持路径仍在执行前拒绝。估算不等于 CLR heap 硬上限，REST 预览、端到端流式与生产性能证据继续独立。
- **M43 CDC 源捕获有界调度（2026-10-01）**：新增 `CdcDocumentSourceCaptureScheduler`，在调用任务内按最大批次数、事件数、间隔和取消令牌连续推进单集合捕获，返回逐批进度与停止原因；不创建无界后台循环，不改变单分区 spool 合同。见 [调度审计](docs/audits/m43-cdc-capture-scheduler-20261001.md)。
- **M43 持久订阅本地运维窗口（2026-10-01）**：文件订阅新增 `MaxDeliveryAttempts` 上限、达到上限的可恢复异常和 `GetStatusAsync` backlog/最早事件年龄/in-flight 状态快照；事件仍可显式 ACK 回收。远程 parity、分布式租约、DLQ 和 exactly-once 继续待补。
- **M35 RAG 真子进程硬杀恢复（2026-10-01）**：新增 `Process.Kill(true)` 中断 embedding 后重开并续跑的 CrashTests，验证已 durable chunk 复用、未完成 chunk 单次重算和完整 generation 发布；证据只覆盖本机 staging/WAL，不代表真实模型质量、掉电或固定硬件容量。见 [恢复审计](docs/audits/m35-rag-hard-kill-20261001.md)。
- **M43 #397 发布证据汇总工具**：新增固定八组清单与 `verify-m43-release-evidence.ps1`，重新调用既有候选/nightly/M19/M25/Graph verifier，保存原始结果与 SHA-256；绑定 M19 仓库/run 身份、复核验证后原始文件、拒绝链接及重叠输出目录，M40 从干净候选源码重新构建 evaluator。公开 `PASS`/`NOT_READY`/`DEFERRED`，保留 M25 attestation 缺口及未取得的模型/安装/长稳证据，30 项本地脚本合同接入 CI。见[汇总合同](docs/m43-release-evidence.md)。
- **M43 #398/#401 资料草稿与复现索引**：新增[DBDB.io/DB-Engines 中英文资料包](docs/ecosystem-directory-dossier.md)和[生态样例/基准复现索引](docs/ecosystem-reproduction-index.md)，与十四能力索引保持分类、`partial` 和 Graph `beta` 口径；外部提交仍为 `NOT_SUBMITTED`，不宣称收录、排名或发布级基准完成。
- 管理后台新增“关于”页面与 `/v1/system/about` 只读端点，展示 SonnetDB 版本、版权、GitHub/Gitee 仓库、企微群二维码，以及服务主机的操作系统、CPU、内存、磁盘和 GPU 快照。
- 新增龙芯 `linux/loong64` 专用 Docker 运行镜像配方，使用已验证的原生 .NET 10 runtime 与发布产物、固定的 Debian Loong 基础镜像和容器内健康检查；构建与部署步骤见 `deploy/loongarch64/README.md`。
- 完善 4.0.0 中文候选发行说明，汇总关系 SQL、ADO.NET 写入、向量与图片处理、多模型客户端和部署变化，补充主版本升级建议及带提交身份的候选验证结果；保留 Graph Beta、七天 scheduled 和现场验收边界，并加入发布文档索引。
- **M27 #340 ServerRelay 功能完成（2026-09-26）**：功能交付和本机合同标记完成；当前主分支 Release 构建 0 warning/0 error，双独立 Server smoke 的 live follow、hard-kill failure seal、稳定失败重放和清理通过（`PASS_LOCAL_ONLY`）。用户将人工验证真实 IdP、部署双网与 Studio 现场；这些现场验收保持待执行，不影响功能完成标记。见[复验与验收边界](docs/audits/relay-multi-instance-closure-20260923.md#2026-09-26-主分支复验)。

### Changed
- CAP 原实现继承当前 main 后完成准确集成树的真实嵌入式/Kestrel 全项目 42/42 回归，保留 Document 业务/Outbox 与 MQ 慢消费组重开合同；完整 CAP NativeAOT 宿主和正式依赖包消费者仍未验收，不能由适配层零警告构建或历史候选包推导。
- CI CodeQL 的 init/analyze 同步从 v4.38.1 升级为 v4.38.2（PR #213）；保持原分析语言和构建步骤，合入独立 Windows 测试稳定性前置。

- 测试侧 xunit.runner.visualstudio 从 4.0.0 升级为 4.0.1（PR #269），合入独立 Windows 测试稳定性前置；不增加产品运行时依赖。

- PR #215/#216：将 SkiaSharp 与 Linux NoDependencies 原生资产同步升级到 4.153.1，修正单独升级产生的托管/原生版本失配；图像解码、缩放及对象处理按真实 native 路径回归。
- PR #267：升级 Microsoft.ML.OnnxRuntime 1.30.0→1.31.0；保持显式模型 profile 和 native/synthetic tensor 合同，不以合成模型结果代替真实模型质量验收。
- `Testcontainers` 从 4.15.0 升级至 4.16.0，保留现有 SonnetDB 容器模块的五个目标框架与非 AOT 宿主边界（PR #268，M43 生态工具链）。
- 测试侧 `coverlet.collector` 从 10.0.1 升级至 10.1.0，保持私有测试依赖与现有覆盖率采集入口（PR #214，M43 生态验证工具链）。

- **Workbench Preview 1 路线整理（2026-10-10）**：将根 ROADMAP 中已完成的切片、旧状态表和窗口检查点归档；主路线改为范围/候选冻结、Web smoke、真实登录与权限、发行物、实际部署、文档与分发、放行、发布回读八步。同步 README 与旧队列的当前入口；本条仅记录文档整理，没有新产品实现、测试或发布结果。

<a id="roadmap-completed-archive-2026-10-10-workbench"></a>

#### Workbench 与路线完成范围归档（2026-10-10）

核查基线为本地 `78045e8b` 与 [2026-10-10 发布审查](docs/design/m47-unified-management-workbench/release-readiness-2026-10-10.md)。以下归档已发生的实现或指定场景证据，不宣布三面整体完成，不新增包版本，也不把 dirty 工具集成记录作已提交交付。剩余任务和 Preview 1 发布顺序只在根 [ROADMAP](ROADMAP.md#workbench-preview-1)维护。

| 已交付范围 | 完成内容 / 证据 | 保留边界 |
|---|---|---|
| M47 WB00～WB10 | 设计/原型、资源与能力、导航/Explorer、结果/草稿/历史/审批、外壳和深链接切片 | 设计覆盖不是全部页面生产完成；真实服务与宿主分别验收。 |
| 九模型页面 WB11～WB25 | Measurement、Relation、Document、FullText、Vector、KV、MQ、Graph、Object 的身份/状态/权限/预览及审批切片 | 不把局部读写/预算合同当完整矩阵；Graph Beta、MQ实例持久化边界保持。 |
| 九模型真实 Web 旅程 WB26～WB37 | Document26、Relation27、FullText28、KV29、Measurement31、Vector32/33、MQ34、Graph35/36、Object37 的本机真实 Kestrel 明确场景 | 登录 API/token 注入不算真实登录 UI；各自全量权限/恢复/预算、其它宿主与发行物仍开放。WB33 已补 Vector 子页权限上行；WB36 已补 Graph 恰满顶点预算导出。 |
| Studio WB13/15/39/40/80 | bridge/Managed Local 与 Web消费、本机宽窗和默认/窄窗生命周期；WB80 两次启动、A/B切库、B被动恢复/真实查询和两次正常关闭通过 | WB80复用旧Release；当前候选包来源、OS文件对话框、干净Windows/WebView2、安装升级卸载和Server优雅关闭不由此证明。 |
| VS Code WB14/16/48/58/59 | 九模型导航/治理深链接、history完成与安全观察、SQL elapsed字段兼容；历史实际查询子集和基础Host证据 | 完整候选 Query/history/退出、向导/分页/Notebook/LSP和新VSIX仍独立待验；不能用局部响应或PID absence升级失败。 |
| 诊断/证据记录（WB49～WB84 内已提交切片） | 有界身份、cleanup子检查、Native/VS Code诊断合同及对应本地集成/运行记录 | 只归档已提交切片，不称该编号范围全部成功；诊断/工具合同不等于产品宿主旅程，旧失败保留。WB85～WB98 dirty 集成没有提交，不纳入本行已提交范围。 |
| M45-C01 首批 TAG/time 分组 | 首批代码、SQL/EXPLAIN和定向回归 | 通用分组/state、更新删除修正、批流等价及恢复/预算仍在M45。 |
| 2026-10-08 木垒只读五子项 | ARM64配置挂载、六索引定义、DESC/OFFSET及读取放大比、REST截断边界、三图读取/内容SHA256 | 绑定现场提交6a8d82d2；跨架构/全部模型客户端、冻结指纹、恢复容量与168小时未完成。 |

##### 未完成集成的记录校正

原 CHANGELOG 顶部 WB97/WB98 的“集成”草稿不再作为 Added 完成交付：WB97-C3 仅 inert micro 通过，restore在dotnet启动前失败；C4及WB98独审超时，最终完整restore/format/commit均未放行。WB96的97项命名测试/语法是本地合同证据，不是一次新实际Host或发行物验收。三脚本pending和所有原失败/收据保留；不为首个Web预览继续默认实施该循环。

下列原文作为迁移时的历史快照保存。包含当时未完成边界、FAILED/UNKNOWN和旧ACTIVE/自动接续措辞，**均不具有当前派单或自动化授权效力**；当前顺序与暂停状态以根ROADMAP/HANDOFF为准。快照只保证原记录可追溯，不把“门禁待验/提交见收据”升格为PASS。

##### 原根路线图开头检查点与规划基线

###### ROADMAP — SonnetDB 4.5

WB84门禁窗口失败闭片（2026-10-09；本会话3/5）：完整restore exit0/naturalExit=true/48.8997354秒且44身份finally absent；Format于10:44:04Z被原600+60秒完整保留时窗守卫拒绝启动，NOT_RUN，未降低门禁/延长10:55Z/新增commit。六自有stage已精确撤回，原proposal/a2a028d9树与审查commitAllowed=false保留；新pending六文档字节、private HEAD+owned HANDOFF及已记录身份fresh退出见artifacts/wb84-vscode-preflight-contract-20261009。提前交接后新本地会话从0/5只接续完整门禁/本地集成，复用已完成调查/独审，不重调查或跑actual/Node/TS/产品build；同一workbench保持ACTIVE30分钟，release和真实目标ID见rollover.json。原WB83FAIL/false/null及全部foreign字节保护；pending变更暂不能提交因为强制Format未运行。

WB84 preflight合同（2026-10-09；本会话第3项）：已核helper6秒与Host命令20秒分立；源码/command/时序支持初始ports helper wait关联推断，内层原因unknown。PID72608前后调用缺immutable关联，原WB83 FAIL/回收false/null不升级。冻结下一JS-only调用ID/phase/timer-vs-abort/close观察，原PS/parser/权限/预算/门禁保持，尚未实施；本片源码/actual/Node/TS/产品build0。六文档完整门禁/本地提交/已记录身份退出与闭合3/5见[报告](docs/design/m47-unified-management-workbench/wb84-vscode-preflight-contract.md)及本片artifacts，同workbench ACTIVE30分钟/本chat。

WB83独立真实窗口（2026-10-09；本会话第2项）：两源只迁移元数据，逆SHA/独审/微试/语法/TS1通过，35输入冻结，Node0/新.NETbuild0。CPU准入首次原3秒超时保留、同条件唯一复核PASS；actual1在preflight helper_deadline FAIL，0phase/history且Code/Host/API null，原五回收flags false、remaining/root/audit=refused/passed/passed。新stop ledger四记录complete（三already_exited/一stopped）仅为诊断；根later24已记录身份0存活/未知、0新stop/delete不升级旧false。精确8路径/完整门禁/本地提交及2/5以[报告](docs/design/m47-unified-management-workbench/wb83-vscode-host-validation.md)和artifacts/wb83-vscode-host-window-20261009为准；下一片有界核preflight helper合同，不盲重actual，三宿主边界保持。

WB82同片必要重验（2026-10-09）：新restore通过，但首format因托管observe-failed被安全中断，原结果UNKNOWN_INTERRUPTED未通过，细因unknown。51已记录身份fresh无存活/未知、2PID复用保留；失败原收据保留。三源码SHA不变/88复用，准确文档后完整restore-retry+原CI format-retry至多一次，只有两新门禁和全身份审计通过才本地提交；同片总预算8/计划5、09:45Z不扩，仍为本会话第1项，关闭后1/5。实际结果见WB82报告及artifacts/wb82-local-integration-20261009。

WB82本地集成接续（2026-10-09；本会话第1项）：释放严格匹配，三最终源码SHA不变，复用88/88而不重实施/重测。旧08:25Z失败闭片保留；新09:45Z窗口的新工具micro/独审、最终九路径完整restore/原CI format、本地commit及已记录身份fresh退出以artifacts/wb82-local-integration-20261009真实收据/Git为准，未取得完整PASS不得提交。本片合计一项，关闭后本会话1/5；0actual/产品build，三宿主与原未知边界不提升。

WB82闭片（2026-10-09；本会话1/5）：三源独审/唯一Node88/88及完整restore通过，Format未运行、无新commit。08:25Z门禁窗口剩余不足，300秒收紧scope接收已过期，未放行；精确撤销自有stage、index空，57已记录身份0自有存活/0unknown、1PID复用保留。已保存真实失败检查点，提前交接优先新窗口完整restore/原format及本地集成，源码未变复用测试，不重做WB81/80；详细事实见WB82报告/artifacts。

WB82（2026-10-09；收尾后本会话1/5）：三脚本新增有界stop attempt/candidate/helper关联、固定JS/PS阶段及helper退出/结果类别；非法/缺失/getter/观察异常或未finish归unknown，原终止权限和三门禁保持。唯一Node88/88（13新、48新场景）与三语法通过，0actual/产品build；完整门禁、本地提交及已记录身份退出见[报告](docs/design/m47-unified-management-workbench/wb82-vscode-stop-diagnostics.md)。WB57原FAIL和未知原因不升级。

WB81（2026-10-09；本会话收尾后5/5）：冻结VS Code stop拒绝的实际证据与下一合同。WB57原16次stop尝试/1次回调拒绝，缺调用与helper结果关联，不能判断OS stop是否执行或具体原因，原FAIL不升级。下一片仅补三脚本固定阶段/关联诊断并做模拟验收，保留全部终止权限，0actual/产品build；详见[报告](docs/design/m47-unified-management-workbench/wb81-vscode-stop-contract.md)。本片提交收尾后按同一workbench ACTIVE30分钟规则滚动接续。

WB80（2026-10-09；本会话收尾后4/5）：唯一新Native实际通过普通A/B、两次桌面启动、被动恢复B、真实查询WB61_B及两次正常CloseMainWindow；四端口释放、零fallback、严格cleanup通过。复用旧Release产物，0产品build，不证明source-to-binary等价或Server优雅关停/恢复；WB77/79原失败保留。本机数据库选择恢复证据及最终本地集成见[报告](docs/design/m47-unified-management-workbench/wb80-native-validation.md)，三宿主、OS文件/安装/ExtensionHost/AOT/硬件/长稳/发布继续分列。

WB79（2026-10-09；本会话收尾后3/5）：真实 C# SaveAsync 落盘与共享 JSON 夹具一致，Studio 连接库定向20/20；disk 校验现接受完整且逐值一致的已知派生身份对，保留旧无派生字段形态、未知字段拒绝及精简投影。Node完整80/80通过；此前C#包装器身份观察FAIL和首次Node中止分别保留，不能追认为已证实的瞬态原因。0Native actual，真实恢复仍未验；最终门禁/精确本地提交及新鲜已记录身份核验见[报告](docs/design/m47-unified-management-workbench/wb79-serialized-library-contract.md)。

WB78（2026-10-09；本会话闭合后2/5）：在WB77本地收尾`25d274ef`后补连接库安全字段类别观察，区分已知存储/派生字段及未知数量，不输出未知名字或值；原disk拒绝和进程门禁保持。新增14项加既有60项合同74/74通过，终态保存保留失败结果，0actual/产品build。最终门禁与精确本地提交见[报告](docs/design/m47-unified-management-workbench/wb78-library-field-observation.md)；实际被拒字段、恢复及正常退出仍未证。

WB77（2026-10-09；闭合后本会话1/5）：wrapper修复/两case微试/独审/micro与语法通过，CPU首次超时后唯一原条件复核通过。唯一Native在第一次A选择前的B preparation磁盘字段守卫失败；fresh PUT/DOM局部通过，恢复未到达、normalExit=false，严格故障cleanup=true。最终门禁与owned本地集成按[接续报告](docs/design/m47-unified-management-workbench/wb77-native-continuation.md)及收据记录；已有14/41/38不重跑、原FAIL不升级，同一workbench仍ACTIVE/每30分钟。

WB76（2026-10-09）：用户要求的远端获取与合并已完成，`main=origin/main=5424b666`、ahead/behind 0/0；同一 workbench heartbeat 已恢复 ACTIVE。新 Native 窗口仅增加固定证据入口及文件前置说明，继承已验 CIM 观测与 PS14/Node41；独审、新鲜资源准入和唯一实际 A/B／双 desktop／B 查询旅程按[WB76 报告](docs/design/m47-unified-management-workbench/wb76-native-actual-window.md)分别记录，尚不声明真实恢复通过。

WB75（2026-10-08）：仅完成已有Native CIM观测的精确本地集成后暂停，修隐藏.git读取并先走真实本机不提交预演，再对新owned树完整restore/原format放行。继承38/14/41、不重跑、不新增actual；旧WB74失败保留。仅原15路径，结果见[WB74/WB75集成报告](docs/design/m47-unified-management-workbench/wb74-owned-handoff-integration.md)与独立收据。heartbeat PAUSED，无push或新切片。

WB74（2026-10-08）：修复本地共享 HANDOFF 的 owned 内容守卫，固定完整工作前缀、精确 owned 段、private blob/index tree 与其它提交路径，仅允许未暂存尾追加。继承 WB73 PS14/Node41，不重测、不改六源，0actual/产品build；原 WB73 集成失败保留。新微试、完整门禁与本地提交以 [WB74 报告](docs/design/m47-unified-management-workbench/wb74-owned-handoff-integration.md) 和独立收据为准；不预填完成。

本文件是 **4.5 版本的主执行路线**：在九种原生模型上强化 AI 应用、通用聚合与持续计算、存储编码和执行成本，并把 Web Admin、Studio 桌面和 VS Code 收敛到一套统一的数据库管理工作台核心，补齐现有能力的远程、恢复、容量、真实质量及三面发布边界。4.5 是规划目标，本文不宣布版本已发布，也不修改当前包版本。

规划基线：2026-10-04，本地提交 `4b004946`；M47 设计基线已于 2026-10-05 获用户确认，生产实现按 [Workbench 队列](docs/design/m47-unified-management-workbench/work-queue.md) 的有界切片推进。当前 WB-00～WB-34 已按各自本地范围验证，WB-34补MQ新Web真实100/51当前JSONL/history、正常Publish/Ack审批和撤权锁存3/3；Node22/22、全Web327/327、MQ fixture16/16与build通过，冒号Topic400/实例恢复等边界单列；最终独立证据复核、完整门禁及本地提交以队列/证据和git log为准。WB-12 Document 为 `54c78755`、WB-13 Studio合同为 `74835847`、WB-14 VS Code资源导航为 `e7cf2fe5`、WB-15 Studio客户端为 `e1f93a69`、WB-16导航/认证为 `0bb628ad`、WB-17 Relation为 `f1978263`；这些局部状态不等同 M47/U01~U09 全量完成。已完成范围归入 [CHANGELOG 本轮归档](CHANGELOG.md#roadmap-completed-archive-2026-10-03-45)，历史背景见[原归档](docs/roadmap-history.md)。本轮研究、证据复核与文档验证见[规划核查记录](docs/audits/sonnetdb-45-roadmap-planning-20261003.md)及 [M47 统一管理工作台专题](docs/design/m47-unified-management-workbench.md)。

现有能力事实继续沿用[综合审计](docs/audits/2026-09-05_project-SonnetDB-report.md)、[九模型证据](docs/audits/nine-model-capability-evidence-20260905.md)、[gap catalog](docs/audits/nine-model-gap-catalog-20260905.json)和[十四能力索引](docs/audits/fourteen-capability-evidence-index.json)，结合后续已核实切片判断。已撤回的系统性能原始报告不作为验收依据。

##### 原完成度与剩余边界表（历史快照）

###### 当前完成度一览（2026-10-06）

这张表放在路线图前部，直接区分“本地切片已经完成”和“整个里程碑仍未完成”。`🟢` 只表示表中列出的切片已通过其记录的本地验收；它不会把真实 Server、固定硬件、长期运行、三宿主安装或发布证据一并标记为完成。

| 范围 | 当前状态 | 已完成 / 当前证据 | 尚未完成 |
|---|---|---|---|
| M47 WB-00～WB-10 | 🟢 | 设计基线、原型交互/状态合同、资源身份、导航/Explorer 兼容、结果/审批工作流、外壳迁移与深链接切片均已提交；提交哈希和测试见 [Workbench 队列](docs/design/m47-unified-management-workbench/work-queue.md)。 | 全量九模型页面、真实 Server 旅程、Studio/VS Code/安装/发布证据仍待补。 |
| M47 WB-11/WB-30/WB-31 Measurement Workbench | 🟡 | 原迁移`390ff526`；WB-30补点/monitor/write精确权限锁存、readonly、连接/Schema代际与旧finally隔离、一次审批/原身份unknown，500行先截断、auto12轮/60秒和导入1000语句/10批/60秒。Node16/16、全Web311/311、Chrome fixture20/20、旧Measurement/Vector子页8/8、build与独立复核通过；既有Kestrel SQL预览端点兼容3/3单列；WB-31新UI真实4/4、当前100/500/61与导出、WRITE两点逐INSERT终态、撤权锁存，Document回归3/3、审计Node10/10/全Web321/321及完整成功证据独立复核通过；最终完整门禁为本地提交放行条件，实际结果见验证记录。 | 显式恢复、完整COMMIT/权威影响数/Int64、raw defaults无信号ABA、文件/Server预算、三宿主/安装/AOT/发行物。 |
| M47 WB-12/WB-18/WB-26 Document Workbench | 🟡 | WB-12身份/六态/隔离与WB-18显式Find100恢复/输出预算已有实现；WB-26补本机新Web→真实Kestrel权限/恢复及Aggregate1001、Distinct501/1000旅程3/3，修空IDs省略与满1000完整性unknown。Node21/21、全Web295/295、Chrome fixture11/11、TypeScript/Vite与独立复核通过。 | 登录UI/routed readonly props、扫描/中间物化/字节/总堆预算、Advanced完整读写、其它九模型真实旅程、三宿主与发行物。 |
| M47 WB-13 Studio宿主合同 | 🟡 | 真实bridge身份/URL与Managed Local生命周期合同已提交 `74835847`，Release定向34/34；客户端消费由WB-15另验。 | 干净Windows/WebView2、安装与发行物。 |
| M47 WB-38 Studio原生宿主诊断 | 🚧 / 完整烟测❌ | 新增真实Studio/WebView2诊断runner；宽窗CSS1266×663/DPR1.5下native bootstrap/manifest、正常Health/Stop/Start与old/new Studio-owned Server身份已核验。Web build、Server/Studio Release通过，根89记录身份回收审计无自有存活；生产不改，详细原失败/局部证据及最终门禁见M47验证记录。 | 三run额度耗尽，CloseMainWindow前helper未发现窗口、process-events超512KiB导致终态缺失；完整正常退出未通过。默认/窄窗Health隐藏、OS文件对话框、安装、完整三宿主/NativeAOT/硬件/长稳/发行物另验。 |
| M47 WB-39 本机宽窗Studio生命周期 | 🟡 | 修诊断普通可见启动/有界主窗发现与独立终态、schema2完整身份去重；16内存故障测试/语法通过，第二真实run普通CloseMainWindow退出0/signalnull、Health/Stop/Start、四端口/旧新Server释放、零fallback/五terminal保存及根63身份回收核验通过。生产不改、旧产物hash复用；首轮root monitor超时/缺文件与WB-38失败保留，最终完整门禁/独立复核/提交见队列。 | 只证本机宽窗，不推断旧windowsHide因果或Server优雅恢复；默认/窄窗Health本机局部证据由WB-40补齐，原生文件对话框、安装/升级卸载、完整Studio/VS Code/Extension Host、NativeAOT/硬件/长稳/发行物及三宿主整体仍待验。 |
| M47 WB-40 默认/窄窗Studio Health | 🟡 | 修native响应式身份/状态/Health与现有生命周期入口隐藏；最终fixture12/12零skip、Web/Server/Studio构建通过，两真实native default不传尺寸/CSS946×556与1000×800/CSS652×476（DPR1.5）六geometry/fiveDOM、正常Health/Stop/Start/CloseMainWindow exit0、四端口/零fallback和独立终态通过。Explorer实测已收起；原skip/9-3/首native wrapper失败保留，首run runtime策略拒删而保留，root164身份无自有业务存活与独立复核通过；最终九文件完整门禁/本地提交见队列。 | 展开Explorer、native库database同步/恢复、登录UI/OS文件对话框、安装/升级卸载、完整Studio/VS Code/Extension Host、NativeAOT/硬件/长稳/发行物与整体三宿主未闭环；保留runtime不计完整cleanup。 |
| M47 WB-41 SQL原生文件诊断 | 🚧 | 显式四phase诊断入口、真实DTO/普通DOM/磁盘与OS独立ack门禁已实现，syntax通过；首actual Open POST观察到，桌面工具无可见picker/激活失败、60秒无ack超时，后三phase NOT_RUN。六失败终态、9核验fallback/四ports/三runtime回收与root失败checkpoint、独立复核保留；生产及旧default不改，最终八文件门禁/提交见队列。 | 四phase真实OS/响应/结果仍未通过；native窗口可观测/正常激活条件须先确认，不盲重跑。不计普通退出、安装/AOT或三宿主整体完成，其它未完成VS Code有界队列仍继续。 |
| M47 WB-42 真实VS Code只读Query诊断 | 🚧 / 两条局部证据 | 新增独立Host/真实Server runner，syntax/TS与既有Node20/20通过；current两行/selection一行、原列及生成SQL/database与独立真实reference相符，HTTP200。首helper parent2秒超时有据修为一次有界snapshot lookup；两失败原证据保持。根两单PIDfallback、两个端口/282对象runtime回收另验，最终八文件完整门禁/本地提交见队列。 | EXPLAIN第三POST200但断言失败/无phase3载荷，history未运行，Code正常退出/runner cleanup未证；reference preview32与生产{sql}差异、partial discovery29/24与finally回收须先修，不第三跑本片。向导UI/Webview分页/Notebook/LSP/安装/AOT/整体三宿主仍另验。 |
| M47 WB-43 查询诊断恢复 | 🚧 / 三条局部数据证据 | sql-only参考/安全观察/身份ledger与独立终态已修；TS/语法、纯内存9/9和Node20/20通过。第二真实Host current2/selection1/EXPLAIN45行与独立真实reference逐值一致、POST200；完整实跑仍FAIL/process_audit，原两失败/false保持，根fresh PID与281对象runtime回收另证，最终十文件门禁/本地提交见M47验证记录。 | ledger近256KiB准入与固定合并reason需有界诊断；history/API恢复/Code正常退出未验证，不第三跑本片。生产maxRows、向导UI/DOM分页/Notebook/LSP、安装/AOT/整体三宿主另验。 |
| M47 WB-44 有界身份ledger/公开历史 | 🚧 / 历史与API局部实证 | 保原门禁压缩external ancestor正文，完整own/连续anchor链与event exact引用/hash保留，missing/changed父拒绝stop；TS/PS7/内存17/17和既有20/20通过。新Host current/selection/EXPLAIN与真实参考同，publichistory3/apiRestored真/清理0已核；完整runner仍FAIL/process_audit，原false保持，root273对象清理另证，最终十文件门禁/提交见验证记录。 | 三个完整identity先验未知拒绝须安全细分，Code正常退出仍未证；不能重复盲跑。生产maxRows、向导/DOM分页/Notebook/LSP、WB41 OS、安装/AOT与三宿主另验。 |
| M47 WB73 Native CIM 本地验收 | 🟡 / 本地夹具通过、0actual | 新窗口 micro 各1/1、PS14/14与Node41/41，原full空结果mock失败保留，夹具bare-return窄修独审；最终owned树完整门禁/本地提交以WB73收据为准。 | 不升级WB72过期或旧strict/actual失败；下一窗口冻结真实Native准入，恢复/正常退出/三宿主/安装/AOT/硬件/长期仍另验。 |
| M47 WB72 Native CIM 阶段观测 | 🟡 / 已实现、验收未全 | 四阶段/160槽/结果数/单调时间及100ms记账停止，原查询/cache/预算/权限保持；静态独审与修复PS micro1/1通过，初始micro语义失败保留，0actual。 | 窗口过期失败关闭，本会话1/5；WB73新本地验收窗口完成Node micro/PS14/Node/full restore-format/owned集成，不升级历史失败或真实恢复。 |
| M47 WB71 Native运行时/snapshot合同 | 🟡 / 静态报告、0actual | 原.62 CDP HTTP成功与.53 prereq文件分列；启动无明确选择绑定，coarseCDP fatal含snapshot，20s总/单CIM20s及四helper统计已核，逐query原因unknown。七路径集成见收据。 | 下一独立WB72冻结既有query阶段耗时观察/夹具，NOT_IMPLEMENTED；不重跑WB70，三宿主/安装/AOT/硬件/长期及旧FAIL保持。 |
| M47 WB70 Native真实窗口 | ❌ / 唯一actual失败检查点 | CR/LF与五metadata inverse/source syntax、fresh资源准入通过；唯一actual CDP helper13/17/21.537s失败，原恢复/normalExit/cleanupfalse。根18identity/四ports门禁后回收324自有对象另证，六路径集成见收据。 | 下一独立片有界查actual runtime .53/.62选择与helper snapshot合同；WB70不重跑，overall/processIntegrityfalse及旧失败保持，三宿主/安装/AOT/硬件/长期分列。 |
| M47 WB69 Native资源准入 | 🟡 / 0actual失败检查点 | 五metadata inverse/source syntax、23依赖/九runtime SHA、.NET10三framework/Playwright CDP与四exclusiveports已核；独立source/tools局部通过但180s有效期晚5.894s，actual NOT_RUN。六路径本地集成见收据。 | 下一独立资源窗口先review再fresh gate/单次actual；过程完整性false与旧WB68/67/66/65/64失败保持，三宿主/安装/AOT/硬件/长期分列。 |
| M47 WB68 普通异库准备 | 🟡 / 本地合同、0actual | runner显式opt-in；首次A已active时最多一次独立B控件准备、完整ack/DOM/disk并重读A守卫，默认及原A→B旅程/关闭/预算保持；Node60/60（新增17）、micro1和源码29/工具27独立检查通过，最终8路径集成见收据。 | 下片先冻结独立native metadata/runtime资源窗口，再验真实恢复/正常退出；旧WB67 numeric退出FAIL、三宿主/安装/AOT/硬件/长期分列。 |
| M47 WB67 helper三统计保留 | 🟡 / 本地合同、0actual | H→R→E固定安全三标量，unknown不造0；Node31/31（新增15）与两个纯PS输出夹具、源码22/工具v2 26独立检查通过，最终九文件门禁/本地集成见收据。 | 具体慢query/phase仍unknown，不升级旧真实失败；可成立的异库前置与完整恢复/正常退出、三宿主/安装/AOT/硬件/长期另验。 |
| M47 WB66 helper预算合同 | 🟡 / 本地报告、0actual | 实际协议/关键source range/SHA核验，独立28/28 PASS；20.19s墙钟拒绝，CIM15/cache16非数量耗尽，success统计丢弃已证。六docs完整门禁/本地集成见收据。 | 下一片仅保留existing三安全统计，逐查询原因unknown；旧actual/退出/cleanup与WB65strict失败不升级，真实恢复及三宿主另验。 |
| M47 WB65 Studio选库前置 | 🟡 / 本地合同、0actual | 普通DOM同值/unknown拒绝与异库身份一致前置；原fresh七谓词/成功链保持。micro1/1、最终43/43（32+11新），旧fixture精确适配新读取；继承WB64终审v2新收据PASS，原FAIL不升级。 | helper采集/缓存/回收预算独立后续；真实A→B/两desktop/B查询/正常关闭未验，超时不证明底层evaluate取消，三宿主/安装/AOT另验。 |
| M47 WB64 Studio真实选库观察 | 🟡 / 真实FAIL、失败检查点 | 独立runtime SHA与唯一actual：A PUT200 seq6/request43对pre-click barrier6/57，两fresh谓词false、其余五项true；DOM匹配A，30call/7658ms。六终态与执行源码保持，根exact owner资源回收另证；0新build/0定向测试，最终六路径门禁与提交见收据。 | 原normalExit/cleanupProven false，helper预算CIM15/20.19s/cache16失败；B/关闭/第二desktop/恢复/查询未运行。下一片先冻结同值选择fresh前置合同，helper回收另片；WB62历史原因与三宿主/安装/AOT不升级。 |
| M47 WB63 Studio选库失败观察 | 🟡 / 本地32合同，0actual | 同步pre-click双barrier、至多2次选择记录；至多128既有候选的原七find谓词及有界普通DOM计数/布尔，原error对象保持。实际轮询次数/耗时单列；原30次/250ms/20秒上限与ack/DOM/disk/close不变。最终七路径门禁/本地提交见收据。 | WB62实际A PUT200的拒绝原因仍unknown；不得把20秒上限当耗时。后续一次有界真实运行须绑定新源/runtime/失败检查点；A→B、两desktop、B查询及正常退出仍待验，不盲重跑旧证据。 |
| M47 WB62 Studio恢复接续 | 🟡 / 本地23合同，真实FAIL | seed有界脱敏观察/原HTTP失败保持、固定证据目录与源码独立PASS；9产品SHA复用，0新build。唯一actual已越过STRING/普通PK双库seed，但A选库fresh PUT验收失败；实际A PUT200存在，失败barrier缺证。9fallback/四ports/481对象回收另证，最终8路径门禁/本地提交见收据。 | WB63已补选择双barrier与候选/DOM观察合同，0新actual，旧原因未证；selections0/launch1、第二desktop/恢复/B查询NOT_RUN，normalExit=false。三宿主/文件/安装/AOT/长期/发布另验。 |
| M47 WB-61 Studio 数据库恢复验收 | 🟡 / 本地合同，真实旅程FAIL | 独立两desktop场景、双ack barrier与候选身份准入，最终14/14本地合同和Web/Server/Studio构建通过。唯一actual native bootstrap有效，夹具建表HTTP400，9归属fallback/四port/481对象回收另证。最终完整门禁/本地提交以M47收据为准。 | 原TEXT不合原生关系SQL，已修正STRING/普通主键但0新actual；选库、库持久化、第二desktop和B查询均NOT_RUN。须接续真实恢复旅程，登录/对话框/安装及三宿主整体仍未闭环。 |
| M47 WB-59 VS Code SQL耗时字段兼容 | 🟡 / 本地兼容，真实另验 | 唯一parser补native/legacy→optional elapsedMs；finite非负原值保持，缺失/非法/冲突省略alias，其他metadata保持。唯一TS、新Node5/5及最终九文件完整门禁/本地提交见M47收据，0actual/.NETbuild。 | parsed Raw可能增加alias；WB57原FAIL不升级。真实history/ack、stop-verification及三宿主/安装/AOT继续另验。 |
| M47 WB-58 公开history/错误通知安全观察 | 🟡 / 本地合同，真实另验 | 原完整await/串行Store及catch fulfilled保持；3phase固定通知/公开history snapshot，原总20命令及final3断言不放宽。必要synthetic ack/deferred与保密测试、唯一TS及九文件完整门禁/本地提交见M47收据，0actual/.NETbuild。 | 不推WB57 history2原因，不授持久ack；elapsed DTO、真实history/stop-verification及三宿主/安装/AOT继续另验。 |
| M47 WB-57 真实终态三检查 | 🟡 / 三检查到达实证，原旅程/cleanupFAIL | 唯一真实finalChecks refused/passed/refused，原首失败与authority保持；三payload2/1/45对真实reference同。49ledger/64refs/27helper/16原文件冻结，根62PID absent/0新stop/282对象回收及八文件完整门禁/本地提交另列。 | history20/2失败、Host0byte/API恢复unknown、Code/Hostnull/normalfalse、原FAIL/cleanupfalse保持。下一片先冻结history/ack及stop-verification合同，三宿主/安装/AOT另验。 |
| M47 WB-56 终态三安全检查 | 🟡 / 本地75通过，真实cleanup另验 | 三原同步检查独立执行、首失败保持、三passed且无terminal才proof；fixed finalChecks单读/异常隔离/幂等，syntax与75/75通过，0actual。最终八文件完整门禁/本地提交见M47收据。 | 原WB55 FAIL/false/null及remaining1/blocking7不升级；不新增stop/采样或authority，真实cleanup及三宿主/安装/AOT另验。 |
| M47 WB-55 真实Query/history/Code | 🟡 / 局部生命周期通过，原严格cleanupFAIL | 唯一actual三query2/1/45与真实reference逐值同，公开history3/HostPASS/API恢复/Code0-signalnull正常退出；3 existing-fresh安全观察、37ledger/43refs/17helper与16原文件完整，根44PID absent/0kill/280对象回收另列。最终八文件完整门禁/本地提交见M47收据。 | 原runner FAIL/unknown reason-type null/cleanupfalse保持；lookup变化不推权限/短命/复用或stop。下一片先冻结blocking/refusal生命周期，DOM/分页/Notebook/LSP/安装/AOT/三宿主另验。 |
| M47 WB-54 既有fresh安全观察 | 🟡 / 本地67合同通过，真实Host另验 | 仅复用同batch lookup追加有界transition投影；原deadline双guard、failure/authority/ledger/events/stop不变，syntax/source绑定与67/67通过，0actual；原工具与fixture FAIL保留。最终八文件完整门禁/精确提交/post见接续收据。 | 关系不证明权限/短命/复用/stop；WB53 history/Host终态/Code正常退出与原cleanup仍未证，三宿主和发布矩阵另验。 |
| M47 WB-53 真实Query与snapshot再验证 | 🟡 / 三payload局部通过，原FAIL保留 | 单次current2/selection1/EXPLAIN45逐值对真实reference与POST200一致；actual initial缺parent command与fresh lookup缺项已区分。51ledger/68events/28helper和12项manifest342719B完整；根fresh54PID absent/0新增kill/新runtime292对象回收另列，最终八文件完整门禁/失败检查点提交见收据。 | history/Host终态缺席、API恢复unknown，Code/Host null、原cleanupfalse；缺失原因、正常退出及UI/分页/Notebook/LSP/三宿主另验，不放松原stop。 |
| M47 WB-52 父链snapshot诊断 | 🟡 / 本地安全合同通过，真实Host待验 | fixed来源/角色/count/presence，未知role/null及同PID单读；最终55/55、syntax/双PS7 AST通过，初53保留。只已有lookup，不改准入/stop/预算或runner；503后旧closed4/14，新窗口续八文件完整门禁/本地提交，0actual/TS/新build。 | WB50采集原因unknown，原FAIL保持；真实三phase/history3/Code退出/cleanup与三宿主另验。 |
| M47 WB-51 调用时SQL上下文 | 🟡 / 本地合同通过，真实Host待验 | execute首await前捕获SQL字符串，保selection/statement/EXPLAIN/错误/数据库顺序与history ack；最终调用21/21、必要history6/6，同一TS项目初版/v2编译通过，failure cleanup先drain再恢复stub。14wrapper/0actual/0新.NETbuild、最终八文件完整门禁/提交见收据。 | 已证异步漂移风险不等于WB50因果；真实三phase/history3/Code退出、parent snapshot/cleanup与三宿主另验。 |
| M47 WB-50 真实Query/history窗口 | 🟡 / 实跑失败检查点 | 两源metadata逆投影与syntax/TS通过，唯一actual current2/selection1对reference通过，Host FAIL explain/source、history缺席，runner process_audit/Code与hostOutcome null。新cleanup诊断6identity拒绝/终态残余1；原false、根45PID absent/0kill/280对象回收与五terminal/15原证据绑定分列，最终八文件门禁/提交见M47收据。 | 先诊断前一payload观察与parent snapshot缺失合同，不追加actual或升级旧FAIL；UI/Webview分页/Notebook/LSP/安装/AOT/硬件/长稳/发布及三宿主整体另验。 |
| M47 WB-49 Cleanup子检查诊断 | 🟡 / 本地合同通过，真实Host待验 | 为既有owned-processes补固定安全subcheck与有界数值，恢复性/终态失败分列；原身份/stop/roots/audit/预算门禁保持。最终46/46、Node syntax/双PS7 AST通过；首44/45夹具失败保留。0actual/0TS/0新.NETbuild，最终九文件门禁/验收/提交见M47收据。 | 旧具体cleanup失败与history20/2因果、Code正常退出未知；真实新窗口/三宿主与安装/AOT/硬件/长稳/发布另验。 |
| M47 WB-48 Query history完成 | 🟡 / 本地合同验证，真实Host待验 | immutable FIFO/写ack与读屏障、50 pending/持久50条cap、当前失败可观察/队列恢复；真实production command/panel的确定性Memento夹具与唯一TS编译通过，0actual。旧冻结树完整门禁已过；foreign追加后旧窗口未提交，现以新窗口重新冻结/完整门禁与WB48集成，实际见M47记录与git log。 | 尚非旧20/2根因确证；下一片独立cleanup固定安全subcheck后新冻结真实history/Code退出窗口。安装/AOT/硬件/长稳/三宿主另验。 |
| M47 WB-47 新真实Host窗口 | 🟡 / history失败检查点 | metadata-only、TS/Node/双PS7 AST通过；唯一actual三查询2/1/45行与真实参考同、Host API restored；history20/2失败，Code1/null、runner hostOutcome null。原FAIL与process/runtime false保留；根283对象回收、current tuple/event审计分列，八文件完整门禁/本地提交见M47记录。 | actual1/1已关闭；下一片定位history与独立cleanup失败，不推断旧候选全部恢复或正常退出。向导/DOM分页/Notebook/LSP、OS文件窗口/安装/AOT/硬件/长稳与完整三宿主另验。 |
| M47 WB-46 有界候选重采 | 🟡 / 合成合同完成，真实Host待验 | 一次fresh批次仅补同PID/creation/parent缺命令，原/fresh完整父链至exact Node anchor不变；12hop/1秒/stop保持，async串行。最终37/37、Node/双PS7 AST与源码复核通过，0actual/0新build；最终九文件门禁/提交见M47记录。 | WB45原FAIL/false/null不改，不承诺旧五candidate全部恢复；新metadata/冻结窗口补history/API与正常Code退出。生产maxRows、向导/DOM分页/Notebook/LSP、WB41 OS与完整三宿主另验。 |
| M47 WB-45 身份先验安全诊断 | 🟡 / 诊断切片完成，完整实跑❌ | fixed安全subreason/字段/数值与单读getter，不减原identity/ledger/event/stop合同；最终26/26、既有20/20、TS/PS7与源码复核通过。新run五candidate缺commandLine首guard已证；current2/selection1/EXPLAIN45与真实reference相同，原FAIL/false及根279对象回收保持分列，最终十文件门禁/提交见M47记录。 | history/host-result缺、API恢复与正常Code退出未证；两次调用已用尽，不第三跑。缺命令需先设计有界fresh重采/完整anchor合同；旧WB44 unknown不能猜因果。生产maxRows、向导/DOM分页/Notebook/LSP、WB41 OS、安装/AOT与整体三宿主另验。 |
| M47 WB-14 VS Code资源导航 | 🟡 | 九模型/index/backup资源与Web导航入口已提交 `e7cf2fe5`，Node20/20与本机Extension Host注册检查；浏览器回选/认证夹具由WB-16补证据。 | 真实Server权限、外部OS浏览器交接、VSIX与发布。 |
| M47 WB-15 Studio客户端 | 🟡 | 宿主身份/lifecycle、保守操作门禁与迟返隔离已提交 `e1f93a69`；Node10/10、全Web137/137、Studio定向40/40、TypeScript/Vite与浏览器夹具8/8。 | 真实Server、WebView2/干净Windows、安装与三宿主全旅程；旧Header与完整Explorer异步组合另验。 |
| M47 WB-16 导航/认证与部署base | 🟡 | 已提交 `0bb628ad`；登录返回、base/SSE和index分组修复，Node9/9、全Web146/146、两部署浏览器各17/17、根/代理构建、本机真实Host13节点命令调用及独立复核/完整门禁通过。 | 真实Server权限/代理部署、远程SSE、同origin存储隔离、VSIX与三宿主全旅程。 |
| M47 WB-17/WB-27 Relation Workbench | 🟡 | WB-17迁移 `f1978263`；WB-27新Web→本机Kestrel分页50/200/尾51与当前导出、两insert四终态、COMMIT冲突及撤权403/regrant锁存3/3。返回编辑保留暂存，影响数取COMMIT避免重复；Node20/20、全Web300/300、Chrome fixture12/12、共享runner Document真实3/3、build与独立复核通过。 | 显式安全恢复、完整权限/SQL名称矩阵、物化/字节/总堆预算、完整九模型/三宿主、登录UI/readonly props、安装/AOT/发布。 |
| M47 WB-19/WB-28 FullText Workbench | 🟡 | WB-19迁移已有；WB-28新Web→本机Kestrel Top-K20/100/当前导出、数据库Admin同步重建151权威终态、撤权403/regrant READ锁存真实3/3，成功证据独立落盘及SHA256核验。Node15/15、全Web300/300、Chrome fixture8/8、TypeScript/Vite与独立复核通过；生产组件/Server不改。 | 显式读取恢复、完整权限矩阵/typed全文分页/facet/highlight、服务端扫描/物化/字节/堆预算、登录UI/readonly props、完整三宿主和发行物。 |
| M47 WB-20/WB-32/WB-33 Vector Workbench | 🟡 | 原名/六态、请求快照/迟返隔离、Top-K100与Profile缺失门禁已有切片。WB-32补真实READ L2 Top-K20/100、当前JSON/CSV/history与Search403；WB-33修当前child拒绝typed上行、原目标/render generation/authority校验及父载荷清理，Schema/auth/重挂/空身份ABA保持锁，旧拒绝不锁新上下文。Node39/39、全Web327/327、Vector/Measurement fixture13/13与20/20、build及新UI真实首轮3/3通过；三JSON/manifest核验，最终独立复核/完整门禁及提交见证据。 | 索引Profile、显式恢复、Server预算、子页完整写终态、Recall/模型质量、三宿主与发行物。 |
| M47 WB-21/WB-29 KV Workbench | 🟡 | WB-21原名/六态/锁存/readonly与1000项/4096字节预览已有；WB-29新Web→本机Kestrel首轮3/3，真实Scan100/opaque cursor尾51、Get/当前JSONL，普通WRITE三审批NX成功/未应用影响0/交换版本history与管理员Get对拍，撤权403/regrant READ锁存。Node17/17、全Web300/300、Chrome fixture12/12与build通过，三成功JSON/manifest独立落盘，生产组件/Server不改。 | 显式恢复、完整权限/atomic/Int64/TTL/CAS、瞬时原子结果表展示、Base64解码/传输/字节/堆预算、三宿主和发行物。 |
| M47 WB-22/WB-34 MQ Workbench | 🟡 | WB-22原名/六态/权限/一次审批/有界预览与Seek/auto已有；WB-34新增普通READ100/51当前JSONL/history、正常WRITE Publish201 offset151/Ack200 nextOffset1与管理员对拍、旧审批REVOKE403清载荷/READ重授及同tokenSchema200锁存真实首轮3/3。Node22/22、全Web327/327、MQ fixture16/16/build和源码复核通过，三JSON195806字节/manifest核验；成功独立复核、最终完整门禁与提交见证据。 | 冒号Topic真实400不支持、真实文件导入/显式恢复、Int64/Nack全矩阵、metadata/解码/传输/堆/Server预算、实例恢复、三宿主和发行物。 |
| M47 WB-23 Graph Workbench | 🟡 / Graph Beta | 权限锁存、只读浏览/元素读取/导出、请求代际隔离、客户端 10～1000 总元素画布预算、32项/4096字符 Inspector、safe-number ID 门禁及维护终态校验完成本地切片；Node27/27、Graph Chrome16/16、既有浏览器3/3、真实Kestrel兼容4/4及独立复核通过。 | 完整 Graph Int64 字符串身份、真实新 UI 权限/恢复、服务端长期预算、三宿主、AOT、固定硬件和发行物。 |
| M47 WB-24/WB-25/WB-37 Object Workbench | 🟡 | WB-24读取锁存/代际/readonly与1000列表/4096 Range，WB-25写终态已有；WB-37接续首run新Web真实3/3：151原DTO/100+51 continuation/当前51截断结果、206 Range4096/Download8192与history，普通text一次审批PUT200/69字节完整DTO及管理员GET/list/history1，旧审批REVOKE403/admin404/先前值保持和READ重授同tokenSchema200六tabs锁存。三JSON335381字节/manifest及独立源码复核通过，完整成功复核/最终门禁/本地提交见证据。 | 初始三失败与未知reset保留；Blob body unavailable/null不计raw上传字节观测。完整语义/Multipart/Server预算/显式恢复/OS对话框/三宿主/安装/发行物另验。 |
| M47 U01～U05 | 🚧 | 设计、首批共享合同、结果/审批语义及 Web Admin 页面切片已有局部实现。 | 完整九模型适配器、分页/取消/离线组合、真实权限与全量生产旅程。 |
| M47 U06～U08、U10 | 📋 | 已记录规划边界和退出条件。 | Studio、VS Code、WorkBuddy/stdio bridge、manifest/签名/插件安全尚未启动完整验收。 |
| M45-C01 首批实现 | 🟡 | TAG/time 分组首批代码、SQL/EXPLAIN 合同和定向回归已完成。 | C02～C09、更新/删除修正、增量物化、恢复预算及真实性能证据。 |
| M44、M46 及其余 4.5 必选包 | 📋 | 已有设计专题、现存底座和验收边界记录。 | 计划中的 AI 应用、编码/成本优化及对应真实质量、容量、恢复和发布验收尚未启动或未闭环。 |

历史上已经完成且不再作为当前待办的范围，请看 [CHANGELOG 完成归档](CHANGELOG.md#roadmap-completed-archive-2026-10-03-45)；路线图下方的“里程碑总览”保留每个里程碑的剩余交付。

##### 原M47设计、实施状态与三面边界（历史快照）

###### Milestone 47 — 统一数据库管理工作台与三面发布（UI）

M47 将 Web Admin、Studio 桌面和 VS Code 扩展规划为“一套核心、三个宿主、三个发布物”。共用资源上下文、九模型能力矩阵、设计令牌、Explorer、Workspace、结果平面、Inspector、History、Approval、MCP/AI onboarding 和验证夹具；宿主分别承载完整治理、原生桌面和开发者 Remote-first 子集。M29 的已有工作台与原型是实现基线，M47 不重新包装已完成的页面为新功能。

对照学习范围包括 dbx、Tabularis、DBeaver、DataGrip、pgAdmin、MongoDB Compass、RedisInsight、Kafka UI/RabbitMQ Management、Milvus Attu/Qdrant Console、Kibana/OpenSearch、MinIO Console 和 Neo4j Browser。逐模型、三面边界、发布形态、MCP/插件安全和原型目录见 [M47 统一管理工作台专题](docs/design/m47-unified-management-workbench.md)。

**M47 设计基线已确认，生产迁移按切片执行。** 外壳、七个一级/二级导航、九模型数据库逻辑资源树、共享对话框/状态和 MQ `database + Topic` 身份已确认；当前 [设计评审包](docs/design/m47-unified-management-workbench/README.md) 仍记录逐页像素与三宿主证据边界。规划覆盖七一级（概览/工作台/观测/数据流/AI 与 MCP/治理/设置）、30 个全局页面、九模型和 166 个任务页签（模型任务 60 个），不等同逐页生产完成。MQ 逻辑作用域在数据库资源树内，物理持久化为实例 `.system/mq`；单库备份不覆盖共享 MQ。

| ID | 优先级 / 范围 | 交付与退出条件 |
|---|---|---|
| M47-U01 | P0 | 竞品矩阵、完整菜单/导航、五区外壳、共享对话框/提示、九模型能力矩阵、三面边界和资源合同设计；先取得用户原型确认，再逐页细化与生产实现，不重复声明 M29 已实现内容。 |
| M47-U02 | P0 | `management-core` 规划与首批实现：typed API/MCP client、资源类型、能力/状态/权限/结果/审批合同、设计令牌；三个宿主各消费一个真实共享合同。 |
| M47-U03 | P0 | 统一 Explorer、Workspace、Result Plane、Inspector、History；九模型上下文一致，分页、取消、截断、离线和局部失败语义一致。 |
| M47-U04 | P0 | 九模型 adapter 和模型专用工作台：measurement、relational、document、KV、MQ、vector、full-text、object、Graph Beta；正常/空/错/只读/长内容状态可验证。 |
| M47-U05 | P0 | Web Admin 完整治理工作台：查询、编辑、导入导出、索引/策略维护、监控、审批、审计和九模型真实旅程闭环。 |
| M47-U06 | P0 | Studio 桌面宿主：同一 Web Admin 资源、native bridge、Managed Local、原生文件对话框以及干净 Windows/WebView2/升级卸载/端口/进程回收证据。 |
| M47-U07 | P1 | VS Code 开发者面：连接向导、可复用 Query/Notebook、分页结果、LSP/EXPLAIN、稳定宿主 Chat/MCP 能力或保留自有 Copilot WebView，并可深链接转交治理面。 |
| M47-U08 | P1 | WorkBuddy/Claude/Cursor/Codex AI Connect：现有 HTTP MCP、`sonnetdb mcp` stdio bridge、配置生成/自检、只读工具、凭据隔离、结果预算和数据外发说明。 |
| M47-U09 | P1 | M47 原型目录、完整页面/任务规范与三面发布矩阵：共享外壳、查询结果、审批/AI、九模型、Studio、VS Code 原型及正常/空/加载/错/只读/离线/长内容状态；HTML 评审稿、逐页最终视觉与真实宿主证据分别记录。 |
| M47-U10 | P2 条件 | manifest、签名/哈希、插件/连接器目录、权限撤销、升级/回滚和市场提交流程；先完成核心工作台和安全评审再启动。 |

###### M47 实施状态索引（2026-10-06）

状态只反映当前本地证据：`🟢` 已完成本地切片，`🚧` 仍有实现或验收，`🟡` 指定切片完成但外部/宿主证据待补，`📋` 尚未启动。

| 范围 | 状态 | 当前证据与剩余边界 |
|---|---|---|
| WB-00～WB-03C | 🟢 | 设计基线、原型交互/页面合同、资源身份/能力合同与 Explorer 兼容证据已提交；静态/内存证据不等同真实 Server 或三宿主。 |
| WB-04～WB-04B | 🟢 | 导航与 Explorer→SQL 兼容预检已提交；迁移前证据不等同全量运行时验收。 |
| WB-05～WB-06 | 🟡 | 结果/草稿/历史/审批和生产外壳/七模块导航已完成本地切片；全量模型、真实宿主、安装、发布仍待补。 |
| WB-07～WB-10 | 🟢 | Explorer 资源身份、旧深链接回选、database 上下文及手动切库投影已提交并通过 Web 回归。 |
| WB-11 Measurement Workbench | 🟡 | 用户已确认页面基线；状态、动作、旧路由和 UI 证据已通过本地验证，提交为 `390ff526`。真实 Server、三宿主、安装、发布和全量九模型证据仍待补。 |
| WB-12/WB-18/WB-26 Web Admin Document Workbench | 🟡 | WB-12 `54c78755`、WB-18 `1d9465fb`为既有基线；WB-26取得本机新Web→真实Kestrel权限/Find100恢复、Aggregate1001→1000及Distinct501→500/1000unknown真实3/3，Node21/21、全Web295/295、fixture11/11与build通过。空IDs省略且显式IDs保留；登录UI/readonly props、Server预算、Advanced完整读写及三宿主另验，提交以最终完整门禁为前提，实际哈希见git log。 |
| WB-13 SonnetDB Studio 宿主合同 | 🟡 | 宿主身份/URL与Managed Local生命周期合同已提交 `74835847`，定向34/34、独立复核和完整提交门禁通过；Web消费由WB-15另验，干净Windows/WebView2、安装与发行物证据仍待补。 |
| WB-14 VS Code Workbench 合同 | 🟡 | 九模型资源/深链接入口已提交 `e7cf2fe5`，原名与旧key保留，Node20/20、本机Extension Host、独立复核与完整门禁通过；WB-16补浏览器回选/认证夹具，真实Server权限和VSIX仍另验。 |
| WB-15 Studio Web客户端合同消费 | 🟡 | 已提交 `e1f93a69`；宿主原名身份、完整生命周期、保守操作门禁、初始化/状态/保存迟返与目录ABA隔离；Node10/10、全Web137/137、Studio定向40/40、TypeScript/Vite、浏览器夹具8/8、独立复核和完整提交门禁通过；真实Server/桌面安装及三宿主整体仍另验。 |
| WB-16 VS Code→Web导航/认证 | 🟡 | 已提交 `0bb628ad`；九模型/index/backup最终回选、登录返回、显式base/SSE、同名MQ及index分组收口；Node9/9、全Web146/146、根/代理构建、浏览器各17/17、真实Host13节点命令调用、独立复核与完整门禁通过。真实Server/代理部署/远程SSE/同origin存储隔离/VSIX另验。 |
| WB-17 Relation Table Workbench | 🟡 | 已提交 `f1978263`；Node15/15、全Web161/161、TypeScript/Vite、Chrome12/12、既有设计器2/2、独立复核及完整代码门禁通过；readonly浏览/结果导出/纯DDL，HTTP403清载荷锁存，缺终态/断连/408/5xx为unknown。真实Server权限、预算、三宿主与发行物另验。 |
| WB-19 FullText Workbench | 🟡 | 已提交 `2f9a5477`；原名/六态/Top-K100、载荷清理与一次审批终态，Node15/15、全Web184/184、Chrome8/8和既有真实Kestrel兼容4/4。新客户端权限/写旅程、恢复、预算与三宿主另验。 |
| WB-20 Vector Workbench | 🟡 | 原名/六态、参数/身份快照、空Schema权限锁存、严格维度/Top-K100和子页门禁；Node17/17、全Web201/201、Chrome10/10、导入1/1和既有真实Kestrel兼容3/3。Profile、真实权限/恢复、服务端预算、子页写终态与三宿主另验；本轮提交哈希见git log。 |
| WB-21/WB-29 KV Workbench | 🟡 | 原名/权限锁存/readonly与有界预览已有；新Web→本机Kestrel首轮3/3补游标/Get/当前JSONL、NX成功/未应用影响0/交换实际版本+history、撤权403后READ/同token刷新锁存。Node17/17、全Web300/300、Chrome fixture12/12和build通过，成功JSON+manifest独立落盘；完整atomic/恢复/资源预算与三宿主另验，实际提交见git log。 |
| WB-22 MQ Workbench | 🟡 | 原名/六态、锁存/readonly、实际Topic同步代际、一次审批与unknown、Browse1000/Inspector4096、Seek25窗/60秒和自动采样12轮/60秒；Node22/22、全Web240/240、Chrome16/16+既有3/3、真实Kestrel兼容2/2。真实新UI权限/恢复、完整metadata/资源预算和三宿主另验；本轮提交哈希见git log。 |
| WB-23/WB-35/WB-36 Graph Workbench | 🟡 / Graph Beta | 原名/六态、401/403锁存、readonly与请求隔离、有界画布/Inspector及safe-number门禁已有；WB-35真实Canvas250/10/1000、typed vertex、独立snapshot JSON导出/history、单vertex Upsert/version+1与REVOKE锁存取得3/3，Node27/27、全Web327/327、fixture16/16/build为该片证据。WB-36旧DLL正常导出151实际truncatedfalse定向失败后，两行修复精确顶点预算边哨兵；GraphEndpointTests17/17含8例新Kestrel矩阵、Server Release/trim-AOT分析0警告错误、修复后真实Web3/3/四JSON1015208字节manifest通过。完整最终门禁与本地提交见git log；完整Int64/edge/import/维护/显式恢复、通用Server预算、NativeAOT发布及三宿主另验。 |
| M47-U01～U03 | 🟡 | 设计、首批合同和共享结果/审批语义已有局部实现；三个宿主真实消费、完整分页/离线/取消证据仍待补。 |
| M47-U04～U05 | 🚧 | 九模型专用工作台和 Web Admin 仍按页面切片迁移；WB-11 是其中一个页面样板，不代表整包完成。 |
| M47-U06～U08 | 📋 | Studio、VS Code、WorkBuddy/stdio bridge 与配置自检尚未形成完整真实宿主验收。 |
| M47-U09 | 🟡 | 原型目录、页面规范与发布矩阵已提交；逐页最终视觉、三面真实旅程和发布证据仍待补。 |
| M47-U10 | 📋 | 条件项，尚未启动。 |

逐个 WB 的文件归属、测试数量、提交哈希和剩余边界以 [Workbench 队列](docs/design/m47-unified-management-workbench/work-queue.md) 与 [HANDOFF](HANDOFF.md) 为准；本表用于路线图快速查看，不能替代两份交接记录。

M47 的代码边界是“共享合同和组件优先、宿主适配器隔离”：不把 VS Code 变成完整 Web Admin，不改变九模型存储语义、SQL 名称合同、MCP 只读边界或 M29 的写审批规则。三面分别产出 Web 静态资源、Studio 安装包和 VSIX，但使用同一版本、MCP contract version、能力清单和兼容矩阵。任何一个宿主未通过自己的安装、Electron/Extension Host 或真实 Server 旅程，不能把三面整体标为发布完成。

##### 原木垒现场已完成五子项

2026-10-08 木垒现场只读核验已完成 5 个明确子项：当前 ARM64 配置/挂载、六个索引定义、真实 DESC/OFFSET 及 48/48 实际读取放大比、REST 默认完整与恰好 N/超 N 截断边界、真实流水三图完整读取/内容 SHA-256。见[完成清单及原始证据](docs/audits/mulei-field-readonly-verification-20261008.md)。这些子项标记为 ✅；证据绑定现场提交 `6a8d82d2`，冻结语料/现网规范化指纹对账、跨架构、全部模型/客户端、恢复/容量与 168 小时仍待验，表中整体边界继续保留。

##### 原README中的Workbench切片检查点（历史快照）

WB-48本地history集成已提交 `fa7b9528`，6项合同与完整restore/format通过。WB-49补既有query cleanup的固定安全子检查诊断，最终46/46故障合同、Node syntax/双PS7 AST通过，不放松身份/stop/预算，0actual；旧history20/2真实因果、Code正常退出/原cleanup及三宿主整体仍待验，实际本片结果见M47验证记录。

WB-50接续c0475103，仅换隔离metadata；syntax/TS通过，唯一actual当前语句2行与选区1行对真实reference通过，但EXPLAIN来源断言失败、history未运行、Code正常退出未证。原cleanup false保留，新诊断与根当前回收分列；无新.NETbuild，不重做已过合同，详细失败检查点见M47验证记录，三宿主仍未闭环。

WB-51冻结调用时SQL上下文，处理异步token/database等待期间编辑器变化的已证合同风险；最终deferred调用21/21、必要history6/6及同一TS项目初版/v2编译通过，独立复核补测试failure cleanup结算合同。本地stub证据与真实Host因果分列，0actual，原EXPLAIN/history/正常退出与cleanup缺口仍待独立验证。

WB-59修复VS Code SQL耗时字段兼容：唯一parser接受有效native/legacy耗时，缺失、非法或冲突时省略alias；新5项合成client/Panel/history/Webview测试PASS，仅本地metadata证据、0actual，parsed Raw可能增加alias。原WB57真实FAIL保持，最终九文件门禁/提交与三宿主边界详见[M47验证记录](docs/design/m47-unified-management-workbench/validation-report.md)。

WB-58补公开Query错误通知和每phase历史安全观察，保持完整await、现有生产吞错提示语义、总20历史命令预算与最终3条历史断言；新合成ack行为验证仅为本地合同，0actual。WB57原真实旅程与cleanup仍FAIL，时长澄清及最终九文件门禁/提交详见[M47验证记录](docs/design/m47-unified-management-workbench/validation-report.md)，三宿主继续另验。

WB-57单次真实窗口已观察cleanup三终态检查为refused/passed/refused，证明remaining拒绝后仍执行root和audit检查。三Query载荷2/1/45对真实Server参考一致；history失败且仅2项、Host文件0bytes、Code/Host终态缺席，原runner与cleanup仍FAIL。根当前回收和最终八文件门禁/本地提交另列，详见[M47验证记录](docs/design/m47-unified-management-workbench/validation-report.md)；三宿主仍未闭环。

WB-56修复cleanup终态检查短路：remaining-processes拒绝后仍执行root-identities与audit-failures，保留首失败且只有三项通过才授予proof；新增固定finalChecks安全投影。最终本地75/75与两源syntax通过，0真实Host运行；WB55原FAIL/false/null保持，真实严格cleanup与三宿主仍另验。最终八文件门禁/本地提交见[M47验证记录](docs/design/m47-unified-management-workbench/validation-report.md)。

WB-55单次真实Query窗口已取得current2/selection1/EXPLAIN45与独立Server参考一致、公开history3、Host PASS/API恢复及Code0正常退出的局部证据。原runner仍FAIL且reason/type null、严格cleanup/runtimefalse保持，三次既有fresh观察不证明缺失原因或stop权限；根当前回收、完整门禁与八文件本地提交另列，详见M47验证记录。三宿主/DOM/分页/Notebook/LSP/安装/AOT尚未闭环。

WB-54在原准入完成后仅复用既有initial/fresh lookup追加安全观察，原deadline、失败和stop权限保持；55既有+12新增本地合同最终67/67、syntax及source绑定通过，原工具/fixture失败分别保留。0真实Host运行，WB53历史/正常退出/原回收仍待独立实证；最终完整门禁与八文件本地提交见M47验证记录，三宿主尚未闭环。

WB-53单次真实窗口的current2/selection1/EXPLAIN45已与独立Server参考逐值一致；history/Host终态缺席，原process audit失败、正常Code退出与原回收仍未证。父链诊断实际区分initial缺command与fresh lookup缺项，但原因unknown；根当前进程/runtime回收另列，失败检查点、完整门禁与本地提交见M47验证记录，三宿主仍未闭环。

WB-52补父链snapshot拒绝的安全来源、角色和presence诊断，未知role保null、同PID单读；最终55/55及syntax/双PS7 AST通过，原身份/stop门禁与runner/Host保持。provider503后已在新窗口接续最终门禁/本地提交，0actual；旧采集原因与真实生命周期仍unknown，局部证据见M47验证记录。

Studio WB-40 已修默认/窄窗 native 工具区隐藏，本机真实窗口 CSS946×556 与652×476（DPR1.5、Explorer已自动收起）均验证身份/状态/Health、Managed Local Health/Stop/Start、普通原生关闭退出0与四端口/进程回收；fixture12/12与最终Web/Server/Studio构建通过。原失败保留，首轮验证runtime删除被策略拒绝而保留，不能计完整清理通过。WB-41新增SQL原生文件诊断入口；首轮真实Open POST后桌面工具未看到picker且激活失败，超时未通过，后三步未运行，失败证据与任务资源回收已核验。WB-42新增真实VS Code查询诊断，当前语句和选区两条生成结果已与真实Server独立参考核对；EXPLAIN/历史及正常退出仍未通过，公开API驱动不等同向导UI或Webview分页。WB-43已对齐sql-only参考与安全诊断，真实current/selection/EXPLAIN三条生成数据均与独立Server参考一致（EXPLAIN45行）；完整实跑仍因进程审计失败，历史/API恢复/正常退出未验证，原失败与根回收证据分别保留。WB-44已在既有门禁下压缩重复外部祖先正文、绑定完整身份ledger与事件引用，定向17/17及既有20/20通过；新真实Host取得三条公开查询历史、API恢复和零清理错误，完整runner仍因三个身份先验未知拒绝而失败，正常Code退出未证，原失败/false与根回收分别保留。WB-45新增固定安全先验诊断，26/26故障测试与既有20/20通过；新实跑五候选缺commandLine，完整runner仍失败，三查询数据与真实参考同而history/API恢复/正常退出未证，原失败及根279对象runtime回收单列，旧unknown不推断。WB-46新增一次有界fresh候选重采，只补同PID/creation/parent缺命令且完整父/anchor不变的候选，保原准入/stop门禁与串行cleanup；37/37合成合同、Node/PS7和独立复核通过，0actual，原失败保持，正常Code退出仍须新窗口验证。WB-47只改实际窗口metadata并完成唯一新实跑：三条查询2/1/45行与真实参考相同，但Host history pickCount20/entryCount2失败，Code退出1，runner hostOutcome仍null；API恢复已在Host文件核验，原process/runtime cleanup false与根283对象当前回收分列，未取得完整旅程或正常退出。原成功门禁保持，actual1/1关闭，下一片先定位history与cleanup各自失败。WB-48补齐query history写ack完成合同：immutable FIFO/50 pending与50条cap、读屏障、失败恢复，查询progress等待持久写完成；确定性Memento/公开API夹具和TS编译通过，0actual；旧窗口最终提交因foreign更新使冻结失效而延后；现接续新窗口重新冻结/完整门禁和集成，实际结果见M47记录与git log，旧20/2真实根因与正常退出、独立cleanup诊断仍待补。展开Explorer、库身份同步/恢复、登录UI、文件对话框、安装/AOT及三宿主整体仍待验，详情见 [M47 验证记录](docs/design/m47-unified-management-workbench/validation-report.md)。

##### 原WB97/WB98未完成集成草稿（不计完成）

- **M47 WB98 helper observation**：集成VS Code三脚本的有界逐调用ID/phase、已接纳身份、计时/race/close观察及97项合同；复用冻结97/97与三syntax，终止权限和旧失败不变。最终完整restore/原CI format、本地commit及身份收口仅依WB98独立收据；真实Host/OS-stop/M47整体另验。
- **M47 WB97 helper observation本地集成**：集成VS Code诊断三脚本的有界逐调用ID/phase、已接纳身份、计时/race/close观察与97项合同；复用WB96当前97/97及三语法natural0，保留旧UNKNOWN/真实宿主失败与终止权限。WB97释放超时及WB97-C1实施失败原样保留，新WB97-C4完整门禁（C3 prelaunch CIM失败原样保留）及本地提交按独立收据（C2独审超时原样保留）；无新Host运行或产品构建。

##### 当前主路线

已归档条目的后续验收、发布步骤及延期清单以 [根ROADMAP](ROADMAP.md#workbench-preview-1)为准；本归档不会发布新版本或恢复旧自动化。


- **M47 WB70 Native真实窗口失败检查点（2026-10-08）**：runner唯一CR修LF及独立metadata/source/runtime准入；唯一actual在WebView2 loopback CDP helper预算失败，未证明A/B恢复，原actual/normalExit/cleanupfalse保留。明确prerequisite .53与actual .62差异，根18identity fresh门禁后仅回收324自有临时对象；0新build/Node重测，局部门禁/本地集成见收据，整体过程缺口不升级。
- WB69：冻结 Studio Native 数据库恢复的新 metadata/runtime 窗口，复用普通异库准备与 helper 统计；依赖/端口预检通过但独立复核超出 180 秒准入有效期，保留 0 actual 失败检查点，不声明真实恢复通过。
- **4.5 路线规划与完成范围整理（2026-10-03）**：根 `ROADMAP.md` 增加 M44 AI 应用与可治理推理、M45 聚合与持续计算深化、M46 存储编码与执行成本优化，并沿用既有九模型、资源、恢复与真实证据队列；同步总索引和三个研究专题。新增功能均为规划，不是本条目已实现功能；TsFile 不纳入路线。下表归档的是此前已发生的实现与对应证据，未提升远程、容量、真实模型或长期状态。

<a id="roadmap-completed-archive-2026-10-03-45"></a>

**4.5 路线整理时归档的已完成范围（2026-10-03）**

本轮从根路线图移出完成范围的详细列表，按当前切片汇总，避免重新派单。原始实现条目仍保留在本文件 `[Unreleased]` 及[2026-09-21 归档](#roadmap-completed-archive-2026-09-21)，不重复宣布发布。核查基线为 `4b004946`；静态事实、原始 TRX/摘要与源码哈希复核见[规划核查记录](docs/audits/sonnetdb-45-roadmap-planning-20261003.md)。

| 已完成范围 | 归档的实际交付与依据 | 继续保留的边界 |
|---|---|---|
| M27 #340 BrowserDirect / StudioNative / ServerRelay | OAuth/PKCE 获取入口、原生宿主本地合同、relay journal/单执行者/live follow/失败重放及双进程 smoke；沿用 [Browser 报告](docs/audits/m27-browser-oauth-closure-20260923.md)、[Studio 报告](docs/audits/m27-studio-native-closure-20260923.md)、[Relay 报告](docs/audits/relay-multi-instance-closure-20260923.md)。 | 真实 IdP、provider/模型质量、双网部署、WebView2/Studio 实机和生产接管未由本轮验证。 |
| GH-Issue #177/#180/#193/#196 的已交付子集 | 关系 JOIN、JSON 查询、关系 VECTOR/GEOPOINT 类型边界，以及参数化 measurement INNER JOIN/排序/分页和能力预检；沿用 [SQL 交付记录](docs/audits/sql-provider-closure-20260923.md)与 [measurement JOIN 记录](docs/audits/measurement-join-196-closure-20260923.md)。 | 历史推送/关闭仅按报告当日记录，本轮未重新查询 GitHub；不等同全部 SQL 或固定硬件完成。 |
| M42 覆盖索引与 REST/Web 预览 | index-only、快照/取消/回表边界、预览截断元数据与单表早停；依据[覆盖索引合同](docs/benchmarks/m42-covered-index-scans.md)和[SQL 预算合同](docs/benchmarks/m42-sql-result-bounds.md)。 | 默认 REST/Frame/嵌入式全结果、存储工作集、整体 CLR heap 和首行 gate 独立。 |
| M42 2026-09-30~10-03 物化准入切片 | 关系 SELECT/DML、measurement raw/排序/KNN、文档 raw/TTL/排序、Graph 及直接向量/JSON 文件 TVF 累计行/估算字节；依据[并行实现](docs/audits/roadmap-parallel-implementation-20261001.md)、[TVF 报告](docs/audits/roadmap-parallel-next-20261003.md)和[后续排序报告](docs/audits/roadmap-continuation-20261003.md)。 | 预算只覆盖声明路径，不能称全链路流式、全模型总内存上限或 M42 整项 PASS。 |
| M43 本地 CDC、窗口与订阅运维 | spool/源捕获/快照增量接收、多分区独立位点、持久滚动/滑动数值与字符串分组、会话 COUNT、暂停/恢复/重试、DLQ、任务目录；依据[第二批](docs/audits/roadmap-four-task-closure-20261001.md)、[第三批](docs/audits/roadmap-three-task-closure-20261001.md)、[第四批](docs/audits/roadmap-next-three-closure-20261001.md)、[滑动/DLQ](docs/audits/roadmap-parallel-20261002.md)和[会话/目录](docs/audits/roadmap-next-three-closure-20261002.md)。 | 文件/本地单执行者合同不升级远程 HA、全部聚合/SQL 增量语义、跨分区业务事务或掉电结论。 |
| M43 自动投递与 CDC→流接线 | 单分区 outbox/目标接收凭证、有界退避投递、多分区公平桥接、caller-owned 任务运行和真实 `--bridge`/`--topology` 组合重开；依据[桥接报告](docs/audits/roadmap-parallel-next-20261002.md)与[后续报告](docs/audits/roadmap-continuation-20261003.md)。 | 未知结果按核对/交接处理；至少一次与窗口去重不等于任意外部副作用 exactly-once。 |
| M43 #382~#384/#397/#398/#401 的本地文档/工具范围 | 十四能力/旅程索引、成熟度、发布汇总 verifier、资料草稿及复现索引；已有条目与[总索引](docs/roadmap-total-milestone.md)继续可追溯。 | 原始发布报告、外部提交/收录、公开案例和十四项完整验收尚未闭环。 |
| 既有基础、M35/M36/M40/M41 的已归档代码范围 | 继续沿用[原归档](#roadmap-completed-archive-2026-09-21)与对应专题；包括时序压缩/聚合、预测/异常/变点、持久摄取、九模型客户端、图和规划器本地合同。 | 本轮不重新证明所有历史声明；真实语义质量、远程、安装、容量、跨架构和长期 gate 留在 4.5 主路线。 |

复核最近两轮完整 Core 原始 TRX 分别为 6072/6072 与 6140/6140，哈希与当轮摘要一致；最近增量记录的 13 个源码哈希与规划基线一致。这是对已有本地证据的只读核查，不是本轮重新运行测试。Server 首轮 69/70 与后续按类 12/12 的历史失败/重跑记录保留，不能重写成从未失败。M42/M43 保持进行中，Graph 保持 Beta，未执行的固定硬件/真实模型/远程/长期项目不移为完成。

- **M42 measurement KNN 候选有界化（2026-10-01）**：扫描时直接进入共享 Top-K 堆，替代 worker 和总结果的全量候选列表；候选入堆前过滤墓碑，复用 SQL worker/工作集预算，传播根取消并按距离、时间戳、序列 ID 稳定排序。ANN 时间窗不足时丢弃该批候选并精确补扫，修复 `k * 8` 溢出。候选保留量为 O(K)，存储页解码、首行和整体 CLR heap 门禁仍未完成。见[合同](docs/benchmarks/m42-measurement-knn-bounds.md)。
- **GH-Issue #211 SQL 标识符规则**：表、measurement、视图/物化视图与列/TAG/FIELD 保留创建时的拼写，普通引用按 `OrdinalIgnoreCase` 匹配，双引号按 `Ordinal` 精确匹配；禁止新增或重命名为仅大小写不同的同作用域名称。旧 catalog 冲突须显式迁移，普通引用报歧义；设备摄取名称绑定到已保存的 schema 拼写。
- SQL 与 Bulk VALUES 的未知字符串列默认推断为 `FIELD STRING`；新增 `INSERT` 列列表中的 `TAG` / `FIELD` 显式角色提示，现有列仍以持久化 schema 为准。原本依赖字符串自动成为 TAG 的写入需显式声明 TAG 或先执行 `CREATE MEASUREMENT`。

### Fixed

- 修复跨表事务恢复日志发布：`transaction.sdbtxn` 临时文件 rename 后同步父目录元数据，避免掉电后恢复日志目录项丢失导致部分提交无法撤销；新增 prepare/read 回归测试。

- 修复 GitHub CodeQL `cs/insecure-randomness` 在 M41 差异测试上的误报：测试数据改为固定算术分布，保留索引/全扫描对拍覆盖，不再把 `System.Random` 数据流带入 SQL 密码解析上下文。

- 修复 M49 Protocol/Native 目录迁移后的构建接线：Docker restore 与源码层纳入 Protocol 和 MQTT 源生成项目，Protocol 改动触发镜像工作流；连接器发布工具和 Rust 默认目录改用 embedded/native，Windows 重新取得配套导入库，CMake 跟踪 Protocol 源码和项目依赖。

- 修复 Docker 构建上下文的忽略顺序：保留源码 `.gitignore` 的例外先于产物目录排除，避免旧验证产物中的嵌套 `.gitignore` 被重新打包；使用真实 Testcontainers 归档器和独占嵌套哨兵覆盖该边界，不清理历史证据。
- GH-Issue #261：空/NULL/非空普通及物化视图中的内置 CONCAT 提供 STRING、非空声明，同名 UDF 保留未知类型；补齐真实 XCode 反向工程、XML 名称往返及实体生成编译证据，正式发行和上游交付另计。

- GH-Issue #262：关系日期函数投影提供原生 DateTime 声明；SQL 结果 v4 在 REST 与 Frame 中显式协商并保留墙钟 ticks 和 Kind，修复泛型 DATE_ADD 将 DateTime 隐式提升为 DateTimeOffset 的问题。旧结果编码和参数/持久 DATETIME 的 UTC Unix 毫秒合同保留；真实三协议 Chloe 候选验证另附，正式引擎/驱动/Server 与 Chloe 发布验收仍待完成。
- GH-Issue #314：`round` 对 DECIMAL 直接使用十进制 ToEven 舍入，支持 0～28 位并明确拒绝非法位数；参数、关系 SELECT/WHERE/UPDATE 与嵌入式/REST/h2c ADO 保持精度及 Decimal 零行声明，不伪报输入 precision/scale。保留 INT/FLOAT 与 NULL 拒绝合同；首次可安装版本及 FreeSql 发布包消费者仍待验收。
- M45-C01/C02/C06 与 M46-S06：Int64 `mode` 和 `distinct_count` 保留完整整数身份；后者拒用旧 Double 来源段摘要并扫描原始值，防止超过 2^53 的相邻值混淆，不修改既有段格式。
- M45-C06/C08：HLL 状态校验寄存器上界，TDigest 拒绝未排序均值、非法权重与累计计数溢出；Histogram 拒绝无法表示的桶索引/边界且保留失败前的状态。
- 对象回填调用方取消测试先确认真实取消钩子已触达：只在无钩子、无进度、无持久任务时有界推进合法空页，随后严格核对调用方 token、唯一钩子、未提交进度及原恢复结果；不修改生产页预算或放宽异常断言。

- KNN 分配回归将线程分配计数限定在显式无 GC 测量窗口，规避.NET 10后台GC把未使用allocation context计入计数的已复现问题；保留K=8、100000次替换、4096/8192字节阈值与排序断言，并增加真实小对象分配对照。生产算法和GC默认配置不变。

- 对象回填故障注入测试区分页预算产生的合法零进度页与异常传播：只在钩子未触达、计数/任务均为零时最多推进8页/10秒，仍严格要求同一IOException和一个持久任务；生产250ms预算保持，修复PR #306 Windows CI暴露的测试前置条件缺口。

- 同步六个 Document/JSON 查询测试对整数结果的严格 Int64 期望（13 行替换），覆盖嵌套数组、分页、TTL、视图及整数表达式；保留数值、预算和浮点断言，修复联合候选首次完整 CI 暴露的 11 个旧 Double 期望失败。
- GH-Issue #263：远程 ADO 参数绑定补齐 SByte/UInt16/UInt32 的无损整数格式化，嵌入式与远程枚举按原始底层类型复用既有整数分支；保留 Value/DbType、NULL、精度及原 UInt64 边界。新增三协议21例覆盖极值、枚举、谓词、批量、事务回滚及重开；正式包未包含前保持等待发布。

- PR #302：后台 Flush 集成 fixture 显式关闭自动 Compaction，保留每批 15 秒、最终至少 8 个物理段和 5000 点查询合同；每批等待退出后立即断言段数增长，写入/等待循环增加次数、总墙钟与取消边界，正常重新打开用例保留。生产默认配置不改，原 Windows CI FAIL 与新 head 门禁独立。
- 物化视图读取保留已持久化的完整输出名称，修复带点 quoted alias 在空结果/CTE 的声明及重开后值绑定；补齐 Streaming ACK 两层精确超时文案测试合同，原预算与恢复断言保持（Issue #261、#300、#301；13 PR 组合验证）。
- **GH-Issue #228 ADO.NET HasRows**：按真实结果探测是否存在行，保留空 SELECT/RETURNING 的声明列元数据；首行探测复用命令超时、取消与远程错误传播，并由后续同步/异步 Read 返回该行。正式 NuGet/Server 发布与 FreeSql 消费者回归仍待验收。

- 后台语义回填区分恢复工作预算与真实宿主取消：工作预算到期停止新增对象，以原五秒提交窗口保存已持久入队前缀的游标和计数；HTTP仍使用请求取消，生产默认250ms、调用者取消及存储错误合同不变。新增真实KV整库重开、宿主取消和Kestrel自然预算回归；跨崩溃的任务/计数原子性与真实模型质量独立验收。
- PR #298：后台已领取语义回填页的控制测试仅为独立宿主配置匹配的恢复/页预算，在释放三秒有界观察前确认页与调用者令牌未取消；保留真实进行中响应和终态 scanned=1/queued=1/skipped=0，生产默认预算与原页预算取消用例不变。默认恢复取消后的持久计数缺口已受控复现，另行跟踪。
- 批量集成已审 PR #281 与 #217；语义图片回填测试对明确进行中 response 等待同一持久终态，保留首次排队1及重复回填scanned1/queued0/skipped1、thumbnail/search/delete/disable原断言；真实后台持页 partial0 回归与完整18个Semantic测试通过，生产代码及真实模型质量声明不变。
- **前端依赖与 MapLibre 6 迁移（2026-10-12）**：保留 PR #217 覆盖 #218 的 Web/VS Code 锁定依赖更新；两个地图组件改用 ESM namespace/type 导入和局部创建实例，并通过 Vite `worker&url` 打包和显式配置独立地图 worker，保留现有瓦片、投影、结果地图和轨迹逻辑。


- 持久死信 CI 测试：检查点锁故障恢复使用一致的 2 秒存储预算和贯穿初始化/故障/重开的 15 秒调用者取消边界；精确区分订阅包装层、检查点操作层和文件锁三个已存在的超时消息及 inner exception，保留意图恢复与恰好一次断言，不修改生产超时。

- GH-Issue #261 声明元数据回归：已知 Int64 在两种 ADO schema API 中保留 precision=19/scale=0，未知 Decimal 和未知类型仍不编造精度；LIMIT 0 的 JOIN 声明探测不再伪增实际输入 LIMIT 下推计数，保留主 FROM 的下推与零候选/零保留行合同。

- **GH-Issue #273 EF Core 迁移脚本合同**：生成 `Idempotent` 或 `Idempotent | NoTransactions` 脚本时明确抛出 `NotSupportedException`，避免缺少迁移历史保护的脚本被误当作可重放脚本；普通脚本与按历史表顺序执行的 `MigrateAsync` 保持原合同。

- GH-Issue #235 的 NumericText 保留后，修正三个既有 parser AST 相等性预期，分别覆盖 0.5 默认值、1.5 INSERT 和负数字面量的正数 operand；保留精确原数字文本与生产相等性合同。原 Ubuntu 全量失败与修正后定向验证分列。
- GH-Issue #241：ADD/ALTER DECIMAL/NUMERIC 的 precision/scale 穿透解析、schema 演进及持久化，未修改列保留原声明，远程表元数据保留 scale=0；DDL 在发布 schema 前无损预检全部旧行与默认值，拒绝小数损失/溢出及原始字符串转换舍入。表 schema v10、public 构造/解构和普通写入合同保持；持续值约束归 #264，正式包及 XCode 迁移验收另列。
- GH-Issue #235：嵌入式和远程 ADO 的 Decimal 参数保留实际 System.Decimal 精度；DECIMAL 写入、CAST、比较及算术按原数值文本解析浮点字面量，明确拒绝舍入、下溢和溢出。既有 Double/Float/Int64 及非关系模型的浮点参数语义保持；纯数值转换不重复执行动态 UDF。声明 p/s 约束、正式修复包和上游 ORM 验收独立推进。
- 修复 #271 BLOB 参数查询：嵌入式保留二进制字面量快照，远程使用严格十六进制 `X'...'` 字面量，普通及关联等值按字节内容比较，并保留主键/二级索引与 EXISTS 访问路径；公开修复包及实际 ABP Passkey/A06 验收仍独立进行。


- 持久订阅确认、死信 pending isolation 与重新签名意图测试精确区分2000ms wrapper取消和checkpoint锁重试耗尽的合法超时消息/inner合同，保留调用者未取消和原恢复断言；Linux原两项失败保留，生产计时器和预算不变。
- Linux CI：仅持久死信重新签名意图不匹配理论用例统一使用两秒初开/重开预算，以45秒调用者截止覆盖初始化、锁故障、损坏状态读写与重开；保留真实存储超时原消息、损坏拒绝和原spool不确认断言，原200ms初开失败保持。
- Windows CI：持久订阅确认的锁故障用例统一使用2秒初开/重开预算，分别核生产超时原消息和实际调用者取消，并验证相同 delivery/checkpoint/pending 恢复；原200ms Open失败保留。
- Windows CI：持久死信 pending isolation 恢复用例统一使用2秒存储预算，保留真实锁超时和重开恢复，并独立核15秒调用者未取消；保留原200ms Open/recovery失败，生产配置不变。

- 修复 #271 BLOB 参数查询：嵌入式保留二进制字面量快照，远程使用严格十六进制 `X'...'` 字面量，普通及关联等值按字节内容比较，并保留主键/二级索引与 EXISTS 访问路径；公开修复包及实际 ABP Passkey/A06 验收仍独立进行。
- Windows Outbox 批次取消测试使用 2 秒初始化/批次预算，并独立核处理器真实取消、15 秒调用者未取消及租约可恢复；保留原 100ms 构造阶段取消失败，生产预算不变。
- Windows CI：持久订阅确认故障用例为初始存储提供合理有界预算，独立区分真实存储超时、dispatcher总预算和调用者取消，保留持久批次恢复断言。
- Windows CI：物理读取过载 HTTP 映射用例为冷启动共享主机提供 15 秒有界请求预算，保留真实 503、Retry-After 和错误码断言；不改变服务端过载或生产超时配置。
- **Windows CI 时间与并发断言（2026-10-12）**：缓存端到端用例核对后台/手动清理竞争后的物理过期项，独立精确删除计数用例关闭后台维护；进程取消用例用专用线程和有界 readiness 观测保留原执行/整树回收断言，空流窗口用例保留真正的操作超时与超时后恢复验证。仅调整测试，不修改生产逻辑。

- **GH-Issue #270 EF Core 字符串字面查询**：对 `Contains`、`StartsWith`、`EndsWith` 的运行时 LIKE 参数按反斜杠、`%`、`_` 顺序转义并保留参数化；显式保护 NULL 参数，避免 CONCAT 将其转为空串后误匹配。修正引擎 `%` 通配符遇到文本 `%` 时的匹配顺序和回溯，普通 SQL LIKE 通配符合同保持，消费方无需预转义。

- EF Core 建表保留并安全引用命名外键，按模型原名执行删除、替换与回退迁移（GH-Issue #272；源码修复，正式包与消费者验收待完成）。
- M47-P07 分发/P07调用层改读真实Job收据的status/exitCode/cleanupComplete，拒绝失败和未清理；修正新PowerShell进程未设置或残留LASTEXITCODE导致实际prepare完成却误报失败，6项真实进程包装合同通过（仅合成controller）。
- **M47-P03 KV及时序预览渲染收尾（2026-10-10）**：KV及时序点表/监控结果表使用虚拟滚动，避免500～1000行同时挂载导致原30秒旅程或5秒点击超时；保留全部已加载数据及KV1000/measurement500条上限、cursor截断、原值导出与权限合同，验证末尾行选择和筛选后的选中导出。P03按[R1本地验收](docs/design/m47-unified-management-workbench/preview-1-p03-controls.md)完成；七模型专用读取仍延期，发行/实物/远端门禁与NOT_READY保持。
- **M47-P02 Web smoke 修复（2026-10-10）**：修复 logout 的 Vite/Node 环境错用和 Studio 夹具身份/生命周期前置；fixture smoke 与独立真实 Server suite 分开收集，Node 合同成为 CI 必经步骤。相同 236 个输入上 Web build、448 条 Node 合同、307 条 fixture 与 12 条 StudioHost 回归通过；默认 25 条宿主专用 skip、30 条独立 real suite 和远端门禁分列。保留两次历史 CI 失败、原执行器越界清理及本地中断记录，产品仍 NOT_READY；见 [P02 报告](docs/design/m47-unified-management-workbench/preview-1-p02-smoke.md)。

- **M47 WB79 磁盘派生身份兼容（2026-10-09）**：通过真实C# SaveAsync与Node共用夹具验证实际JSON形态，磁盘校验接受完整且一致的已知派生身份对，保留旧双缺省形态、未知字段拒绝和精简投影；不让磁盘身份代替native ack。C#20/20、Node80/80通过，原包装器失败和首次Node中止分别保留，真实Native恢复另验。

- **M47 WB-59 VS Code SQL耗时字段兼容（2026-10-07）**：唯一parser边界把有效native elapsedMilliseconds或legacy elapsedMs映射为optional规范alias，缺失/非法/冲突省略耗时；保留原metadata，parsed Raw可能增加alias。唯一TS及新5项合成client/Panel/history/Webview测试PASS，仅本地兼容、0actual/新.NETbuild，旧真实FAIL保持；最终九文件完整门禁、本地提交及退出/独立收据分列。
- **M47 WB-58 公开history/错误通知观察（2026-10-07）**：在现有完整await/串行Store合同上补三phase固定公开通知与history安全快照，20命令总budget及原最终3条断言不放宽；必要synthetic ack拒绝/deferred与保密边界仅本地验证，0actual/新.NETbuild，生产语义/runner保持。完整九文件门禁、本地提交与退出/独立收据另列；纠正WB57阶段55秒与完整outer73.6699466秒措辞，原失败不升级。
- **M47 WB-57 真实终态三检查（2026-10-07）**：单次真实finalChecks为refused/passed/refused，remaining拒绝后root/audit均执行，原首失败与cleanup false保持；三Query2/1/45对真实参考同，history20picks/2entries失败/Host0bytes/Code及Hostnull不升级。49ledger/64refs/27helper、16原文件/343013B冻结；根62PID absent/0新stop/282runtime对象回收、完整八文件门禁和本地失败检查点提交另列，三宿主/安装/AOT/发布另验。
- **M47 WB-56 终态安全门禁（2026-10-07）**：三个原同步cleanup终态检查独立执行，拒绝不跳过后续检查，首terminalFailure保留，仅三passed且无terminal才proof；固定finalChecks单读安全投影，本地75/75（67+8）和两源syntax通过、0actual。WB55原FAIL/false/null与旧证据保持，完整八文件门禁/精确本地提交见M47收据；真实cleanup、三宿主/安装/AOT/发布另验。
- **M47 WB-55 真实Query与transition再验证（2026-10-07）**：metadata-only新独立窗口三query2/1/45逐值对真实reference、公开history3/HostPASS/API恢复/Code0正常退出局部通过；原runner FAIL/reason-type null/严格cleanupfalse保持。3 existing_fresh parent观察不授stop或推因果，37ledger/43refs/17helper及16原证据冻结，根44PID absent/0kill/280新runtime对象回收单列；原准备/actual/checkpoint拒绝保留，最终八文件完整门禁与精确本地提交见M47收据，三宿主/发布另验。
- **M47 WB-54 既有fresh父观察（2026-10-07）**：原准入后仅复用source/fresh追加有界安全transition投影，沿原deadline双guard，原失败/authority/ledger/events/stop不改；55既有+12新增最终67/67、syntax与source绑定通过。原0启动工具失败及66/67非法ISO fixture失败保留，只修finite创建夹具；0actual，WB53真实Host/history/退出/原cleanup与三宿主仍另验，最终完整门禁与精确八文件本地提交见接续收据。
- **M47 WB-53 真实Query与父链诊断再验证（2026-10-07）**：仅迁移runner/Host隔离metadata，单次真实current2/selection1/EXPLAIN45与独立参考逐值一致；但history/Host终态缺席、原process audit FAIL、Code/Host null与cleanupfalse保持。initial父缺command和fresh lookup缺项只作安全观察，原因unknown；51ledger/68events/28helper及14原证据冻结，根当前54PID absent/0新增kill/新runtime292对象一次回收另列。syntax/唯一TS与最终完整门禁、失败检查点本地提交见收据，不计完整Host或三宿主成功。
- **M47 WB-52 父链snapshot诊断（2026-10-07）**：拒绝候选补安全initial/fresh、candidate/parent/anchor、有限计数与command presence观察，unknown为null；仅已有lookup、无raw身份/命令/error/hash或新采集，原准入/stop/预算保持。独立复核补未知role坐标与同PID单读，最终55/55（46受影响+9新）、syntax/双PS7 AST通过，初53保留。provider503后旧窗closed4/14，新独立窗口续最终八文件完整门禁/本地提交；runner/Host冻结、0actual/0TS/0新build，不推断WB50原因或三宿主完成。
- **M47 WB-51 调用时SQL上下文（2026-10-07）**：query/selection/EXPLAIN的SQL读取移到首异步token/database解析之前，保持原SQL与history完成语义；最终deferred编辑器变动合同21/21、必要history6/6，同一TS项目初版/v2编译均通过，失败cleanup先drain再恢复stub。0actual，不将已证调用漂移风险称WB50真实根因，完整门禁及八文件检查点见M47记录；原失败与三宿主边界保持。
- **M47 WB-50 真实Query再验证（2026-10-07）**：两源仅隔离metadata、五组literal逆投影等于c0475103；syntax/双PS7 AST/唯一TS通过，0新.NETbuild、不重测旧合同。唯一actual current2/selection1对真实reference通过，但Host FAIL explain/source、history缺席，runner FAIL process_audit/Code与hostOutcome null；新cleanup诊断定位3父命令缺失+3快照缺席及终态残余1。原false保持，根45PID当前无存活/0追加kill/280新runtime对象回收与五terminal/15证据SHA分列，完整门禁及八文件本地检查点提交见M47收据；整体三宿主仍未闭环。
- **M47 WB-49 Cleanup子检查诊断（2026-10-07）**：为既有query runner owned-processes增加固定白名单子检查和有界计数，恢复性与终态失败分列，raw错误/命令/未准入身份不保存；保留原完整身份/stop/根/审计/预算与独立回收/终态门禁。最终46/46故障合同、Node syntax与双PS7 AST通过；首44/45夹具链长问题修复而原失败保留，最终九文件完整restore/原format及本地提交以M47收据为准；0actual，旧history20/2与正常退出/cleanup不转为成功。
- **M47 WB-48 Query history完成合同（2026-10-07）**：保留历史key/schema/最新50条，immutable FIFO串行持久写、最多50待写、当前错误可观察且后续队列恢复；query/selection/EXPLAIN完成等待写ack，公开历史读等待既有写，showRows/Copilot保持同步且不写query history。deferred Memento与生产command/panel夹具和TS编译通过；0actual，原WB47失败及独立cleanup风险不转为成功，最终十文件完整restore/原format/本地提交见M47记录。
- **M47 WB-47…92904 tokens truncated…n、聚合 / GROUP BY / 窗口函数 JOIN。
- **MM1 内置 KV Keyspace**：`SonnetDB.Core` 新增独立 `Kv` 模块，`Tsdb.Keyspaces.Open(name)` 可打开轻量 keyspace，支持 `Put` / `Get` / `Delete` / `ScanPrefix`、独立 KV WAL、崩溃恢复、快照和 compaction 段文件；不修改现有时序 `.SDBWAL` / `.SDBSEG` 二进制格式。
- **MM2 关系表 MVP**：`SonnetDB.Core` 新增 `Tables` 模块与 `Tsdb.Tables` 入口，基于 MM1 `KvKeyspace` 在 `tables/` 目录实现关系表 rowstore；SQL 支持 `CREATE TABLE ... PRIMARY KEY (...)`、`DROP TABLE`、`INSERT`、`SELECT`、`UPDATE`、`DELETE`、`SHOW TABLES` 与 `DESCRIBE TABLE`，覆盖 `INT` / `FLOAT` / `BOOL` / `STRING` / `DATETIME` / `BLOB` / `JSON` 基础类型、主键查找、NOT NULL 校验和 ADO.NET BLOB 读取；不修改现有时序 `.SDBWAL` / `.SDBSEG` 二进制格式。
- **MM3 二级索引、约束和轻事务**：关系表新增 `CREATE [UNIQUE] INDEX`、`DROP INDEX ... ON`、`SHOW INDEXES ON`、索引 schema 持久化、普通 / 唯一索引 rowstore 维护和 rebuild；`SELECT` / `UPDATE` / `DELETE` 在索引列完整等值谓词下可走二级索引候选行，`EXPLAIN` 返回 `access_path` / `index_name`；`SqlExecutor.ExecuteScript` 和服务端 `/sql/batch` 支持 `BEGIN [TRANSACTION]`、`COMMIT`、`ROLLBACK` 单表小批量 DML 轻事务，提交失败时回滚已应用 rowstore / index 变更。
- **向量索引 adapter 层**：`src/SonnetDB.Core/Storage/Segments` 新增内部 `IVectorIndexBuilder` / `IVectorIndexReader` adapter，先通过 legacy HNSW wrapper 包住现有 `HnswVectorBlockIndex`；`SegmentWriter` 构建向量索引改走 builder，`KnnExecutor` 的 ANN 查询改走 reader，保持现有 `.SDBSEG` embedded extension / legacy `.SDBVIDX` 格式和查询行为不变，为后续接入 DotVector.Indexing 预留边界。
- **Prometheus Remote Write 兼容入站端点**：`src/SonnetDB` 新增 `POST /api/v1/prom/write?db=<name>`，请求体为 `snappy(block) + protobuf(prometheus.WriteRequest)`，让 Prometheus / VictoriaMetrics agent / Grafana Alloy / OpenTelemetry Collector 可以无需改 URL 直接把指标写入 SonnetDB。映射规则：`__name__` label → `measurement`，其余 label → `tags`，每条 `Sample(value:double, ts:int64 ms)` 展开为一个 `Point` 的 `value:double` field；NaN 样本（Prometheus stale marker）与名称含保留字符（`, = \n \r \t "`）的 series 静默跳过；snappy / protobuf 解码失败返回 `400`，否则返回 `204 No Content`（Prometheus 协议约定）。仅 server 层引入新依赖 `Snappier 1.3.1`（纯托管 Snappy 块格式解压，AOT 友好）；protobuf 解码采用手写最小 decoder（仅识别 WriteRequest/TimeSeries/Label/Sample，未知 wire 字段安全跳过），不引入 `Google.Protobuf` / `Grpc.Tools`，不改动 `src/SonnetDB.Core`。新增 13 个端到端测试覆盖 happy path + SQL 回查、多 series、NaN 跳过、缺失 `__name__`、保留字符、空 body、bad snappy / bad protobuf、缺/未知 db、ReadOnly 403、未认证 401、JSON 错误体。
- **InfluxDB 兼容写入端点**：`src/SonnetDB` 新增 `POST /write`（v1：`?db=&precision=`）与 `POST /api/v2/write`（v2：`?bucket=&org=&precision=`）两个 InfluxDB Line Protocol 兼容端点，复用现有 `LineProtocolReader` + `BulkIngestor`，让 Telegraf / EMQX / `influx` CLI / Prometheus `influxdb_v2` remote write 等生态工具可直接对接 SonnetDB；与既有 `/v1/db/{db}/measurements/{m}/lp` 的关键差别是 `measurement` 由每行 LP 自行解析（`measurementOverride: null`），符合 InfluxDB 协议语义。支持 `Content-Encoding: gzip` 请求体、`precision` 别名（`n`/`ns`、`u`/`us`/`µs`、`ms`、`s`，缺省 ns），成功返回 `204 No Content`，错误返回 `{ "error", "message" }` JSON；`BearerAuthMiddleware` 同步增加 `Authorization: Token <token>` 别名，与 `Authorization: Bearer <token>` 等价，以兼容 InfluxDB v2 客户端约定。新增 19 个端到端测试覆盖 v1/v2、precision 全别名、gzip、Token 头、measurement 解析、204、400/401/403/404 与 JSON 错误体。
- **地理坐标系转换与国内瓦片切换**：Web Admin 的轨迹地图和 SQL Console 地图视图现支持 OSM / 高德 / 腾讯 / 百度瓦片下拉切换；SQL 内置 `geo_transform` 以及 `geo_wgs84_to_gcj02`、`geo_gcj02_to_wgs84`、`geo_gcj02_to_bd09`、`geo_bd09_to_gcj02`、`geo_wgs84_to_bd09`、`geo_bd09_to_wgs84` 等坐标系转换函数，并在结果区按当前底图投影自动重投影 `GEOPOINT`。
- **数据库创建独立对话框**：`web/src/views/SqlConsoleView.vue` 的 Create Database 动作改为先弹出独立对话框，再输入名称并确认创建，避免在侧边栏中直接编辑数据库名。
- **SQL Console 结果区三视图升级**：`web/src/views/SqlConsoleView.vue` 现直接复用 `SqlResultPanel.vue` 作为结果展示卡片，结果区可在表格 / 图表 / 轨迹地图之间切换；`SqlResultChart.vue` 继续提供时间轴和值轴下拉选择，带明显时间列和数值列的结果会优先默认进入图表视图。
- **SonnetDB Workbench 首版**：Web Admin 的 `/admin/app/sql` 现已升级为 Workbench，按 Schema Explorer / SQL Editor / Staged Preview / Result Grid 组织布局；继续复用 `GET /v1/db`、`GET /v1/db/{db}/schema`、`POST /v1/db/{db}/sql` 与现有 Copilot stream 协议。写操作会先进入 staged preview，`DELETE` / `DROP` / `GRANT` / `REVOKE` / `USER` / `TOKEN` 类危险操作需要用户勾选确认；Copilot 仍保持右下角全局浮窗，不新增工作台内专栏。
- **SQL `EXPLAIN` 落地**：`POST /v1/db/{db}/sql` 现在支持 `EXPLAIN SELECT`、`EXPLAIN SHOW MEASUREMENTS` / `SHOW TABLES` 与 `EXPLAIN DESCRIBE [MEASUREMENT]`，返回 `key` / `value` 结果行而不是白页；同一套只读估算逻辑也复用到 MCP `explain_sql`。
- **WHERE time now()/duration 求值**：`WHERE time` 现在支持 Unix 毫秒整数字面量、duration 字面量以及 `now()` 参与的算术表达式，`SELECT`、`DELETE` 与 `explain_sql` 共享同一求值路径，因此 `time >= now() - 1d`、`time < now() + 1d` 这类查询可以直接执行。
- **云端 Copilot 直连桥接**：SonnetDB Web 端的 `/v1/copilot/chat` 与 `/v1/copilot/chat/stream` 现已改为只走 `https://ai.sonnetdb.com` 官方云端 Copilot Runtime，本地不再提供知识库 / 技能库 / 本地模型兜底；本地服务仅负责上下文摘要、数据库权限校验、受确认约束的工具执行与 tool result 回传。
- **sonnetdb.com 账号绑定与唯一 AI Gateway**：Web Admin「Copilot 设置」改为设备码绑定 sonnetdb.com 账号，绑定成功后本地 `.system/ai-config.json` 仅保存 Cloud Access Token / Refresh Token；OSS 端固定通过 `https://ai.sonnetdb.com` 调用平台 AI Gateway，不再支持国内 / 国外节点选择、手动 API Key、`SonnetDBServer_Ai_Example` 或本地模型选择。平台模型列表改为绑定后从 `ai.sonnetdb.com/v1/models` 读取，聊天请求自动使用平台返回的默认模型。
- **GitHub 协作模板与治理规范**：新增 `.github/ISSUE_TEMPLATE/`（bug/feature/task + config）、`.github/pull_request_template.md`，并新增 `.github/project-management.md` 统一 Milestone / Label 命名、颜色与 Issue 生命周期流转规则。
- **WAL LastLsn footer 元数据**：新 WAL segment 会在记录区后追加 32 字节 LastLsn footer，`WalWriter.Open` 优先通过 footer 快速恢复 `NextLsn`，旧 WAL、损坏 footer 与截断尾部会自动回退到顺序扫描并重写 footer；`WalRecordHeader` 与既有 WAL 记录格式保持不变，旧 WAL 继续可读。
- **WAL group-commit**：`SyncWalOnEveryWrite=true` 时新增可配置的 `WalGroupCommitOptions`（默认 2 ms 窗口），多个并发 `Write` / `WriteMany` / `Delete` 请求会在写入 WAL 后共享一次 `Flush(true)`，写请求仍会等待该批 fsync 完成后返回；WAL record/header 二进制格式不变，旧 WAL 继续可读。新增 WAL group-commit 崩溃恢复、`WriteMany` 批量写入、并发写入测试与 `WalGroupCommitBenchmark` 基准。
- **WAL 小记录写入优化**：`WalWriter.AppendRecord` 现在会将 `WalRecordHeader` 与 payload 合并到同一块 `stackalloc` / `ArrayPool` 缓冲后尽量单次 `Stream.Write`，保留原有 CRC32 payload 校验与 WAL 二进制布局不变；补充小/大 payload round-trip、CRC 损坏和截断容忍回归测试。
- **WAL 写缓冲策略评估**：新增 `WalBufferingBenchmark` 对比 `BufferedStream(FileStream)` 与 `FileStream + self buffer`；本机 microbenchmark 显示 BCL `BufferedStream` 在 200 万条 WAL-like 小记录写入中吞吐更优（约 91.15 ms vs 96.94 ms），因此生产路径继续保留 `BufferedStream`，`Sync()` 的 `Flush(true)` 持久化语义不变。
- 新增独立 `OPTIMIZATION_ROADMAP.md`，用于跟踪 `src/SonnetDB.Core` 核心库性能与可靠性优化路线，覆盖写入吞吐、MemTable 热路径、查询索引与缓存、窗口函数、崩溃恢复和现代 C# / Analyzer 六个阶段，并为每项任务提供状态标记、执行顺序、验收标准与可复用提示词。
- **Copilot Agent 提示词增强**：参考 VS Code Copilot 的行动型助手原则，强化 SonnetDB Copilot 的身份边界、工具优先、上下文事实校验、安全/版权边界、模型回答规则与 SQL 方言纠错约束，避免冒充外部产品或编造数据库结构。
- **SQL DDL 兼容修饰符（PR 4）**：lexer 新增 `DEFAULT` 关键字；`CREATE MEASUREMENT` parser 接受列级 `NULL` / `NOT NULL` 与 `DEFAULT <expr>` 并在 AST 中保留；执行层对 `DEFAULT` 返回明确 unsupported，`NULL` / `NOT NULL` 保持兼容性 no-op，并在 SQL 文档中说明 SonnetDB 的稀疏字段语义。
- **SQL 单表别名与限定列名（PR 3）**：SQL lexer 新增 `.` token；parser 支持 `FROM measurement [AS] alias` 与 `alias.column` / `alias."Column"` 列引用；执行器在查询执行前校验限定符必须匹配当前单表别名。
- **SQL `ORDER BY time`（PR 2）**：SQL lexer 新增 `ORDER` / `ASC` 关键字识别（`DESC` 复用已有 `DESC` token），`SELECT` AST 增加 `OrderBySpec`，parser 支持 `ORDER BY time [ASC|DESC]`，执行器会在 `LIMIT/OFFSET/FETCH` 前按结果集中的 `time` 列排序，并同步修正分页文档示例。
- **SQL 兼容性基础（PR 1）**：`SELECT` 现在支持常见探活写法 `SELECT 1 ... LIMIT 1` 的字面量投影；聚合函数 `count` 额外兼容 `count(1)`，语义等同于 `count(*)`，方便 Copilot / ORM 生成 SQL 直接执行。
- 新增 `connectors/` 连接器目录，预留 C / Go / Rust / Java / ODBC 连接器；首个 C 连接器通过 .NET Native AOT 将 `SonnetDB.Core` 发布为原生共享库，并导出 open / close / execute / result cursor / flush / last_error 等 C ABI 函数，附带 `sonnetdb.h`、C quickstart 示例与 CMake 构建入口（Windows x64/x86/ARM64、Linux x64）。
- 新增 Java 连接器第一版：提供 Java 8 兼容的默认 JNI 后端与 JDK 21+ 可选 FFM 后端，基于 C ABI 暴露 `SonnetDbConnection` / `SonnetDbResult` / `SonnetDbValueType` / `SonnetDbException`，支持打开嵌入式库、执行 SQL、读取 typed result cursor、Flush 与版本查询，并提供 CMake 构建入口和 quickstart 示例。
- 新增 Go 与 Rust 连接器第一版：Go 连接器基于 cgo 包装 SonnetDB C ABI，提供 `sonnetdb.Open` / typed result cursor / `database/sql` driver 与 quickstart；Rust 连接器提供手写 FFI、`Connection` / `ResultSet` 安全封装、typed getter、版本/错误读取与 quickstart，二者均复用既有 Native AOT `SonnetDB.Native` 共享库。
- 新增 Python 连接器第一版：基于标准库 `ctypes` 直接加载 SonnetDB C ABI，提供 `sonnetdb.connect`、`Connection.execute` / `execute_non_query`、typed result cursor、轻量 DB-API-style `cursor()`、quickstart 与 unittest smoke，运行时不引入第三方依赖。
- 新增 Visual Basic 6 与 PureBasic 连接器第一版：VB6 连接器提供 x86 stdcall 桥接 DLL 源码、`.bas` / `.cls` 封装与 quickstart，解决 VB6 stdcall 与 SonnetDB C ABI cdecl 的调用约定差异；PureBasic 连接器提供 `SonnetDB.pbi` 动态加载封装与 quickstart。二者因托管 CI 缺少授权语言工具链，不在 GitHub Actions 中构建各自语言产出的动态库。
- 新增连接器专用发布 CI：`.github/workflows/connectors-release.yml` 在非 tag 事件只编译连接器，在 `vX.Y.Z` tag 上使用 C# 工具 `eng/tools/connectors-release` 为各连接器按 RID 生成独立 zip 包，并把所有 zip 上传到对应 GitHub Release；每个包包含连接器产物、Native runtime、示例、启动脚本与说明文档。
- **写入路径支持受控 schema-on-write**：`Tsdb.Write/WriteMany` 现在会在写 WAL / MemTable 前自动创建或扩展 measurement schema，并先持久化 `measurements.tslschema`，覆盖 SQL `INSERT`、Line Protocol、JSON points 与 Bulk VALUES；缺失 TAG / FIELD 自动补齐，已有 `INT` 字段遇到 `FLOAT` 值会提升为 `FLOAT`，已有 `FLOAT` 字段接收整数时会转换为浮点保存，其它类型漂移仍拒绝。
- 新增 `eng/build-windows.ps1`，一键完成 Windows `win-x64` Release 构建、NuGet 打包、ZIP Bundle 与 MSI 输出，并把最终可发布文件汇总到 `artifacts/windows/final/` 后清理中间产物；同步修正发布脚本 NuGet 清单为 `SonnetDB.Core` / `SonnetDB` / `SonnetDB.Cli`，让服务端 publish 正确尊重 `BuildAdminUi` 开关，并让 Windows MSI 安装 `SonnetDB` 服务、通过 `DATAROOT` 指定数据目录、把 `sndb` 加入系统 `PATH`。
- **Apache IoTDB / PostgreSQL TimescaleDB 服务端基准**：`tests/SonnetDB.Benchmarks` 新增 IoTDB REST v2 `insertTablet` / SQL 查询 / `GROUP BY` 时间窗口基准，以及 TimescaleDB hypertable + binary COPY / range query / `time_bucket` 基准；Docker benchmark 环境、启动脚本、README 与 `docs/blogs/111-113` 对比通稿同步补齐实测数据，并统一 benchmark 文档中的时间单位为 ms、数据大小单位为 MB。
- **LiteDB 嵌入式基准**：`tests/SonnetDB.Benchmarks` 新增 LiteDB 5.0.21 对照，覆盖 100 万点 `InsertBulk` 写入、`Ts` 索引范围查询与 1 分钟桶文档顺扫聚合，并在 benchmark 文档与对比通稿中补充实测数据。
- **Benchmark 服务端端口可配置**：`tests/SonnetDB.Benchmarks/docker/docker-compose.yml` 新增 `SONNETDB_BENCH_PORT` 宿主机端口覆盖，`ServerBenchmark` 新增 `SONNETDB_BENCH_URL` 覆盖，并修复 SonnetDB / TDengine compose 健康检查，方便在本机已有 SonnetDB 开发容器占用 `5080` 时隔离运行基准。
- **PID 控制函数基准**：新增 `PidBenchmark`，覆盖 50k 阶跃响应数据上的 `pid_series`、`pid(...) GROUP BY time(1m)`、`pid_estimate(..., 'zn', ...)` 与 `pid_estimate(..., 'imc', ...)`，用于回归工业控制函数端到端 SQL 性能。
- **PR #70 — GEOPOINT 数据类型 + 编解码**：新增 `FieldType.GeoPoint = 6`、`GeoPoint` / `FieldValue.FromGeoPoint`，Segment 格式升级到 v4 并支持 `BlockEncoding.GeoPointRaw`（lat/lon 各 8 字节 little-endian），保留 v3 只读兼容；WAL、Segment、SQL `GEOPOINT` 列声明、`POINT(lat, lon)` 字面量、ADO.NET `GeoPoint` 参数化与 `lat(field)` / `lon(field)` 标量提取函数已接通。
- **CopilotDock 页面感知快捷能力**：全局 Copilot 仍保持伴随式聊天入口，但主界面收起模型选择与知识库状态到「选项」弹层；根据当前页面自动展示快捷动作，SQL Console 页面提供「生成 SQL / 修复 SQL / 解释 SQL / 优化 SQL」，其它页面提供结构梳理、事件排查、权限检查、配置检查等上下文能力。
- **PR #71 — 地理空间标量函数（Tier 1）**：新增 `geo_distance` / `geo_bearing` / `geo_within` / `geo_bbox` / `geo_speed`，基于 Haversine 计算距离、方位角、圆形围栏、矩形围栏与速度，并注册 `ST_Distance` / `ST_Within` / `ST_DWithin` PostGIS 兼容别名。
- **PR #72 — 轨迹聚合函数（Tier 2）**：新增 `trajectory_length` / `trajectory_centroid` / `trajectory_bbox` 与 `trajectory_speed_max` / `trajectory_speed_avg` / `trajectory_speed_p95`，支持 `GEOPOINT` 轨迹总路程、重心、外包矩形与基于相邻点时间差的速度统计，并接入 `GROUP BY time(...)`。
- **PR #73 — GeoJSON 序列化 + REST 端点扩展**：`GEOPOINT` 查询结果 ndjson 自动输出 GeoJSON Point（`[lon,lat]`），新增轨迹 REST 端点 `GET /v1/db/{db}/geo/{measurement}/trajectory`，支持 Point FeatureCollection 与 `format=linestring`；远程 ADO.NET `DbDataReader` 现在会把 GeoJSON Point 反序列化回 `GeoPoint` struct。
- **PR #74 — Web Admin 轨迹地图标签页**：新增 Vue3 TrajectoryMap.vue，引入 maplibre-gl 与 echarts，支持按数据库 / Measurement / GEOPOINT 字段 / TAG / 时间范围加载轨迹端点，展示 OSM 底图、LineString 轨迹、起终点标记、时间轴回放与速度折线图。
- **PR #75 — SQL 控制台地图渲染集成**：查询结果自动检测 GeoJSON Point / GeoPoint 列并显示地图视图，SqlResultPanel 支持文本 / 表格 / 图表 / 地图切换；ResultMapPreview.vue 支持散点、按时间排序轨迹连线与低基数列分组，多点结果可直接在 SQL Console 预览。
- **PR #76 — 地理空间索引（Geohash 段内过滤）**：Segment 格式升级到 v5，`BlockHeader` 新增 `GeoHashMin` / `GeoHashMax` 32-bit geohash 前缀；`SegmentWriter` 为 GEOPOINT Block 写入空间范围，`QueryEngine` 在 `geo_within` / `geo_bbox` WHERE 谓词下对落盘 block 做 geohash 剪枝，同时保留 v4 段文件只读兼容。
- **PR #77 — 地理空间基准 + 文档完善**：新增 `GeoQueryBenchmark`，覆盖 `100k` 默认轨迹点和可选 `1M` 档位下的 `geo_within`、`geo_bbox`、`trajectory_length` 与 `GEOPOINT` range scan；README 与 `docs/geo-spatial.md` 补齐地理空间功能矩阵、Web Admin / SQL Console 地图用法、基准运行方式和车辆追踪 / 户外运动 / IoT 地理围栏端到端示例。

### Changed
- **NuGet / Actions dependency refresh**：按 NuGet latest stable 与当前 GitHub dependency PR 批量升级中央包版本，覆盖 .NET `10.0.10` 系列、`Microsoft.Extensions.AI 10.8.0`、`ModelContextProtocol 1.4.1`、`Microsoft.ML.OnnxRuntime 1.27.1`、OpenTelemetry `1.17.0`、`Microsoft.NET.Test.Sdk 18.8.1`、`Testcontainers 4.13.0`、Roslyn `5.6.0`、NATS client `3.0.0`、`AWSSDK.S3 4.0.101.3`、`StackExchange.Redis 3.0.17` 与 SQLite native bundle `3.0.4`；两个源码子模块同步升级 Roslyn fallback、MSTest 和 NUnit adapter。GitHub workflows 同步使用 `actions/setup-dotnet@v6`、`actions/setup-node@v7`、`actions/setup-go@v7` 与 `actions/upload-artifact@v7`。
- **门面文档叙事收敛**：根 README / README.en 改为“一句话定位 + 能力矩阵 + 使用方式 + 专题文档索引”的简洁结构，剔除长 SQL 函数表、架构细节和基准明细，突出时序、关系、KV、文档、全文、向量、对象存储、消息队列、Copilot、Workbench、MCP 和多语言连接器；文档首页、Workbench 文档和 Web 欢迎页同步从单一时序数据库定位升级为多模型数据底座定位。
- NuGet 发布链路新增 `SonnetDB.EntityFrameworkCore`：EF Core Provider 现在会随 `eng/release.ps1 -Tasks nuget`、GitHub Publish workflow、SDK Bundle 和发布文档一起输出。
- 文档统一修正 ADO.NET 提供程序的 NuGet 包名：`src/SonnetDB.Data` 发布为 `SonnetDB`，代码命名空间仍为 `SonnetDB.Data`；根 README、文档首页、综合指南、包说明和产品首页同步收敛当前能力说明。
- **Block 级跳跃索引（MaxTimestamp 前缀最大值）**：`SegmentIndex` 在 `Build` / `BuildFromBlocksForTesting` 时为每个 `(seriesId, fieldName)` 桶与每个 `seriesId` 桶预计算 `MaxTimestamp` 前缀最大值数组。`GetBlocks(seriesId, fieldName, from, toInclusive)` 中原来"二分上界 + 线性扫描下界 (`MaxTimestamp >= from`)"的实现改为"二分上界 + 在前缀最大值上二分下界 + 仅在 `[lower, upper)` 区间补一次微扫描"，渐近复杂度由 O(upper) 改善为 O(log n + 命中桶数)；新增 `GetBlocks(seriesId, from, toInclusive)` 多 field 单 series 时间窗剪枝重载，`MultiSegmentIndex.LookupCandidates(seriesId, from, to)` 改为转发该重载。压缩产生的重叠 block（`MaxTimestamp` 非单调）下结果集与朴素实现一致。新增 `SegmentIndexSkippingTests` 12 个场景：非重叠窄/宽窗、查询完全前/后于所有 block、边界等值（含单点查询）、压缩重叠场景两例、按 series 多 field 路径、未知 series/field 空返回、64 个随机 block × 300 次随机查询的 fuzz 与朴素 O(n) 实现对拍；`Storage/Segments/SegmentIndex.cs`、`Storage/Segments/MultiSegmentIndex.cs` 不修改文件二进制格式，无新增依赖。
- **Codec/BlockDecoder V2 专用化快路径**：评估 source generator、静态泛型与手写 fast path 后，选择零依赖、safe-only 的手写专用化；`TimestampCodec` 可直接把 delta-of-delta 时间戳写入 `DataPoint` 目标视图，`ValuePayloadCodecV2` 新增全量与范围 `DecodeInto` 路径，`BlockDecoder` 避免 V2 全量/范围解码中的中间 `long[]` / `FieldValue[]` 分配。新增 `CodecSpecializationBenchmark`，覆盖旧组合路径与生产快路径对比，并补充 V2 range 语义回归测试。
- **SonnetDB.Core 性能 analyzer 配置**：为核心库启用低噪声性能规则，覆盖热路径 LINQ、重复数组分配、Count/Any、Dictionary 查询、SearchValues 与字符串比较相关建议；高语义取舍规则先以 suggestion 暴露，并修复新增 warning 命中的保留字符搜索与 SQL 元数据列名数组分配点，保持 `TreatWarningsAsErrors` 通过且不新增 suppress。
- **Options/config 值对象化**：`TsdbOptions`、flush/WAL/segment/compaction/retention/background/pid 等配置类型改为 `sealed record`，继续保留 `new Options { ... }` 对象初始化器与 init-only 属性，同时支持 `with` 生成新配置快照，降低运行时共享配置被意外修改带来的并发不确定性。补充对象初始化器兼容、`with` 快照与值语义测试。
- **SQL Lexer 字符分类快路径**：`SqlLexer` 的空白、标识符、数字、duration 后缀与运算符起始字符判断改为基于 `SearchValues<char>` 的 ASCII 快路径，并保留 Unicode 标识符/空白 fallback；补充 `SqlLexerBenchmark` 对比旧分支分类 lexer 与新实现。
- **Catalog/schema/tag index 冻结快照**：`SeriesCatalog`、`MeasurementCatalog` 与 `TagInvertedIndex` 改为写入路径维护 mutable builder 并原子发布 `FrozenDictionary` / `FrozenSet` 快照，`MeasurementSchema` 的列名索引也改为构造后冻结；读路径无锁查询已发布快照，保持并发 `GetOrAdd` 幂等与 tag 查询语义不变。补充查找正确性、更新后可见性与并发读写回归测试。
- **Segment v6 主格式整合**：Segment 写入版本升级到 v6，新段把原先为保持 v5 而外置的 HNSW `.SDBVIDX` 与扩展聚合 `.SDBAIDX` 内容内嵌为 `.SDBSEG` 索引区之后、Footer 之前的 extension section；`SegmentHeader` 保留区新增 mini-footer 摘要副本（IndexCount / IndexOffset / FileLength / IndexCrc32），用于尾部损坏时的诊断与受控 fallback。读取层继续兼容 v4/v5，并保留旧 sidecar 按需读取回退。
- **WalSegmentSet checkpoint replay 单遍化**：`ReplayWithCheckpoint` 改为单次扫描 WAL records，并利用 segment `LastLsn` 元数据或相邻 segment 起始 LSN 推导结果跳过 durable checkpoint 之前的整段；旧 WAL、无 footer WAL 与 legacy `active.SDBWAL` 升级路径继续兼容，补充多段、checkpoint 命中和 legacy 升级回归测试。
- **Tombstone manifest 周期性 checkpoint**：新增 `TombstoneCheckpointOptions`，Delete 路径可按累计删除数或时间间隔把当前 tombstone 快照持久化到 manifest；恢复时以 manifest 最大 `CreatedLsn` 作为 Delete replay 下界并对 tombstone 去重，降低大量删除后崩溃恢复对 WAL Delete 全量重放的依赖，同时保持 Delete 过滤与 Compaction 消化语义不变。
- **Checkpoint LSN 持久化语义增强**：Flush 成功写出 Segment 并 Sync WAL checkpoint 后，会把 durable checkpoint LSN 以 tmp 写入、fsync、原子 rename、父目录 best-effort fsync 的方式保存到 WAL 元数据文件；恢复时仅当对应 Segment 文件存在且长度匹配才采用该 checkpoint，否则忽略并完整 replay WAL，避免坏中间状态错误跳过未 flush 数据。
- **WAL record torn-write 检测增强**：新写入的 WAL record 在不改变 32 字节 header 尺寸的前提下启用 header checksum，`WalReader.Replay` 与 `WalWriter.Open` 扫描旧 WAL 时会在第一条 header/payload/CRC 损坏记录处停止并忽略尾部；`Flags=0/Reserved=0` 的旧 WAL record 继续按原格式读取。
- **数值聚合 SIMD 快路径**：新增可选 `TsdbOptions.UseSimdNumericAggregates`（默认开启），`sum` / `min` / `max` / `count` 在适合的落盘数值 block 部分范围聚合上可使用 `System.Numerics.Vector<T>` 处理 Float64 / Int64 payload；不支持硬件加速、遇到 NaN、Boolean 或不适合的编码时自动回退标量路径，保持查询结果语义不变，并补充 scalar/SIMD 一致性测试与 `NumericAggregateSimdBenchmark` BenchmarkDotNet 对比。
- **扩展聚合 block sketch 快路径**：为数值 block 写入 TDigest 与 HyperLogLog sketch，`percentile` / `p50` / `p90` / `p95` / `p99` / `tdigest_agg` / `distinct_count` 在全局聚合且无 tombstone/geo 过滤时可按 block 合并 sketch；v6 新段将 sketch section 内嵌到 `.SDBSEG`，旧 `.SDBAIDX` sidecar 仍可按需读取回退，损坏或缺失时自动回退旧解码扫描路径，并补充内嵌读取、legacy sidecar 回退与 SQL 快路径测试。
- **窗口函数流式状态接口**：新增 `IWindowState` / `IWindowStreamingEvaluator`，流式窗口函数可通过 `Update(timestamp, value)` 按行推进；`DoubleWindowEvaluatorBase` 保留旧 `Compute` / `ComputeDouble` 适配层，`SelectExecutor` 在窗口 evaluator 全部支持流式状态时跳过 `long[]` / `FieldValue?[]` 对齐数组和预计算输出，仍对未迁移函数自动回退旧 materialized 路径。补充流式状态语义与大数据集内存占用回归测试。
- **窗口函数 Span 批量路径**：`moving_average`、`running_sum` 及新增 `running_min` / `running_max` 的 typed/boxed 批量计算改为基于 `ReadOnlySpan` / `Span` 单遍填充输出；`moving_average` 小窗口使用栈上环形缓冲，减少临时数组，同时保持前 `n-1` 行 NULL、缺失值跳过/延续和时间顺序语义不变。补充空输入、全缺失、窗口大小 1 与累计极值边界测试。
- **窗口函数 typed evaluator**：新增 `IWindowDoubleEvaluator` / `WindowDoubleOutput` 数值窗口函数输出接口，`SelectExecutor` 对 typed evaluator 优先复用 `double[] + bool[]`，避免 `Compute` 先生成 `object?[]` 导致每行提前装箱；`moving_average`、`ewma`、`holt_winters` 与累计求和路径已迁移，并新增 `running_sum` 作为 `cumulative_sum` 兼容别名。补充 typed 语义测试、SQL 兼容测试与 BenchmarkDotNet 分配基准。
- **SegmentReader 可选 memory-mapped 读取路径**：新增 `SegmentReaderOptions.UseMemoryMappedFileForLargeSegments` 与 `MemoryMappedFileThresholdBytes`，大段文件可通过 safe-only `MemoryMappedViewAccessor` 按需读取 header/index/block payload，避免默认 `File.ReadAllBytes` 把整段放入 LOH；默认仍保留 `byte[]` reader，mmap 打开失败会回退。补充默认回退、阈值回退、mmap 解码与 Dispose 释放文件句柄测试。
- **SegmentReader HNSW vector 索引懒加载**：`SegmentReader.Open` 不再 eager 反序列化 HNSW 图，改为 `TryGetVectorIndex` 首次命中 VECTOR block 时从 v6 内嵌 section 或旧 `.SDBVIDX` sidecar 按需读取，并通过进程内共享 LRU 预算 `SegmentReaderOptions.VectorIndexCacheMaxBytes` 控制常驻引用；冷段打开后不占用 HNSW 图内存，reader Dispose 会移除本段缓存。补充懒加载、预算淘汰、KNN 结果一致性与 compaction 后内嵌索引可加载测试。
- **QueryEngine tombstone 过滤热路径优化**：`Execute(PointQuery)` 不再通过 LINQ `Where` 过滤墓碑，改为手写迭代器循环，并在查询前预筛与时间窗不相交的 tombstone；保持结果顺序、Limit 过滤后计数和墓碑闭区间覆盖语义不变，补充边界/Limit 与分配回归测试。
- **SegmentReader block 解码缓存**：`SegmentReader` 新增按 `(SegmentId, BlockIndex, Crc32)` 标识的已解码 `DataPoint[]` LRU 缓存，默认单 reader 预算 16 MB，可通过 `SegmentReaderOptions.DecodeBlockCacheMaxBytes` 调整或禁用；`QueryEngine` 重复查询同一落盘 block 时复用解码结果，缓存受内存上限约束并在 reader Dispose 时清空引用。补充重复查询命中与预算淘汰测试。
- **QueryEngine reader map 缓存**：`QueryEngine` 现在通过 `SegmentManager` 的绑定快照获取 `Index + Readers`，并在快照未变化时复用 `SegmentId -> SegmentReader` 映射，避免每次查询重复构建字典；`AddSegment` / `SwapSegments` / `Dispose` 发布新快照后会自动失效旧缓存，compaction swap 会延迟释放仍被查询快照租约持有的旧 reader，避免并发查询使用已 Dispose reader。
- **SegmentReader 时间范围索引优化**：`SegmentReader.Open` 现在会构建仅驻留内存的 block 时间范围索引，`FindByTimeRange` 通过 MinTimestamp/MaxTimestamp 双排序数组二分并扫描较小候选集，避免按时间范围查找时线性遍历全部 block；保持 block 区间重叠、边界 inclusive 与段内 `BlockDescriptor` 顺序稳定，补充乱序 block、多重重叠边界和大 block 列表性能回归测试。
- **SegmentReader series 索引优化**：`SegmentReader.Open` 现在会构建仅驻留内存的只读 `SeriesId -> BlockDescriptor[]` 索引（不改变 `.SDBSEG` 文件格式），`FindBySeries` 直接按 series 命中，`FindBySeriesAndField` 仅扫描该 series 的 block，并保持 `BlockDescriptor` 返回顺序稳定；补充多 series、多 field 与空命中回归测试。
- **MemTable Flush 热路径统计增量化**：`MemTable.EstimatedBytes` / `MinTimestamp` / `MaxTimestamp` 现在由 `Append` / WAL replay / Flush reset 生命周期维护，`ShouldFlush` 不再遍历全部 series；`MemTableSeries` 的字符串字段在 Append 时增量累加 UTF-8 byte count，并通过 immutable snapshot swap 缓存无追加期间的只读 Snapshot，排序在锁外完成以避免查询长时间阻塞 Append；`SnapshotRange` 在有序数据上直接二分并只复制命中区间，避免小范围查询先分配全量数组。补充并发 append/read 压力、replay、string/null/非 string 混合统计、重复 Snapshot 分配、范围查询边界与 flush 后重置回归测试。
- **Measurement schema-on-write 批量持久化**：`Tsdb.WriteMany(ReadOnlySpan<Point>)` 现在会先合并整批新增 TAG/FIELD 与 INT→FLOAT 类型提升，单次原子写入 `measurements.tslschema` 后再写 WAL/MemTable，保持“schema 先于数据可恢复”的崩溃安全语义，同时避免同一批导入中每个新增列都触发一次 schema fsync。
- **WAL catalog checkpoint**：`Tsdb.FlushNowLocked` 不再在每次 Flush 后向新 WAL 重写全量 `CreateSeries` snapshot；当 catalog 出现新增 series 时，Flush 会先原子持久化 `catalog.SDBCAT`，再写 Segment / WAL Checkpoint / 回收旧 WAL segment。崩溃恢复现在由「已 checkpoint 的 series 来自 catalog 文件，checkpoint 之后的新 series 继续来自 WAL `CreateSeries`」共同保证，避免 catalog 大时每次 Flush 产生 O(series_count) WAL 放大。

### Docs
- README 新增企业微信群二维码入口，方便用户扫码加入社群交流。
- 重写 README 顶部“SonnetDB 是什么”简介，明确 SonnetDB 当前已集成时序数据库、关系型数据库、KV NoSQL、S3 兼容存储桶与本地消息队列能力。
- 补充 SonnetDB vs IoTDB 对比文档的两种口径说明：新增 2026-05-06 的 `--comparison-server` 同口径“Server vs Server”实测结果（1,000 设备 × 30 字段 × 12 时间点，AB BA AB BA 四轮，SonnetDB Server 平均 22,867 values/sec、IoTDB 11,541 values/sec、约 1.98x），并同步更新根 README、`tests/SonnetDB.Benchmarks/README.md`、`QUICK_START.md` 与专门对比说明，保留旧的嵌入式 vs REST 历史结果但明确标注为不同方法学。
- 完善 `.agents/skills/sonnetdb-docker-language-build`，将模板 TODO 替换为 Docker-backed Go / Rust / Python / Linux connector 构建与 smoke test 回退流程，并修正技能默认提示词中的技能名。
- 新增 `docs/blogs/129-132` 连接器系列文章，分别介绍 Go、Rust、Visual Basic 6 与 PureBasic 连接器的 C ABI 复用方式、API 形态、构建/部署要求、CI 策略与适用场景。
- 新增 `docs/blogs/117-schema-on-write.md`，介绍受控 schema-on-write 的使用场景、SQL / LP / JSON / Bulk VALUES 自动补列规则、`INT -> FLOAT` 类型提升与 schema 先持久化再写 WAL 的崩溃安全语义。
- 新增 `docs/sql-cookbook.md`，把 `demo.sql` 中高频、当前真实支持的 `CREATE MEASUREMENT`、`INSERT`、`SELECT`、`GROUP BY time(...)`、窗口函数、PID、预测、向量检索、元数据与 `DELETE` 场景整理成可直接复制的 cookbook，并在 `docs/index.md` 与 `docs/sql-reference.md` 中加入入口。

### Fixed
- **CI Format Check 行尾规范化**：新增 `.gitattributes` 固定 C# 文件使用 LF 行尾，并归一化 `src/SonnetDB/Endpoints` 相关文件，补齐 file-scoped namespace 后空行，避免 `dotnet format --verify-no-changes --severity warn` 在 GitHub Actions 中因 CRLF / whitespace 失败。
- **Parity workflow startup diagnostics**：修复 parity compose 中 SonnetDB 容器 healthcheck 依赖 runtime 镜像未安装的 `bash`，改为使用镜像内已有的 `curl /healthz`；GitHub Actions 的 `Start parity stack` 失败时现在会保存 `docker compose ps` 与容器日志到 `artifacts/parity/<profile>/stack-diagnostics`，避免后续只报 “No files were found” 而丢失根因。
- **CI restore vulnerability gate**：`tests/SonnetDB.Benchmarks` 显式引用 `SQLitePCLRaw.bundle_e_sqlite3 3.0.3`，让 `Microsoft.Data.Sqlite` 的 native SQLite provider 解析到 `SourceGear.sqlite3 3.50.4.5`，避免 GitHub Actions 在 `dotnet build SonnetDB.slnx --configuration Release` restore 阶段因 `SQLitePCLRaw.lib.e_sqlite3 2.1.11` 的 NU1903 高危漏洞告警失败。
- 修复 `QueryEngine` 与 Compaction / Retention 段替换并发时，连续快照复用的旧 `SegmentReader` 可能被后续 swap 提前释放，导致查询偶发 `ObjectDisposedException` 的 CI 回归；`SegmentManager` 现在按 reader 维护租约状态，等待所有持有该 reader 的查询快照释放后再 Dispose。
- **GitHub Actions 发布链路修复**：修复 Native AOT 下 `SndbDataReader.GetSchemaTable()` 的 `SchemaTableColumn.DataType` 反射裁剪告警；连接器发布 workflow 递归 checkout DotSearch / DotVector 子模块；Dockerfile 显式复制 DotSearch / DotVector 源码并让 Docker 发布路径过滤覆盖 `src/SonnetDB.Core/**` 与 `modules/**`；同步更新 DotSearch Jieba 词典生成 target 的跨平台路径与工具执行方式，避免 Linux CI、连接器发布和镜像构建因缺少依赖源码或 Windows 路径分隔符失败。
- 修复 Rust connector Linux x64 CI 构建失败：`build.rs` 现在通过 Rust 支持的 `+verbatim` 链接修饰符精确链接 `SonnetDB.Native.so`，避免 `-l:SonnetDB.Native.so` 被 Cargo 解析为 rename 语法并报出 library name must not be empty。
- **CI 测试稳定性**：为服务端与 Accuracy 测试项目补齐 `coverlet.collector`，修复 `--collect:"XPlat Code Coverage"` 找不到 collector；Accuracy Tests 在 Docker/InfluxDB 不可用时改为显式 no-op 通过并记录原因，避免可选外部依赖缺失导致 CI 失败；SSE db 事件断言改为等待当前测试触发的数据库事件，避免 `__copilot__` 系统库后台事件串扰。
- **CI Format Check 回归**：收窄 `.editorconfig` 私有字段命名规则，避免 `dotnet format` 把 `private const` 与 `private static` 字段误判为实例字段；同步修复既有 whitespace、import 顺序和 LF 行尾问题，使 `dotnet format --verify-no-changes --severity warn` 可通过。
- **Tsdb.Dispose final flush 诊断**：关闭路径中的 final flush 失败仍保持 `Dispose` 不抛异常，但会写入 `Tsdb.LastError` 并触发 `Tsdb.DiagnosticEvent`，避免异常被静默吞掉；诊断事件订阅者自身抛错不会影响关闭语义。
- 修复 Linux x64 C connector quickstart 运行时错误：CMake 现在在 Linux 下通过精确文件名链接 `SonnetDB.Native.so`，避免 Native AOT 共享库无 SONAME 时把构建目录写入 `DT_NEEDED`，并补充 WSL 开发环境与连接器验证文档。
- **普通用户登录不再显示控制面虚拟库 `__control_plane__`**：Web Admin 会在普通用户进入后台时清理 SQL Console 与 Copilot 会话历史中残留的控制面本地状态；SQL Console 的新建标签、刷新数据库、运行 SQL 与待执行 SQL 注入路径也增加二次防护，避免同一浏览器先用 admin 打开 system tab 后再切换普通账号时暴露 `__control_plane__`。
- **Copilot 会话历史补齐 assistant 回复与引用保存**：CopilotDock 现在按发起请求时的会话 ID 追加 user / assistant 消息，避免请求完成前切换或新建会话导致最终回复没有落盘；assistant 消息会连同 citations 一起写入本地历史，并在历史会话中渲染引用标题、来源与摘要。标题栏新增「+ 新会话」入口，历史弹层保留切换、重命名、删除与清空能力。
- **CopilotDock 回答改为 Markdown 渲染并隐藏裸 citation 标记**：聊天浮窗现在会把 Copilot 回复按 Markdown 渲染，代码块、列表、表格与行内代码可正常排版；渲染时转义模型返回的原生 HTML，并隐藏回答末尾类似 `[C11][C12]` 的内部引用编号，避免用户误以为是 SQL 或异常内容。
- **Copilot 错误提示不再暴露原始 JSON**：Web Admin 的 Copilot API 客户端现在会解析服务端 `{ error, message }` 与流式 provider 错误，把 `copilot_not_ready / chat.endpoint_invalid` 等内部代码映射成可操作的中文提示，引导用户检查「Copilot 设置」中的服务地址、API Key、模型或 embedding 配置，避免直接显示 `Copilot 请求失败 503: {"error":...}`。
- 统一 Copilot skills、Web starters 与 SQL 编辑器方言文案中的 SonnetDB SQL 示例口径：聚合示例统一回到当前真实支持的 `GROUP BY time(...)`，修正 `pid_tune` / `pid_compute`、`time_bucket(...)`、`LAG/LEAD OVER (...)` 等会误导当前版本能力边界的过时或未公开支持写法。
- **Token / API Key 管理现在支持带连字符的用户名**：控制面 SQL parser 为 `SHOW TOKENS FOR`、`ISSUE TOKEN FOR`、`SHOW GRANTS FOR` 等语句补齐 quoted username 语法，`TokensView.vue` 与 `UsersView.vue` 在回填已有用户名时统一走字符串 quoting，修复用户名如 `ops-admin` 时签发 token 报 `期望标识符（位置 16）`，并补 parser 与控制面集成回归测试。
- **Copilot 现在能直接理解“新建仓库并建表”并在无 db 场景继续工作**：新增 `CopilotProvisioning` 结构化意图抽取，把“建数据库 + 建 measurement + 从描述中抽字段”从 prompt 规则落到后端代码；`draft_sql` / `execute_sql` 现已支持 `CREATE DATABASE`，`/v1/copilot/chat` 对 provisioning 请求放开 `db` 必填限制，普通问题仍保持原有校验；Web 端 `CopilotDock` 同步支持在明显建库请求下绕过“先手工创建数据库”弹窗，并把工具产出的 SQL 自动绑定到目标库。
- **CopilotDock 在没有任何业务数据库时不再 400 `请求体需包含 db。`**：上一轮把 `__copilot__` / `_internal` 等系统库从 `dbs` 列表里过滤后，新装实例（或仅持有系统库的账号）`selectedDb` 永远为空，发送时直接被服务端 `CopilotChatEndpointHandler` 用 `400 bad_request` 拦下。现在 `CopilotDock.send()` 在 `targetDb` 为空时拦截发送，弹出 NDialog 引导用户输入数据库名（沿用 `isValidIdentifier` 校验、字母开头 + 字母数字下划线），点击「创建并继续」直接走 `execControlPlaneSql('CREATE DATABASE <name>')`，成功后 `reloadDbs()` 并把新库写回 `selectedDb` 再继续 `send` 流程；非超级用户提示「请联系管理员先创建一个」。配合上一轮服务端隐藏 `__copilot__` 的修复，确保新部署 / 空白账号也能从零开始让 Copilot 帮忙建第一个仓库。
- **Copilot 文档知识库索引不再因标题含保留字符而 500 `ingest_failed`**：`DocsIngestor` 新建 `__copilot__.docs` 时把 `section` / `title` 从 `TAG` 调整为 `FIELD STRING`，允许保留 Markdown 标题中的 ``=``, `,`, 引号和换行等字符；同时兼容旧库中遗留的 `TAG` 模式，在写入时仅对旧 schema 做最小归一化，避免 `bulk ingest / \`onerror=skip\`` 这类标题继续触发 `tag value contains reserved characters`。新增测试覆盖新 schema 精确保留标题，以及旧 schema 不抛异常回归。

### Fixed
- **SQL Console 的 GEOPOINT 结果现在可读且可上图**：`web/src/components/SqlResultPanel.vue` 现在会把 `GEOPOINT` / GeoJSON Point / `POINT(...)` 值格式化为可读文本，Markdown / 表格 / CSV 也会复用同一套值格式；`web/src/components/ResultMapPreview.vue` 与 `web/src/views/TrajectoryMap.vue` 则统一改成在 `style.load` 后先注入点线图层，不再等待底图瓦片完全加载，修复 `SELECT time, device, position, speed FROM vehicle LIMIT 100;` 这类结果里 `position` 看不到值、地图也不出点的问题。
- **Docker 镜像缺少 SPA 静态文件**：`src/SonnetDB/Dockerfile` 发布服务端时显式传入 `-p:BuildAdminUi=true`，让 `dotnet publish` 在镜像构建阶段执行 `web` 的 `npm ci && npm run build`，并把 `wwwroot/index.html` 与前端 assets 发布进最终镜像，避免容器访问 `/` 或 `/admin` 返回 `SonnetDB SPA static files are missing`。
- **隐藏 Copilot 系统库 `__copilot__` + 修复"创建数据库"被误解为列 measurement**：`DatabaseAccessEvaluator.GetVisibleDatabases` 现在统一过滤名字以双下划线开头并以双下划线结尾的系统库（含 `__copilot__`），管理员与普通用户均不再在 `GET /v1/db`、Web Admin Dashboard / Databases / SQL Console / Grants 下拉、Copilot Dock 数据库选择器、`/v1/copilot/chat` 的 `VisibleDatabases`、MCP `list_databases` 工具结果里看到它；`CopilotChatEndpointHandler` 同步对 `req.Db` 命中系统库时直接返回 `403 system_database`，防止旧客户端 localStorage 中残留的 `selectedDb = "__copilot__"` 触发 LLM 把系统知识库当成业务库去 `SHOW MEASUREMENTS`。`CopilotDock.vue` 客户端 `SYSTEM_DATABASES` 同步加入 `__copilot__` 并补充 `__xxx__` 通配过滤，作为防御性双重保险。`CopilotAgent` 的 Planner / Answer System Prompt 增加专门分支：用户说"建一个仓库 / 新建数据库 / create database"时，必须先 `draft_sql` 一条 `CREATE DATABASE <name>`（如未指定名字则建议合理名）+ 必要的 `CREATE MEASUREMENT`，禁止再去 `list_measurements` / `describe_measurement`，并显式告诉 LLM "不要把 `__copilot__` / `_internal` 当成业务库"。

### Added

- **Copilot 内置零依赖 embedding（M1）**：新增 `BuiltinHashEmbeddingProvider`，基于 SHA-256 + 词袋哈希投影生成 384 维 L2 归一化向量，无需任何模型文件即可让 Copilot 子系统就绪；`CopilotEmbeddingOptions.Provider` 默认值由 `local` 改为 `builtin`，`CopilotReadiness` 接受 `builtin`，DI 工厂在 `local` 模型缺失时自动降级到 `builtin`，从根上消除"`503 copilot_not_ready: embedding.local_model_path_missing`"。同步保证 `CopilotDocsIngestionService` 在新部署即可自动把 `./docs`（含 `sql-reference.md` 等）/`./web/help`/`./src/SonnetDB/wwwroot/help` 全部 markdown/html 摄入系统库 `__copilot__.docs`（`embedding FIELD VECTOR(384)`）作为 Copilot 知识库。

- **Copilot 知识库可视化端点（M1.5）**：新增 `GET /v1/copilot/knowledge/status`，返回当前 embedding provider（含是否处于 builtin 降级）、向量维度、扫描根目录绝对路径、已索引文件数 / 块数 / 最近一次摄入 UTC 时间、技能库条数，便于 Web Admin "Copilot 设置" 页面显示知识库实况和提供"立即重建索引"按钮。

- **SQL 控制台生成走 SonnetDB 方言（M2）**：重写 `AiEndpointHandler.BuildSqlGenPrompt`，明确告诉模型 SonnetDB 使用 `time` 列（不是 `ts`）、`CREATE MEASUREMENT … (col TAG, col FIELD <FLOAT|INT|BOOL|STRING|VECTOR(N)>)`、`GROUP BY time(1m)`（不支持 `date_trunc` / 按 tag 分组）、`knn(measurement, vec_col, [向量], k [, 'cosine'])` 向量检索、`LIMIT n OFFSET m` 分页、`DELETE FROM ... WHERE ...` 删除，禁止生成 MySQL/PostgreSQL/InfluxQL 方言；未指定 `db` 时也提供通用 SonnetDB system prompt（原实现会跳过提示）；前端 `SqlConsoleView.generateSql` 新增 `stripCodeFence` 防御性剥离 ```sql ... ``` 代码围栏，避免编辑器里残留 Markdown 标记。配套把 prompt 模板抽到 `src/SonnetDB/Copilot/Prompts/sql-gen.md` / `sql-gen-no-db.md`，通过 `<EmbeddedResource>` 嵌入程序集，由 `PromptTemplates` 加载器（带缓存 + `{{key}}` 占位符替换）按需读取，便于非编程人员维护提示词与未来多语言/A-B 测试。

- **Copilot Agent 支持 DDL/DML SQL 起草与执行**：在 `/v1/copilot/chat` 后台代理中新增 `draft_sql` 与 `execute_sql` 两个工具。`draft_sql` 会用 `SqlParser` 校验 `CREATE MEASUREMENT` / `INSERT` / `DELETE` / `SELECT` 语句并附带 measurement 是否已存在等说明，但不写入数据；`execute_sql` 在调用方对当前数据库具备 `Write` 权限时才会真正执行写入语句。Planner / Answer / 启发式回退、`CopilotAgentContext` 都已同步扩展，遇到“建表 / 写入 / 删除”意图时会先用 `list_measurements` / `describe_measurement` 收集上下文，再生成可直接复制执行的 SQL（放进 ```sql 代码块）。在 `tests/SonnetDB.Tests/Copilot/copilot-eval-scenarios.json` 新增 2 个 `write` 类场景覆盖回归。

- **全局 CopilotDock 浮窗 + 知识库可视化卡片（M4）**：在 Web Admin `AppShell.vue` 右下角注入新组件 `web/src/components/CopilotDock.vue`（折叠态为 52px 圆形 FAB，展开态为 380×540 浮窗，支持顶部拖拽和「全屏 / 收起到角标」切换），任意页面均可呼出 Copilot 助手；浮窗内置数据库选择、最近 3 条进度状态、最终回答展示、停止按钮、3 个示例 quick-prompt，复用既有 `streamCopilotChat` SSE 端点；同时为 `Copilot 设置`（`AiSettingsView.vue`）新增「本地知识库」卡片，消费 `GET /v1/copilot/knowledge/status` 显示 embedding provider（含 builtin 降级提示）、向量维度、根目录、已索引文件 / 块 / 技能数量、最近摄入 UTC 时间，并提供「立即重建索引」按钮（POST `/v1/copilot/docs/ingest {force:true}`，仅 admin 可见）；`web/src/api/copilot.ts` 增加 `fetchCopilotKnowledgeStatus` / `triggerCopilotDocsIngest`。

- **Copilot 会话历史 — 客户端持久化（M5 第一阶段）**：新增 Pinia store `web/src/stores/copilotSessions.ts`（`useCopilotSessionsStore`），把 Copilot Dock 内的会话以 `localStorage` key `sndb.copilot.sessions.v1` 持久化（最多 50 条，按 `updatedAt` 倒序，自动从首条用户消息派生 ≤ 32 字符标题，深度 `watch` 自动落盘）；`CopilotDock.vue` header 新增「会话历史」Popover 与 `+ 新会话` / `清空` 按钮，列表项支持点击切换、`✎` 重命名（`useDialog` + `NInput`）、`×` 删除；`send()` 改为写入 `sessions.current.messages`；切换会话时自动还原 `db` 选择。第二阶段（服务端 `__copilot__.conversations` 持久化 + `/v1/copilot/conversations` REST 端点）保留在 ROADMAP。

- **Copilot 页面上下文感知（M6）**：CopilotDock 选择数据库下方新增 `📍 当前页面：Xxx [· SQL N 字符] [· db=xxx]` 状态标签，默认启用、可点“×”关闭；发送时（`buildContextMessage()`）根据当前路由（`dashboard` / `sql` / `databases` / `events` / `ai-settings` / `chat` / `home`）生成一条 `role: system` 上下文消息，在 `sql` 页面额外携带当前 SQL Console 选中的数据库与正在编辑的 SQL（超过 2000 字符自动截断）包裹在 ```sql ``` 中一同提供给 LLM。上下文消息仅在 `send()` 时临时拼到 `messages[]` 头部，不会写入 `useCopilotSessionsStore` 会话历史，避免污染本地持久化。同步扩展 `useSqlConsoleStore`：新增 `currentSql` / `currentDb` / `setCurrent(db, sql)`，`SqlConsoleView.vue` 通过 `watch([targetDb, sql], ...)` 实时同步。

- **Copilot 权限选择器 + 写操作审批（M7、一阶段）**：CopilotDock 数据库选择下方新增权限模式指示器：`🔒 只读模式`（默认，绿色 NTag）点击后弹 NPopconfirm “切换为读写模式后，Copilot 可直接执行 INSERT / DELETE / CREATE MEASUREMENT 等写入语句。是否启用？” 二次确认，确认后变为 `⚠️ 读写模式`（黄色，可点 × 关闭回退）。偏好持久化在 localStorage `sndb.copilot.permission.v1`。请求载荷新增 `mode: "read-only" | "read-write"` 字段，服务端 `CopilotChatEndpointHandler` 仅当其严格等于 `read-write` 时才使 `effectiveCanWrite = canWrite && true`，其余取值（未提供 / `read-only` / 任意拼写）一律强制收紧为只读。该限制在服务端生效，即使客户端被绕过也无法越权调用 `execute_sql` 写入。服务端原有凭据权限仍是上限（读只凭据即使选 read-write 也仍然只读）。

- **Copilot 模型选择器（M8）**：服务端新增 `GET /v1/copilot/models` 端点，返回 `{ default, candidates[] }`（候选来自新增的 `CopilotChatOptions.AvailableModels` 配置，默认模型会被自动插入候选首位）；`CopilotChatRequest` 增加 `Model` 字段，`IChatProvider.CompleteAsync` 签名增加 `string? modelOverride` 参数，`OpenAICompatibleChatProvider` 优先使用 override、仅 fallback 到 `CopilotChatOptions.Model`；`CopilotAgentContext` 增加 `ModelOverride`，Planner / Answer / SQL Repair 三处 `CompleteAsync` 都会透传该 override。CopilotDock 数据库下方新增模型 NSelect（`filterable + tag` 允许自由输入，服务端默认模型标为「默认」，其他候选按原文列出），选择持久化在 localStorage `sndb.copilot.model.v1`，发送时仅在用户显式选中某个模型时才携带 `model` 字段，保持「不选择 = 走服务端默认」语义。
- **SQL Console 多标签页持久化 + Copilot SQL 落盘展示**：SQL Console 状态升级为 Pinia + localStorage 持久化的多标签页模型，每个选项卡独立保存目标库、SQL 文本、执行结果与摘要，切换页面或刷新后不会丢失；页面使用 `KeepAlive` 固化 SQL Console 实例。Copilot 流式返回的 `draft_sql` / `query_sql` / `execute_sql` 会自动创建新的 SQL Console 选项卡，其中查询/执行工具结果直接转换为 Console 结果展示，避免写入类 SQL 被重复执行。
- **SQL Console 语法高亮回归（M9）**：新增 `web/src/components/sonnetdb-dialect.ts` 定义 `SonnetDbSQL = SQLDialect.define({ ...StandardSQL.spec, keywords + 'measurement measurements tag field bucket show describe explain knn', types + 'vector float int bool string', builtin + 'knn time time_bucket forecast pid_compute pid_tune' })`；`SqlEditor.vue` 改为 `dialect: SonnetDbSQL`，这样 `MEASUREMENT` / `TAG` / `FIELD` / `VECTOR` 等关键字与 `knn` / `time_bucket` / `forecast` / `pid_compute` / `pid_tune` 等内置函数同时获得语法高亮（keyword/type/builtinName 三种 token）与 lang-sql 内置的关键字自动补全。
- **新手引导 / 提示词模板（M10）**：新增 `web/src/copilot/starters.ts` 定义 `COPILOT_STARTERS` 集合（建表 / 写入 / 聚合 / 向量 / 预测 / PID / 排查共 7 大分类、指定路由 `routeKeys` 过滤）与 `pickStarters(routeKey, max)` 函数；CopilotDock 空白态从原来的 3 条硬编码 `<li>` 重构为 `grid-template-columns: repeat(auto-fill, minmax(140px, 1fr))` 的 starter 卡片（分类胶囊 + 标题 + tooltip 说明），点击后 `prompt` 填入输入框，路由感知下会优先提示当前页面（`sql` / `databases` / `dashboard`）独有的模板。

### Docs
- 新增 `extensions/sonnetdb-vscode/` 规划骨架：包含 VS Code 扩展 `package.json` / `tsconfig.json` / `src/` 占位代码、专属 `ROADMAP.md`、`docs/architecture.md` 与 `docs/api-contract.md`，用于承接官方 `SonnetDB for VS Code` 实现。
- 主 `ROADMAP.md` 同步新增 **Milestone 18 — VS Code 数据库扩展（SonnetDB for VS Code）**，把原 Milestone 10 中模糊的 `#40` 占位需求细化为 `#99 ~ #108` 的可执行 PR 路线；首批推荐从 `#99 ~ #103` 先闭环“远程连接 + Explorer + SQL + 结果三视图”。

### Fixed
- **Copilot 建表 SQL 兜底回复**：当用户请求“建温度/湿度监测表”而 planner 只返回 `list_measurements`、最终回答模型又失败或返回空时，Agent 现在会自动补 `draft_sql` 并在 deterministic fallback 中输出可复制的 `CREATE MEASUREMENT` 代码块，不再回复“请结合返回的结构化结果继续确认或缩小问题范围”。
- **ADO.NET 提供程序支持 `USE <db>` 切库与 `SELECT current_database()`（M16）**：与 SQL Console 同步，新增 `SonnetDB.Data.Internal.SqlMetaCommand` 在 `SndbCommand` 真正发送 SQL 之前拦截元命令——远程模式下 `USE foo` 直接修改连接当前 `Database`（后续命令路由到 `/v1/db/foo/sql`，不做服务端校验，下一条业务 SQL 自然返回 404 / 403），`SELECT current_database()` / `SELECT database()` / `SHOW CURRENT_DATABASE` / `SHOW CURRENT DATABASE` 合成单行 `current_database` 结果集；嵌入式模式下 `current_database()` 返回当前 Data Source 路径，但 `USE` 因数据源等价于物理路径而抛 `NotSupportedException`。`SndbConnection.ChangeDatabase(name)` 不再无条件抛错，改为内部执行一条 `USE \`name\`` 复用同一路径，让用户在 `CREATE DATABASE foo` 之后可直接 `conn.ChangeDatabase("foo")` 或 `cmd.CommandText = "USE foo"; cmd.ExecuteNonQuery();` 在同一连接上继续 `CREATE MEASUREMENT … / INSERT …`。同步新增 4 个 ADO 单元测试覆盖嵌入式行为。
- **SQL Console 支持 `USE <db>` 切库与 `SELECT current_database()`（M16）**：SonnetDB 服务端按 URL 路径 `/v1/db/{db}/sql` 强绑定目标库，没有连接级 "current database" 状态。新增客户端元命令解析 `web/src/api/sqlMeta.ts` `parseSqlMetaCommand()`，在 SQL Console `run()` 循环里优先识别 ① `USE <db>`（MySQL / SQL Server 风格，`USE system` 切控制面、需 superuser，未知库返回 `database_not_found` 并列出可用库）、② `SELECT current_database()` / `SELECT database()` / `SHOW CURRENT_DATABASE` / `SHOW CURRENT DATABASE`（PostgreSQL / MySQL 风格混合兼容），合成本地结果集（`build​Client​Result​Set`）展示并自动同步「目标」选择器，不发请求到服务端，避免触发服务端 SqlParser 的「未知关键字」错误。SqlEditor 上方新增提示行说明这两个命令。
- **Docker 镜像随构建打包 `docs/` 与 `copilot/` 知识库素材（M16 / 知识库零启动）**：原 `Dockerfile` 仅把 `dotnet publish` 输出（含 jekyllnet 渲染好的 `wwwroot/help`）拷进运行时镜像，源码目录 `docs/` 与 `copilot/skills/` 不在 `/app` 下，导致默认 `Copilot.Docs.Roots = ["./docs", "./web/help", "./src/SonnetDB/wwwroot/help"]` 与 `Copilot.Skills.Root = "./copilot/skills"` 在容器里只能命中 `wwwroot/help`，且技能数始终为 0。现在在最终镜像额外 `COPY docs/ /app/docs/` 与 `COPY copilot/ /app/copilot/`，让 `CopilotDocsIngestionService` / `CopilotSkillsIngestionService` 在容器首次启动即可把 docs（含 `sql-reference.md` 等）与 6 条内置技能向量化进 `__copilot__.docs` / `__copilot__.skills`，无需挂载额外卷。
- **SQL Console 支持多语句逐条执行（M9 配套）**：服务端 `SqlParser` 一次只接受一条语句（`ExpectEndOfFile` 见到第二条会抛 `语句末尾存在多余内容`）。新增客户端 `web/src/api/sqlSplit.ts` `splitSqlStatements()`，按顶层 `;` 切分并正确忽略 `'...'` 字符串、`-- ...` 行注释和 `/* ... */` 块注释；`SqlConsoleView.vue` 改为顺序执行每条语句、渐进展示结果、任一条失败即停止后续执行，并在底部汇总 `共 N · 成功 X · 失败 Y · 合计 ts.ms`，解决执行 `demo.sql` 时 `[http_400] 语句末尾存在多余内容` 的问题。
- **SQL Console 结果三视图：文本 / 表格 / 图表（M16）**：每条语句结果改为独立卡片 `web/src/components/SqlResultPanel.vue`，header 显示 `#N` 状态徽标 + 单行 SQL 预览 + 元信息（行数 / 受影响 / 耗时）+ 三段式 `n-tabs`：① **文本** — 用 `marked` 把结果集渲染成 Markdown 表格（>100 行截断并提示），允许列值内嵌 SVG/HTML 直接渲染；② **表格** — 仍用 `n-data-table`；③ **图表** — 新组件 `SqlResultChart.vue` 自动检测时间列（按列名 `time`/`ts`/`timestamp` 或可解析为时间的列）+ 数值字段 + 字符串低基数 tag 列，按 `tag` 维度 group，原生 SVG 折线图（4×4 网格 + 5 段时间刻度 + 8 色配色），无任何 chart 第三方依赖。新增依赖 `marked@14.1.4`。
- **Copilot 浮窗调用 503 `chat.endpoint_invalid`**：Web Admin 的「Copilot 设置」保存的 `AiOptions`（国际版 / 国内版 + ApiKey + Model）原本只持久化到 `<DataRoot>/.system/ai-config.json`，并未同步到 `CopilotChatOptions` / `CopilotEmbeddingOptions` 单例，导致 `/v1/copilot/chat` 走的 `CopilotReadiness.EvaluateChat` 因 `Endpoint` 为空一直返回 `copilot_not_ready: chat.endpoint_invalid`。新增 `AiCopilotBridge.Apply`，在 `AiConfigStore` 构造时（启动加载已有配置）和 `PUT /v1/admin/ai-config` 保存后，立即把 provider URL（`https://sonnet.vip/v1/` 或 `https://ai.sonnetdb.com/v1/`）、ApiKey、Model 写入 `CopilotChatOptions`，并对 `provider=openai` 的 embedding 选项做空值回填。
- **SQL Console 移除内嵌 Copilot 面板（M16）**：删除 `SqlConsoleView.vue` 内的 AI 助手卡片（`生成 SQL` / `分析结果`）及相关脚本和样式，统一由全局右下角 CopilotDock 浮窗提供问答能力，避免双入口造成的体验割裂。
- **下线 `Copilot Chat` 顶层菜单（M16）**：从 `AppShell.vue` 主导航与 `router/index.ts` 移除 `chat` 路由，删除 `views/CopilotChatView.vue`；Copilot 已通过全局 CopilotDock 浮窗在所有页面随时可呼出。
- **CopilotDock 浮窗按窗口高度自适应（M16）**：把固定 `380×540` 调整为 `width: 420px; height: 61.8vh`（黄金比例），新增 `min-height: 480px` 与 `max-height: calc(100vh - 48px)` 边界，避免在大屏上显得过小、在小屏上溢出。
- 重写首页欢迎页为产品介绍页：去掉安装/帮助导向叙述，改为展示数据库简介、核心功能、产品形态和路线图对应能力，并保留进入后台入口。
- 修复 Production 模式下 `GET /admin/` 自重定向死循环（`ERR_TOO_MANY_REDIRECTS`）：原 `UseDefaultFiles({ RequestPath = "/admin" })` 未提供针对 `wwwroot/admin` 的 `FileProvider`，与 `MapGet("/admin")` / `MapFallbackToFile` 共同作用导致 Vite 构建产物无法被正确解析；现改为使用专用 `PhysicalFileProvider(wwwroot/admin)` 的 `UseStaticFiles`，并显式 `MapGet("/admin/")` 直接返回 `index.html`。
- 修复访问根路径 `/`（产品宣传首页）被 Bearer 认证中间件拦截返回 `401`：将 `/`、`/favicon.ico`、`/robots.txt` 加入匿名白名单，使 `MapHomePage()` 渲染的官网首页可直接访问。
- **Milestone 14 核查与修复**：核查 PR #63 ~ #69 的实际落地情况，确认 `SonnetDB.Copilot` 命名空间骨架、文档/技能摄入管线、MCP 工具增强、单轮/多轮 Agent 编排、Web Admin Chat Tab 与 nightly eval 套件均已落库；修复 `tests/SonnetDB.Tests/Copilot/copilot-eval-scenarios.json` 中 5 个场景（`metadata_show_measurements_sql` / `query_cpu_time_filter` / `analytics_changepoint_cusum` / `troubleshoot_explain_slow_query_log` / `troubleshoot_multiturn_sample_memory`）的 `answerSummary` 与 `expectedAnswerContains` 不一致问题，使 nightly eval 准确率从 86.11% 恢复到 100%（≥ 95% 阈值），citation 命中率 100%，p95 延迟 < 20 ms。同步把 `ROADMAP.md` 中 PR #66 / #67 / #69 与 Milestone 14 总览状态由 📋 回填为 ✅。

> 2026-09-05 核查补注：上条 M14 的 100% accuracy / citation 和 p95 < 20 ms 是历史测试报告中的编排回归结果；`CopilotEvalSuiteTests` 注入 `ScriptedChatProvider` 与 `KeywordEmbeddingProvider`，不能代表真实模型准确率、embedding 质量或在线推理延迟。其当时将 M14 总体标为完成的判断过宽；当前使用自研 `CopilotAgent`，Microsoft Agent Framework 未集成，真实模型与成本证据须按 M27 单独验收。

### Changed (品牌重命名 / Breaking)
- **项目重命名 `TSLite` → `SonnetDB`**：因与其他品牌名称冲突，全仓代码、命名空间、包名、Docker 镜像、文档、CI 脚本统一改名为 `SonnetDB`。
  - **NuGet 包 ID**：
    - `TSLite` → `SonnetDB.Core`（核心嵌入式引擎库）
    - `TSLite.Server` → `SonnetDB`（HTTP 服务端 / Docker 镜像主品牌）
    - `TSLite.Cli` → `SonnetDB.Cli`（dotnet tool）
    - `TSLite.Data` → `SonnetDB.Data`（ADO.NET 提供程序）
  - **dotnet tool 命令名**：`tslite` → `sndb`。
  - **ADO.NET 连接字符串 scheme**：`tslite://` / `tslite+http://` / `tslite+https://` → `sonnetdb://` / `sonnetdb+http://` / `sonnetdb+https://`。
  - **Docker 镜像**：`iotsharp/tslite-server` → `iotsharp/sonnetdb`，`ghcr.io/<owner>/tslite-server` → `ghcr.io/<owner>/sonnetdb`。
  - **环境变量前缀**：`TSLITE_*` → `SONNETDB_*`。
  - **Prometheus 指标前缀**：`tslite_*` → `sonnetdb_*`。
  - **解决方案 / 目录**：`TSLite.slnx` → `SonnetDB.slnx`；`src/TSLite*` 与 `tests/TSLite*` 整体迁移至 `src/SonnetDB*` 与 `tests/SonnetDB*`。
  - **服务端 Bundle / 安装包**：`tslite-server-full-<ver>-<rid>` → `sonnetdb-full-<ver>-<rid>`；启动脚本 `start-tslite-server.{cmd,sh}` → `start-sonnetdb.{cmd,sh}`；Linux 包路径 `/opt/tslite-server` → `/opt/sonnetdb`。
  - **代码命名空间**：`TSLite.*` → `SonnetDB.*`，`TSLite.Server.*` → `SonnetDB.*`（服务端去掉 `.Server` 子命名空间，与对外品牌一致）。
  - **保留**：核心类型名 `Tsdb` / `TsdbOptions` / `SndbConnection` 等不变（`Tsdb` 是通用时序库缩写而非品牌词）。
- **版本升级**：`0.1.0` → `1.0.0`。
- Server Admin UI 从“嵌入式资源”切换为官方 SPA 模式：开发期由 `SpaProxy` 自动启动 `web` 的 `npm run dev`，发布期改为 Static Web Assets 输出到 `/admin/`，以便更贴近 ASP.NET Core 推荐做法并减少 AOT 发布链路的额外定制。

> 历史 Copilot 计划已归档至[历史计划快照](docs/audits/historical-plans-from-changelog.md)。该计划不属于已交付清单；实际 provider、Agent 与验收边界见当前 ROADMAP 的 M27。

### Fixed
- 回填 `ROADMAP.md` 的 PR #60 状态与 Milestone 13 进度，统一为与现有代码、测试和 `docs/vector-search.md` 一致的已实现状态。
- 修正 `KnnExecutor` 在 HNSW + 时间范围过滤下“部分 ANN 命中后再精确补扫”时可能把同一点重复计入候选的问题，并补充 compaction 后 v6 内嵌索引 / legacy `.SDBVIDX` fallback 仍可加载使用的回归测试。
- 回填 `ROADMAP.md` 的 PR #62 状态与 Milestone 13 里程碑进度：默认 `10k / 100k` 向量基准与 README 实测结果已闭环，`1M` 长测与外部库同机对比保留为显式 / 环境可选的后续补数项，不再阻塞 Milestone 14。

### Added
- **PR #68 — Copilot 多轮对话 + SQL 自我纠错 + Web Admin Chat（Milestone 14 第六切片）**
  - `POST /v1/copilot/chat` / `/v1/copilot/chat/stream` 请求体升级为兼容 `message` 与 `messages[]` 两种模式；服务端会对最近对话按 token 预算裁剪，只保留最新且最相关的上下文进入 planner / answer prompt，支持多轮追问。
  - `CopilotAgent` 新增 `query_sql` 自我纠错回路：当模型规划出来的只读 SQL 在解析、校验或执行阶段失败时，会封装为 `SqlExecutionException` 回喂给模型改写，最多重试 3 轮，并通过 `tool_retry` 事件把错误与改写后的 SQL 流式回传前端。
  - Web Admin 新增 `Copilot Chat` 页面与路由，接入 `/v1/copilot/chat/stream` SSE 事件流，实时展示检索/工具执行过程，支持 skill/citation 折叠查看，以及将候选 SQL 一键发送到 `SQL Console` 并立即执行。
  - `SQL Console` 新增跨页面待执行 SQL 队列，允许 Chat 页把修正后的查询直接落到控制台执行；前端构建已随路由与页面一并通过。
  - 测试补齐：新增多轮 history 裁剪回归用例与 `query_sql` 自动重写回归用例，确认历史上下文按预算收敛、失败 SQL 会触发改写并闭环返回结果。

- **PR #67 — Copilot 单轮问答闭环（Milestone 14 第五切片）**
  - 新增内部 `CopilotAgent` 编排器：对单轮问题执行 docs/skills 召回、工具规划、只读工具执行与最终回答生成，串起 `IEmbeddingProvider`、`IChatProvider`、技能库、文档库与现有 MCP 只读工具语义。
  - 新增 HTTP 端点：`POST /v1/copilot/chat` 返回 `application/x-ndjson` 事件流，`POST /v1/copilot/chat/stream` 返回 `text/event-stream` SSE；统一输出 `start` / `retrieval` / `tool_call` / `tool_result` / `final` / `error` / `done` 事件，并在最终回答中附带 `citations`。
  - 新增 Bearer + 数据库级 `read` 权限校验：Copilot 聊天请求必须显式指定数据库，服务端会在进入编排前校验数据库名、数据库存在性以及当前凭据是否具备该库的 `read` 权限。
  - `Program.BuildApp(...)` 新增可选 `configureServices` 覆盖入口，便于在集成测试中注入 fake embedding/chat provider，对 Copilot 闭环做稳定的端到端验证。
  - 测试补齐：新增 Copilot chat 端到端用例，覆盖无 grant 返回 `403`、NDJSON 事件流返回、SSE 事件流返回三条关键链路。

- **PR #66 — MCP schema 工具增强 + 抽样 / explain（Milestone 14 第四切片）**
  - MCP 新增只读工具：`list_databases()`、`sample_rows(measurement, n=5)`、`explain_sql(sql)`；其中 `list_databases()` 会按 `GrantsStore` 与当前 Bearer 身份过滤为“当前可见数据库”集合。
  - 新增 `SonnetDbMcpSchemaCache`：对 `list_measurements` / `describe_measurement` 及对应 schema resources 统一提供 30 秒进程内缓存，降低 Copilot / MCP 高频探测时的重复 schema 开销。
  - 新增 `SonnetDbMcpExplainSqlService`：对只读 SQL 估算 `matchedSeriesCount`、`estimatedSegmentCount`、`estimatedBlockCount` 与 `estimatedScannedRows`，覆盖普通 `SELECT`、`SHOW MEASUREMENTS`、`DESCRIBE MEASUREMENT`，并支持 `forecast(...)` / `knn(...)` 表值函数的主扫描字段估算。
  - 端到端测试补齐：覆盖新工具返回结构、动态用户数据库可见性过滤，以及 schema 工具 30 秒缓存窗口内返回旧快照的行为。

- **PR #65 — Copilot 技能库 + 技能路由（Milestone 14 第三切片）**
  - 新增 `SkillSourceScanner` / `SkillFrontmatter` / `SkillRegistry` / `SkillSearchService`：扫描 `copilot/skills/*.md`（含 YAML frontmatter：`name`/`description`/`triggers`/`requires_tools`），把 `description + triggers` 嵌入到 `__copilot__.skills(name TAG, description, triggers, requires_tools, path, body, embedding VECTOR(384))`，并维护 `skills_state` 做 mtime/fingerprint 增量同步。
  - 新增 `CopilotSkillsIngestionService`（`BackgroundService`）：服务端启动时按 `Copilot.Skills.AutoIngestOnStartup` 自动执行一次技能库摄入，未就绪 / 未启用则安全跳过。
  - HTTP 端点：`POST /v1/copilot/skills/reload`（仅 server admin 触发增量摄入）；`POST /v1/copilot/skills/search` 走向量召回；`GET /v1/copilot/skills/list` 列出全部技能；`GET /v1/copilot/skills/{name}` 读取完整 markdown body；与 `/v1/copilot/docs/*` 一致地在 `Copilot.Embedding` 未就绪时返回 `503`。
  - MCP 工具：在 `/mcp/{db}` 上新增只读 `skill_search(query, k=5)` 与 `skill_load(name)`，结构化返回技能元数据与完整正文，方便 Agent 在对话开始时按问题召回少量技能并装配进上下文。
  - CLI：`sndb copilot skills [reload|list|show <name>]`，复用 `SONNETDB_COPILOT_URL` / `SONNETDB_COPILOT_TOKEN` 环境变量。
  - 首批入库 6 个技能：`query-aggregation` / `pid-control-tuning` / `forecast-howto` / `troubleshoot-slow-query` / `schema-design` / `bulk-ingest`，覆盖聚合、控制整定、预测、慢查询排查、Schema 设计与批量导入场景。

- **PR #64 — Copilot 文档摄入管线 + Knowledge 库（Milestone 14 第二切片）**
  - 新建系统级嵌入式 `Tsdb` 实例 `__copilot__`（按需创建），自动创建 `docs(time, source TAG, section TAG, title TAG, content STRING, embedding VECTOR(384))` measurement，dogfooding Milestone 13 的 `VECTOR(384)` + `knn(...)` 召回。
  - 新增 `DocsSourceScanner` / `DocsChunker` / `DocsIngestor` / `DocsSearchService`：扫描 `docs/*.md` 与 `web/admin/help/`，按 H2/H3 切片（≤ 800 字 / 100 字 overlap） → 嵌入 → 批量入库；`mtime` + 内容哈希做增量识别，避免重复嵌入。
  - 新增 `CopilotDocsIngestionService`（`BackgroundService`）：服务端启动时按 `Copilot.Docs.AutoIngestOnStartup` 自动执行一次摄入，未就绪 / 未启用则安全跳过。
  - HTTP 端点：`POST /v1/copilot/docs/ingest`（仅 server admin）触发增量摄入；`POST /v1/copilot/docs/search` 走向量召回返回命中片段；两者均在 `Copilot.Embedding` 未就绪时返回 `503`。
  - MCP 工具：在 `/mcp/{db}` 上新增只读 `docs_search(query, k=5)`，返回结构化的命中片段（source / section / title / content / score）。
  - CLI：`sndb copilot ingest [--root ./docs]... [--endpoint] [--token] [--force] [--dry-run]`，通过 `SONNETDB_COPILOT_URL` / `SONNETDB_COPILOT_TOKEN` 环境变量便捷接入远端服务端。

- **PR #63 — `SonnetDB.Copilot` 命名空间骨架 + Embedding/Chat Provider 抽象（Milestone 14 第一切片）**
  - 新增 `SonnetDB.Copilot` 命名空间（位于现有 `src/SonnetDB/` 项目内，不新建项目）：`IEmbeddingProvider` / `IChatProvider` 抽象、`CopilotOptions` 配置模型与 DI 装配。
  - 提供两类 provider 骨架：`LocalOnnxEmbeddingProvider`（默认 `bge-small-zh-v1.5`，模型缺失时返回未就绪而非抛异常）与 `OpenAICompatibleEmbeddingProvider` / `OpenAICompatibleChatProvider`（兼容 OpenAI / Azure OpenAI / DashScope / 智谱 / Moonshot / DeepSeek / SiliconFlow / 火山方舟等任意 OpenAI-compat 网关）。
  - 配置节 `SonnetDBServer__Copilot__*`：`Enabled` / `Embedding.Provider` / `Embedding.Endpoint` / `Embedding.ApiKey` / `Embedding.Model` / `Chat.*`，支持环境变量与 `appsettings.json` 同时配置。
  - `/healthz` 输出新增 `copilot` 子节，暴露 `enabled` / `embedding_ready` / `chat_ready` 与诊断原因，便于上层判定是否启用 Copilot 流程；不接入任何业务流程，纯骨架不破坏既有功能。

- **PR #62 — 向量召回基准骨架（Milestone 13 第七切片）**
  - `tests/SonnetDB.Benchmarks` 新增 `VectorRecallBenchmark`，覆盖 SonnetDB 自身 `384-dim` 向量的 brute-force Top10、HNSW Top10 与平均 `Recall@10`。
  - 默认档位为 `10k / 100k`；设置环境变量 `SONNETDB_VECTOR_BENCH_INCLUDE_1M=1` 后可额外启用 `1M` 数据集，避免日常基准意外占满内存。
  - `HnswVectorBlockIndex` 新增直接基于连续 `float32` 向量 payload 建图的重载，减少基准场景为构图额外复制 `DataPoint[]` 的内存开销。
  - `tests/SonnetDB.Benchmarks/README.md` 与根 `README.md` 已补回 `10k / 100k` 两档实测耗时；`1M` 档位保留为显式长测入口，`sqlite-vec` / `pgvector` 同机粗略对比在 README 中保留结果区，后续如具备环境可单独补数。

- **PR #61 — HNSW 段内 ANN sidecar 索引（Milestone 13 第六切片）**
  - `CREATE MEASUREMENT` 新增向量索引声明语法：`embedding FIELD VECTOR(384) WITH INDEX hnsw(m=16, ef=200)`；AST、`SqlParser`、`SqlExecutor`、`MeasurementSchema` 与 `MeasurementSchemaCodec` 已贯通，schema 文件格式升级到 v3 并兼容读取 v1/v2。
  - 新增 `VectorIndexDefinition` / `HnswVectorIndexOptions` 元数据模型，按列持久化 HNSW 参数 `m` / `ef`，并在 schema 校验阶段拒绝“非 VECTOR 列声明索引”或非法参数组合。
  - `SegmentWriter` 在 flush / compaction 写段时，按 schema 为声明了 HNSW 索引的 `VECTOR` block 生成 `.SDBVIDX` sidecar；`SegmentReader.Open` 会自动探测并加载 sidecar，段文件本体 `.SDBSEG` 保持不变。
  - 新增 `HnswVectorBlockIndex` 与 `SegmentVectorIndexFile`：实现段内 HNSW 图构建、sidecar 序列化/反序列化，以及 block 级 ANN 搜索入口。
  - `KnnExecutor` 现可在 `cosine` 度量下优先走 `.SDBVIDX` ANN；对部分时间窗会先做 ANN 候选过滤，不足以覆盖 Top-K 时再自动回退精确扫描，sidecar 缺失时仍保持 brute-force 行为。
  - 新增测试覆盖：`SqlParserVectorTests`、`MeasurementSchemaVectorTests`、`SqlExecutorVectorTests`、`VectorSegmentTests`、`SqlExecutorKnnTests`，覆盖索引语法解析、schema 持久化、sidecar 写读与 flush 后 KNN 查询闭环。

- **PR #60 — `knn(...)` 表值函数：brute-force KNN 向量检索（Milestone 13 第五切片）**
  - 新增内置表值函数 `knn(measurement, column, query_vector, k[, metric])`，支持 `SELECT * FROM knn(...)` SQL 语法，返回 `(time, distance, ...tags, ...fields)` 结果集。
  - 支持三种距离度量：`'cosine'`（余弦距离，默认）、`'l2'`（欧几里得距离）、`'inner_product'`（负内积），每种度量均支持多个别名（`cosine_distance` / `euclidean` / `dot` / `ip` 等）。
  - `KnnExecutor` 实现：段级时间窗剪枝 + `Parallel.ForEach` 多序列并行扫描（MemTable + 全量 Segment），扫描结果按距离升序排列后取前 k 条。
  - `WHERE` 子句同时支持 tag 等值过滤与 `time` 时间范围过滤，缩减召回范围。
  - 新增文档 `docs/vector-search.md`，包含 Schema 设计、写入、查询语法、度量说明与嵌入式 API C# 示例。
  - 新增测试 `SqlExecutorKnnTests`（14 个用例），覆盖余弦/L2/负内积排序、k 大于数据量、tag 过滤、时间过滤、多字段输出、错误边界等路径。

- **PR #59 — 向量距离函数与 `centroid` 聚合（Milestone 13 第四切片）**
  - `FunctionRegistry` 新增 4 个向量标量函数：`cosine_distance(a,b)`、`l2_distance(a,b)`、`inner_product(a,b)`、`vector_norm(a)`；均支持 `VECTOR(dim)` 列与 SQL 向量字面量 `[v0, v1, ...]` 直接混合计算，并在参数为 `NULL`、零向量或维度不一致时给出明确错误。
  - `SqlLexer` / `SqlParser` 新增 PostgreSQL/pgvector 兼容运算符 `<=>`、`<->`、`<#>`，语法层会分别重写为 `cosine_distance(...)`、`l2_distance(...)`、`inner_product(...)` 函数调用，因此现有 SELECT 标量函数执行路径与列依赖分析无需额外分支即可复用。
  - 扩展聚合新增 `centroid(vec)`：按维度累计向量和并在最终阶段输出 `float[]` 均值；`IAggregateAccumulator` / `SelectExecutor` 同步扩展了向量累加入口，使 `GROUP BY time(...)` 与跨桶/跨段合并场景都能复用同一套聚合实现。
  - 测试补齐：新增/扩展 `FunctionRegistryTests`、`ExtendedAggregateAccumulatorTests`、`SqlLexerTests`、`SqlParserVectorTests`、`SqlExecutorVectorTests`，覆盖函数注册、pgvector 运算符改写、标量执行、`centroid` 聚合与维度校验路径。

- **InfluxDB 2.x 数据准确性对照测试项目**
  - 新增独立测试项目 `tests/SonnetDB.Accuracy.Tests`，通过 Testcontainers 启动 InfluxDB 2.7 容器，同时在进程内启动 SonnetDB Server，向两侧写入同一批 Line Protocol 测试数据。
  - 新增准确性对照 fixture：自动创建 SonnetDB 数据库与 measurement schema，复用同一份 LP 数据分别写入 SonnetDB 批量入库端点与 InfluxDB `/api/v2/write`，用于验证服务端真实入库与查询链路，而不是仅验证进程内对象。
  - 新增结果归一化器与对照矩阵，覆盖 `SHOW MEASUREMENTS`、多 series 原始投影、稀疏字段查询、`LIMIT/OFFSET`、`sum/avg/min/max/count/first/last`、`GROUP BY time(...)` 桶聚合以及第二 measurement 的原始查询。
  - 对照测试会把 SonnetDB SQL NDJSON 结果与 InfluxDB Flux 结果统一归一化后逐行比较，减少时间格式、浮点表示或列序差异带来的噪音；当 Docker / InfluxDB 环境不可用时按 skip 处理，不阻塞普通无容器环境下的基础测试运行。

- **PR #58 c — `BlockEncoding.VectorRaw` + Segment Header v3 升级（Milestone 13 第三切片）**
  - `BlockEncoding` 新增 `VectorRaw = 4`：与 `DeltaValue` 互斥的值编码标志；payload 为 `count × dim × float32(LE)` 紧凑序列，dim 由列 schema 携带（不进 BlockHeader）。
  - `TsdbMagic.SegmentFormatVersion`：`2 → 3`（**写入版本**），新增 `TsdbMagic.SupportedSegmentFormatVersions = [2, 3]` 用于读时兼容。BlockHeader 大小保持 72B 不变，旧 v2 段（无 Vector 列）仍可被新 Reader 直接打开。
  - `SegmentHeader` / `SegmentFooter` 新增 `IsCompatibleForRead()`：magic 校验 + version ∈ `SupportedSegmentFormatVersions`；`IsValid()` 仍是严格相等（写时校验）。
  - `SegmentReader.Open` 改为按 `IsCompatibleForRead` 校验头/尾，错误信息升级为提示当前支持的版本列表；`ValueEncoding` 抽取支持 `VectorRaw` 标志位。
  - `SegmentWriter.WriteOneBlock`：`FieldType.Vector` 走 V1 raw 路径并在 `BlockHeader.Encoding` 上置 `VectorRaw`，禁止与 V2 `DeltaValue` 同时使用。
  - `ValuePayloadCodec`：新增 `MeasureVectorPayload` / `WriteVectorPayload`；要求同一 block 内所有点维度一致（不一致抛 `InvalidOperationException`）。
  - `BlockDecoder`：`ReadValues` / `ReadValuesRange` 新增 `FieldType.Vector` 分支；按 `bytesPerPoint = totalBytes/count`、`dim = bytesPerPoint/4` 反序列化，每点拷贝出独立 `float[]` 以 `FieldValue.FromVector` 包装。
  - 测试：新增 `VectorSegmentTests` × 8（payload 度量 / LE 字节序 / 维度不一致抛异常 / 段 round-trip / 版本常量 / `IsCompatibleForRead` v2+v3 接受、其它拒绝 / Reader 接受 v2 段）；`SonnetDB.Core.Tests` 1554 全绿，`SonnetDB.Tests` 116、`SonnetDB.Accuracy.Tests` 8 全绿。
  - 兼容性：v2 段仅可被 v3 Reader 读取（v2 Reader 无法读 v3）。Vector 列至此 WAL → MemTable → Segment 持久化链路打通；查询 / 索引 / KNN 相关能力将由 PR #59 起继续。

- **PR #58 b — Schema VECTOR(dim) 列声明 + SQL `[v0, v1, ...]` 字面量（Milestone 13 第二切片）**
  - `MeasurementColumn` 新增可选 `int? VectorDimension`；`MeasurementSchema.Create` 校验：Vector 列必须 `Field` 角色且 `dim > 0`，非 Vector 列禁止携带 dim。
  - SQL 解析层：新增 `KeywordVector` / `LeftBracket` / `RightBracket` token；`SqlLexer` 识别 `vector` 关键字与 `[` / `]` 标点；`SqlDataType.Vector` + `ColumnDefinition.VectorDimension` + `VectorLiteralExpression(IReadOnlyList<double>)` AST。
  - `SqlParser`：`ParseColumnDefinition` 支持 `<col> FIELD VECTOR(N)` 语法（N ∈ [1, int32]）；`ParsePrimary` 支持 `[a, b, c]` 字面量（拒绝空 `[]`，组件接受可选 `+/-` 前缀的整数 / 浮点）。
  - `SqlExecutor`：`CREATE MEASUREMENT` 透传 dim 至 schema；`INSERT` 在 Vector 列上要求 `[..]` 字面量并校验维度匹配（错误信息含 `维度不匹配`），非 Vector 列拒绝向量字面量；`DESCRIBE` 输出 `vector(N)`；`MapType` 新增 `SqlDataType.Vector → FieldType.Vector` 映射。
  - `MeasurementSchemaCodec` v1 → **v2**：在 `Vector` 列的类型字节后追加 4 字节 little-endian `dim`；读取兼容 v1（仅当文件中无 Vector 列时），v1 文件含 Vector 列则抛 `InvalidDataException`。`measurements.tslschema` 文件版本号字段同步升级为 2。
  - `MemTableSeries.ComputeEstimatedBytes`：新增 `FieldType.Vector` 分支，按 `16 + dim*4` 估算每点常驻字节。
  - `SelectExecutor.UnboxFieldValue`：Vector → `float[]`，便于 `SELECT embedding` 直接吐出数组。
  - 测试：新增 26 个测试（`SqlParserVectorTests` × 12、`SqlExecutorVectorTests` × 7、`MeasurementSchemaVectorTests` × 7）；总计 **1662 测试全部通过**。
  - 兼容性：仍未升级 `FileHeader.Version`（Segment 编码层尚未涉及）；Vector 数据落 WAL → MemTable → flush 走现有路径，落 segment 暂不支持，将随 PR #58 c 引入 `BlockEncoding.VectorRaw` + `FileHeader v3`。
- **PR #58 a — `FieldValue.Vector` 与 WAL `WritePoint` Vector 编解码（Milestone 13 第一切片）**
  - 新增 `FieldType.Vector = 5`：定长 32 位浮点向量，dim 由后续 schema 声明，WAL 内按 `dim(4) + dim×float32(LE)` 排布。
  - `FieldValue` 新增 `Vector` 分支：`FromVector(ReadOnlyMemory<float>)` / `FromVector(float[])` 工厂；`AsVector()` / `VectorDimension` 取值；`Equals` 全量序列比较，`GetHashCode` 采样首/中/末三分量；`ToString` 形如 `vector(N)[a,b,...]`，超过 8 维自动截断。
  - `WalPayloadCodec`：`MeasureWritePoint` / `WriteWritePointPayload` / `ReadWritePointPayload` 三处支持 `FieldType.Vector`；新增 `ReadVectorPayload`，对 `valueLen != 4 + dim*4`、`dim < 1` 等坏 payload 抛 `InvalidDataException`。
  - 兼容性：`FileHeader.Version` 暂未升级（本切片只动 WAL `WritePoint` 序列化层；Schema/Segment 层尚未声明 Vector 列，因此现有 segment 文件格式完全不变）。Schema VECTOR(dim) 列、`BlockEncoding.VectorRaw`、SQL 字面量与 `FileHeader` v3 升级将随 PR #58 后续切片合入。
  - 测试：`FieldValueTests` 新增 13 个用例（Vector round-trip / Equals / dim 不等 / `AsDouble` 抛异常 / `TryGetNumeric=false` / ToString 截断）；`WalPayloadCodecTests` 新增 4 个 dim variant（1 / 3 / 8 / 384）的 WritePoint round-trip。全量回归 1520 + 116 = 1636 通过。

- **服务端内建 MCP（Model Context Protocol）只读入口**
  - `src/SonnetDB` 新增基于官方 `ModelContextProtocol.AspNetCore` 1.2.0 的 Streamable HTTP MCP 端点：`/mcp/{db}`。启用 `Stateless=true`，关闭 legacy SSE，`ConfigureSessionOptions` 会把当前数据库名写入 `ServerInstructions`，明确这是绑定到单个数据库的只读 SonnetDB MCP 会话。
  - 新增 MCP 上下文解析与预校验：所有 `/mcp/{db}` 请求在进入 MCP SDK 前先复用现有 `TsdbRegistry` 校验数据库名与存在性；非法库名返回 `400 bad_request`，不存在数据库返回 `404 db_not_found`，并把当前 `db` 与 `Tsdb` 实例缓存到 `HttpContext.Items` 供 tools/resources 读取。
  - 新增只读 MCP tools：
    - `query_sql(sql, maxRows)`：仅允许 `SELECT` / `SHOW MEASUREMENTS` / `SHOW TABLES` / `DESCRIBE [MEASUREMENT]`；对 `SELECT` 在 AST 层自动补/收紧分页，并采用“多抓 1 行”检测 `truncated`。
    - `list_measurements(maxRows)`：返回当前数据库 measurement 名列表。
    - `describe_measurement(name)`：返回指定 measurement 的 tag/field schema。
  - 新增 MCP resources：
    - `sonnetdb://schema/measurements`
    - `sonnetdb://schema/measurement/{name}`
    - `sonnetdb://stats/database`
    三个资源统一返回 `application/json` 文本，分别暴露 measurement 列表、单 measurement schema 与当前数据库统计（measurement 数、segment 数、memtable 点数、checkpoint LSN 等）。
  - 新增 `src/SonnetDB/Mcp/` 实现层：结果 DTO、`JsonElementValue` 转换、只读 SQL 裁剪逻辑、`CallToolResult`/`TextResourceContents` 构造与基于 `IHttpContextAccessor` 的数据库绑定上下文解析。
  - 测试：新增 `tests/SonnetDB.Tests/McpEndToEndTests.cs`，通过真实 Kestrel + `McpClient` 覆盖 `list_tools`、`query_sql` 自动截断、`list_measurements`、`describe_measurement`、`list_resources` / `list_resource_templates` / `read_resource` 的端到端路径。
  - 权限模型补齐：`/mcp/{db}` 与其余数据库作用域 HTTP 入口现在会把“动态用户 token”映射到 `GrantsStore` 的数据库级 `Read/Write/Admin` 权限；静态 `ServerOptions.Tokens` 仍保持全局 role 语义。无 grant 的用户访问数据库作用域 MCP / SQL / schema / bulk 端点将返回 `403 forbidden`，superuser 保持全放行。
  - 数据库可见性补齐：`GET /v1/db` 与数据面 SQL 中的 `SHOW DATABASES` 现在会按当前请求可见范围过滤数据库列表。普通动态用户只会看到自己有 `Read/Write/Admin`（含 `*` 通配）权限的数据库；静态全局 token 与 superuser 继续看到全部数据库。
  - AI 权限补齐：`POST /v1/ai/chat` 在 `mode=sql_gen` 且携带 `db` 时，现在会先校验数据库名、数据库存在性与当前请求对该数据库的 `Read` 权限，再拼接 schema 系统提示词；未授权用户无法再借助 AI SQL 生成读取其他数据库的 measurement/column 元数据。
  - SSE 权限补齐：`GET /v1/events` 对动态用户 token 的数据库相关事件现在按 grant 实时过滤。`db` 与 `slow_query` 只会下发当前用户对该数据库具备 `Read` 以上权限的事件，控制面慢查询（`__control`）对普通动态用户隐藏；`metrics` 事件中的 `databases` 与 `perDatabaseSegments` 也会裁剪为当前用户可见数据库集合，避免从实时事件流泄露未授权数据库名。
  - 控制面自服务权限补齐：普通动态用户现在可以通过 `/v1/sql` 或有权访问的 `/v1/db/{db}/sql` 执行 `SHOW GRANTS` / `SHOW TOKENS` / `ISSUE TOKEN FOR <self>` / `REVOKE TOKEN '<self-token-id>'` 等“只操作自己”的控制面语句；对其他用户的授权或 token 执行查询、签发、吊销将返回 `403 forbidden`。`SHOW USERS`、用户管理、授权管理、数据库管理等仍保持 admin-only。

- **元数据 SQL：`SHOW MEASUREMENTS` / `SHOW TABLES` / `DESCRIBE [MEASUREMENT] <name>`**
  - 新增 AST 节点 `ShowMeasurementsStatement` 与 `DescribeMeasurementStatement`；`SqlLexer` 增加关键字 `MEASUREMENTS` / `TABLES` / `DESCRIBE` / `DESC`；`SqlParser` 在 `SHOW` 分支识别 `MEASUREMENTS` 和兼容别名 `TABLES`，并新增顶层 `DESCRIBE` / `DESC` 入口（关键字 `MEASUREMENT` 可省略）。
  - `SqlExecutor` 新增 `ShowMeasurements(Tsdb)` 与 `DescribeMeasurement(Tsdb, name)` 执行路径，统一返回 `SelectExecutionResult`：`SHOW MEASUREMENTS` / `SHOW TABLES` 输出单列 `name`（按字典序升序）；`DESCRIBE` 输出三列 `column_name` / `column_type`（`tag` / `field`）/ `data_type`（`float64` / `int64` / `boolean` / `string`），按 schema 声明顺序返回。
  - 引入 `SHOW TABLES` / `DESC` 兼容别名以适配 DBeaver / DataGrip / 通用 ADO.NET schema 浏览器。
  - 测试：新增 `tests/SonnetDB.Core.Tests/Sql/SqlExecutorMetadataTests.cs`，11 个用例覆盖空库 / 字典序排序 / `SHOW TABLES` 等价 / `DESCRIBE`+`DESC` 等价 / 关键字 `MEASUREMENT` 可省略 / 不存在 measurement 抛 `InvalidOperationException` / Parser AST 形状校验。

- **数据面分页：`OFFSET/FETCH` + `LIMIT` 兼容语法**
  - `SELECT` 新增可选分页子句：支持 SQL 标准风格 `OFFSET <n> [ROW|ROWS] FETCH FIRST|NEXT <m> ROW|ROWS ONLY`，以及兼容风格 `LIMIT <m> [OFFSET <n>]`。
  - AST `SelectStatement` 增加 `Pagination` 参数，执行层在最终结果集统一应用分页切片，覆盖 raw / aggregate / TVF 三条路径。
  - 为避免与聚合函数 `first(...)` 冲突，`FIRST/NEXT/ROW/ROWS/ONLY` 按普通标识符词法处理，仅在 `FETCH` 子句按上下文识别。
  - 测试：补充 parser / lexer / executor 用例，覆盖 `LIMIT`、`OFFSET`、`FETCH` 语义以及越界 offset 返回空集。

- **Milestone 12 — PR #57：函数族基准 + README 函数支持矩阵**
  - 新增 `tests/SonnetDB.Benchmarks/Benchmarks/FunctionBenchmark.cs`：以 50,000 个数据点为样本，对 PR #50 ~ #56 引入的窗口 / 聚合 / TVF 函数族走完整 SqlParser → SqlExecutor 流水线的端到端基准。覆盖 SonnetDB 自身的 `derivative` / `moving_average` / `ewma` / `holt_winters` / `anomaly(zscore)` / `p99` / `distinct_count` / `forecast(linear)` / `forecast(holt_winters)` 9 项基线，以及 InfluxDB Flux（`derivative` / `movingAverage` / `holtWinters` / `quantile(method:"estimate_tdigest")`）与 TDengine REST（`DERIVATIVE` / `MAVG` / `PERCENTILE`）的等价语义对照；外部数据库不可用时按 `[SKIP]` 提示，不阻塞 SonnetDB 基线运行。
  - `README.md` 新增「支持的 SQL 函数」矩阵章节：按 PR 引入顺序枚举 PR #50 ~ #56 全部内置函数（聚合 / 标量 / 窗口 / TVF）共 50+ 项，并列出 InfluxDB / Timescale / TDengine / Prometheus 的对标函数与备注；同步在 `README.en.md` 增加「Built-in SQL functions」英文版矩阵。
  - 矩阵章节同时指向 `docs/extending-functions.md`（UDF 注册）与 `tests/SonnetDB.Benchmarks/Benchmarks/FunctionBenchmark.cs`（性能对照），使函数体系的「能做什么 / 怎么扩展 / 性能如何」三条线索从 README 一处可达。

- **Milestone 12 — PR #56：Tier 5 用户自定义函数（UDF）注册 API**
  - 新增公开类型 `SonnetDB.Query.Functions.UserFunctionRegistry`：按 `Tsdb` 实例隔离的 UDF 注册表，挂在新增的 `Tsdb.Functions` 属性上。提供 `RegisterScalar(name, evaluator, min, max)` / `RegisterScalar(IScalarFunction)` / `RegisterAggregate(IAggregateFunction)` / `RegisterWindow(IWindowFunction)` / `RegisterTableValuedFunction(name, executor)` 五条注册路径，以及 `Unregister(name)` 与 `TryGet*` 查询。聚合 UDF 强制 `LegacyAggregator == null`（仅内置 7 个聚合可用 legacy fast-path）；TVF UDF 不允许使用保留名 `forecast`。
  - 通过 `AsyncLocal<UserFunctionRegistry?>` + `UserFunctionRegistry.AmbientScope` 提供查询作用域 ambient；`SqlExecutor.ExecuteSelect` 在执行前 `EnterScope(tsdb.Functions)`、退出时自动恢复，确保多 `Tsdb` 实例并发执行 SQL 时互不可见。
  - `FunctionRegistry.GetFunctionKind` / `TryGetAggregate` / `TryGetScalar` / `TryGetWindow` 全部改为「优先查 ambient UDF，未命中再回退内置」，保持 PR #50~#55 的所有内置函数行为零变化。`TableValuedFunctionExecutor.Execute` 同样优先匹配用户 TVF，再走内置 `forecast` 路由。
  - 新增 `TsdbOptions.AllowUserFunctions` 选项（默认 `true`，嵌入式启用）；`SonnetDB.Hosting.TsdbRegistry` 在两条 `Tsdb.Open` 调用上将其设为 `false`，从而 Server / HTTP 模式默认禁用 UDF 以保证 AOT 兼容（`Functions.IsEnabled == false`，所有 `Register*` 抛 `InvalidOperationException`）。
  - 新增 `docs/extending-functions.md`：覆盖标量 / 聚合 / 窗口 / TVF 四类 UDF 的注册示例（`Func` 委托形态 + `IScalarFunction` 等接口形态）、`Merge` 可结合性约束、跨实例隔离、`forecast` 保留名、Server 模式禁用策略与 UDF 不覆盖的功能边界。
  - 新增测试：`tests/SonnetDB.Core.Tests/Query/Functions/UserFunctionRegistryTests.cs`（9 项端到端）：委托标量 UDF 解析、UDF 覆盖同名内置（`abs` 路径）、聚合 UDF 通过 `IAggregateAccumulator` 接入 SELECT、TVF UDF 路由 + 行集构造、TVF 保留名 `forecast` 拒绝、`Unregister` 后查询失败、`AllowUserFunctions=false` 时 Register 全部抛、ambient 在两个 `Tsdb` 实例间隔离、聚合 UDF 设置 `LegacyAggregator` 时拒绝注册。

- **Milestone 12 — PR #55：Tier 4 Forecast TVF + 异常 / 变点检测**
  - 新增公开类型 `SonnetDB.Query.Functions.Forecasting.TimeSeriesForecaster`：纯 C#、零外部依赖的预测库 API，提供 `Forecast(long[] timestampsMs, double[] values, int horizon, ForecastAlgorithm algorithm, int season = 0)`，输出 `ForecastPoint[] (TimestampMs, Value, Lower, Upper)`；支持 **线性最小二乘外推** 与 **Holt / Holt-Winters 三次指数平滑（加性季节）**，置信区间按残差 RMSE × $z_{0.975}$ × $\sqrt{h+1}$ 给出。
  - 新增 SQL 表值函数 `forecast(measurement, field, horizon, 'algo'[, season])`：在 `FROM` 子句中作为数据源，按 measurement / FIELD 拉取历史数据并按 series 维度独立预测；输出列 `(time, value, lower, upper, ...tag_columns)`。Parser 在 `ParseSelect()` 中识别 `FROM <ident>(` 调用形态并填充 `SelectStatement.TableValuedFunction`；新增 `TableValuedFunctionExecutor` 路由器：校验参数（measurement / FIELD 标识符、`horizon` 正整数字面量、`'linear'` / `'holt_winters'` / `'hw'` 算法、可选 `season` 非负整数），复用 `WhereClauseDecomposer` 处理标签过滤，按 series 调用 `TimeSeriesForecaster.Forecast` 并按预测点落行。PR #129.2 起支持直接列投影。
  - 新增 SQL 窗口函数 `anomaly(field, 'zscore' | 'mad' | 'iqr', threshold)`：行流→行流，输出 `bool?`；`zscore` 用样本标准差（N−1），`mad` 用 1.4826 × MAD 鲁棒尺度，`iqr` 用 0.25 / 0.75 分位线性插值 + Tukey $k$ 倍 IQR 围栏。`null` 输入透传 `null`。
  - 新增 SQL 窗口函数 `changepoint(field, 'cusum', threshold[, drift])`：双边 CUSUM 累积和变点检测；用前 `max(5, n/4)` 个非空样本估计基线均值与样本标准差以避免变点本身污染参考；触发后累积器复位以探测下一个变点。`drift` 默认 `0.5`。
  - `FunctionRegistry` 注册 `anomaly` 与 `changepoint` 为 Tier 3 窗口函数，与 PR #53 框架共享 `IWindowFunction` / `IWindowEvaluator` 协议。
  - 新增 `docs/forecast.md`：完整覆盖 SQL 语法、输出列、算法公式、嵌入式库 API、局限与与 UDF 扩展的关系。
  - 新增测试：`tests/SonnetDB.Core.Tests/Query/Functions/TimeSeriesForecasterTests.cs`（7 项算法层单测：线性外推方向 / 截距、Holt-Winters 季节恢复、置信区间宽度、退化输入、空 / 单点输入边界）+ `AnomalyChangepointFunctionTests.cs`（8 项窗口函数单测：z-score / MAD / IQR 各方法、`null` 输入、常数序列、CUSUM 检测均值漂移并触发后复位）+ `tests/SonnetDB.Core.Tests/Sql/SqlExecutorForecastTests.cs`（SQL 端到端：线性 / HW 预测列形态、与 WHERE 标签过滤组合、参数校验、forecast 列投影、anomaly / changepoint 输出 bool 列）。

- **Milestone 12 — PR #54：Tier 4 PID 控制律内置函数 + 自动整定库 API**
  - 新增公开类型 `SonnetDB.Query.Functions.Control.PidController`：纯 C# 离散 PID 状态机，提供 `Update(timestampMs, processVariable, setpoint)`、`Snapshot()` / `Restore(...)` 与 `Reset()`；首行只输出比例项（无 dt 参考），$\Delta t \le 0$ 时跳过 I/D 更新避免发散；状态结构 `PidControllerSnapshot { Integral, PrevError, PrevTimeMs, HasHistory }`。
  - 新增 SQL 行级窗口函数 `pid_series(field, setpoint, kp, ki, kd)`（`IWindowFunction`）：与 PR #53 窗口算子框架共享 `Compute(long[] timestamps, FieldValue?[] values)` 协议，按 series 独立维护控制器实例；`null` 输入透传 `null` 输出且不推进状态。
  - 新增 SQL 聚合函数 `pid(field, setpoint, kp, ki, kd)`（`IAggregateFunction` + `PidAccumulator`）：与 `GROUP BY time(...)` 组合时桶内逐行推进、桶尾输出最终 $u(t)$；`Merge` 取 `PrevTimeMs` 更晚的段以支持跨段拼接；拒绝 `pid(*)`、错误参数个数、非 FIELD 列、字符串字段。
  - `IAggregateAccumulator` 增加默认接口方法 `Add(long timestampMs, double value) => Add(value);`；`SelectExecutor.AggSlot.Update` 改为通过该重载传递时间戳，对既有 Welford / TDigest / HLL / Histogram 累加器**零行为变化**。
  - `FunctionRegistry` 注册 `pid` 为 `Aggregate`、`pid_series` 为 `Window`，纳入既有 `GetFunctionKind` / `TryGetAggregate` / `TryGetWindow` 路由。
  - 既有 `PidParameterEstimator`（Sundaresan & Krishnaswamy 35%/85% 两点法识别 FOPDT 模型 + Ziegler-Nichols / Cohen-Coon / Skogestad IMC 三种整定规则）保持库级 API；同时新增 SQL 聚合函数 `pid_estimate(field, method, step_magnitude, initial_fraction, final_fraction, imc_lambda)`，对结果集中 (time, value) 样本调用辨识 + 整定，输出 JSON `{"kp":..,"ki":..,"kd":..}`。`method` 接受字符串字面量 `'zn'` / `'cc'` / `'imc'` 或 NULL（默认 ZN），数值参数允许 NULL 取默认值。`docs/pid-control.md` 提供端到端工作流（采集 → 离线整定 → SQL 回测 → 控制回写 → 监控）与 SQL/库 API 双形态示例。
  - 新增测试：`tests/SonnetDB.Core.Tests/Query/Functions/Control/PidControllerTests.cs`（14 项控制器与累加器/求值器单元测试）+ `tests/SonnetDB.Core.Tests/Sql/SqlExecutorPidFunctionTests.cs`（10 项 SQL 端到端：行级输出、桶级最终 u、与时间/字段投影混合、负增益字面量、参数校验、Tag/字符串列拒绝、控制回写两步流程）。

- **Milestone 12 — PR #53：Tier 3 窗口算子框架（17 个窗口函数）**
  - 新增公共契约 `IWindowFunction` / `IWindowEvaluator`：window 函数为「行流→行流」的逐序列算子，由 `CreateEvaluator(call, schema)` 工厂在查询计划阶段完成参数解析与字段绑定，运行阶段调用 `Compute(long[] timestamps, FieldValue?[] values) → object?[]` 输出与输入等长的列结果。
  - 落地 17 个窗口函数（位于 `src/SonnetDB/Query/Functions/Window/`），按语义分为 5 组：
    - **差分类**：`difference` / `delta`（当前 − 上一行）；`increase`（仅保留正差，counter 重置返回 `null`）；`derivative` / `non_negative_derivative` / `rate` / `irate`（按时间归一化的瞬时变化率，可指定单位 `1s` / `100ms` 等）。
    - **累计类**：`cumulative_sum`（运行总和，首个有效值前为 `null`）；`integral(field [, unit])`（梯形面积，默认按秒积分）。
    - **平滑类**：`moving_average(field, n)`（N 点滑动平均，前 N−1 行返回 `null`）；`ewma(field, alpha)`（指数加权移动平均，校验 `alpha ∈ (0, 1]`）；`holt_winters(field, alpha, beta)`（加性 Holt 双指数平滑，无季节性，校验 `alpha`、`beta` ∈ `(0, 1]`）。
    - **缺失值处理**：`fill(field, value)`（数值字面量填充 `null`，支持 `-1` 等带负号字面量）；`locf(field)`（last observation carried forward）；`interpolate(field)`（两遍扫描线性插值，前导/尾随 `null` 保持 `null`）。
    - **状态分析**：`state_changes(field)`（基于 `FieldValue.Equals` 的状态切换计数，支持 string/bool）；`state_duration(field)`（当前状态持续毫秒数，状态切换时归零）。
  - `FunctionRegistry` 新增 `WindowFunctions` 集合 + `TryGetWindow(name, out function)` API；`GetFunctionKind` 新增 `FunctionKind.Window` 分支。
  - `SelectExecutor` 新增 `ProjectionKind.Window` 投影类别：`ClassifyProjections` 将 `Window` 函数路由到该类别；`ExecuteRaw` 在每个 series 内构造 `long[] timestamps` + 与之对齐的 `FieldValue?[]`，预计算所有 window evaluator 输出后按行下标注入结果，与 `time` / `tag` / `field` / `scalar` 投影任意组合。窗口函数与聚合函数互斥（沿用既有 `_ → 内部错误` 拒绝路径）。
  - 新增 `tests/SonnetDB.Core.Tests/Query/Functions/WindowFunctionTests.cs`（35 项，覆盖各 evaluator 的纯算法语义、`Welford` / counter 重置 / 梯形积分 / 线性插值 / EWMA 递推 / 状态分析等基线，外加全部 17 个函数的 `FunctionRegistry` 注册校验）与 `tests/SonnetDB.Core.Tests/Sql/SqlExecutorWindowFunctionTests.cs`（21 项 SQL 端到端，覆盖各窗口函数典型用法、与 `time` / `field` / `tag` 投影混合、与聚合互斥、参数错误、字符串/Tag 字段拒绝等）。

- **Milestone 12 — PR #52：Tier 2 扩展聚合（可合并累加器）**
  - 新增 9 个扩展聚合函数：`stddev` / `variance` / `spread` / `mode` / `median` / `percentile(field, q)` / `p50` / `p90` / `p95` / `p99` / `tdigest_agg` / `distinct_count` / `histogram(field, bin_width)`，全部走新增的 `IAggregateAccumulator` 路径，与既有 7 个 legacy 聚合并存、零性能回归。
  - 新增公共契约 `IAggregateAccumulator`（`Count` / `Add` / `Merge` / `Finalize`）与 `IAggregateFunction.CreateAccumulator(call, schema)`（默认实现返回 `null`），用于让扩展聚合声明跨段、跨桶、跨序列可合并的中间状态。
  - 新增三类核心累加器算法（位于 `src/SonnetDB/Query/Functions/Aggregates/`）：
    - **Welford** 在线方差/标准差，附 Chan-Golub-LeVeque 并行合并公式；样本数 < 2 时 `stddev` 返回 `null`。
    - **TDigest** 简化的 Ben Haim merging digest（compression=200、k(q) ≈ 4q(1−q)/δ），支持 `Add` / `Merge` / `Quantile` / `Serialize` / `ToJson`，作为 `percentile` / `pXX` / `median` / `tdigest_agg` 的统一后端。
    - **HyperLogLog**（precision 14、16384 寄存器、AlphaMM 修正、小基数 linear-counting），作为 `distinct_count` 的统一后端，使用 `System.IO.Hashing.XxHash64` 哈希双精度浮点的 IEEE 字节序。
  - `SelectExecutor.ExecuteAggregate` 重构为 `AggSlot` 分发：legacy 聚合走原 `BucketState`，扩展聚合走 `IAggregateAccumulator`；`AggSpec` 新增 `IsExtended` / `IsCountStar` 与字段解析支持，`first` / `last` 多序列保护仅作用于 legacy 路径。
  - `histogram(field, bin_width)` 输出 `{"[lo,hi)":n,...}` 格式 JSON，跨段合并时校验 `bin_width` 一致性；`tdigest_agg` 输出可后续合并的 JSON 状态串。
  - 新增 `tests/SonnetDB.Core.Tests/Query/Functions/ExtendedAggregateAccumulatorTests.cs`（14 项单元测试，覆盖 Welford / TDigest / HLL 合并一致性、空集 / 单点边界、参数校验）与 `tests/SonnetDB.Core.Tests/Sql/SqlExecutorExtendedAggregateTests.cs`（13 项 SQL 端到端测试，覆盖单序列、多序列合并、`GROUP BY time(...)` 桶聚合、混合 legacy + 扩展聚合、参数错误）。

- **PR #39：Docker 镜像自动发布**
  - 新增 `.github/workflows/docker-publish.yml`：在 `main` 分支相关文件变更、`v*` 标签或手动触发时，自动构建 `src/SonnetDB/Dockerfile` 并推送镜像。
  - 目标镜像仓库：
    - Docker Hub：`iotsharp/sonnetdb`
    - GHCR：`ghcr.io/<owner>/sonnetdb`
  - 标签策略覆盖 `latest`、`edge`、`vX.Y.Z`、`X.Y` 与 `sha-<commit>`，并写入 OCI labels。
  - 工作流接入 `docker/setup-buildx-action`、`docker/metadata-action`、`docker/login-action`、`docker/build-push-action`，并启用 GitHub Actions Docker layer cache。
- 新增 `docs/releases/docker-image.md`，补齐 Docker 镜像的启动方式、标签策略、自动发布触发条件和仓库 Secrets 要求。

- **PR #37b：GitHub Pages 文档自动发布**
  - 新增 `.github/workflows/docs-pages.yml`：在 `main` 分支文档变更或手动触发时，自动执行 `dotnet tool restore` + `jekyllnet build`，并通过 GitHub Pages 官方 Actions 上传和部署静态文档站点。
  - Pages 构建阶段会基于仓库名动态注入文档基址（例如 `/SonnetDB`），因此无需维护第二套独立文档源码。

- **Milestone 12 — PR #51：Tier 1 标量函数 + SQL 函数调用扩展**
  - 新增公共类型 `ISqlFunction` / `IScalarFunction` / `FunctionKind`，并扩展 `FunctionRegistry` 的 `TryGetScalar(name, out function)` / `ScalarFunctions` / `GetFunctionKind(name)` API；`IAggregateFunction` 改为继承 `ISqlFunction` 共享 `Name` 契约。
  - 落地内置标量函数：`abs` / `round(value[, digits])` / `sqrt` / `log(value[, base])` / `coalesce(...)`；统一在 `BuiltInScalarFunction` 中做参数个数校验，并通过 `RequireDouble` 兼容 byte/int/long/float/double/decimal。
  - `SelectExecutor` 新增 `ProjectionKind.Scalar` 投影路径，支持在 SELECT 投影中嵌套调用、与算术表达式混用，并自动汇总标量函数引用的字段名加入 `QueryFieldValues` 的字段集；为 `Window` / `TableValued` 类别预留诊断分支，便于 PR #53 / #55 接入。
  - **AST 重构（破坏性内部 API）**：`SelectStatement.GroupByTime: TimeBucketSpec?` 替换为 `SelectStatement.GroupBy: IReadOnlyList<SqlExpression>`，由 `SelectExecutor.ResolveGroupByTime` 在执行阶段把 `time(duration)` 形式归约为 `TimeBucketSpec`；`SqlParser.ParseGroupByList` / `ParseGroupByExpression` 取代原 `ParseGroupByTime`，仍在 parser 阶段拒绝 `time(0)` 之类的非法 duration。无 GROUP BY 时 `GroupBy` 为空集合（不再为 `null`）。
  - 新增/扩展测试：`tests/SonnetDB.Core.Tests/Sql/SqlExecutorSelectTests.cs` 覆盖标量函数投影、`coalesce` 跨字段时间轴、别名、未知函数与参数个数错误；`SqlParserTests` 覆盖 `GROUP BY time(...)` 解析为 `FunctionCallExpression` 与标量函数调用解析；`FunctionRegistryTests` 增加 `TryGetScalar` / `GetFunctionKind` 与标量函数求值校验。
  - `docs/sql-reference.md` 补充 SELECT 投影中标量函数的支持范围、嵌套规则与 `coalesce` 时间轴说明。

- **Milestone 12 — 函数注册表基础设施（`FunctionRegistry`）**：新增 `src/SonnetDB/Query/Functions/` 目录，承载 Tier 1~3 函数扩展（PR #51~#57）所需的注册与解析骨架，零第三方依赖。
  - 新增公共类型 `FunctionRegistry`（静态注册表）+ `IAggregateFunction`（命名 / SQL 调用语法校验 / `LegacyAggregator` 桥接），通过 `TryGetAggregate(name, out function)` 与 `GetAggregate(Aggregator)` 双向查找内置 7 个聚合（count/sum/min/max/avg/first/last）。
  - `BuiltInAggregateFunction.ResolveFieldName` 集中实现 `*` 形式校验（仅 `count(*)` 允许）、参数个数校验、列存在性、Tag/Field 角色与 String 类型限制。
  - 重构 `SelectExecutor`：移除内部硬编码 `_aggregateFunctions` HashSet 与 `switch` 解析逻辑，改为统一走 `FunctionRegistry`；保留现有高性能 `Aggregator` 枚举执行路径，本 PR 不影响查询性能。
  - 新增 `tests/SonnetDB.Core.Tests/Query/Functions/FunctionRegistryTests.cs`：8 项单元测试覆盖大小写不敏感、未知函数、`count(*)` 接受 / 其他函数拒绝 `*`、Tag 列拒绝、String 字段拒绝、合法字段返回原列名等关键分支。
  - 新增 `tests/SonnetDB.Core.Tests/Sql/SqlExecutorSelectTests.cs::Select_SumStar_Throws` / `Select_AggregateLookup_IsCaseInsensitive`，验证 SelectExecutor 经注册表后的对外行为不变。

- **Milestone 12 — PID 参数估算（`PidParameterEstimator`）**：新增 `src/SonnetDB/Query/Functions/Control/` 目录，提供从历史阶跃响应时序数据自动推算 PID 控制器参数的纯 C# 实现，零第三方依赖。
  - `PidTuningMethod` 枚举：`ZieglerNichols`（Ziegler-Nichols 阶跃响应法）/ `CohenCoon`（Cohen-Coon 法）/ `Imc`（Skogestad SIMC/IMC 法）。
  - `PidParameters` 记录类型：封装 `Kp`（比例增益）、`Ki`（积分增益）、`Kd`（微分增益）。
  - `PidEstimationOptions`：可选配置项，包含整定方法、阶跃幅度 `StepMagnitude`、IMC 闭环时间常数 `ImcLambda`、初始/末尾稳态窗口比例。
  - `PidParameterEstimator.Estimate`：接受 `IReadOnlyList<(long TimestampMs, double Value)>` 或 `IReadOnlyList<DataPoint>`，采用 **Sundaresan & Krishnaswamy 35%/85% 两点法** 辨识一阶纯滞后（FOPDT）模型（K、τ、θ），再按所选整定规则输出 Kp/Ki/Kd。
  - 新增 `tests/SonnetDB.Core.Tests/Query/Functions/Control/PidParameterEstimatorTests.cs`：20 项单元测试，覆盖三种整定方法的数值精度验证、非零基线、DataPoint 重载、Int64 字段值、边界/错误校验、负向阶跃及三种方法正参数一致性。

### Changed
- `README.md` 与发布文档新增预编译 Docker 镜像入口，支持直接通过 `docker run iotsharp/sonnetdb:latest` 启动服务端。
- `ROADMAP.md` 中 `PR #39` 状态更新为已完成，Milestone 9 随之闭环。

- 文档站点模板与交叉链接支持双部署模式：
  - `docs/_config.yml` 新增 `docs_baseurl`、`app_link_url`、`app_link_text`、`home_primary_url`、`home_primary_text` 配置项。
  - `docs/_layouts/default.html` 与多篇文档内的站内链接改为基于配置拼接，默认继续服务于 `SonnetDB` 的 `/help/`，同时兼容 GitHub Pages 的仓库子路径。
- 将 `ROADMAP.md` 中的 `PR #37b` 标记为已完成，并更新 Milestone 9 的当前推进顺序。

- 核查并修正 `ROADMAP.md` 中的 Milestone 9 状态：
  - 补记已在仓库落地但路线图遗漏的 `PR #36`（`SonnetDB` Docker 基准环境与 `ServerBenchmark`）。
  - 将 `PR #38` 调整为已完成：仓库已具备 `eng/release.ps1`、`.github/workflows/publish.yml`、NuGet 包元数据、SDK / Server Bundle 与 `msi` / `deb` / `rpm` 打包能力。
  - 将原 `PR #37` 拆分为 `#37a`（已完成：文档重写、JekyllNet `/help` 站点、README 与发布文档核对）和 `#37b`（现已完成：GitHub Pages 自动发布流水线），使路线图状态与当前代码一致。

- **PR #50：查询元数据快路径 — Format v2 + 跨桶融合 + MemTable 增量聚合 + ExecuteMany 共享快照**（**Breaking 段文件格式变更**）
  - **Format v2**（破坏性，不兼容旧 segment 文件）：`BlockHeader` 由 64 B 扩展到 72 B，将 `AggregateMinBits` / `AggregateMaxBits` 由 `int`（4 B）升级为 `AggregateMin` / `AggregateMax`（`double`，8 B），覆盖 Float64 任意值与 ±2^53 内的 Int64。新增 `TsdbMagic.SegmentFormatVersion = 2` 常量与 `TsdbMagic.FormatVersion = 1`（用于 FileHeader/WAL/Catalog）解耦；`SegmentHeader.IsValid` 与 `SegmentReader` 拒绝读取 v1 段文件并给出明确升级提示。`SegmentWriter.TryBuildAggregateMetadata` 不再因窄类型截断而放弃 `HasMinMax`，对所有数值字段一律写入 `HasSumCount | HasMinMax`。`SegmentReader` 删除 `DecodeAggregateBoundary` 私有桥接，直接读取 8 B `double`。**升级路径：删除 `*.SDBSEG` 后启动可由 WAL 重放重新生成 v2 段；混用旧版本数据将抛 `SegmentCorruptedException`。**
  - **跨桶 block 元数据下推**：`QueryEngine` 新增 `CanFuseDeltaTimestampInline` + `FuseDeltaBlockToGlobal` / `FuseDeltaBlockToBuckets` 融合内联路径——对 `(delta-of-delta 时间戳 + 原始数值)` 的数值 block，仅租用一份 `ArrayPool<long>` 解码时间戳后内联走 `ReadRawNumericValue` + `AddValueToBucket`，避免 `BlockDecoder.DecodeRange` 物化 `DataPoint[]`。大 block 跨桶聚合分配显著下降。
  - **MemTable 运行期 sum/min/max**：`MemTableSeries` 在 `Append` 阶段对 Float64/Int64/Boolean 增量维护 `_numericSum / _numericMin / _numericMax`，新增 `HasNumericAggregates` / `NumericSum` / `NumericMin` / `NumericMax` / `TryGetNumericAggregateSnapshot` 公共 API；`QueryEngine.ExecuteAggregateFast` 在范围全覆盖且单桶或全局聚合时直接合并 MemTable 元数据，跳过对 `ReadOnlyMemory<DataPoint>` 切片的逐点扫描。`AggregateState` 新增 `AddMemTableAggregate` 合并入口。
  - **ExecuteMany 共享快照**：`QueryEngine.ExecuteMany` 不再每个 series 重建 `BuildReaderMap` 与重新读取 `_segments.Index`，改为外层一次构建并通过新的私有重载 `ExecuteAggregateFast(query, index, readers)` 透传；`ShouldUsePointAggregatePath` 仍按 series 分流到 `Execute` 慢路径以保证 First/Last 与墓碑场景行为不变。
  - **测试**：新增 `MemTableSeriesAggregateTests` 5 项（数值字段增量聚合、String 字段不维护、空序列）+ `QueryEngineV2OptimizationTests` 7 项（Int64 极值/高精度 Float64 round-trip、跨桶大 block 聚合一致性、MemTable 全/部分覆盖路径、`ExecuteMany` 与单次 `Execute` 一致性）；同步更新 `BlockHeaderTests` / `FormatSizesTests` / `SegmentHeaderTests` / `SegmentFooterTests` 以反映 72 B BlockHeader 与 `SegmentFormatVersion = 2`。`SonnetDB.Core.Tests` 1263/1263 + `SonnetDB.Tests` 97/97 通过。

### Changed
- 完善 Block 聚合元数据语义并修复 Min/Max 精度漏洞，新增桶聚合元数据快路径：
  - `BlockHeader.AggregateFlags` 由「0/1 单值」语义改为按位组合：`HasSumCount = 0x01`（sum/count 可信）、`HasMinMax = 0x02`（min/max 无损）。旧 v1 segment 写入的 `1` 自动等价于「仅 HasSumCount」，min/max 不会被误用，无需 Format 版本号变更（仍为 1）。
  - `SegmentWriter` 在 Float64 时只在 `(float)min == min && (float)max == max` 时才置 `HasMinMax`，避免向 `Min`/`Max` 查询返回 float 截断后的错误值；Int64 在 min/max 落入 int32 范围时置 `HasMinMax`；sum/count 仍始终写入。
  - `SegmentReader` 解析两个独立标志，`BlockDescriptor` 新增 `HasAggregateSumCount` / `HasAggregateMinMax`（旧 `HasAggregateMetadata` 改为派生属性）。
  - `QueryEngine.CanUseAggregateMetadata` 改为按聚合函数挑选元数据：`Count` 始终命中（用 `descriptor.Count`），`Sum/Avg` 需 `HasAggregateSumCount`，`Min/Max` 需 `HasAggregateMinMax`。
  - 桶聚合（`AddSegmentBlocksToBuckets`）新增元数据快路径：当 block 完整落入查询范围且 `[MinTimestamp, MaxTimestamp]` 整体落在同一个桶内时，直接合并元数据到桶状态，避免读取 payload 与逐点扫描，写入密集场景对 `SUM/COUNT/AVG/MIN/MAX` 的桶聚合直接获益。
  - 测试：新增 4 项 `QueryEngineAggregateTests`（Float64 非可表示数的 Min/Max 精度、Sum/Avg/Count 仍走快路径、桶 SUM/MIN/MAX 命中元数据、跨桶 block 回退扫描），1 项 `BlockHeaderTests` 校验旧 `flags == 1` 的兼容映射；`SonnetDB.Core.Tests` 1250/1250 + `SonnetDB.Tests` 97/97 通过。
  - 不修改文件二进制格式与 `FileHeader.Version`。

### Added
- 新增跨平台 C# 基准运行入口：`eng/benchmarks/start-benchmark-env/start-benchmark-env.csproj` 负责 Docker Compose 构建、启动、健康等待与停止，`eng/benchmarks/run-benchmarks/run-benchmarks.csproj` 负责调用环境入口并运行 BenchmarkDotNet；根 README 改为嵌入 `docs/assets/benchmark-summary.svg` 基准摘要图，后续刷新性能数字时无需反复改根 README 表格。
- **PR #49：基准刷新 + 对外对比（写入快路径专题收尾）**
  - 「写入：100 万点（单序列）」表新增 PR #47 三条服务端 LP/JSON/Bulk 端点的实测数据（1.10–1.20 s / 34–71 MB），把服务端 vs 嵌入式的写入差距从 ~33.8×（SQL Batch 路径）收敛展示到 **~1.77–1.93×**（绕开 SQL parser 路径）。
  - 「嵌入式 vs. SonnetDB 同机对比」表把写入行拆分为 SQL Batch + LP + JSON + Bulk 四行，标注各自的额外开销与主要来源。
  - 「批量入库快路径」段补充 PR #48 `?flush=` 三档位语义表（None / Async / Sync 与适用场景）。
  - 「小结」最后一行更新为反映 PR #47/#48 后的写入收敛事实。
  - **新增 `InsertBenchmark.TDengine_InsertSchemaless_1M`**：走 TDengine InfluxDB-compat 端点 `POST /influxdb/v1/write?precision=ms`，按 100,000 行/批切片避开 taosadapter 16 MB body 上限；`TDengineRestClient` 配套新增 `WriteLineProtocolAsync(db, lp, precision)`。配合 `bench_insert_schemaless` 隔离 DB，与已有显式 STable 路径互不干扰。
  - **全量重跑 24 个基准**（i9-13900HX / .NET 10.0.6 / Docker WSL2，~20 分钟）真实数字写入 `tests/SonnetDB.Benchmarks/README.md`：
    - InsertBenchmark（1M 点）：SonnetDB **544.9 ms / 530 MB**（1.00×）、SQLite 811.4 ms / 465 MB（1.49×）、InfluxDB 5,222 ms / 1,457 MB（**9.58×**）、TDengine REST INSERT 44,137 ms / 156 MB（81×）、**TDengine schemaless LP 996 ms / 61 MB（1.83×）**〔同库 schemaless 路径比 REST INSERT 子表路径快 44× / 分配缩到 39%〕
    - QueryBenchmark：SonnetDB **6.71 ms / 18.7 MB**，比 InfluxDB 快 61×、比 SQLite 快 6.6×
    - AggregateBenchmark：SonnetDB **42.3 ms / 39.4 MB**，比 SQLite 快 7.7×
    - CompactionBenchmark：SonnetDB **16.3 ms / 28.3 MB**
    - **ServerInsertBenchmark（重建镜像后首次全部跑通）**：SQL Batch `19.80 s / 655 MB`、LP `1.293 s / 52 MB`、JSON `1.352 s / 71 MB`、Bulk VALUES `1.120 s / 34 MB`——PR #47 三端点仍稳定在「秒级 1M 点 + ≤ 80 MB」区间，比 SQL Batch 快 **15–7×** / 分配缩到 **5–11%**、仅比嵌入式多 **~2.0–2.5×**额外开销
    - ServerQuery `88.4 ms / 16 MB`、ServerAggregate `88.8 ms / 2.5 MB`、BulkIngestBenchmark 四个路径均保持 PR #46/#47 后「百万点 / ~110–200 ms / 130–220 MB」区间。

### Changed
- 优化查询热路径：未压缩时间戳 block 的范围裁剪改为直接在 little-endian byte payload 上二分，避免为整块 block 分配 `long[]`；数值聚合在无墓碑且非 `First/Last` 时下推到 Segment block payload 扫描，使用 `ReadOnlySpan<byte>` + `CollectionsMarshal.GetValueRefOrAddDefault` 直接更新桶状态，减少 `DataPoint[]` 物化与托管堆分配。

### Added
- **PR #48：批量入库端点 Flush 三档位 `?flush=false|true|async`（写入快路径专题，第 3/4 步）**
  - `Tsdb` 新增 `public void SignalFlush()`：仅向 `BackgroundFlushWorker` 发信号后立即返回，不阻塞调用方；若未启用后台 Flush（`BackgroundFlush.Enabled = false`），降级为同步 `FlushNow()`，保证 `flush=async` 始终具备「最终一致」语义。
  - `BulkIngestor` 新增 `enum BulkFlushMode { None, Async, Sync }` 与新主重载 `Ingest(tsdb, reader, errorPolicy, BulkFlushMode flushMode)`；旧 `Ingest(..., bool flushOnComplete)` 重载保留，转发到新重载（`true → Sync` / `false → None`），向后兼容现有调用方。
  - `BulkIngestEndpointHandler.ParseFlush` 重写为 `BulkFlushMode` 解析：`async` → Async；`true|sync|yes|1` → Sync；其它（含缺省、`false`）→ None。三个端点 `/lp` / `/json` / `/bulk` 共享同一档位。
  - ADO 嵌入式 `EmbeddedConnectionImpl.ExecuteBulk` 新增 `internal static BulkFlushMode ParseFlushMode(string?)` 并改用 `BulkFlushMode` 透传到引擎；远程 `RemoteConnectionImpl.ExecuteBulk` 自然透传 `flush=` query string，无需改动。
  - 默认行为（缺省 `flush` 参数）维持 PR #45 / #47 一致：`BulkFlushMode.None`，仅入 MemTable + WAL，最快路径。
  - 测试：`SonnetDB.Core.Tests` 新增 3 项（Sync/None/Async 三档位的 BulkIngestor 直测，包括 async 不阻塞 < 1s 验证）；`SonnetDB.Tests` 新增 2 项（端点 `?flush=async` / `?flush=false`）。全量回归 `SonnetDB.Core.Tests` 1241/1241 + `SonnetDB.Tests` 97/97 通过。
  - 不修改文件二进制格式与 `FileHeader.Version`。

### Added
- **PR #37：JekyllNet 帮助文档站点 + 镜像内 `/help` 帮助中心**
  - 新增 `.config/dotnet-tools.json`，将 `JekyllNet 0.2.5` 固化为仓库本地工具，统一 `dotnet tool restore` 与 `dotnet tool run jekyllnet build` 的构建入口。
  - `docs/` 现在作为 JekyllNet 站点源目录，新增帮助中心布局、样式以及 `index / getting-started / data-model / sql-reference / file-format / releases` 页面。
  - `SonnetDB` 新增匿名可访问的 `/help/*` 静态文档端点，运行时从 `wwwroot/help` 提供帮助中心，并支持目录式 URL。
  - `src/SonnetDB/Dockerfile` 新增文档构建步骤，在镜像构建时执行 `jekyllnet build --source docs --destination src/SonnetDB/wwwroot/help`，再随 `dotnet publish` 一起打包进镜像。
  - `web/admin` 首页与管理后台头部新增“帮助”入口，直接打开 `/help/`。

### Changed
- 文档：重写仓库 `README.md`，新增 `README.en.md`，并把根 README 调整为当前项目真实形态说明，移除“单文件持久化”与过时目标 API 描述。
- 文档：扩展 `docs/` 帮助中心，新增嵌入式 API、ADO.NET、CLI、批量写入、架构总览页面，并重写首页、快速开始、数据模型、SQL 参考与文件布局说明。
- 文档：修正文档中的实际磁盘布局描述，补充 `measurements.tslschema`、`.system/` 首次安装文件和 `/help` 内置文档站点说明。
- **PR #47：服务端 + Reader 零拷贝（写入快路径专题，第 2/4 步）**
  - `BulkIngestEndpointHandler.ReadAllAsync` 改为返回 `(byte[] Buffer, int Length)`，统一走 `ArrayPool<byte>.Shared.Rent`：已知 `Content-Length` 时按精确长度租借，未知长度则 4KB 起步翻倍扩容；`finally` 必归还，避免大 payload 直入 LOH。
  - `JsonPointsReader` 字段重构为 `ReadOnlyMemory<byte> _utf8Memory + byte[]? _pooledBuffer`：`(ReadOnlyMemory<byte>)` ctor 直接零拷贝持有 caller buffer（原先需 `utf8Json.ToArray()` 全量复制）；`(string)` ctor 走 `ArrayPool<byte>.Shared.Rent` 转码后 Dispose 归还。
  - `BulkIngestEndpointHandler.HandleAsync` JSON 路径直接构造 `new ReadOnlyMemory<byte>(bodyBuffer, 0, bodyLength)` 喂 reader，杜绝 string 中转；LP 路径新增 `CreateLineProtocolReader`，从 `ArrayPool<char>.Shared.Rent` 借出精确长度 char buffer + `Encoding.UTF8.GetChars` 解码后包成 `ReadOnlyMemory<char>`；BulkValues 路径走 `Encoding.UTF8.GetString(buffer, 0, length)` 精确长度版本。`finally` 顺序：dispose reader → return char buffer → return byte buffer。
  - 服务端 `Program.cs` 三个批量端点（`/lp` / `/json` / `/bulk`）追加 `WithMetadata(new DisableRequestSizeLimitAttribute())`，移除 Kestrel 默认 30MB request body 上限，使 1M-row payload 真正可达。
  - 旁路修复：`HelpDocsEndpoints.cs` 集合表达式三元运算符歧义（CS0173）改写为 `new[] { ... }`。
  - **基准复测**（`ServerInsertBenchmark`，1 000 000 点 / i9-13900HX / .NET 10 / Release / `bench-admin-token` 本地 dotnet run）：

    | 路径 | Mean | Allocated | vs PR #45 baseline |
    |------|-----:|----------:|--------------------|
    | `POST /sql/batch` 单行（baseline，PR #45 = 21.36s） | **5.09 s** | 668 MB | **−76% Mean**（受益于 PR #46 真批量） |
    | `POST /v1/db/{db}/measurements/{m}/lp` 1M 点 | **1.20 s** | **52 MB** | **~17.8× faster** / **alloc −92%** |
    | `POST /v1/db/{db}/measurements/{m}/json` 1M 点 | **1.20 s** | **71 MB** | **~17.8× faster** / **alloc −89%** |
    | `POST /v1/db/{db}/measurements/{m}/bulk` 1M 点 | **1.10 s** | **34 MB** | **~19.4× faster** / **alloc −95%** |

    服务端三端点首次进入「秒级 1M 点 + ≤ 80 MB 分配」区间，远超 Milestone 11 既定目标（≥ 700k pts/s）。嵌入式 `BulkIngestBenchmark` 复测无显著变化（该基准不经服务端 handler，对 PR #47 不敏感，符合预期）。
  - 测试：`SonnetDB.Core.Tests` 1237/1238 通过（`BackgroundFlushIntegrationTests.ContinuousWrite_5000Points_AutoFlushesMultipleSegments` 1 处时序敏感 flake，独立跑 2/2 通过）；`SonnetDB.Tests` 95/95 通过。
  - 兼容性说明：`LineProtocolReader` / `BulkValuesReader` / `SchemaBoundBulkValuesReader` 接口保持 `ReadOnlyMemory<char>` / `string`（未做接口级 byte 化），如未来需要彻底 byte-path（避免 LP/Bulk UTF-8→char 一次解码），将作为独立小 PR 推进。

- **PR #46：`Tsdb.WriteMany` 真批量快路径（写入快路径专题，第 1/4 步）**
  - 新增 `Tsdb.WriteMany(ReadOnlySpan<Point>)` 重载：整批写入只获取 **一次** `_writeSync` 锁、批末仅调用 **一次** `BackgroundFlushWorker.Signal`，消除原 `foreach Write(point)` 退化批量在 N 次入锁/信号上的开销。
  - 旧 `Tsdb.WriteMany(IEnumerable<Point>)` 自动嗅探 `Point[]` / `List<Point>` / `ArraySegment<Point>` 并下沉到 span 重载（`CollectionsMarshal.AsSpan` 零拷贝），其它枚举回退到逐点写入；行为对调用方完全透明。
  - WAL 记录格式与 `_walSet` 锁结构 **保持不变**，新旧库双向兼容（`FileHeader.Version` / `TsdbMagic.FormatVersion` 不升）。
  - `BulkIngestor.FlushBatch` 改走 `buffer.AsSpan(0, count)`（替代 `new ArraySegment<Point>(buffer, 0, count)`），消除新重载导致的歧义并直达 span 快路径；`/v1/db/{db}/measurements/{m}/{lp|json|bulk}` 端点与 `RemoteConnectionImpl` 自动受益。
  - **基准复测**（`BulkIngestBenchmark`，100k 点 / i9-13900HX / .NET 10）：

    | 路径 | Mean | Allocated | vs SQL baseline |
    |------|-----:|----------:|-----------------|
    | SQL INSERT VALUES（baseline） | 170 ms | 224 MB | — |
    | TableDirect Line Protocol | 178 ms | 131 MB | **alloc −42%** |
    | TableDirect JSON | 176 ms | 167 MB | alloc −25% |
    | TableDirect Bulk VALUES | 159 ms | 130 MB | **alloc −42%** |

    Mean 与 PR #45 持平（瓶颈仍在 reader 解析 + Catalog/WAL field-level 层），但 **托管堆分配降低 42–58%**（少 N 次 lock entry + iterator boxing），降低 GC 压力。
  - 测试：`SonnetDB.Core.Tests` 1238/1238 通过，`SonnetDB.Tests` 89/90 通过（1 处 pre-existing `UserStore` 并发 IO race 与本 PR 无关）。

### Added
- **SonnetDB 首次安装向导 + 产品首页（未编号）**
  - 新增 `GET /v1/setup/status` 与 `POST /v1/setup/initialize`，当 `<DataRoot>/.system` 未完成初始化时返回首次安装状态，并支持一次性写入服务器 ID、组织、管理员用户名密码与初始 Bearer Token。
  - 新增 `installation.json` 持久化安装元数据；初始 Bearer Token 作为受管用户 token 持久化，后续与现有登录/鉴权体系保持一致。
  - `web/admin` 重构为“产品首页 / 首次安装向导 / 管理后台”三段式路由，新增品牌 logo、帮助导航和首次安装引导页，避免空 `.system` 时直接落到不可登录后台。
- **PR #45：批量入库基准（绕开 SQL 解析的快路径，第 4/4 步）**
  - 新增 `tests/SonnetDB.Benchmarks/Benchmarks/BulkIngestBenchmark.cs`：嵌入式 100 000 点 4 路对比。
    - **baseline**：`SQL INSERT VALUES`（100 行/条 × 1 000 条）走 `SqlParser` + `SqlExecutor.ExecuteInsert` 流水线。
    - **TableDirect Line Protocol**：`LineProtocolReader` + `BulkIngestor.Ingest`。
    - **TableDirect JSON**：`JsonPointsReader` + `BulkIngestor.Ingest`。
    - **TableDirect Bulk VALUES**：`SchemaBoundBulkValuesReader` + `BulkIngestor.Ingest`。
    - 初次运行结果：同量级吞吐，但 LP / Bulk VALUES 内存分配仅为 baseline 的 ~58%，JSON ~75%。
  - `tests/SonnetDB.Benchmarks/Benchmarks/ServerBenchmark.cs::ServerInsertBenchmark` 增加 3 个服务端基准方法：
    - `SonnetDBServer_BulkLp_1M` / `SonnetDBServer_BulkJson_1M` / `SonnetDBServer_BulkValues_1M`，均走 PR #44 新端点 `POST /v1/db/{db}/measurements/{m}/{lp\|json\|bulk}?flush=true`，与原有的 `SonnetDBServer_Insert_1M`（`/sql/batch` SQL 路径）同列对比。
    - LP / JSON / Bulk payload 均在 `[GlobalSetup]` 预生成，不计入迭代耗时；value 统一 `:F4` 格式化避免被 parser 当作 Int64、与 measurement schema (`Float64`) 类型冲突。
  - README 「性能基准」节新增「批量入库快路径（PR #45）」子节：收录 4 路嵌入式对比表与服务端复现命令。

- **PR #44：服务端三端点 + 远程 ADO 客户端打通批量入库（绕开 SQL 解析的快路径，第 3/4 步）**
  - 服务端新增三个端点：
    - `POST /v1/db/{db}/measurements/{m}/lp` —— Line Protocol（`text/plain`）。
    - `POST /v1/db/{db}/measurements/{m}/json` —— JSON points（`application/json`）。
    - `POST /v1/db/{db}/measurements/{m}/bulk` —— `INSERT INTO ... VALUES (...)` 快路径。
    - 三个端点共用 `BulkIngestEndpointHandler`：对 `BulkValues` 走 `SchemaBoundBulkValuesReader`（按 measurement schema 解析列角色），其余直接 new reader → `BulkIngestor.Ingest`。
    - 通过 query string 传参：`?onerror=skip` 切换到 `BulkErrorPolicy.Skip`，`?flush=true` 触发结尾 `Tsdb.FlushNow`。
    - 鉴权：`CanWrite` 才允许（readwrite / admin），与 SQL 端点保持一致；非法 db / measurement 名走 400/404。
    - 响应：`200 OK { "writtenRows": N, "skippedRows": K, "elapsedMilliseconds": ms }`；解析阶段失败 `400 { "error": "bulk_ingest_error", ... }`。
    - 写入行数同步进入 `ServerMetrics.AddInsertedRows`，与 SQL 路径的 `sonnetdb_rows_inserted_total` 计数对齐。
  - `SonnetDB.Data.Remote.RemoteConnectionImpl.ExecuteBulk` 完成实现：
    - 以 `BulkPayloadDetector.DetectWithPrefix` 嗅探协议、切首行 measurement 前缀。
    - measurement 优先级：`cmd.Parameters["measurement"]` > 首行前缀 > JSON `m` 字段 > `INSERT INTO <name>`。
    - 选择 `lp / json / bulk` 端点；`onerror`、`flush` 透传为 query string；payload 直接以 `text/plain`（LP / Bulk）或 `application/json`（JSON）POST。
    - 解析 `BulkIngestResponseBody.WrittenRows` 后包成 `MaterializedExecutionResult.NonQuery`。
  - 抽取共用 `SonnetDB.Ingest.SchemaBoundBulkValuesReader.Create(tsdb, sql, measurement)` 工厂：把 `tsdb.Measurements` 的 schema 桥接到 `BulkValuesReader` 的列角色 resolver。`EmbeddedConnectionImpl` 与服务端共用此工厂，避免逻辑重复。
  - 新增 `BulkIngestResponse`（服务端契约）、`BulkIngestResponseBody`（远程客户端 DTO）；两端均纳入 source-gen JSON context（AOT 友好）。
  - 测试：`tests/SonnetDB.Tests/BulkIngestEndpointTests.cs`（10 用例：LP/JSON/Bulk × {成功 / onerror=skip / FailFast 400 / flush=true / RBAC / 404 / 401 / 写后 SELECT 回查}）+ `RemoteAdoBulkIngestTests.cs`（7 用例：远程 ADO `CommandType.TableDirect` 三协议 + measurement 参数 / onerror / 403 / 缺 measurement 抛 InvalidOperationException）。

- **PR #43：`SonnetDB.Data` 接入批量入库快路径（绕开 SQL 解析的快路径，第 2/4 步）**
  - 扩展 `IConnectionImpl` 增加 `ExecuteBulk(commandText, parameters)`，与 `Execute(sql, …)` 并列。
  - `SndbCommand.CommandType` 由只读 `Text` 改为可读写字段，允许 `CommandType.Text`（默认）与 `CommandType.TableDirect`；其它值（如 `StoredProcedure`）仍抛 `NotSupportedException`。
  - `SndbCommand.ExecuteCore` 在 `TableDirect` 下跳过 `ParameterBinder`，把 `CommandText` 整段交给 `IConnectionImpl.ExecuteBulk`。
  - `EmbeddedConnectionImpl.ExecuteBulk` 桥接 `SonnetDB.Ingest`：用 `BulkPayloadDetector.DetectWithPrefix` 嗅探协议并切首行 measurement 前缀；按 `LineProtocol`/`Json`/`BulkValues` 路由到对应 reader；对 `BulkValuesReader` 通过闭包从 `Tsdb.Measurements.TryGet(measurement).TryGetColumn(col).Role` 解析列角色（Tag/Field/Time）；最终经 `BulkIngestor.Ingest` 写入并把写入行数包成 `MaterializedExecutionResult.NonQuery`。
  - 命令参数 hooks：`measurement`（覆盖 payload 内的 measurement）、`onerror=skip`（切换到 `BulkErrorPolicy.Skip`）、`flush=true`（写入完成后触发 `Tsdb.FlushNow`）；参数名兼容 `@`/`:` 前缀。
  - `RemoteConnectionImpl.ExecuteBulk` 占位实现：抛 `NotSupportedException`，明确指向 PR #44。
  - 新增 `tests/SonnetDB.Core.Tests/Ado/TsdbBulkIngestAdoTests.cs`：10 个 xUnit 用例覆盖 LP / JSON / BulkValues / 首行前缀 / 参数（onerror/flush/measurement）/ 未知列 / `StoredProcedure` 拒绝 / TableDirect 与普通 SELECT 在同一连接共存等场景。
  - 兼容性：现有 `CommandType.Text` 路径行为完全不变；不引入新依赖。

- **PR #A：批量入库核心 `SonnetDB.Ingest` 命名空间（绕开 SQL 解析的快路径，第 1/4 步）**
  - 新增 `src/SonnetDB/Ingest/BulkPayloadFormat.cs`：`BulkPayloadFormat` 枚举（`LineProtocol` / `Json` / `BulkValues`）+ `TimePrecision`（`Nanoseconds` / `Microseconds` / `Milliseconds` / `Seconds`）。
  - 新增 `src/SonnetDB/Ingest/BulkPayloadDetector.cs`：O(1) 前后字节嗅探协议；`DetectWithPrefix` 支持可选首行 measurement 前缀（首行不含空白/`=`/`,`/`{}`/`()`/`;` 时视为 measurement）。
  - 新增 `src/SonnetDB/Ingest/IPointReader.cs`（与 `LineProtocolReader.cs` 同文件）：流式 `TryRead(out Point)` 通用契约。
  - 新增 `src/SonnetDB/Ingest/LineProtocolReader.cs`：InfluxDB Line Protocol 子集（`double` / `42i` / `t`/`f`/`true`/`false` / `"…"` field；`\,` `\=` `\空格` `\\` 转义；ns/us/ms/s 精度换算；`measurementOverride`；空行与 `#` 注释跳过）。
  - 新增 `src/SonnetDB/Ingest/JsonPointsReader.cs`：基于 `Utf8JsonReader` 的流式 JSON reader，schema `{"m":"…","precision":"ms","points":[{"t":…,"tags":{…},"fields":{…}}]}`，避免一次性反序列化大 payload；支持 `measurementOverride` 与单点级 `measurement` 覆盖。
  - 新增 `src/SonnetDB/Ingest/BulkValuesReader.cs`：`INSERT INTO m(cols) VALUES (…),(…),…;` 形式的快路径 reader；表头按需解析一次后，VALUES 走自写扫描器（支持单引号字符串 + `''` 转义 / 整数 / 浮点 / `TRUE`/`FALSE`/`NULL` / 双引号或反引号包裹的标识符）；列角色由调用方 `Func<string, BulkValuesColumnRole>` resolver 提供，便于与 measurement schema 解耦。
  - 新增 `src/SonnetDB/Ingest/BulkIngestor.cs`：统一消费入口；`ArrayPool<Point>` 8192 批 → `Tsdb.WriteMany`；支持 `BulkErrorPolicy.FailFast` / `Skip` 与可选 `flushOnComplete`；返回 `BulkIngestResult(Written, Skipped)`。
  - 新增 `src/SonnetDB/Ingest/BulkIngestException.cs`：批量入库专用异常类型。
  - 新增 `tests/SonnetDB.Core.Tests/Ingest/` 下 5 个测试类（`BulkPayloadDetectorTests` / `LineProtocolReaderTests` / `JsonPointsReaderTests` / `BulkValuesReaderTests` / `BulkIngestorTests`），共 38 个 xUnit 用例覆盖：协议嗅探、首行前缀切分、LP 与 JSON 与 Bulk INSERT VALUES 解析与异常路径、`BulkIngestor` 在 batch 边界（>8192 行）下的正确性、`Skip` 策略与 `flushOnComplete` 路径。
  - 仍保持 `src/SonnetDB` 零第三方运行时依赖（仅 `System.IO.Hashing`），不引入新的 NuGet 包。

- **PR #38：发布 `SonnetDB 0.1.0` 的 NuGet、二进制包、完整服务端包与安装包**
  - 新增 `src/SonnetDB/PackageReadme.md`、`src/SonnetDB.Data/PackageReadme.md`、`src/SonnetDB.Cli/PackageReadme.md`，并在三个项目文件中补齐 `PackageId`、`PackageReadmeFile`、版本元数据；其中 `src/SonnetDB.Data` 项目发布为 `SonnetDB` NuGet 包，程序集和命名空间保持 `SonnetDB.Data`。
  - `SonnetDB.Cli` 从占位程序升级为可用命令行工具：支持 `sndb version`、`sndb sql --connection ... --command|--file ...` 和 `sndb repl --connection ...`，可直接连接本地嵌入式数据库或远程 `SonnetDB`。
  - 新增 `tests/SonnetDB.Core.Tests/Cli/CliApplicationTests.cs`，覆盖 CLI 帮助输出与本地 SQL 执行回归场景。
  - 新增 `src/SonnetDB.Data/Internal/ExecutionFieldTypeResolver.cs`，并为 `SndbDataReader` / `IExecutionResult` / `MaterializedExecutionResult` / `RemoteExecutionResult` 补齐 trim/AOT 注解与显式类型映射，保持 `SonnetDB.Cli` 接入 `SonnetDB.Data` 后仍可 `PublishAot=true` 通过发布。
  - 新增 `docs/releases/README.md`、`docs/releases/sdk-bundle.md`、`docs/releases/server-bundle.md`、`docs/releases/installers.md`，说明 NuGet 包、SDK Bundle、Server Full Bundle、MSI/DEB/RPM 安装包的用途、目录结构、默认启动方式与凭据。
  - 新增跨平台发布脚本 `eng/release.ps1`：
    - 生成 `SonnetDB.Core` / `SonnetDB` / `SonnetDB.Cli` NuGet 包；
    - 发布 Windows / Linux 原生 AOT CLI 与 Server；
    - 生成 `sndb-sdk-<version>-<rid>` 与 `sonnetdb-full-<version>-<rid>` 压缩包；
    - 自动写入默认本地启动配置、预置管理员 `admin / Admin123!` 与 Bearer Token `sonnetdb-admin-token`；
    - 生成 SHA256 校验文件；
    - 生成 Windows `msi` 与 Linux `deb` / `rpm` 安装包。
  - 新增 `.github/workflows/publish.yml`，在 `v*` tag 或手动触发时自动执行：
    - NuGet 打包与发布；
    - Windows / Linux Server + CLI + 前端完整打包；
    - MSI / DEB / RPM 安装包构建；
    - GitHub Release 附件上传。
- **PR #35：BenchmarkDotNet 五库性能基准全量收敛**
  - 所有基准代码与 docker compose 编排在前序 PR（#32 / #33 / #36 工作流）中已陆续落地，PR #35 收敛验收并将 ROADMAP 状态置 ✅。
  - 五个数据库的覆盖矩阵（详见 README「五库基准覆盖一览」）：
    - **SonnetDB（嵌入式）**：写入 + 范围查询 + 聚合 + Compaction（4→1 段合并）。
    - **SQLite**（`Microsoft.Data.Sqlite`，WAL）：写入 + 范围查询 + 聚合。
    - **InfluxDB 2.7**（HTTP Line Protocol + Flux）：写入 + 范围查询 + 聚合。
    - **TDengine 3.3**（REST + 超级表/子表）：写入 + 范围查询 + 聚合。
    - **SonnetDB**（HTTP Batch SQL + ndjson）：写入 + 范围查询 + 聚合。
  - 数据规模统一 100 万点、单序列 `host=server001`、`value DOUBLE`，外部数据库不可用时各基准独立 `[SKIP]`。
  - README 新增「五库基准覆盖一览」表，明确标注 Compaction 仅适用于 SonnetDB，并指向各详细结果章节；同时补 ROADMAP Milestone 9 推进顺序为 PR #37 → PR #38 → PR #39。
- **SonnetDB 实时事件流：SSE + 前端订阅自动刷新（PR #34c）**
  - 服务端新增 `src/SonnetDB/Hosting/EventBroadcaster.cs`：基于 `System.Threading.Channels` 的多路广播器（`BoundedChannel` + `FullMode.DropOldest`，容量 64），按通道 `metrics` / `slow_query` / `db` 过滤订阅，慢消费者自动丢最旧帧不阻塞 publish。
  - 新增 `src/SonnetDB/Endpoints/SseEndpointHandler.cs`：实现 `GET /v1/events`，响应 `text/event-stream`，禁用 buffering（`X-Accel-Buffering: no`），按 SSE 帧格式输出 `event:` + `id:` + `data:` 三行；30 秒空闲发 `: heartbeat` 注释行心跳；支持 `?stream=metrics,slow_query,db` 通道筛选；连接建立先发 `hello` 帧。
  - 新增 `src/SonnetDB/Hosting/MetricsTickService.cs`：`BackgroundService` + `PeriodicTimer`，按 `MetricsTickSeconds`（默认 5s）周期生成 `MetricsSnapshotEvent`（含数据库数 / 用户数 / 订阅者数 / 各库 segment 计数），仅在有订阅者时构造 + 推送，避免无人订阅时空转。
  - 新增 `src/SonnetDB/Contracts/Events.cs`：`ServerEvent` / `MetricsSnapshotEvent` / `SlowQueryEvent` / `DatabaseEvent` DTO（DTO 入 `ServerJsonContext` source-gen 满足 AOT），通道常量 `ChannelMetrics` / `ChannelSlowQuery` / `ChannelDatabase`。
  - `Configuration/ServerOptions.cs` 新增 `SlowQueryThresholdMs`（默认 500ms）/ `MetricsTickSeconds`（默认 5s）。
  - `Endpoints/SqlEndpointHandler.cs`：`HandleSingleAsync` / `HandleBatchAsync` 增加 `string databaseName` 参数；新增 `MaybePublishSlow` helper 在所有路径（成功 / 失败 / 控制面 / 单条 / 批量）统计耗时，超阈值即广播 `SlowQueryEvent`（控制面用 `__control` 标签，SQL 截断到 1024 字节）。
  - `Hosting/TsdbRegistry.cs`：构造器接受 `EventBroadcaster?`，`TryCreate` / `Drop` 成功后广播 `DatabaseEvent`（`created` / `dropped`）。
  - `Auth/BearerAuthMiddleware.cs`：当请求路径是 `/v1/events` 且无 `Authorization` 头时，从 `?access_token=<tok>` query 取 token，因为浏览器 `EventSource` API 无法发自定义 header。
  - 前端新增 `web/admin/src/api/events.ts`：`subscribeServerEvents(token, opts)` 封装 `EventSource`，挂载 `hello` / `metrics` / `slow_query` / `db` 监听并把 401 / `EventSource.CLOSED` 标记为 `unauthorized` 状态，返回关闭函数。
  - 前端新增 `web/admin/src/stores/events.ts`：Pinia store，缓冲最近 100 条慢查询 + 100 条 db 事件，维护 `dbEventBumper` 计数信号 + 当前 `metrics` 快照，监听 `auth.isAuthenticated` 自动 connect / disconnect。
  - 前端新增 `web/admin/src/views/EventsView.vue`：实时指标 grid（8 个 statistic）+ 慢查询表（带成功/失败 tag）+ 数据库事件表（带 created/dropped tag）。
  - `views/AppShell.vue` 顶栏增加 SSE 状态指示（n-tag + 状态点 CSS）+ 新增「事件流」菜单项；`router/index.ts` 注册 `/admin/events` 路由。
  - `views/DashboardView.vue` / `views/DatabasesView.vue` 各 `watch(() => events.dbEventBumper, reload)` + `watch(() => events.metrics, ...)`，CREATE/DROP DATABASE 在所有打开的客户端无需手动刷新即可即时同步；Dashboard 的 segment 计数也由 metrics 帧覆盖。
  - 测试：新增 `tests/SonnetDB.Tests/SseEndToEndTests.cs` 端到端覆盖：401 拒绝匿名访问、`?access_token=` query 鉴权通过、收到 `hello` → CREATE → `db.created` → 触发 SQL → `slow_query`（阈值压到 0）→ 周期性 `metrics`（tick 设 1s）→ DROP → `db.dropped` 完整链路。
  - `web/admin/tsconfig.json` 把 `ignoreDeprecations` 从 `"6.0"` 调整为 `"5.0"`，使本地 TypeScript 5.6.x 可继续构建；语义不变（仍是抑制 `baseUrl` 废弃告警）。
- **SonnetDB Admin Vue3 管理后台完成（PR #34b）**
  - `web/admin/`：Vite + Vue 3 + TypeScript + Naive UI + Pinia + Vue Router 单页应用，完整涵盖登录页、数据库列表/状态、SQL 控制台、用户/权限/Token 管理七个视图：
    - `LoginView.vue`：Bearer token 登录，结果存 localStorage，axios 拦截器自动注入 `Authorization: Bearer`。
    - `AppShell.vue`：响应式侧边栏 + 顶栏（用户名 / 角色标签 / 退出），超级用户额外显示用户 / 权限 / Token 菜单，路由 Guard 拦截未登录与越权访问。
    - `DashboardView.vue`：数据库数量 / 用户数量 / 授权条目三个统计卡，数据库状态表格（在线状态 + Segment 数），admin 额外展示用户列表。
    - `DatabasesView.vue`：`GET /v1/db` + `/metrics` 展示数据库列表/Segment 数，admin 可创建（`CREATE DATABASE`）与二次确认 DROP。
    - `SqlConsoleView.vue`：目标数据库选择器（admin 额外有控制面选项）+ 多行 SQL 编辑器 + 运行/清空按钮 + ndjson 结果表格 + 行数/耗时 meta 行。
    - `UsersView.vue`（admin only）：`SHOW USERS` 列表、`CREATE USER ... [SUPERUSER]` 表单、改密弹窗（`ALTER USER`）、二次确认 DROP。
    - `TokensView.vue`（admin only）：`SHOW TOKENS [FOR user]` 列表、`ISSUE TOKEN FOR <user>` 签发并弹窗展示明文、按用户筛选、行级 `REVOKE TOKEN`。
  - 服务端嵌入资源管线：`AdminUiAssets` / `AdminUiEndpoints` 把 `web/admin/dist/**` 以 `EmbeddedResource` 嵌入 dll，运行时按路径提供文件，SPA 路由 fallback 到 `index.html`，hash 化资产设 `immutable` 缓存，dist 未 build 时返回 503 提示。
  - 专用控制面 SQL 端点 `POST /v1/sql`（admin only）运行控制面语句（CREATE USER / GRANT / SHOW USERS …），数据面 SQL 走 `POST /v1/db/{db}/sql`；前端通过 `execControlPlaneSql` / `execDataSql` 统一调用。
  - `tsconfig.json` 添加 `"ignoreDeprecations": "6.0"` 抑制 TypeScript 对 `baseUrl` 的废弃警告（TS5101：`baseUrl` 将在 TypeScript 7.0 停止工作）。
- **SonnetDB Docker 性能测试（PR #36）**
  - 新增 `src/SonnetDB/Dockerfile`：基于 `mcr.microsoft.com/dotnet/sdk:10.0` 多阶段构建，最终镜像基于 `mcr.microsoft.com/dotnet/aspnet:10.0`，框架依赖发布（Framework-dependent）。
  - 更新 `tests/SonnetDB.Benchmarks/docker/docker-compose.yml`：新增 `sonnetdb` 服务，暴露端口 5080，默认 token `bench-admin-token`，含健康检查（wget `/healthz`）和 Docker Volume 持久化。
  - 新增 `tests/SonnetDB.Benchmarks/Benchmarks/ServerBenchmark.cs`：包含 `ServerInsertBenchmark`、`ServerQueryBenchmark`、`ServerAggregateBenchmark` 三个基准类，通过 `HttpClient` 直接调用 SonnetDB HTTP API（Batch SQL insert / SELECT / GROUP BY 聚合），服务不可用时自动 `[SKIP]`。
  - 更新 `README.md`：新增「SonnetDB 服务器模式性能基准」章节，记录 Docker 容器测试环境（AMD EPYC 9V74，Ubuntu 24.04，.NET 10.0.5）及实测结果：写入 13.16 s（HTTP Batch 2k/批）、范围查询 210.8 ms、1 分钟桶聚合 138.3 ms，并对比嵌入式模式额外开销。
- **SonnetDB Admin SPA：数据库状态 + Token 管理（PR #34b-4）**
  - 前端新增 `web/admin/src/views/TokensView.vue`，提供 admin-only 的 Token 管理页：`SHOW TOKENS [FOR user]` 列表、`ISSUE TOKEN FOR <user>` 一次性签发明文 token、`REVOKE TOKEN '<tokenId>'` 行级吊销，并在弹窗中提示“token 明文只展示一次”。
  - `web/admin/src/router/index.ts` / `views/AppShell.vue` 新增 `tokens` 路由与侧边栏菜单；用户 / 权限 / Token 三个控制面页面现在形成完整闭环。
  - `UserStore` / `ControlPlane` / `SqlExecutor` 的 token 查询与吊销链路补齐单元/集成/E2E 覆盖：新增 `ListTokensDetailed` + `RevokeTokenById` 验证、控制面 SQL 的 `ISSUE/SHOW/REVOKE TOKEN` round-trip，以及 token 吊销后旧 Bearer 立即失效的端到端断言。
- **SonnetDB 控制面：用户 / 权限 / 数据库 DDL（PR #34a）**
  - 新增持久化用户与权限存储（仅服务端）：`src/SonnetDB/Auth/UserStore.cs` + `GrantsStore.cs`，文件落 `<DataRoot>/.system/{users.json,grants.json}`，原子写入（temp + `Flush(true)` + `File.Move(overwrite=true)`）。
  - 密码：PBKDF2-HMAC-SHA256，100 000 轮、16 字节随机 salt、32 字节 hash；校验用 `CryptographicOperations.FixedTimeEquals` 防侧信道。
  - 动态 API token：32 字节随机（`RandomNumberGenerator`）→ Base64Url，仅存 SHA-256 hex 哈希；token id 形如 `tok_<8hex>`，便于审计与单条吊销。
  - 权限模型：`enum DatabasePermission { Read=1, Write=2, Admin=3 }`，按 `(database,user)` 单条记录；`*` 通配整个集群。
  - **SQL 控制面 DDL**：在 `src/SonnetDB/Sql/` 新增 7 条语句类型 + parser 分支（`ParseStatement` 添加 `Drop`/`Alter`/`Grant`/`Revoke`，新增 `ParseCreate` 二级分发）：
    - `CREATE USER <name> WITH PASSWORD '<pwd>' [SUPERUSER]`
    - `ALTER USER <name> WITH PASSWORD '<new>'`（成功后吊销该用户全部旧 token）
    - `DROP USER <name>`（级联删除其所有 grants）
    - `GRANT READ|WRITE|ADMIN ON DATABASE <db|*> TO <user>`
    - `REVOKE ON DATABASE <db|*> FROM <user>`
    - `CREATE DATABASE <name>` / `DROP DATABASE <name>`（通过 `IControlPlane` 触发 `TsdbRegistry.TryCreate/Drop`，并级联 grants）
  - **执行层 `IControlPlane`**：`src/SonnetDB/Sql/Execution/IControlPlane.cs` 在核心库声明（零依赖），`SqlExecutor.ExecuteStatement` 新增 `IControlPlane?` 参数；嵌入式连接传入 `null` → 控制面 DDL 抛 `NotSupportedException`（"控制面 DDL（CREATE USER / GRANT / CREATE DATABASE 等）仅在服务端模式可用。"）。服务端在 `src/SonnetDB/Auth/ControlPlane.cs` 提供桥接实现：用户/权限/数据库三向级联（DROP USER → DeleteUserGrants，DROP DATABASE → DeleteDatabaseGrants）。
  - **认证扩展（PR #34a-5）**：`BearerAuthMiddleware.Authenticate` 新增 `UserStore?` 入参，先匹配 `ServerOptions.Tokens` 静态映射，未命中再走 `UserStore.TryAuthenticate`（哈希查表）；命中动态 token 时把 `AuthenticatedUser` 写入 `HttpContext.Items["sndb.user"]`，超级用户映射 `admin` 角色，普通用户映射 `readwrite`。`/v1/auth/login` 路径始终匿名。
  - **新端点 `POST /v1/auth/login`**：接收 `{username,password}`，PBKDF2 校验通过后调用 `UserStore.IssueToken` 颁发新 token，返回 `{username,token,tokenId,isSuperuser}`。⚠️ 该端点用 `app.MapMethods(path, ["POST"], (RequestDelegate)(async ctx => ...))` 直接以 `RequestDelegate` 形式注册（详见 `Fixed`）。
  - **SQL 端点权限收紧**：`src/SonnetDB/Endpoints/SqlEndpointHandler.cs` 在执行前 parse 一次，识别为控制面 DDL 时要求 `isAdmin == true`，否则返回 `forbidden`；写操作（INSERT/DELETE）仍按 `canWrite` 判定。批处理同步收紧。
  - **测试**：5 个端到端用例 `tests/SonnetDB.Tests/AuthControlPlaneEndToEndTests.cs` 覆盖：登录字段缺失 → 400、未知用户 → 401、CREATE USER + GRANT + 登录拿 token + 用 token 调 `/healthz` 与 SQL 端点（普通用户控制面 DDL → forbidden）、动态非 admin token 控制面 DDL → forbidden、ALTER USER 改密后旧 token 立即失效 → 401。服务端测试总数：49 通过 / 0 失败。
- **SonnetDB 控制面查询 SQL：SHOW USERS / GRANTS / DATABASES（PR #34b-1）**
  - 新增 SQL 关键字 `SHOW / USERS / GRANTS / DATABASES / FOR`（`src/SonnetDB/Sql/TokenKind.cs` + `SqlLexer.cs`）。
  - 新增 AST：`ShowUsersStatement` / `ShowGrantsStatement(UserName)` / `ShowDatabasesStatement`，`SqlParser.ParseShow()` 分发；`SHOW GRANTS [FOR <user>]` 中 `FOR` 子句可选。
  - `IControlPlane` 扩展 3 个查询方法：`ListUsers()` → `IReadOnlyList<UserSummary>`、`ListGrants(string?)` → `IReadOnlyList<GrantSummary>`、`ListDatabases()` → `IReadOnlyList<string>`；`UserSummary(Name, IsSuperuser, CreatedUtc, TokenCount)` 与 `GrantSummary(UserName, Database, Permission)` 在核心库声明。
  - `SqlExecutor` 把 SHOW 语句包装成 `SelectExecutionResult`，复用现有 `/v1/db/{db}/sql` ndjson 渲染管线，无需新增 REST 端点。
  - 服务端 `UserStore.ListUsersDetailed()` 与 `GrantsStore.ListAll()` 提供枚举支撑；`ControlPlane` 用反向权限映射 (`MapPermissionBack`) 把 `DatabasePermission` 转回 `GrantPermission`。
  - 权限收紧：`SHOW USERS` / `SHOW GRANTS` 在 `SqlEndpointHandler.IsControlPlaneStatement` 中归为 admin-only；`SHOW DATABASES` 任何已认证用户均可执行。
  - 测试：parser 5 例（`Parse_ShowUsers/ShowDatabases/ShowGrants_NoFilter/WithFor` + 3 个 bad grammar Theory）+ ControlPlane 集成 3 例（`ListUsers_ReturnsCreatedUsersOrderedByName` / `ListGrants_NullFilter_ReturnsAll` / `ListDatabases_ReflectsRegistry`）+ E2E 3 例（`ShowUsers_AsAdmin_ReturnsRows` / `ShowUsers_AsRegularUser_Forbidden` / `ShowDatabases_AsAdmin_ReturnsRows`）。当前测试总数：1174 + 55 = 1229 全绿。
- **SonnetDB Admin SPA 脚手架：嵌入式静态资源管线（PR #34b-2）**
  - 新增 `web/admin/` 完整 Vite + Vue 3 + TypeScript + Naive UI + Pinia + Vue Router 单页应用脚手架，含 `LoginView`（PBKDF2 登录 → token 存 localStorage）+ `DashboardView`（数据库 / 用户 / grants 概览，**全部走 SHOW SQL**，零额外 REST 端点）。
  - 路由前缀固定为 `/admin/`；axios 拦截器自动注入 `Bearer <token>`；vite dev 反代 `/v1`、`/healthz`、`/metrics` → `:5000`。
  - 服务端 `src/SonnetDB/Hosting/AdminUiAssets.cs`：启动时一次性把 `web/admin/dist/**` 嵌入资源（`sndb.admin/...`）加载到 `FrozenDictionary`，AOT 友好的 MIME 类型 switch；`AdminUiEndpoints.MapAdminUi()` 注册 `GET /admin` 与 `GET /admin/{**path}`，命中具体文件返回原字节，未命中且无扩展名时回退 `index.html`（SPA 客户端路由），manifest 为空时返回 503 + 提示 `npm run build`。
  - `BearerAuthMiddleware.Authenticate` 豁免 `/admin/*` 路径匿名访问（仅静态资源；任何管理动作仍需登录后凭 token 调 `/v1/db/{db}/sql`）。
  - csproj 集成：`web/admin/dist/**` 通过 `<EmbeddedResource>` 自动嵌入，`LogicalName` 写为 `sndb.admin/%(RecursiveDir)%(Filename)%(Extension)`，C# 端把 `\` 规范化为 `/`；可选 target `BuildAdminUi=true` 自动跑 `npm install && npm run build`（默认 false，避免日常 `dotnet build` 被 npm 拖慢）。dist 目录通过 `web/admin/.gitignore` 排除，不入库。
  - 缓存策略：`/admin`、`/admin/index.html` → `no-cache`；其他 hash 化资产 → `public, max-age=31536000, immutable`（与 Vite 默认 contenthash 命名匹配）。
  - 测试：6 个端到端用例 `tests/SonnetDB.Tests/AdminUiEndToEndTests.cs` 覆盖 `GET /admin` 返回 HTML、SPA fallback (`/admin/login` → index.html)、带扩展名缺失 → 404、匿名可访问、favicon → image/svg+xml、`/v1/db` 仍要求 Bearer。当 dist 未 build 时所有用例自动跳过断言（CI 友好）。当前测试总数：1174 + 61 = **1235 全绿**。
- **SonnetDB Admin SPA：SQL Console + 数据库 / 用户 / 权限管理（PR #34b-3）**
  - 新增专用控制面 SQL 端点 `POST /v1/sql`（admin only，无 db 路径），仅接收控制面语句（CREATE USER / GRANT / CREATE DATABASE / SHOW USERS / SHOW DATABASES 等）；数据面语句 → 400。仍然以 `application/x-ndjson` 流式输出，行格式与 `/v1/db/{db}/sql` 完全一致，前端可共享解析器。复用 `app.MapMethods(path, ["POST"], (RequestDelegate)(...))` 模式绕过 AOT RequestDelegateGenerator 拦截。
  - 核心库新增 `SqlExecutor.ExecuteControlPlaneStatement(SqlStatement, IControlPlane)` 入口，独立于 `Tsdb` 实例运行；用于 `/v1/sql` 端点，使前端无需先选数据库就能跑控制面 SQL。
  - **SQL grammar 补全 `SUPERUSER` 关键字**：`CREATE USER <name> WITH PASSWORD '<pwd>' [SUPERUSER]` 末尾可选关键字现在被解析（此前 `IsSuperuser` 始终为 `false`）。新增 `TokenKind.KeywordSuperuser` + lexer 映射 + parser 可选消费 + 2 个 parser 单测。
  - **前端 SPA 重构**：抽出 `web/admin/src/api/sql.ts` 共享 ndjson 解析（`parseNdjson` / `execControlPlaneSql` / `execDataSql` / `rowsToObjects` / `quote` / `isValidIdentifier`），所有视图共用 `axios.validateStatus:()=>true` + `responseType:'text'` 模式正确处理 4xx / 5xx 响应。
  - 抽出 `views/AppShell.vue` 共享布局壳子（sider + header + `<router-view/>`），由 `App.vue` 顶层挂载 `NDialogProvider / NMessageProvider / NNotificationProvider`；普通用户菜单显示「概览 / SQL Console / 数据库」，超级用户额外显示「用户 / 权限」。
  - 4 个新视图：
    - `SqlConsoleView.vue`：目标选择器（控制面 / 任意数据库）+ 多行 SQL 编辑 + 运行按钮 + 结果表格 + meta（行数 / 受影响行数 / elapsedMs）+ error alert。
    - `DatabasesView.vue`：`SHOW DATABASES` 列表 + admin 创建（`CREATE DATABASE`，标识符校验）+ 二次确认 DROP。
    - `UsersView.vue`（admin only）：`SHOW USERS` 表格 + `CREATE USER ... [SUPERUSER]` 表单 + 改密弹窗（`ALTER USER ... WITH PASSWORD '...'`）+ 二次确认 DROP。
    - `GrantsView.vue`（admin only）：`SHOW GRANTS` 表格 + `GRANT READ|WRITE|ADMIN ON DATABASE <db> TO <user>` 表单 + 行级 `REVOKE ON DATABASE <db> FROM <user>` 二次确认。
  - 路由结构调整：`/admin/login` 匿名；其余路由全部嵌套在 `AppShell` 子路由下（`/dashboard` / `/sql` / `/databases` / `/users` / `/grants`），全局 guard 增加 `meta.admin` 判断（非 admin 访问 admin 路由 → 重定向 `/dashboard`）。
  - 测试：parser 新增 2 例（`Parse_CreateUser_WithSuperuserKeyword_SetsFlag` / `Parse_CreateUser_SuperuserKeyword_CaseInsensitive`）+ 服务端 E2E 新增 4 例（`ControlPlaneEndpoint_AsAdmin_RunsCreateUserAndShowUsers` / `ControlPlaneEndpoint_CreateSuperuser_FlagPersisted` / `ControlPlaneEndpoint_AsRegularUser_Forbidden` / `ControlPlaneEndpoint_RejectsDataPlaneStatement`）。当前测试总数：1176 + 65 = **1241 全绿**。

### Fixed
- **Admin SPA 普通用户数据库列表修正（PR #34b-4）**
  - `DashboardView` / `DatabasesView` / `SqlConsoleView` 不再错误地把 `SHOW DATABASES` 走到 admin-only 的 `POST /v1/sql`；数据库列表改走 `GET /v1/db`，数据库状态通过 `GET /metrics` 读取 `sonnetdb_segments{db="..."}`，因此普通已登录用户也能查看数据库概览与段数量。
  - `SqlConsoleView` 的“控制面”目标选择现在仅对 admin 显示；普通用户默认落到首个可访问数据库，避免一进控制台就遇到 `/v1/sql 仅 admin 可调用` 的无意义报错。
- **AOT RequestDelegateGenerator workaround**：`WebApplication.CreateSlimBuilder` + `EnableRequestDelegateGenerator=true` 下，对于
  `app.MapPost(path, async (HttpContext ctx) => Results.Json(value, typeInfo, statusCode: 4xx))` 形态的 lambda，生成的 interceptor 会
  错误地把响应吞成 `200 + 空 body`（lambda 实际执行，但 statusCode 与 body 全部丢失）。`/v1/auth/login` 改为
  `app.MapMethods(path, ["POST"], (RequestDelegate)(async ctx => { ctx.Response.StatusCode = ...; await JsonSerializer.SerializeAsync(...); }))`
  绕过 generator 拦截，行为稳定（PR #34a）。

- **SonnetDB.Data**：将 ADO.NET API 从 `SonnetDB` 核心库剥离为独立的 `src/SonnetDB.Data/`（PR #33）
  - 公共表面保持兼容：`SndbConnection` / `SndbCommand` / `SndbDataReader` / `SndbParameter` / `SndbParameterCollection` / `SndbConnectionStringBuilder` 命名空间从 `SonnetDB.Ado` 迁移到 `SonnetDB.Data`；`src/SonnetDB/Ado/` 目录整体删除。
  - **嵌入式 + 远程双模式**：通过连接字符串 scheme 自动分派，由内部接口 `IConnectionImpl` 统一抽象。
    - 嵌入式：`Data Source=<path>` 或 `sonnetdb://<path>` → `EmbeddedConnectionImpl` 复用 `SharedSndbRegistry` + 进程内 `Tsdb`，行为与原 `SonnetDB.Ado` 完全一致。
    - 远程：`Data Source=sonnetdb+http://host:port/<db>;Token=<bearer>` 或 `http(s)://...` → `RemoteConnectionImpl` 通过 `HttpClient` + ndjson 流式协议直连 `SonnetDB` 的 `POST /v1/db/{db}/sql` 端点；服务端错误抛 `SndbServerException`（含 `Error` / `ServerMessage` / `StatusCode`）。
    - `SndbConnectionStringBuilder.ResolveMode()` 支持显式 `Mode=Embedded|Remote` 覆盖；新增 `Token` / `Timeout`（默认 100s）键。
    - `SndbConnection.ProviderMode` 暴露当前模式；`UnderlyingTsdb` 仅在嵌入式模式返回非空。
  - 新增 `SndbProviderFactory : DbProviderFactory`（单例 `Instance`），可注册到 `DbProviderFactories` 供通用 ADO 工具使用。
  - `IsAotCompatible=false` 并附详细注释（理由：`DbConnection` / `DbCommand` 基类大量反射；主流 ADO 提供程序如 Npgsql、MySqlConnector 也未承诺 AOT；需要 AOT 的场景请直接使用 `Tsdb` API）。
  - 远程客户端 ndjson 解析使用 `System.Text.Json` 源生成器（`RemoteJsonContext`）+ `JsonDocument` 解析行级数组，`HttpCompletionOption.ResponseHeadersRead` 实现真流式读取。
  - 9 个端到端测试（`tests/SonnetDB.Tests/RemoteAdoEndToEndTests.cs`）覆盖：scheme 分派、嵌入式回退、CREATE→INSERT→SELECT 全链路、参数绑定与单引号转义、`ExecuteScalar`、只读令牌 INSERT 拒绝、SQL 错误、缺失令牌 401、未知数据库 404；既有 31 个 `TsdbAdoApiTests` 全部保持通过。

- **SonnetDB**：Native AOT 友好的 Minimal API HTTP 服务器（PR #32）
  - 新项目 `src/SonnetDB/`，基于 `Microsoft.NET.Sdk.Web` + `WebApplication.CreateSlimBuilder` + `EnableRequestDelegateGenerator=true`，全程零反射，可 `dotnet publish -p:PublishAot=true` 产出单文件可执行（win-x64 ~11.5MB），AOT 警告数为 0。
  - 多租户：进程内 `TsdbRegistry`（`ConcurrentDictionary<string, Tsdb>`）+ `DataRoot/<db>/` 子目录隔离，启动时按需加载已存在数据库；`POST /v1/db`（admin）创建、`DELETE /v1/db/{db}`（admin）销毁、`GET /v1/db` 列表；数据库名校验通过 `[GeneratedRegex]` 源生成器。
  - SQL 端点：`POST /v1/db/{db}/sql`（单条）+ `POST /v1/db/{db}/sql/batch`（多条），结果以 `application/x-ndjson` 流式返回（meta 行 + 每行 JSON 数组 + end 行），通过手写 `Utf8JsonWriter` 避免多态序列化；其余 DTO 全部走 `System.Text.Json` 源生成器（`ServerJsonContext`）。
  - 认证：`Authorization: Bearer <token>` 三角色（`admin` / `readwrite` / `readonly`），自定义中间件直接读 `ServerOptions.Tokens` 静态映射，非 `/healthz` `/metrics` 一律强制鉴权；写操作（INSERT/DELETE/DDL）需 `readwrite` 或 `admin`，建删数据库需 `admin`。
  - 可观测性：`GET /healthz` 返回 JSON 健康摘要；`GET /metrics` 输出 Prometheus 文本格式（`sonnetdb_uptime_seconds` / `sonnetdb_databases` / `sonnetdb_sql_requests_total` / `sonnetdb_sql_errors_total` / `sonnetdb_rows_inserted_total` / `sonnetdb_rows_returned_total` / per-db `sonnetdb_segments{db="..."}`）。
  - 6 个端到端集成测试（`tests/SonnetDB.Tests/ServerEndToEndTests.cs`）覆盖 Healthz / Metrics 匿名访问、SQL 鉴权、admin 角色限定、CREATE→INSERT→SELECT→DROP 全链路、ndjson 解析、未知数据库 404。
- **整库 Native AOT 兼容**：`Directory.Build.props` 默认开启 `IsAotCompatible=true`（测试与基准项目显式关闭）；`SonnetDB` / `SonnetDB.Cli` / `SonnetDB` 全部以零 IL/AOT 警告通过 `dotnet publish -p:PublishAot=true`。
  - `SndbDataReader.GetFieldType` 重构：内部 `Type[]` 改为 `enum ColumnTypeKind`，并添加 `[DynamicallyAccessedMembers]` 标注 + `typeof(...)` 常量 switch，消除 IL2063/IL2093 警告，对外 API 与运行时行为完全保持。
- **CI**：`.github/workflows/ci.yml` 新增 `aot-publish` job（Linux + Windows 矩阵），执行 `dotnet publish -p:PublishAot=true /warnaserror` 验证 `SonnetDB.Cli` 与 `SonnetDB`，并上传 publish 产物（PR #32）。

### Changed
- `InsertBenchmark`、`QueryBenchmark`、`AggregateBenchmark`：将内存占位实现替换为真实 `Tsdb` 引擎调用（PR #35）
- `README.md` 性能基准章节扩展为 **SonnetDB vs SQLite vs InfluxDB 2.7 vs TDengine 3.3.4** 四方对比（基于 1M 点数据集，单机容器）

### Fixed
- 基准测试 `GlobalCleanup` 中 SQLite 连接池文件锁问题（`SqliteConnection.ClearAllPools()`）（PR #35）
- `_influxAvailable` 现正确使用 `PingAsync()` 返回值而非无条件设为 `true`（PR #35）
- `InsertBenchmark.GlobalCleanup` 不再删除 InfluxDB bucket，避免后续 benchmark 进程的 `IterationSetup` 因 bucket 缺失而抛 `NotFoundException`
- `EnsureInfluxBucketAsync()`：三个 benchmark 在 `GlobalSetup` 中自动创建缺失的 `benchmarks` bucket
- TDengine SQL：`value` / `host` 列名加反引号绕开保留字解析错误，确保 4-DB 全部产出有效结果


### Added
- 新增段文件编码 / 字节统计快照 `SegmentReader.GetStats()`（PR #31）
  - 新增公开 record `SonnetDB.Storage.Segments.SegmentStats`（含 `BlockCount` / `TotalPointCount` / `TotalFieldNameBytes` / `TotalTimestampPayloadBytes` / `TotalValuePayloadBytes` / `RawTimestampBlocks` / `DeltaTimestampBlocks` / `RawValueBlocks` / `DeltaValueBlocks` / `ByFieldType` 以及计算型属性 `AverageTimestampBytesPerPoint` / `AverageValueBytesPerPoint`）与 `FieldTypeStats`（`BlockCount` / `PointCount` / `ValuePayloadBytes` / `DeltaValueBlocks`），为运维巡检、压缩率对比、基准测试提供结构化输出。
  - `SegmentReader.GetStats()`：按需遍历 `BlockDescriptor[]`，一次迭代同时计算总量 / 按 `BlockEncoding` 拆分 / 按 `FieldType` 分组三个维度；不缓存。可用于对同一 `MemTable` 分别以 V1 / V2 写入后对比 `Total*PayloadBytes` 验证压缩效果。
  - `SegmentStats.ByFieldType` 使用 `IReadOnlyDictionary<FieldType, FieldTypeStats>` 提供面向查询，默认为空字典以避免空段访问 NRE。
  - 6 个新测试（`SegmentReaderStatsTests`）覆盖：默认 V1 全部计入 raw 且字节数符合 8B/点；单独开启 V2 时间戳验证只选取时间戳压缩、值字节数不变；单独开启 V2 值（String 字典）验证值字节压缩、时间戳保持 V1；双 V2两个计数器都增加、平均字节/点均 < 8；多 `FieldType` 混合段按组计数与点数一致；空 `SegmentStats` 除零防护。

- 新增数值列 V2 编码：Float64 Gorilla XOR + Boolean RLE + String 字典（PR #30）
  - 新增内部位流工具 `SonnetDB.Storage.Segments.BitIo`：`BitWriter` / `BitReader` ref struct，高位优先按位写读，最大 64 位/调用。
  - 新增内部值列 V2 编解码器 `SonnetDB.Storage.Segments.ValuePayloadCodecV2`：
    - **Float64**：简化版 Gorilla XOR — 第一个值 64 位锚点，之后每点 1 位控制位；变化点再写 6 位 leadingZeros + 6 位 (meaningful-1) + meaningful 位有效位。常量序列压缩到 ≈1 位/点。
    - **Boolean**：游程长度编码（RLE）— 1 字节初值 + 交替 varint 段长。
    - **String**：按出现顺序构建字典 — `varint(dictSize)` + `dictSize × (varint(byteLen) + UTF-8)` + `count × varint(idx)`，重复值高度压缩。
    - **Int64**：本 PR 暂不压缩，仍为 8B LE 直存（与 V1 等价）。
  - `SegmentWriterOptions.ValueEncoding`：默认 `None`（V1）以保证已有段文件与测试行为不变；显式设为 `DeltaValue` 启用 V2 并在 `BlockHeader.Encoding` 与 `TimestampEncoding` 标志位独立组合。
  - `BlockDecoder.ReadValues` / `ReadValuesRange` 新增基于 `descriptor.ValueEncoding` 的 V1/V2 分发；V2 范围读取需先全量解码再切片（XOR/RLE/字典本质顺序）。
  - 19 个新测试（`ValuePayloadCodecV2Tests`）覆盖：Float64 空/单点/常量序列压缩/递增序列/特殊值（NaN/±Inf/±0）round-trip；Bool 全 true/交替/混合 run/损坏 run 越界；String 全相同/含 unicode/含空串/字典索引越界；Int64 V2 透传；SegmentWriter 默认无标志、单独 `DeltaValue`（Float64/Bool/String 均显著小于 V1）、`DeltaTimestamp | DeltaValue` 双标志组合及 `DecodeBlockRange` 与 V1 一致。

- 新增时间戳 Delta-of-Delta + ZigZag varint 编码（V2 block payload，向后兼容 V1）（PR #29）
  - 新增内部 `SonnetDB.Storage.Segments.TimestampCodec`：`MeasureDeltaOfDelta` / `WriteDeltaOfDelta` / `ReadDeltaOfDelta`。V2 格式：8 字节定点锐 + 1 个一阶差分 + 剩余二阶差分，常规采样间隔下压缩到 ≈1 字节/点。
  - `BlockEncoding` 改为 `[Flags]`：`DeltaTimestamp` (1) 与 `DeltaValue` (2) 可独立开关；`SegmentReader` 根据 bit 拆分到 `BlockDescriptor.{TimestampEncoding, ValueEncoding}`。
  - `SegmentWriterOptions.TimestampEncoding`：默认 `None`（V1）以保证已有文件与测试行为不变；显式设为 `DeltaTimestamp` 则启用 V2 并在 `BlockHeader.Encoding` 中置位。
  - `BlockDecoder` 联合读取路径（全量与范围）根据 `descriptor.TimestampEncoding` 分发；V2 路径需要完整重现时间戳后才能二分，已与现有范围查询逻辑保持一致。
  - 13 个新测试（`TimestampCodecTests`）覆盖：空序列、单点、规则间隔压缩占比、不规则间隔、负二阶差分、大锐点、buffer 长度不匹配、锐点截断、varint 越界、SegmentWriter 默认 V1、V1↔V2 跳点一致、`DecodeRange` 一致、`BlockDescriptor` 标志保留。

- 新增标准 ADO.NET API，提供 `SndbConnection` / `SndbCommand` / `SndbDataReader` / `SndbParameter` / `SndbParameterCollection` / `SndbConnectionStringBuilder`（PR #28）
  - `SonnetDB.Ado.SndbConnection : System.Data.Common.DbConnection`：连接字符串为 `Data Source=<根目录>`（大小写不敏感，由 `DbConnectionStringBuilder` 提供）；同进程同路径多次 `Open` 通过内部 `SharedSndbRegistry` 引用计数共享同一 `Tsdb`，避免 WAL 锁冲突；事务与 `ChangeDatabase` 抛 `NotSupportedException`
  - `SonnetDB.Ado.SndbCommand : DbCommand`：包装 `SqlExecutor`；`ExecuteNonQuery` 返回 INSERT 写入行数 / DELETE 増加的墓碑总数 / CREATE MEASUREMENT 0 / SELECT -1；`ExecuteScalar` 返回 SELECT 首行首列（空集返 null）；`ExecuteReader` 包装 `SelectExecutionResult`，非 SELECT 语句返回零行 reader 并携带 `RecordsAffected`
  - 参数绑定：支持 `@name` 与 `:name` 占位符，执行前以状态机扫描 SQL 文本并跳过字符串字面量 / 双引号标识符 / 行注释；支持类型包括 `string` / `bool` / 整型 / 浮点 / `decimal` / `DateTime` / `DateTimeOffset`（后两者转为 Unix 毫秒）/ `null` / `DBNull`；字符串值会被单引号包裹并把内部 `'` 转义为 `''`，避免 SQL 注入
  - `SndbDataReader : DbDataReader`：完整实现 `Read` / `GetXxx` / `IsDBNull` / `GetOrdinal` / `GetFieldType`（以首个非 null 行推断）/ `HasRows` / `RecordsAffected` / `CommandBehavior.CloseConnection`。`NextResult` 总为 `false`，`GetBytes` / `GetChars` 抛 `NotSupported`
  - 单元测试：31 个端到端测试（`TsdbAdoApiTests`）覆盖连接生命周期 / 共享 `Tsdb` / `BeginTransaction` 不支持 / `ConnectionStringBuilder` 大小写不敏感 / 三种 `ExecuteXxx` / 参数状态机（跳过字面量与标识符）/ 参数转义防注入（`O'Brien` 场景）/ 缺失参数报错 / `:name` 形式 / NULL 参数 / 多个 CommandText 错误路径 / `CloseConnection` 行为

- 新增 Tag 倒排索引以加速 `SELECT/DELETE` 的 `WHERE tag = '...'` 过滤（PR #27）
  - `SonnetDB.Catalog.TagInvertedIndex`（internal）：维护 `measurement → SeriesId 集合` 与 `measurement → tagKey → tagValue → SeriesId 集合` 两级映射；全部使用 `ConcurrentDictionary` 实现单写多读线程安全；集合本身用 `ConcurrentDictionary<ulong, byte>` 模拟并发集合
  - `SonnetDB.Catalog.SeriesCatalog.Find(measurement, tagFilter)`：从全表线性扫描改为基于倒排索引的候选集交集（基准选最小集合，规模上界为 `min(|S_i|)`）；返回前仍执行一次防御性 measurement+tag 重校验以容忍倒排索引与 `_byCanonical` 的瞬间不一致
  - 索引在 `GetOrAddInternal` 中仅由胜出的 `candidate` 线程写入（`ReferenceEquals(entry, candidate)`），并在 `LoadEntry`（`CatalogFileCodec` 重放路径）与 `Clear` 中维护——索引本身不进入持久化格式，启动时由现有持久化条目重建，因此**未变更磁盘 catalog 文件格式**
  - 单元测试：11 个新增测试（`TagInvertedIndexTests`）覆盖无 tag 过滤 / 单 tag / 多 tag 交集 / 未命中值 / 缺失 tagKey / 未知 measurement / measurement 隔离 / `Clear` 后清空 / 重复 `GetOrAdd` 索引不膨胀 / `LoadEntry` 重建 / 并发写读

- 新增 SQL `DELETE FROM ... WHERE ...` 执行支持（PR #26）
  - `SonnetDB.Sql.Execution.DeleteExecutionResult`（record，含 `Measurement` / `SeriesAffected` / `TombstonesAdded`）
  - `SonnetDB.Sql.Execution.DeleteExecutor`（internal）：复用 `WhereClauseDecomposer` 解析 tag 等值过滤 + 时间窗，对所有命中 tag 过滤的 series × schema 中所有 Field 列调用 `Tsdb.Delete(seriesId, fieldName, from, to)`，落到 PR #20 的 Tombstone 体系（WAL 追加 + 内存墓碑表 + 查询时过滤）
  - `SqlExecutor.ExecuteDelete(Tsdb, DeleteStatement)` 公共入口；`Execute` 派发新增 `DeleteStatement` 分支
  - 语义：`WHERE host = 'h1' AND time >= a AND time <= b` 等价于命中 series 的所有 field 列在 `[a, b]` 闭区间打墓碑；省略 time 比较则覆盖全时间轴；省略 tag 过滤则作用于该 measurement 下所有 series；命中 0 series 直接返回零计数（不抛错）
  - 校验规则：measurement 必须存在；WHERE 与 SELECT 共用同一套约束（仅 AND、tag 等值、time 比较、不支持 OR/NOT/field 过滤）；空时间窗抛 `InvalidOperationException`
  - 单元测试：13 个端到端测试覆盖时间窗 + tag 过滤 / 仅时间窗 / 仅 tag 过滤 / `time = X` 单点删除 / 命中 0 series / 跨重启持久化（WAL replay）/ 删除后聚合验证 / 各类错误场景（缺 measurement / OR / field 过滤 / 未知 tag 列 / 空时间窗 / null 参数）

- 新增 SQL `SELECT ... [WHERE ...] [GROUP BY time(...)]` 执行支持（PR #25）
  - `SonnetDB.Sql.Execution.SelectExecutionResult`（record，含 `Columns` / `Rows`；行内运行时类型：time→`long`、tag→`string?`、field→`double/long/bool/string?`、count→`long`、其他聚合→`double`）
  - `SonnetDB.Sql.Execution.WhereClauseDecomposer`（internal）：将 WHERE AST 拆分为 `(TagFilter, TimeRange)`；仅支持顶层 `AND` 合取、`tag = 'literal'` 等值过滤、`time {= != >= > <= <}` 时间窗（`time !=` 暂不支持）；OR / NOT / field 过滤 / 非字面量右值 / 同 tag 列冲突值均抛 `InvalidOperationException`；自动检测空时间窗
  - `SonnetDB.Sql.Execution.SelectExecutor`（internal）：投影分类（time/tag/field/aggregate）；原始模式按 series 做时间戳并集 outer-join，缺失字段输出 `null`；聚合模式以 `SortedDictionary<long, BucketState[]>` 按桶累积 count/sum/min/max/first/last，`GROUP BY time(d)` 由 `TimeBucket.Floor` 对齐，无 GROUP BY 则全局单桶；多 series 的 sum/avg/min/max/count 自动跨 series 合并；`count(*)` 跨 schema 全部数值 field 求总点数（跳过 String）；`count(field)` 计数任意类型；其他聚合拒绝 String field
  - `SqlExecutor.ExecuteSelect(Tsdb, SelectStatement)` 公共入口；`Execute` 派发新增 `SelectStatement` 分支
  - 校验规则：聚合不可与裸列混用；`GROUP BY time(...)` 仅在聚合中有效；`first`/`last` 多 series 暂不支持（v1）；未知函数 / 未知列 / 聚合函数作用于 Tag 列均抛错
  - 单元测试：25 个端到端测试覆盖 `SELECT *` / 列投影 / outer-join NULL / WHERE 时间窗 / WHERE tag 过滤 / 别名 / `count(*)` / `count(field)` / `sum/avg/min/max` / `first/last` / 多 series 聚合 / `GROUP BY time(1000ms)` / 空时间窗 / 各类错误场景（缺 measurement / 未知列 / OR / field 过滤 / 混合投影 / 缺聚合的 GROUP BY / first 多 series / tag 不等 / tag 冲突 / String 字段 sum）

- 新增 SQL `INSERT INTO ... VALUES (...)` 执行支持（PR #24）
  - `SonnetDB.Sql.Execution.InsertExecutionResult`（record，含 `Measurement` / `RowsInserted`）
  - `SqlExecutor.ExecuteInsert(Tsdb, InsertStatement)`：完整列绑定 + 类型校验 + 时间戳缺省 + 批量写入
  - 校验规则：measurement 必须已 CREATE；列名必须存在于 schema；同一 INSERT 列列表禁止重复；Tag 必须为字符串字面量且非 NULL；Field 类型必须匹配（INT 字面量可隐式提升为 FLOAT）；每行至少 1 个 Field 列值；`time` 列必须为非负整数字面量；缺省时使用 `DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()`
  - `SqlParser`：新增内部 `ExpectColumnName()`，允许 INSERT 列列表中将保留字 `time` 作为列名使用（与时间戳伪列对应；亦可继续用 `"time"` 引号转义）
  - `SqlExecutor.ExecuteStatement` 现支持 `InsertStatement` 派发
  - 单元测试：17 个端到端测试，覆盖单行 / 批量 / 时间缺省 / 时间大小写不敏感 / Int→Float 提升 / 全四种 FieldType round-trip / 仅 Field 无 Tag / measurement 缺失 / 未知列 / 重复列 / 类型不匹配 / Tag 非字符串 / NULL / 缺 Field / 负时间戳 / 批量部分失败前序已落地 / 参数 null

- 新增 `SonnetDB.Catalog` 命名空间下 measurement schema 体系，并接入 `Tsdb` 与 SQL 执行器（PR #23）
  - `MeasurementColumnRole`（Tag/Field 角色枚举）、`MeasurementColumn`（列定义 record）、`MeasurementSchema`（不可变值对象，工厂 `Create` 校验：列非空、≥1 个 Field、列名唯一、Tag 列必须 STRING、禁止 Unknown 类型）、`MeasurementCatalog`（基于 `ConcurrentDictionary` 的线程安全注册表）
  - `MeasurementSchemaCodec`：新增持久化文件 `measurements.tslschema`；二进制格式 `Magic(8) + FormatVersion(4) + HeaderSize(4) + Count(4) + Reserved(12)` 头 + 变长 measurement 记录 + `Crc32(4) + Magic(8) + Reserved(4)` 尾；`ArrayPool<byte>` + `SpanReader` / `SpanWriter` 实现，`Save` 走临时文件 + 原子 rename + fsync
  - `TsdbPaths.MeasurementSchemaFileName` / `MeasurementSchemaPath(root)` 路径常量
  - `Tsdb.Measurements` 属性 + `Tsdb.CreateMeasurement(MeasurementSchema)`：注册到 catalog 并立刻把全量 schema 集合原子持久化（崩溃安全）；`Open` 启动时加载、`Dispose` 关闭时再次保存
  - 新增 `SonnetDB.Sql.Execution.SqlExecutor`：`Execute(Tsdb, sql)` / `ExecuteStatement(Tsdb, SqlStatement)` / `ExecuteCreateMeasurement(Tsdb, CreateMeasurementStatement)`；把 AST `ColumnDefinition` 映射到 catalog `MeasurementColumn` 后调用 `Tsdb.CreateMeasurement`；其余语句类型暂抛 `NotSupportedException` 留待后续 PR
  - 单元测试：8 个 schema 校验 + 5 个 codec round-trip / 损坏检测 + 5 个执行器与持久化端到端测试

- 新增 `SonnetDB.Sql` 命名空间：纯 Safe-only、零第三方依赖的 SQL 词法 + 语法分析器（PR #22）
  - `TokenKind` / `Token` / `SqlLexer`：单遍词法分析；关键字大小写不敏感；标识符保留原始大小写；支持单引号字符串字面量（`''` 转义）、双引号引用标识符（`""` 转义）、整数/浮点字面量、duration 字面量（`ns / us / ms / s / m / h / d`，统一归一化为毫秒）、`-- 行注释`、`/* 块注释 */`、运算符 `= != <> < <= > >= + - * / %`
  - `Sql.Ast`：AST 节点（`SqlStatement` / `CreateMeasurementStatement` / `InsertStatement` / `SelectStatement` / `DeleteStatement` / `ColumnDefinition` / `SelectItem` / `TimeBucketSpec` / `SqlExpression` 派生：`LiteralExpression` / `DurationLiteralExpression` / `IdentifierExpression` / `StarExpression` / `FunctionCallExpression` / `BinaryExpression` / `UnaryExpression`），均为 `record` 值语义
  - `SqlParser`：递归下降解析器，覆盖 `CREATE MEASUREMENT` / `INSERT INTO ... VALUES (...) [, (...)]*` / `SELECT projections FROM measurement [WHERE ...] [GROUP BY time(duration)]` / `DELETE FROM measurement WHERE ...`；支持 `*` 通配、聚合函数（`count(*) / avg(x) / ...`）、`AS alias` 与裸 alias、`AND / OR / NOT` 短路逻辑、6 种比较与 5 种算术运算、括号显式优先级、`NULL / TRUE / FALSE` 字面量；新增 `SqlParser.Parse(string)` 解析单语句、`SqlParser.ParseScript(string)` 解析多语句脚本（分号分隔）
  - 关键字 `time` 在表达式中既可作为列名（`time >= 100`）也可作为函数（`time(1m)`），通过下一个 token 是否为 `(` 自动消歧
  - `SqlParseException`：携带源 SQL 字符位置的诊断异常
  - 单元测试：50 个 Lexer + Parser 测试，覆盖关键字大小写、字符串/标识符/duration 转义、运算符优先级、注释跳过、错误位置等

- 新增 `SonnetDB.Engine.Retention.RetentionPolicy`：数据保留策略；支持全局 TTL、轮询周期、限流（MaxTombstonesPerRound）及虚拟时钟注入（NowFn）（PR #21）
- 新增 `SonnetDB.Engine.Retention.RetentionPlan` / `TombstoneToInject`：单次 Retention 扫描的产物（纯计算，无副作用）
- 新增 `SonnetDB.Engine.Retention.RetentionPlanner`：从当前段集合产出 `RetentionPlan` 的纯函数；支持整段 drop、部分过期墓碑注入、已有等价墓碑去重及限流截断
- 新增 `SonnetDB.Engine.Retention.RetentionWorker`：后台 Retention 工作线程，双路径回收——整段直接 drop（MaxTimestamp < cutoff） + 墓碑注入（部分过期段，由 Compaction 在下一轮物理删除）
- 新增 `SonnetDB.Engine.Retention.RetentionExecutionStats`：单次 Retention 扫描统计（Cutoff / DroppedSegments / InjectedTombstones / ElapsedMicros）
- `SegmentManager.DropSegments(IReadOnlyList<long>)`：原子移除多个段，重建索引快照，Dispose 旧 reader，返回被移除列表（PR #21）
- `Tsdb.Retention`：暴露后台 Retention 工作线程（仅当 `TsdbOptions.Retention.Enabled=true` 时非 null）
- `TsdbOptions.Retention`：Retention TTL 策略入口（默认禁用，保持向后兼容）
- `RetentionPolicy.NowFn` 支持注入虚拟时钟（测试 + 自定义时间戳单位）

**Milestone 5 完成**（PR #17 后台 Flush + #18 Compaction + #19 多 WAL 滚动 + #20 DELETE-Tombstone + #21 Retention TTL）。

- 删除支持：`Tsdb.Delete(seriesId, field, from, to)` 和 `Tsdb.Delete(measurement, tags, field, from, to)`，返回操作是否成功（PR #20）
- WAL 新增 `RecordType.Delete = 5`（向后兼容），`WalWriter.AppendDelete` / `WalSegmentSet.AppendDelete` 追加删除记录
- 新增 `Tombstone`（readonly record struct）：墓碑数据结构，声明 (SeriesId, FieldName) 在时间窗 [From, To] 内的数据已被永久标记删除（v1 时间窗语义，无 perPoint LSN 比对）
- 新增 `TombstoneTable`：进程内墓碑集合，按 (SeriesId, FieldName) 索引；线程安全（lock 写 + Volatile 读快照）；提供 `IsCovered` / `GetForSeriesField` / `Add` / `LoadFrom` / `RemoveAll`
- 新增 `TombstoneManifestCodec`（`SonnetDB.Wal`）：墓碑清单文件 `<root>/tombstones.tslmanifest` 的序列化与反序列化；包含 Magic / FormatVersion / Crc32 校验；临时文件 + 原子 rename 写入
- 新增 `TsdbPaths.TombstoneManifestPath`：返回清单文件完整路径
- `Tsdb.Tombstones` 属性：暴露进程内墓碑集合
- 查询路径自动应用墓碑：`QueryEngine` 的 `Execute(PointQuery)` 和 `Execute(AggregateQuery)` 均一致过滤被墓碑覆盖的数据点（后者通过复用 PointQuery 路径自动获得）
- `SegmentCompactor.Execute` 新增可选 `TombstoneTable?` 参数：Compaction 时物理删除被墓碑覆盖的数据点；若某 (SeriesId, FieldName) 全部点被覆盖，则不生成对应 Block
- `CompactionWorker`：Swap 完成后自动回收"不再覆盖任何活段"的墓碑，更新 `TombstoneTable` 并重写 manifest
- 崩溃恢复：`Tsdb.Open` 启动时加载 manifest，再从 WAL replay 追加 CheckpointLsn 之后的 Delete 记录，最后重写 manifest（双路恢复）

### Changed
- `FlushCoordinator.Flush` 新增可选 `TombstoneTable?` 参数；Flush 序列在 WriteSegment 之前插入第 0 步：持久化 tombstone manifest（确保 WAL recycle 后墓碑不丢失）
- `WalReplayResult` 新增 `IReadOnlyList<DeleteRecord> DeleteRecords` 字段（含 CheckpointLsn 之后的删除记录）
- `WalReader.Replay()` 和 `WalSegmentSet.ReplayWithCheckpoint` 新增对 `WalRecordType.Delete` 的解析，产出 `DeleteRecord`
- `Tsdb.Dispose`：若 MemTable 为空（无需 Flush），仍会持久化 tombstone manifest


- 新增 `SonnetDB.Wal.WalSegmentSet`：多 WAL segment 管理器（Append / Sync / Roll / RecycleUpTo / ReplayWithCheckpoint），支持多 segment 滚动写入与按 CheckpointLsn 整段回收（PR #19）
- 新增 `WalSegmentLayout`（static）：WAL segment 文件命名约定（`{startLsn:X16}.SDBWAL`）、枚举、`TryParseStartLsn` 及 legacy `active.SDBWAL` 升级工具
- 新增 `WalSegmentInfo`（readonly record struct）：segment 元数据（StartLsn / Path / FileLength）
- 新增 `WalRollingPolicy`：WAL 滚动策略配置（Enabled / MaxBytesPerSegment=64MB / MaxRecordsPerSegment=1M 双阈值）
- 新增 `Tsdb` 启动时的 legacy `wal/active.SDBWAL` 自动升级路径（`UpgradeLegacyIfPresent`）

### Changed
- `FlushCoordinator.Flush` 改为通过 `WalSegmentSet` 工作；Flush 顺序升级为：WriteSegment → AppendCheckpoint+Sync → Roll → RecycleUpTo(checkpointRecordLsn) → MemTable.Reset（PR #19）
- `Tsdb` 内部 `_walWriter` 替换为 `WalSegmentSet _walSet`；`Tsdb.Open` 现在调用 `WalSegmentSet.Open`（自动升级 legacy WAL）和 `WalSegmentSet.ReplayWithCheckpoint`
- `TsdbOptions` 新增 `WalRollingPolicy WalRolling` 属性（默认 `WalRollingPolicy.Default`）
- `WalTruncator.SwapAndTruncate` 标记 `[Obsolete]`，内部保留以兼容外部使用；替代方案：`WalSegmentSet.Roll + RecycleUpTo`


- 新增 `SonnetDB.Engine.Compaction.CompactionPolicy`：Size-Tiered Compaction 触发策略（Enabled / MinTierSize / TierSizeRatio / FirstTierMaxBytes / PollInterval / ShutdownTimeout）
- 新增 `CompactionPlan` / `CompactionResult`：Compaction 计划与执行结果数据对象
- 新增 `CompactionPlanner`（static）：无副作用的 Size-Tiered 计划生成器；tier 划分公式 `tierIndex = max(0, floor(log_TierSizeRatio(fileLength / FirstTierMaxBytes)) + 1)`
- 新增 `SegmentCompactor`：N 路最小堆合并多个段、按 (SeriesId, FieldName) 写入新段；v1 同 timestamp 全部保留、FieldType 冲突抛 `InvalidOperationException`
- 新增 `CompactionWorker`（internal）：后台 Compaction 工作线程，轮询 Plan + Execute + SwapSegments + 删除旧段
- 新增 `SegmentManager.SwapSegments`：在单一锁内原子地移除旧段 + 打开新段 + 重建索引快照，避免中间状态可见
- `TsdbOptions.Compaction` 新增 `CompactionPolicy` 属性（默认 Default，Enabled=true）
- `Tsdb.Open` 末尾：若 `Compaction.Enabled` 启动 `CompactionWorker`
- `Tsdb.Dispose`：先关 CompactionWorker，再关 FlushWorker
- `Tsdb.AllocateSegmentId()`（internal）：线程安全 SegmentId 分配


- 新增 `SonnetDB.Engine.BackgroundFlushWorker`（internal）：后台 Flush 工作线程，含信号 + 周期轮询双触发，与同步 FlushNow 共享 `_writeSync` 锁保证互斥
- 新增 `BackgroundFlushOptions`（Enabled / PollInterval / ShutdownTimeout），`Dispose` 严格不泄漏后台线程
- 新增 `WalReplay.ReplayIntoWithCheckpoint`：基于 Checkpoint LSN 两遍扫描跳过冗余 WritePoint，消除崩溃恢复的冗余回放开销
- 新增 `WalReplayResult` record（CheckpointLsn / LastLsn / WritePoints）
- `TsdbOptions.BackgroundFlush` 暴露后台线程开关（默认 Enabled=true）
- `Tsdb.CheckpointLsn` 诊断属性：最近一次 Flush 的 WAL CheckpointLsn
- `Tsdb.Write` 在锁外向 worker 发送非阻塞信号；移除同步 Write 路径中的自动 Flush（由后台线程接管）
- `Tsdb.Open` 改用 `ReplayIntoWithCheckpoint` 替代 `ReplayInto`，支持 WAL 续写正确 LSN

### Added
- 新增 `SonnetDB.Query.QueryEngine`：合并 MemTable + 多 Segment 的查询执行器；支持原始点查询（`Execute(PointQuery)`）、聚合查询（`Execute(AggregateQuery)`）及批量聚合（`ExecuteMany`）（Milestone 4 完成）
- 新增 `PointQuery` / `AggregateQuery` / `AggregateBucket` / `Aggregator` / `TimeRange` 查询类型
- 支持 Count / Sum / Min / Max / Avg / First / Last 七种聚合函数（Float64 / Int64 / Boolean 字段）
- 支持 `GROUP BY time(...)` 桶聚合（基于 PR #7 的 `TimeBucket`）；空桶不输出
- 内部 N 路有序合并器 `BlockSourceMerger`：段按 SegmentId 升序排列后合并，MemTable 在最末，同 ts 全部 yield（不去重）
- `Tsdb.Query` 属性暴露查询入口（`QueryEngine` 无状态，每次查询时重建 SegmentId→Reader 映射）
- **Milestone 4 完成**：查询路径全面贯通（MemTable + 多段 + 时间过滤 + 7 种聚合 + GROUP BY time）

### Added
- 新增 `SonnetDB.Storage.Segments.SegmentBlockRef`（readonly struct）：跨段统一的 Block 引用（SegmentId + SegmentPath + BlockDescriptor）
- 新增 `SegmentIndex`（sealed class）：单段内 SeriesId / (SeriesId, FieldName) → BlockDescriptor 索引，含段级时间范围与时间窗二分剪枝
- 新增 `MultiSegmentIndex`（sealed class）：跨段只读联合索引快照；`LookupCandidates` 剪枝顺序：段级时间 → series → field → 段内时间窗二分
- 新增 `SonnetDB.Engine.SegmentManager`（sealed class）：已打开 SegmentReader 集合 + 索引快照管理器；lock 写 + Volatile 无锁读并发模型
- `Tsdb` 接入 `SegmentManager`：Open 时扫描段构建初始索引，FlushNow 时增量 AddSegment；Dispose 时关闭全部 SegmentReader
- `TsdbOptions` 新增 `SegmentReaderOptions` 属性

### Added
- 启动 Milestone 4：查询路径
- 新增 `SonnetDB.Storage.Segments.SegmentReader`：不可变段文件只读访问器
  - Open 时校验 Magic / Version / HeaderSize / FooterOffset / IndexCrc32
  - 按 SeriesId / (SeriesId, FieldName) / TimeRange 线性查找 BlockDescriptor
  - `ReadBlock` 返回零拷贝 ref struct `BlockData`
  - `DecodeBlock` / `DecodeBlockRange` 解码出 DataPoint[]
- 新增 `BlockDescriptor`（readonly struct）：描述 Block 元数据与物理位置
- 新增 `BlockData`（readonly ref struct）：零拷贝 Block payload 视图
- 新增 `SegmentReaderOptions`：VerifyIndexCrc / VerifyBlockCrc 选项（默认均启用）
- 新增 `SegmentCorruptedException`：段文件损坏或格式不一致时抛出（含 path + offset）
- 新增 `BlockDecoder`（internal static）：ValuePayloadCodec 的对偶，跨平台读用 BinaryPrimitives LE；支持 Float64 / Int64 / Boolean / String 四种类型及 DecodeRange 时间裁剪

### Added
- 启动 SonnetDB 引擎门面：`SonnetDB.Engine.Tsdb`（Open / Write / WriteMany / FlushNow / Dispose），完成 Milestone 3 写入路径闭环（PR #13）
- 新增 `TsdbOptions`：引擎全局配置（RootDirectory / FlushPolicy / SegmentWriterOptions / WalBufferSize / SyncWalOnEveryWrite）
- 新增 `TsdbPaths`：标准磁盘布局路径管理（catalog.SDBCAT + wal/active.SDBWAL + segments/{id:X16}.SDBSEG）
- 新增 `FlushCoordinator`：MemTable → Segment + WAL Checkpoint + WAL Truncate 三步原子可见
- 新增 `WalTruncator.SwapAndTruncate`：rename + 重建策略，避免就地截断的并发风险
- 新增 `SegmentWriterOptions.PostRenameAction`（internal）：原子 rename 完成后的测试钩子，用于模拟 rename 之后崩溃场景
- 完成 Milestone 3：写入路径闭环，三场景崩溃恢复测试矩阵齐全（未 Flush 崩溃 / Flush 后崩溃 / rename 后未 Checkpoint 崩溃）

### Added
- 初始化项目规划文档：`README.md`、`CHANGELOG.md`、`ROADMAP.md`、`AGENTS.md`
- 确定技术栈：C# / .NET 10 / xUnit / BenchmarkDotNet / GitHub Actions
- 确定核心设计原则：Safe-only、Span/MemoryMarshal、InlineArray、WAL+MemTable+Segment
- 解决方案与项目骨架（`SonnetDB.slnx`、`src/SonnetDB`、`src/SonnetDB.Cli`、`tests/SonnetDB.Core.Tests`、`tests/SonnetDB.Benchmarks`）（PR #2）
- `Directory.Build.props`（统一 `LangVersion` / `Nullable` / `ImplicitUsings` / `TreatWarningsAsErrors`）
- `Directory.Packages.props`（Central Package Management）
- `global.json`（固定 .NET 10 SDK）
- `.editorconfig`（统一代码风格）
- 新增 GitHub Actions CI 工作流（build + test，ubuntu / windows 矩阵）
- 新增 CodeQL 安全扫描工作流
- 新增 Dependabot 依赖更新配置
- 新增 dotnet format 校验
- 新增时序数据库性能对比基准（`tests/SonnetDB.Benchmarks/`）：使用 BenchmarkDotNet 0.15.8 对比 SonnetDB（内存占位）、SQLite、InfluxDB 2.x 和 TDengine 3.x 在相同 Docker 环境下的 100 万条数据**写入、时间范围查询、1 分钟桶聚合**的性能，含 Docker Compose 配置和 README 说明
- 新增 `SonnetDB.IO.SpanWriter`：基于 Span/MemoryMarshal/BinaryPrimitives 的 safe-only 顺序二进制写入器
- 新增 `SonnetDB.IO.SpanReader`：基于 Span/MemoryMarshal/BinaryPrimitives 的 safe-only 顺序二进制读取器
- 支持基础类型、unmanaged 结构体、结构体数组、VarInt(LEB128)、字符串的 round-trip 编解码
- 全程 little-endian，零 `unsafe`（PR #4）
- 新增 `SonnetDB.Buffers.InlineBytes4/8/16/32/64`：基于 `[InlineArray(N)]` 的固定长度内联缓冲区
- 新增 `InlineBytesExtensions`：通过 `MemoryMarshal.CreateSpan` 提供 Safe-only 的 `AsSpan` / `AsReadOnlySpan` 视图
- 新增 `InlineBytesHelpers`：泛型 `SequenceEqual` / `CopyFrom` 辅助方法
- 新增 `TsdbMagic`：定义 SonnetDB 文件 / 段 / WAL 的 magic 与格式版本常量（PR #5）
- 新增固定二进制结构体（namespace `SonnetDB.Storage.Format`）：
  - `FileHeader`（64B）/ `SegmentHeader`（64B）/ `BlockHeader`（64B）
  - `BlockIndexEntry`（48B）/ `SegmentFooter`（64B）/ `WalRecordHeader`（32B）
- 新增枚举：`BlockEncoding` / `FieldType` / `WalRecordType`
- 新增 `FormatSizes` 常量类，所有 header 尺寸由编译期 `Unsafe.SizeOf<T>` 测试守护
- 完成 Milestone 1：内存与二进制基础设施（Span/MemoryMarshal/InlineArray + 全部固定 header）（PR #6）
- 新增逻辑数据模型（namespace `SonnetDB.Model`）：
  - `FieldValue`（readonly struct，零装箱，支持 Float64/Int64/Boolean/String）
  - `Point`（用户层写入对象，含校验规则）
  - `DataPoint`（引擎内单 field 数据点，readonly record struct）
  - `SeriesFieldKey`（series + field 复合键，readonly record struct）
  - `AggregateResult`（Count/Sum/Min/Max/Avg 累加器）
  - `TimeBucket`（时间桶 Floor/Range/Enumerate 辅助）
- 启动 Milestone 2：逻辑模型与 Series Catalog（PR #7）
- 新增 `SonnetDB.Model.SeriesKey`（readonly struct）：规范化 `measurement + sorted(tags)` 为确定性字符串，格式 `measurement,k1=v1,k2=v2`，tags 按 key Ordinal 升序
- 新增 `SonnetDB.Model.SeriesId`（static class）：通过 `XxHash64` 将 `SeriesKey.Canonical` 的 UTF-8 编码折叠为 `ulong`，作为引擎主键（PR #8）
- 新增 `SonnetDB.Catalog.SeriesCatalog`：线程安全的 SeriesKey ↔ SeriesId ↔ SeriesEntry 中央目录（基于 ConcurrentDictionary，单写多读友好）
- 新增 `SonnetDB.Catalog.SeriesEntry`：序列目录条目（Id / Key / Measurement / Tags / CreatedAtUtcTicks），Tags 以 FrozenDictionary 保证不可变
- 新增 `SeriesCatalog.Find`：按 measurement + tag 子集线性查找
- 新增 `SonnetDB.Catalog.CatalogFileCodec`：`.SDBCAT` 目录文件序列化器（含临时文件原子替换写入与规范化校验加载）
- 新增 `SonnetDB.Storage.Format.CatalogFileHeader`（64B）：目录文件头，含 magic "SDBCATv1" / 版本 / 条目数
- 新增 `TsdbMagic.Catalog`（"SDBCATv1"）与 `TsdbMagic.CreateCatalogMagic()`
- 新增 `FormatSizes.CatalogFileHeaderSize = 64`
- 新增 `InlineBytes24` 内联缓冲区及其 `AsSpan`/`AsReadOnlySpan` 扩展
- 完成 Milestone 2：逻辑模型与 Series Catalog（PR #9）
- 启动 Milestone 3：写入路径（PR #10）
- 新增 `SonnetDB.Storage.Format.WalFileHeader`（64B）：WAL 文件头，含 magic "SDBWALv1" / 版本 / FirstLsn
- 新增 `FormatSizes.WalFileHeaderSize = 64`
- 更新 `WalRecordHeader`（32B）：新增 `Magic`（0x57414C52）/ `Flags` / `PayloadCrc32` / `Lsn` 字段，移除 `SeriesId` 至 payload
- 更新 `WalRecordType`：重命名 `Write→WritePoint`、`CatalogUpdate→CreateSeries`，新增 `Truncate=4`
- 新增 `SonnetDB.Wal` 命名空间：
  - `WalRecord` 抽象基类及派生：`WritePointRecord` / `CreateSeriesRecord` / `CheckpointRecord` / `TruncateRecord`
  - `WalWriter`：append-only WAL 写入器，含 CRC32（`System.IO.Hashing.Crc32`）+ fsync 支持
  - `WalReader`：迭代式回放，支持文件尾截断与 CRC 校验失败的优雅停止，暴露 `LastValidOffset`
  - `WalReplay`：将 WAL 回放到 `SeriesCatalog`，并 yield 出 `WritePointRecord` 流
  - `WalPayloadCodec`（internal）：4 种 RecordType × 4 种 FieldType 的 payload 编解码
- 新增 `SonnetDB.Memory.MemTableSeries`：单 (SeriesId, FieldName, FieldType) 桶，
  支持顺序与乱序追加，Snapshot 稳定排序（`_isSorted` 快速路径 + 索引辅助稳定排序）
- 新增 `SonnetDB.Memory.MemTable`：以 SeriesFieldKey 为主键的写入内存层，
  支持 WAL Replay 装载（`ReplayFrom`）、阈值触发 Flush（`ShouldFlush`）、Reset 与 SnapshotAll（PR #11）
- 新增 `SonnetDB.Memory.MemTableFlushPolicy`：MaxBytes / MaxPoints / MaxAge 三种阈值策略
- 新增 `SonnetDB.Storage.Segments.SegmentWriter`：把 MemTable 写为不可变 `.SDBSEG` 文件，使用临时文件 + 原子 rename 保证崩溃安全（PR #12）
- 新增 `SegmentWriterOptions`：BufferSize / FsyncOnCommit / TempFileSuffix 写入选项
- 新增 `SegmentBuildResult`：构建结果记录（路径、BlockCount、时间范围、各区偏移、耗时）
- 新增 `ValuePayloadCodec`（internal）：Float64 / Int64 / Boolean / String 的 Raw 编码
- 新增 `FieldNameHash`（internal）：基于 XxHash32 的字段名哈希，用于 BlockIndexEntry.FieldNameHash
- 启用 `BlockHeader.Crc32`（CRC32(FieldNameUtf8 ++ TsPayload ++ ValPayload)）与 `SegmentFooter.Crc32`（IndexCrc32）

---

历史 `0.1.0` / `0.2.0` / `0.3.0` 的 Planned 草案已移至[历史计划归档](docs/audits/historical-plans-from-changelog.md)，不再列作发布变更。较早版本的逐标签归档仍待补齐，本文件已有历史实现条目继续保留。

[Unreleased]: https://github.com/IoTSharp/SonnetDB/compare/v3.1.0...HEAD
[3.1.0]: https://github.com/IoTSharp/SonnetDB/compare/v3.0.1...v3.1.0
[3.0.1]: https://github.com/IoTSharp/SonnetDB/compare/v3.0.0...v3.0.1
[3.0.0]: https://github.com/IoTSharp/SonnetDB/compare/v2.5.0...v3.0.0
[2.5.0]: https://github.com/IoTSharp/SonnetDB/releases/tag/v2.5.0


- 2026-10-12 批次02测试声明合同补正：远程视图 helper 在两种 schema API 中严格核已知 Int64 的19/0与未知类型的 null 精度/scale；生产未改。完整真实两类97/97、焦点2/2以及同旧DLL短TEMP四例4/4已核验并保留旧FAIL，焦点不重复累计；短TEMP通过不作为路径异常根因证明，最终批次门禁仍独立。

- 2026-10-12 批次04 #280组合实测：完整Core String/Expression 78/78、完整EF 155/155，保留参数化LIKE转义、NULL守卫及embedded/REST/h2列表计数分页断言；本验证阶段未改生产或测试源码，最终批次门禁和正式包验收仍独立。


- 2026-10-12 批次04 #282及remoteNULL补测：新增一个REST/HTTP2双模式回归，区分捕获NULL可合法折叠FALSE与typed NULL raw wire实际捕获；实际空列表/计数0/空分页和协议断言通过，命名外键原范围保持。完整EF160/160，焦点5不重复累计；Core78未重跑，正式270/272验收仍开放。
