# #249 动态 SQL/JSON 审计与实现（2026-10-16）

本项已完成 SQL 执行器接线及真实 SqlSugar 本地消费者验证。**正式包发布后的验证仍缺，
不能关闭 #249**。任务树为 `codex/issue249-dynamic-json`；准备起点为 `390af19d`，
实施前按监督授权快进至 `67a5c8c8d64c36002aee5ebcfcb40634e7d5f482`。
快进前后七份准备源码哈希完全相同，原检查点与负向证据保留。
本项不改变持久化格式、AST 定义或协议版本，保持 schema v16 / snapshot v4 / SQL result v6。

## 线上需求与已存在能力

已读取 GitHub IoTSharp/SonnetDB #249 原文及评论，读取时 OPEN、无评论。
本项只补充按行计算的 `JSON_VALUE` path、动态数组下标和类型安全对象数组谓词。
#180 的静态/参数化 path、数组包含和 JSON 索引已有实现；#223 的字符串聚合不属于本项。

实施前 `TableSqlExecutor` 的 SELECT/DML 校验要求第二个参数为字面量，RSE、关系表及
文档执行器有静态 path 专用分支，`FunctionRegistry` 尚未注册 `json_value` 或
`json_object`。文档、JSON 文件和向量检索的显式物化预算校验也要求固定 path，
因此不能只新增一个 registry 项就宣称所有入口已完成。

真实 SqlSugar `65e8226ce25a8e1cb9cca2590683f1fd6778cab3` 的现有公开入口生成：

- `JsonField(json, column)` → `JSON_VALUE(json, CONCAT(...动态 bracket path...))`。
- `JsonIndex(json, column)` → `JSON_VALUE(json, CONCAT('$[', column, ']'))`。
- `JsonListObjectAny(array, key, value)` → `JSON_CONTAINS(array, '$', JSON_OBJECT(key,value))`。

该翻译保留公开业务接口，没有提前禁用函数或把对象谓词改为标量包含。
原始 `SonnetDBExpressionContext.cs` SHA256 为
`4C0B33D3D5A4794BFD9BDFC5299812F09E7EA04B3C53EE168B3256149757B25E`。
真实 provider 与其源文件已从其它任务的冻结原始副本只读复制并核验，未改其目录。

## 已取得的独立证据

原始日志、子进程身份、参数、TRX、运行 DLL 哈希在
`artifacts/issue-supervision-20261016/issue249-work/`，生成物不提交。

| 对照 | 结果 |
| --- | --- |
| 正式 SonnetDB 4.0.0，源码标识 d49babae | `probe-public4-run02`：4 个已有能力通过，11 个本项缺口按预期拒绝 |
| 预览 4.5.0-preview.ef266.5，源码标识 1af23d8f | `probe-preview-run02`：相同 4 通过、11 拒绝 |
| 冻结旧源码，标识 f9ee14b7 | `probe-source-run02`：相同 4 通过、11 拒绝 |
| 新辅助组件、链接旧 Core 的独立小测试项目 | `results/helper02.trx`：24/24，零失败/跳过；包含 writer 块请求余量修订 |
| SQL 正向合同运行于旧 Core | `results/sql-negative01.trx`：1/11 通过，10/11 失败；唯一通过为静态/参数 path、既有包含与 JSON 索引控制 |

三组消费者均调用真实 SqlSugar，上表不代表已发行 SqlSugar SonnetDB provider 包的验证。
通过控制为原生静态 path、参数 path、已有包含和真实 SqlSugar 静态 JsonField。
拒绝项覆盖原生动态 path/数组下标/JSON_OBJECT/decimal/null 对象谓词，以及 SqlSugar
动态 JsonField、多层表达式 JsonField、列 JsonIndex、JsonListObjectAny。

首次 `probe-public4-run01` 有一项无关失败：SqlSugar `First()` 默认生成
`ORDER BY CURRENT_UTC_DATETIME()`，旧正式包不支持该 ORDER BY 表达式。
原失败日志保留；使用现有 `ToList().Single()` / `ToListAsync()` 消费入口隔离本项，
没有修改 provider 或吞掉查询失败。第二次之后输出明确使用 UTF-8，首次旧中文输出
编码不正确也保留，没有事后重写失败记录。

## 辅助组件合同

新增并注册 `JsonDynamicSqlFunctions`：

- `Value` 接受实际求值后的 JSON/path 字符串；SQL NULL 任一输入返回 NULL，缺失、
  越界和 JSON null 返回 NULL。负数组下标、wildcard、无效路径有明确诊断。
- 动态值沿用 `JsonPathEvaluator` 的既有类型合同：Int64 优先，其余 JSON 数字为
  Double，字符串/布尔/null/对象数组文本保持原有表示。没有扩大为全局 decimal 取值
  类型变更；测试逐项比较静态/动态值与 CLR 类型。
- `CreateObject` 使用 `Utf8JsonWriter`，decimal 直接 `WriteNumberValue(decimal)`，
  不经过 Double。字符串始终是 JSON 字符串，即使内容本身看起来像 JSON。
  SQL NULL 写为 JSON null，字段名按 Ordinal 判断重复；null/非字符串字段名、重复名、
  非有限数值及复杂 CLR 类型拒绝。
- JSON 输入最多 8 Mi 字符，path 最多 1024 字符/64 片段，JSON 深度最多 64。
  对象最多 4096 个交替参数，字段名最多 1024 字符；编码前使用 UTF-16 code unit
  最坏六字节转义计算 8 MiB 上界。这是保守编码预算，不声称所有小于 8 MiB 的实际输出
  都能通过。预留当前查询内存、检查取消并在成功/失败后归还临时预留。
- 相邻 `1234567890123456.123456789012m` 与 `...013m` 在 Double 中相同，
  已通过对象构造→既有 JSON_CONTAINS 明确区分，证明该谓词路径保留 decimal 精度。

## 已实施的接线与边界

获得 #250 的最终提交后实施：

1. `FunctionRegistry.cs`：注册动态取值/对象构造。
2. `TableSqlExecutor.cs`：放宽 SELECT/DML path 参数形状校验，递归绑定第二参数；
   仍拒绝错误参数个数/不支持调用形状。
3. `RelationalSelectExecutor.cs`：统一标量调用和 JSON_OBJECT 字符串元数据；
   不把逐行 path 提前执行或按首行缓存。
4. `DocumentSqlExecutor.cs`、`DocumentVectorSearchExecutor.cs`、`JsonFileSqlExecutor.cs`：
   动态 path 准入同步进入原物化预算验证，第二参数仍递归验证；动态求值的临时内存
   额外受 SQL 查询预算约束。非法逐行路径在取到该行后诊断，不冒称扫描前能验证其值。
5. `SqlJsonObjectArguments` 只在 JSON_OBJECT 参数子树将显式 DECIMAL 字面量和已绑定
   decimal 参数改用 invariant 字符串的精确 CAST，避免旧文档/向量/文件/时序标量
   求值器先转 Double。嵌套算术、负号、CASE、COALESCE 保留此合同；预算为
   4096 节点/64 层。现有其它表达式和 JSON_VALUE 数字语义不变。
   普通小数字面量仍为 FLOAT，精确文本应使用 `DECIMAL '9007199254740993.01'`、
   `CAST('9007199254740993.01' AS DECIMAL)`、decimal 参数或 decimal 列。

静态/参数路径仍使用原有 JSON path 索引匹配分支，动态列路径不宣称有索引。
JSON_VALUE 的静态返回声明仍为未知 `object`，实际行值沿用原有 CLR 类型；
JSON_OBJECT 则明确声明 `String`，空结果也不依赖首行推断。
CHECK 的原有限定函数合同没有扩展。

## 实施验证及原始失败

- `core01`：24 helper + 11 SQL 集成，共 35/35。
- `core02`：259/263。三项新测试误把普通 FLOAT 数字字面量当作 DECIMAL；一项旧
  vector 测试仍要求动态 path 在扫描前拒绝。生产代码未为这四项失败降级；测试改为
  显式 DECIMAL 和三模型逐行非法 path 诊断后，`core03` 265/265，零失败/跳过。
- 新测试覆盖无 FROM、JOIN、派生表、UPDATE/INSERT、列/参数/表达式路径、数组下标、
  转义、SQL NULL、JSON null、缺失、精确对象谓词、重开、空结果声明、实际 EXPLAIN
  索引匹配、三个文档入口的默认/显式预算一致性及资源释放。
- 未修改的真实 SqlSugar 公开接口在 `consumer-embedded01` 和重开各 28/28；
  `remote01` 的 REST 与 h2c 各初次运行和强制终止宿主后的重开均 28/28。
  日志记录实际 SQL/参数 CLR 类型、Core/Data/SqlSugar/provider 版本与 DLL SHA；
  宿主日志按每阶段核对真实 HTTP/1.1 或 HTTP/2 请求，h2c 确实包含 Frame 请求。
  强制终止为该测试创建的 Process 实例，不代表断电测试。
- Server `server-build01` 为 Release 构建，0 警告/0 错误。
- 新远程合同首轮 `remote-regression01` 为 153/159：六项新 fixture 使用保留字
  `Path` 作未引号列名，建表前失败；修为 `PathText`/`FieldName`。第二轮仍 153/159，
  因测试错误要求多态 JSON_VALUE/既有 JSON_CONTAINS 在读取前声明 String/Bool；
  改为保留原 `object` 元数据，实际行值仍严格断言 String/Boolean，JSON_OBJECT
  空结果仍严格要求 String。两次原 TRX/日志全部保留。

最终验证：

| 窗口 | 实际结果 |
| --- | --- |
| `core-regression01` / `core-regression-final02` | SQL、Documents、Tables、Protocol 扩大回归各 3682/3682，零失败/跳过 |
| `core-final04` | 新 JSON 与原 JSON/三模型物化预算相邻回归 265/265，含实际 EXPLAIN 静态索引/动态非索引断言 |
| `core-model05` | 最后补充时序投影、聚合、时序 JOIN 与 hybrid 精确 decimal 的四项；两类动态 SQL 测试共 24/24。最后四项不冒称已包含在前述 3682 中；未再修改生产源码 |
| `remote-dynamic03` / `remote-final04` | 新三协议同步异步及完整宿主重开 6/6；最终整个 `RemoteAdoHttp2TransportTests` 为 159/159 |
| `consumer-build02` / `consumer-embedded-final02-*` / `remote-final02` | 最终消费者重建零警告/错误；嵌入式、REST、h2c 初次/重开六个阶段各 28/28 |
| `data-aot01` | Data 显式 `IsAotCompatible` / AOT / Trim Rebuild，零警告/错误 |
| `server-aot01` | Server 在项目原声明作用域内 Rebuild，零警告/错误；不把全局 AOT 属性强行传至 netstandard 生成器 |
| `solution-restore01` / `solution-format01` | 完整 solution restore 和 AGENTS 原 Format 命令均 exit 0；Format 有一条工作区加载警告，无格式违规 |

最终补充测试和本审计完成后会再执行完整 restore/原 Format 并写入冻结清单；
监督者提交前仍须在其最终整合树重跑规定检查。AOT 分析不等于 NativeAOT 发布运行，
本项没有该发布运行证据。

`preparation-source01` 的七源码和原39份证据已再次逐项核SHA一致；所有原失败保留。
`runtime-input-final01.json` 保存扩大回归运行前的DLL哈希；最后四项测试使测试程序集
变化，生产DLL保持同一输入，最终清单明确记录最终源码和程序集，不把两者混称。
这里的消费者使用源码构建 SqlSugar provider 与本地新引擎，并非正式 NuGet 发行。
没有发布或修改 SqlSugar 上游，原 JSON 翻译不需要补丁；监督者负责最终集成、
提交、批次推送、回复及关闭条件复核。
