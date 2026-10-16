# #251 原生 DateTimeOffset 源码与消费验收

日期：2026-10-16（Asia/Shanghai）。本项接续 #252 冻结内容，集成基线提交 `16b266c2e43d2120dd6538c8ced66f270132958e`。实施树仍在 `390af19d2c24ef8e9f0d7ccfa42dbe1f5fd8bb0e`，通过逐 SHA 复制 #252 的 54 个交付文件构成依赖；不能把这些前置文件再次计为 #251 新增实现。根监督负责最终集成、完整 restore/Format、提交、推送和 Issue 回复。

## 实现结果

- 独立 `DATETIMEOFFSET` SQL 声明，列码 11；默认 CLR DateTimeOffset、显式 `DbType.DateTimeOffset` 和该 DbType 的 NULL 都保留原生类型。通用 reader、schema、空结果和全 NULL 使用同一声明。
- 持久值、主键和索引保存完整 UTC ticks，返回零 offset。相同 instant 的不同 offset 相等，相邻 100ns 不相等；排序、分组、DISTINCT、连接、MIN/MAX 和原生时刻相减使用一致规则。相减返回 DURATION。
- 声明验证位于名称绑定后、扫描/索引/hash/短路/空结果之前，覆盖 DML/CHECK、CASE/COALESCE/UNION/VALUES、递归及相关查询、视图与触发器 transition source。不通过运行用户函数或抽样行值推断类型。
- catalog 永久 floor v15，升级先于迁移 journal 和新行写入；物化快照声明 v4。复用 #252 的可恢复迁移流程及已有 DateTimeOffset spill 编码。物化视图预检查仅读取列声明；真正读取仍校验完整载荷 CRC，不把元数据预读当作行数据验收。
- REST/Frame 结果 v6、Frame meta 5/value 12，手写 REST ticks envelope 保持 source-generated JSON/AOT 边界。旧能力遇到原生结果（包括空/NULL/RETURNING）在副作用前拒绝；过程与序列先预检查。只对明确执行前 unsupported-result-version 降级重试。

完整使用与兼容合同见 `docs/compatibility/native-datetimeoffset.md`。默认 DTO 参数对旧 DATETIME 谓词现在明确拒绝，调用者须显式 CAST 或选择 `DbType.DateTime/DateTime2`。后者先在客户端转 UTC DateTime，再走原有毫秒参数合同；旧 DATETIME 目标列转换仍有截断边界。此项没有解决 #236 的旧 DATETIME 精度问题。

## 实际验证

证据根：`D:/source/SonnetDB/artifacts/issue-supervision-20261016/issue251-work`。各次命令、进程身份、退出码、stdout/stderr、TRX 和 SHA 在同名审计 JSON 中列出。以下集合有重叠，不能相加作为总覆盖数。

| 验证 | 实际结果 | 证据 |
| --- | --- | --- |
| 最终 Core 定向 | 174/175；#251 新增的 61 SQL、3 Frame、18 REST 解码共 82 项全部通过；唯一失败为既有递归取消异常包装断言 | `core-native06` |
| SQL/表/协议/ADO 大回归 | 3765/3766，唯一失败同一递归取消断言 | `core-sql02` |
| 最终真实远程 ADO 集合 | 147/147，零失败/跳过；包含本项三协议事务、重开、函数空/NULL声明、协商及旧能力副作用检查 | `remote-ado04` |
| 迁移强杀 | 14/14，其中本项 DTO 7、前置 Duration 7；六个阶段及恢复中再次强杀 | `crash-native01` |
| Chloe 最终候选 | 嵌入式、REST、显式 h2c 各 exercise 42 检查与独立 reopen 34 检查通过；远程重开前终止并重启真实 Server 进程 | `consumer-embedded02-*`、`consumer-remote01` |
| 新 Data 对真实旧 v5 Server | REST/h2c 各 exercise 10、reopen 6 检查通过；显式 DateTime/DateTime2 DTO 参数返回旧 DateTime 毫秒语义 | `consumer-oldserver01` |
| 新/旧 Core 跨进程屏障 | 新建 v15、旧 Core 实际拒绝、新 Core 重新打开并核完整 ticks；旧引擎运行前后 14 个持久文件 SHA 全部不变 | `catalog-*01` |
| 显式 Data AOT/trim 分析 | Release 构建 0 警告、0 错误；Core 依赖同时分析 | `data-aot01` |
| 最终 Core/Server/Chloe/探针构建 | Release 0 警告、0 错误 | `build-core-tests08`、`build-server-tests04`、`chloe-final-build01`、`consumer-build03` |

最终生产 Core SHA 为 `5E8DB75C8059DBF97524457D1402BA4A519403A512DF99D69BCB6C7D6D420B3A`。它与强杀测试、最终远程宿主和最终 Chloe 消费端实际加载的 Core 完全相同。`source-core-final01.json` 之后只新增/修订测试与文档，没有继续修改生产代码。早期 remote-ado02 的 147/147 和 embedded01 使用早期二进制，保留为过程证据，不替代最终矩阵。

## 未通过记录与边界

`core-sql02` 与 `core-native06` 的失败名均为 `SqlRecursiveCteTests.Execute_ExponentialBranch_CancellationStopsCandidateProbe`。既有测试设置 10ms 取消并精确要求 `RoutineExecutionException` 包裹；实际取消从 `SqlPagination.Skip/Take` 的既有资源检查直接抛 `OperationCanceledException`。原失败 TRX 保留，未排除该测试、未重复运行直至变绿、未在本项放宽断言。根监督已接手独立测试修正；以上两个集合不能写成全绿。

保留的其它失败包括：初始生成代码编译问题、未初始化锁定 submodule、snapshot 类型最大值检查、递归声明丢失、故障注入用例重新访问 faulted manager、旧 snapshot unknown-version 断言、默认 DTO 对旧 DATETIME 的旧断言、探针参数拼接及测试 DLL 路径，以及新增测试中无 FROM 的 WHERE 语法和错误 CRC 字节偏移。均有后续修订和相应证据；失败历史未删除。`core-sql01` 的默认 DTO 旧断言修订明确表达新兼容合同，而非恢复隐式混合比较。

公开 4.0.0 和 preview `.5` 原始负基线保留在 `public-02`：各首次/独立重开共四进程，通用值与 schema 是 DateTime，专用 DTO getter 控制成功，真实 Chloe 标量及对象在原转换器失败。该负基线不能写成本项正向验收。

## Chloe 候选身份

最终 `chloe-final` 基于 `bb7ec8735bd4511c35453eb009f70cc606795703`，顺序为：

1. #262 六文件日期补丁，SHA `F11FC18735789805867334F6CAE3CB4D28CC0279B2A77E556F62B5BCFE0DA35E`。
2. #252 五文件 TimeSpan 补丁，SHA `76BA8BD392CA127918A4F44316A9ECDC8599FDC68A873AA3812B33A8957ABB6A`。
3. #251 三文件 DDL/CAST/guard 补丁（+3/-2），SHA `77337861A5F7987E6E3017EB3A10B35A8AB6FFCF89CE85F13CD756E443CBD518`。

框架 `DateTimeOffset_ValueConverter` 没有修改。候选通过真实 DDL、普通/批量/异步写入、事务提交/回滚、表达式更新、参数比较和标量/对象/nullable 物化。原 DateTime MemberInfo handler 不会自动支持所有 DTO CLR 方法/属性，因此不宣称全部表达式翻译已实现。早期 `chloe-candidate` 仅叠 252+251 的矩阵保留，但最终结论只使用同时保留 #262 的 `chloe-final`。初次上游构建的 38 条 nullable 注释警告保留，最终构建以与 #262 相同的 `Nullable=annotations` 配置完成，没有警告压制。

## 交接与未交付条件

源码/本地消费者完成不等于正式发行。本项未发布 SonnetDB 包、未向 Chloe 仓库提交补丁、未执行 NativeAOT 发布运行、未进行跨操作系统/时区部署矩阵或性能/长期运行验收。根仍须完成最终树完整 restore/Format、独立提交/推送、回读 CI 和 Issue 条件；正式 Server/ADO/Core 与上游适配交付仍须分别核验，不应直接关闭 #251。
