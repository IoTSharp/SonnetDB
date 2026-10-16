# #251 原生 DateTimeOffset 设计与实施记录

状态：源码和本地消费验证已完成，正在冻结交付。已同步 #252 的54份冻结文件，根集成提交为 `16b266c2e43d2120dd6538c8ced66f270132958e`；本树保留 `390af19d` HEAD 与其未提交叠加。新值为类型11、catalog15、snapshot4、SQL结果v6、Frame meta5/value12。最终结果见 `issue-251-native-datetimeoffset-20261016.md`：82项新增Core及最终远程147项通过，大回归仍保留一项既有递归取消断言失败，根另案处理；正式发行/上游交付未满足。以下准备记录为历史设计。

准备阶段历史：工作树初始源码 `76955cf604655c2ec32f4db1cf019441d4e19577`；不修改 #252 正在实施的共享生产文件。#251 仍 OPEN，不能标为仅等待发布。以下区分本树基线、父代理已接受的设计和对 #252 未冻结候选的只读评估；候选版本号不能提前作为 #251 实施基线。

## 原基线已核对的行为

- `TableColumnType` 当前最高为 `Time=9`，只有 `DateTime=5`，没有独立 DateTimeOffset 声明。
- Core `SqlParameterBinder.ToLiteral` 和远程 `Data/Internal/ParameterBinder.FormatLiteral` 将 DateTimeOffset 参数变为 UTC Unix 毫秒整数；现有 DateTime 参数也有毫秒边界。
- `TableRowCodec` 的 DATETIME 解码固定返回 DateTime.Utc，Key/Index 编码使用 Unix 毫秒。`TableSqlExecutor` 把 DateTimeOffset 值规范为 DateTime.Utc。
- `ExecutionFieldTypeResolver` 已有 CLR DateTimeOffset 枚举映射，但没有对应的表列声明。`SndbDataReader.GetFieldValue<DateTimeOffset>` 已有专用转换；它不改变通用 `GetValue` / `GetFieldType` 合同。
- Frame 当前将 DateTimeOffset 编为旧 Timestamp tag 6，读取是 DateTime；#262 的 v4 / meta 3 / tag 10 仅为 DateTime 保留 ticks/Kind。REST 未形成原生 DateTimeOffset envelope。
- `SqlScalarComparer` 当前把 DateTime 和 DateTimeOffset 都转为 Unix 毫秒比较；新类型不能仅改变存储后继续以毫秒区分相邻 ticks。
- `SqlSpillRowCodec` 已支持 DateTimeOffset ticks/offset，`SqlRowRetentionBudget` 已计入它，不能把已有分支包装为新实现。后续只核对新值通过此路径后的语义。
- 原 Chloe PR #435 head `bb7ec8735bd4511c35453eb009f70cc606795703`：`Utils.EnsureSupportedMappingType` 拒绝 DateTimeOffset；默认 `DateTimeOffset_ValueConverter` 仍调用 `Convert.ChangeType`。

公开包负基线见 `issue-251-datetimeoffset-probe` 和本地 `artifacts/issue-supervision-20261016/issue251-work/public-02/receipt.json`。4.0.0 与 `.5`，各首次/独立重开共四进程实际通过预期失败断言：通用值/声明/schema 均 DateTime（包括空及 all-NULL），专用 getter 成功；真实 Chloe 标量及对象物化均在原转换器抛 InvalidCastException。该证据是缺口复现，不是功能验收。

## 拟实施合同

1. 追加 contextual SQL `DATETIMEOFFSET`，用于关系表声明与 CAST，映射新的 TableColumnType；不改变 `DATETIME -> DateTime`、旧 DATETIME 毫秒文件布局或 #236 状态。
2. 新类型按 UTC instant 归一，返回 `DateTimeOffset` 且 `Offset=00:00`。不保存原 offset。新类型的 DTO/完整文本输入采用完整 UTC ticks；不同 offset 的同一时刻在值比较、主键、唯一索引、分组与排序中一致。
3. 无 offset 文本按 UTC 解释。带 offset 文本按其显式 offset 转 UTC。DateTime.Utc 按 UTC；DateTime.Unspecified 按 UTC；DateTime.Local 在调用方既有参数绑定边界转 UTC，不能交给 Server 的本地时区重新解释。原 DateTime 参数若已在既有绑定中截到毫秒，则仍明确属于 #236；新类型不会恢复丢失 ticks。
4. 默认 CLR DTO 和显式 `DbType.DateTimeOffset` 参数使用原生类型及完整 UTC ticks。显式 `DbType.DateTime` / `DbType.DateTime2` 加 DTO 在客户端绑定前变为 UTC DateTime，继续使用既有 Unix 毫秒参数；该兼容分支必须实测新 Data 客户端连接真实旧 Server，不能仅以新 Server 上的 SQL CAST 代替。默认 DateTime 参数保持旧毫秒行为；显式 `DbType.DateTimeOffset` 加 DateTime 应先按 UTC/Local/Unspecified 规则在调用方归一，再保留 ticks。
5. 存储行、主键、二级索引使用新独立类型分支；目录版本永久 floor 在任何新格式行/journal 写入前提升。旧 reader 必须先拒绝目录，而非按旧类型写坏数据。新 schema 序列号仅在 #252 最终冻结后确定。
6. 在 #252 冻结的结果版本、meta declaration 和 value tag 后顺延新能力；REST 使用手写精确 envelope，Frame 使用独立 DateTimeOffset tag，传输 UTC ticks，不回退成 DateTime 或字符串。既有 FileHeader/FrameHeader 不假借改版本；具体兼容屏障遵循已冻结实现。
7. 结果类型在执行前绑定。旧客户端的普通结果、空结果、all-NULL、表达式结果、视图/CTE 及 INSERT/UPDATE/DELETE RETURNING 涉及新类型时，都须在副作用发生前明确拒绝。不能执行写入后才因编码失败重放请求。新客户端对旧服务器只在可证明执行前拒绝的精确 unsupported-version 响应下降协商。
8. Chloe 仅移除已验证的 DateTimeOffset guard 并追加原生 DDL/CAST 映射，保留默认转换器；先接入 #252 及已有 #262 独立候选依赖，再生成 #251 自身补丁。TimeSpan 与 DateTime 既有修正不能被覆盖。映射和通用物化通过不代表全部 DateTimeOffset CLR 属性/方法翻译通过。

## 已接受的比较与兼容边界

新原生 DTO 相互比较使用完整 UTC ticks；不同 offset 的同一 instant 相等。旧 DATETIME 相互比较和 DATETIME 与 Unix 毫秒整数比较继续使用既有毫秒合同。原生 DTO 与 DATETIME/Unix 整数的隐式比较在声明绑定阶段拒绝，要求调用者明确转换。

不能一面保留 `DATETIME(ms) == DTO(+1 tick)`，一面使用精确 DTO 相等，否则可形成 `DateTime0 == DTO0`、`DateTime0 == DTO1` 而 `DTO0 != DTO1` 的非传递关系，破坏 hash join、GROUP BY、DISTINCT、唯一键等合同。父代理已接受显式转换边界，不采用这一混合比较方案。

默认 DTO 参数用于旧 DATETIME 谓词时会变成明确的类型错误，这是必须记录的兼容行为变化。调用者设置 `DbType.DateTime/DateTime2` 后恢复旧参数语义；新 Server 也可使用显式 SQL CAST。DATETIME INSERT/UPDATE 的目标列转换可保留旧截断行为，它不授权 WHERE/JOIN 等比较隐式转换。

NULL 分两层：未声明的 CLR null/DBNull 继续是无类型 SQL NULL，不猜测 DTO；新列声明和 `CAST(NULL AS DATETIMEOFFSET)` 在空/all-NULL 结果中必须保持 DTO 元数据。显式 `DbType.DateTimeOffset` 加 null/DBNull 的实施目标同样是原生声明 NULL，须通过参数类型信息生成 typed CAST，不能用任意日期占位，也不能以运行时行值推断。该接线现已实现并纳入嵌入式/REST/Frame测试；最终通过状态另见实施证据。

## 声明验证必须先于快捷路径

2026-10-16 只读核查 #252 树确认，现有 `RelationalSelectExecutor.ValidateDeclaredExpression` 主要校验标识符和表达式形状，仅在部分 `preserveDeclaredJoinOrder` 路径调用。它不是统一类型绑定；`FETCH 0`、连接重排和 hash/index 路径不能靠它保证新合同。单表 `BuildProjections` 会校验投影，但 WHERE 在空表、主键快捷路径和索引候选中也不能依靠运行时比较发现错误。

实施时追加窄的原生时间类型合同，在名称绑定之后、序列消耗及所有 FETCH 0、谓词下推、候选提取、连接重排和 hash/index 算子之前校验。不得用执行查询或调用 UDF 来推断类型。类型来源必须包含基表 schema、CAST、已声明函数、派生 SELECT/VALUES、CTE、视图/物化视图输出及相关子查询外层声明；保留名称绑定已有大小写/引号合同。

- 比较入口覆盖 WHERE、JOIN ON、HAVING、CASE WHEN、IN/BETWEEN、相关 EXISTS、UPDATE/DELETE、ON CONFLICT WHERE 与 CHECK。SQL 布尔短路、空/all-NULL、LIMIT/FETCH 0 或 DELETE LIMIT 0 都不能使已知跨类型错误变成成功。
- 混合 CASE/COALESCE/UNION/VALUES 的声明不能被现有“不同类型返回 unknown”逻辑抹去，再因空输入跳过检查。涉及原生 DTO 的不兼容分支要求显式 CAST，纯 NULL 分支可与 DTO 合并；不透明来源不能由抽样行值证明兼容。
- `SqlScalarComparer` 保留运行时防线；DTO 精确 UTC ticks 相等/排序及 hash 必须一致。`JoinValueKey`、`SqlDistinctValueComparer` 和由其派生的分组/溢写桶都要调整，不能继续把 DTO hash 成 Unix 毫秒。
- 单表索引等值入口必须先确认比较合同再做目标列转换；不能由 `ConvertTableValue` 将原本不兼容的参数吞成索引键。新 DTO 范围优化若未实现可靠 ticks 路径，使用扫描；扫描和有索引两种路径须产生相同值和同样的拒绝。
- 运行时动态值与静态声明不符时仍报错；未知来源的处理要在实施中显式界定，不能把“没有行进入 comparer”当成通过。

## 对 #252 未冻结候选的只读评估

评估来源 `D:/source/SonnetDB-worktrees/issue252-timespan`，当时 HEAD `f8bce9839e7344bd8a8095d14979b0ee24bc574b`，有未提交生产修改。下列是源代码评估，不是本代理复跑 #252 验收；最终仍须父代理提供冻结 manifest 和实际生产基线。

| 现有候选 | #251 接续方式 |
| --- | --- |
| DURATION 列码 10、contextual DURATION、完整 TimeSpan ticks | 在最终值之后追加独立 DTO 类型，不改旧 DATETIME/Time/Duration 数值 |
| `TableSchemaCodec` v14；Save 保留历史 floor；Duration 迁移先 durable barrier 再 journal/raw 改写 | 按 schema 中原生类型计算所需最低版本；native DTO 的更高 floor 必须在任何新格式行/迁移日志前持久化；即使删除最后新列也不能降级 |
| 物化快照 v3 仅用于 Duration 声明/值，其余继续 v2，读取兼容 v1/v2 | 新 DTO 声明需下一快照版本，包括空/all-NULL；既有 `ValueKind.DateTimeOffset=9` 已存 ticks+offset，复用而非重造值编码 |
| Frame/REST 结果 v5、Frame declaration 4、Duration tag 11 | 最终冻结后再顺延新能力；v5 客户端仍可用 Duration，但必须拒绝 DTO；FileHeader/FrameHeader 布局保持已有合同 |
| REST 在构造结果时验证确认能力；Frame 声明标记匹配协商版本 | native DTO 的空/all-NULL 同样受版本约束，不能仅检测非 NULL 行值 |
| REST 精确 `unsupported_result_version` 才 v5→v4；Frame 精确可信单错误帧才 v5→v4→v3→v2 | 扩展版本链但保持仅执行前拒绝可重试，普通 SQL 错误、截断或未知响应不能重放 |
| `SqlDurationResultContract` 处理返回声明、运行时结果、序列及过程依赖树预检 | 优先复用声明与副作用预检逻辑，分别表达 Duration/DTO 能力；序列和 DEFAULT nextval 不能等到事务回滚时补救 |
| ADO `GetBindingValue` 显式 DbType.Time 转 TimeOnly；默认 TimeSpan→DURATION | 同一客户端绑定边界添加 DTO 的显式旧 DateTime 兼容；保留 DbType.Time 的一天范围校验 |

#252 的 RETURNING 目前仅列名或 `*`，表达式在解析时拒绝。#251 首先遵循现有语法，验证 INSERT/UPDATE/DELETE 的原生返回列在任何持久写之前被旧能力拒绝。过程还需保证后续结果不兼容时不会已消耗序列；普通事务外 SELECT 的带序列表达式亦同。

原 Chloe 候选的 #252 依赖共五文件：`Utils.cs`、`SqlGenerator.cs`、`SonnetDBContextProvider.cs`、`DDL/SonnetDBTableGenerator.cs`、`MethodHandlers/Subtract_Handler.cs`。`InsertRange` 独立设置参数 DbType，不能只修普通 SQL generator。#262 候选需按其独立补丁保留。

已检查 Chloe 的 `Now_HandlerBase` / `UtcNow_HandlerBase` 精确匹配 DateTime 属性 MemberInfo，`Subtract_HandlerBase` 精确匹配 `DateTime.Subtract(DateTime)`；这些 handler 不会仅因移除全局 DTO guard 就自动处理 DTO。DateTimeOffset.Now/UtcNow/Subtract 等表达式仍须实际确认支持或明确不支持，不能因名字相同宣称可用。原默认 `DateTimeOffset_ValueConverter` 不替换。

## 验收准备

| 层次 | 必要检查 |
| --- | --- |
| 类型/绑定 | 原始大小写、contextual 名称、显式 CAST、DateTimeOffset/nullable、无类型与显式 DbType 原生 NULL 参数、SQL NULL、正负及零 offset、offset-free 文本、UTC/Local/Unspecified 对照、非法和越界输入 |
| 新类型精度 | UTC ticks 0/最大、相邻 100ns、非整数毫秒、相同 instant 不同 offset；仅对新合同声明，不计为旧 DATETIME #236 修复 |
| 表/索引 | PRIMARY KEY/UNIQUE/二级索引的等值和范围、更新/删除、约束失败原子性、同 instant 重复键、排序/分组/联接/溢写 |
| 声明拒绝 | 相邻 tick 的 DTO 与旧 DATETIME/Unix 整数；空/非空/all-NULL、FETCH 0、有/无索引、hash 不同桶/连接重排、HAVING、CTE/视图/UNION/CASE/COALESCE、相关子查询、DML/ON CONFLICT/CHECK；错误不能依赖是否实际比较到值 |
| 持久化 | 初写、checkpoint 后独立进程重开、事务提交/回滚、批量写入/RETURNING、添加/修改类型及迁移屏障失败前后；目录永久 floor 和旧 reader 拒绝 |
| ADO | GetValue/GetValues/GetFieldType/GetDataTypeName/GetSchemaTable/GetColumnSchema；Read 前、空、all-NULL、计算表达式、nullable；DATETIME 对照仍 DateTime |
| 协议 | 嵌入式/真实 REST/真实 h2c；Server 进程重启后读取；native 版本/声明匹配；畸形 envelope/tag/ticks 拒绝；旧能力普通/空/all-NULL/RETURNING 均在副作用前拒绝 |
| Chloe | 原始 guard/默认转换器失败保留；最小候选后的原生 DDL、实体/表达式 DML、批量/事务、参数比较、SqlQuery 标量/对象、三通路和独立重开 |
| 兼容 | 默认 DTO 对旧 DATETIME 谓词明确拒绝；显式 DbType.DateTime/DateTime2 的 DTO 参数在新 Data→真实旧 Server 保留旧毫秒语义；旧 DATETIME 通用 DateTime；#252 DURATION/DbType.Time、#262 日期 Kind；完整 source-generated JSON / AOT 分析零新警告 |

## 原准备交接顺序与边界

等待 #252 最终冻结文件与 SHA，核对生产及外部 Chloe 依赖，再由父代理审查并同步统一基线。之后一次只实施 #251：先原生类型/存储和目录屏障；再参数显式兼容、统一声明验证/精确 hash；再元数据/结果协商及旧能力副作用前拒绝；最后源单测、独立重开/迁移恢复、真实旧 Server 与真实 Chloe 消费者，再冻结交付。父代理负责共享 HANDOFF/CHANGELOG、完整 restore/Format Check、提交、推送和远端 Issue 状态。

准备阶段结束时没有 SonnetDB 或 Chloe 生产修改，没有提交、推送、PR/Issue 评论或关闭。公开包首次探针语法失败 `public-01` 保留；`public-02` 是完整负基线，其四进程/673文件完整性已完成，不重复运行。原 `preparation-evidence.json` SHA256 `A44A55C808660A6129F71FFC517CE7104A0B48D8F5B46DA76549012955EC1CCA` 保持；新增评估另记证据。源码正向、Server、AOT、新旧互操作和正式发布全部 NOT_RUN。准备交接后结束等待，不进行空轮询；父代理给出正式接续消息后再实施。
