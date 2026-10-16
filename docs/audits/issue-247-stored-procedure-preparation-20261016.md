# #247 标准存储过程命令与输出参数：实施前核查

日期：2026-10-16（Asia/Shanghai）。本文件是准备交付，不是功能完成或关闭报告。

源码基线 `67a5c8c8d64c36002aee5ebcfcb40634e7d5f482`，工作分支 `codex/issue247-stored-procedure`。共享生产文件尚未修改；实施须在 #249、#248 的实际提交后重新接续。本轮不提交、不推送、不发布包、不回复或关闭外部 Issue。

## 原始合同与边界

保存的 #247 原文要求 `CommandType.StoredProcedure` 以过程名和命名参数调用，覆盖同步/异步 Scalar、Reader、NonQuery；支持 Output、InputOutput、ReturnValue，明确 Reader 关闭后回写；维持既有事务、超时、取消和连接释放；用正式包验证真实 SqlSugar `Ado.UseStoredProcedure()` 与 `SugarParameter.Direction`。已有 `CALL` 可执行不能替代这些标准入口。

#229 当前已 CLOSED，其最终评论记录多 SELECT/NextResult 源码在 `ac71b100`，并入 `89674db0` 后按维护者源码范围关闭；正式发行仍归 #230。该项明确未覆盖存储过程。本轮源码普通 SELECT batch 实跑通过，过程体仍丢弃前面的结果。后续复用 #229 的结果读取/生命周期机制，不重新立项普通多结果。

原始 GitHub JSON、原始 SqlSugar 源和本轮失败保存在 `D:/source/SonnetDB/artifacts/issue-supervision-20261016/issue247-work`，最终以准备清单中的 SHA 为准。GitHub #229 首次直连超时和随后代理读取成功均保留。

## 已执行的基线

| 输入 | 原生 ADO | 真实 SqlSugar |
|---|---:|---:|
| 固定正式 4.0.0，提交 d49babae | 2 PASS / 13 FAIL | 2 PASS / 9 FAIL |
| 固定公开 4.5.0-preview.ef266.5，提交 1af23d8f | 2 PASS / 13 FAIL | 2 PASS / 9 FAIL |
| 本轮源码 67a5c8c8 | 3 PASS / 12 FAIL | 2 PASS / 9 FAIL |

三组使用同一预编译消费者字节，只替换对应 Core/Data；不是三个独立重写的模拟提供程序。public4/public-preview5 是固定比较输入，不声称其为当前最新发行。SqlSugar 固定 PR1457 head `65e8226ce25a8e1cb9cca2590683f1fd6778cab3`，原始 ZIP SHA `46615584F90E8E7857A91140130581F9E4C3D7E7AE20B48A9C9F05383633D766`；没有使用 #250 的候选补丁替代原始适配器。

PASS 仅为同步/异步 Text `CALL`，源码组另有普通多 SELECT。FAIL 的标准六入口均停在 CommandType setter；OUT/INOUT 停在声明解析，RETURN 尚无语句支持。`CALL n_two()` 实际第一列是 `second`，证明先前 SELECT 被丢弃。Text CALL 带 Output 参数会把其 Value 当普通输入执行，方向被静默忽略。

Core 正向合同草稿首次 21 例为 2 PASS /19 FAIL；补入命令先释放后 Reader 完成、过程重开/NULL 输出、多结果提前关闭且只写一次，最终 24 例为 **2 PASS /22 FAIL /0 skipped**。这些断言目前受 setter/语法前置失败阻断，不能宣称事务、取消或输出生命周期已经验证。扩充和最终格式整理后结果相同，三份实际 TRX、构建日志和失败堆栈全部保留，构建均为 0 警告/错误。最终程序集对应 `core-contract-before03`，早期两份是历史执行证据，不冒称均使用最终测试 DLL。

最终统一 UTF-8/LF 源码重新编译后，以相同的新消费者 DLL 对三组固定运行时各执行 native/SqlSugar，结果仍与表中一致，日志为 `final-public4-*`、`final-public-preview5-*`、`final-source-*`。远程第三轮保留它实际使用的编译版本和源码快照，不能混用最终消费者 DLL 的 SHA。

### 真实远程基线

使用根 #250 最终提交前的完整 Server 输出，只读复制后校验全部文件。它包含随后进入 67a 的生产实现，但程序集仍是提交前候选身份，不冒称于 67a 提交后构建。服务端 Core SHA `3041ADC7D6FE479F64F81182ADE0E9B450AFBC3D28514B08D65A0462F471D3CE`，Server SHA `6BECE97461FF2538EB96898FED536580B93FEECAE2C0ED8C0F3AB46BF20DCD88`。

首轮远程收集被 runner 的 CIM 子进程身份观察异常中断，没有完整产品结果；仅通过所拥有的根 Process 句柄终止该进程树。原 runner 与原回执保留；后续版本仅把身份不完整的观察项记录为不可单独终止，不依据不完整 PID 操作其他进程。

第二轮真实 Server 启动/建库/建表成功，随后 ADO `CREATE PROCEDURE ... SELECT @p_left` 在客户端 `ParameterBinder` 抛出缺少参数，过程定义未到达 Server。该失败记录在 `remote-before02`，不是网络失败或存储过程 Server 解析结果。

第三轮使用公开 HTTP SQL 端点预建同一组原生过程，再由真实 ADO/SqlSugar 走 REST 与 frame-http2。探针额外保留远程 ADO 建过程用例，预建不计为该用例通过。远程消费者增加环境变量连接和显式外部 fixture 支持，因此其消费者 DLL 与上述三组嵌入式比较 DLL 分开冻结；Core/Data/SqlSugar 输入不变。完整结果以 `remote-before03/summary.json` 为准。

第三轮实际完成，REST 与 frame-http2 各为原生 **3 PASS /13 FAIL（16 例）**、SqlSugar **2 PASS /10 FAIL（12 例）**，无超时；`harnessCompleted=true` 且 `functionalAcceptance=false`。两种协议的 OUT/INOUT 定义被客户端缺参检查先挡住，不能据此声称远程服务端已执行 OUT/INOUT 解析测试；RETURN 没有变量占位符，实际到达 Server 并返回语法错误。

标准命令 setter 拒绝是客户端证据；只有日志中实际出现的 HTTP/1.1 SQL 或 HTTP/2 Frame 请求才是相应远程证据。服务端为清理而终止，不构成故障恢复验收。

实际请求须进一步区分：REST 为 19 条相关请求（17 条 SQL，2 条 session 管理）；`Protocol=frame-http2` 为 19 条 HTTP/2 请求，其中只有普通 SELECT batch 的 2 条走 `/v1/frame`，其余 15 条 SQL 与 2 条 session 管理。当前 `IsFrameEligibleReadOnly` 不接收 CALL，故 CALL 实际走 HTTP/2 的 REST/NDJSON 路由。这里证明的是 h2c 配置下的实际调用行为，**没有证明原生 Frame 过程合同**。

## 代码中的真实缺口

| 部位 | 基线现状 | 实施需要 |
|---|---|---|
| `SndbCommand.CommandType` | 仅允许 Text/TableDirect | 经统一执行租约处理 StoredProcedure；不能为过程另建连接 |
| `SndbParameter.Direction` | 普通可设置属性，Text 忽略 | 验证合法枚举与方向；不支持的命令形态显式拒绝 |
| `IConnectionImpl` / `IExecutionResult` | 仅 SQL 执行、行/元数据/NextResult，无输出完成信息 | 专门的命名过程调用及一次性完成结果，不靠拼接 CALL 或再次执行取得输出 |
| `SqlProcedureParameter` / Parser | 仅 Name/Type，拒绝 OUT/INOUT | 原生方向；过程作用域赋值和返回值语句 |
| `SqlRoutineRuntime.ExecuteCall` | 按位置绑定，循环后返回 lastResult | 保留全部结果、影响行数与成功输出；保留现有 savepoint/预算/诊断 |
| `RoutineCatalogCodec` | 格式 v4，每参数仅名字/类型；重开时重解析 body | 升级专属目录格式并兼容旧版 IN；损坏/未知方向拒绝；不能只改内存 AST |
| `Remote.ParameterBinder` | 逐字符扫描所有 `@`/`:`，误吃过程体变量 | 区分过程定义作用域与 ADO 值参数；保留现有真正占位符的缺参检查 |
| Server/REST/Frame DTO | 当前结果无标准过程输出信封 | source-generated 类型化请求与完成数据，旧服务端能力不支持时准确报错 |

相关源码在 `src/SonnetDB.Core/Routines/`、`src/SonnetDB.Core/Sql/`、`src/SonnetDB.Data/`、`src/SonnetDB/`。根目录的旧约定 `src/SonnetDB` 在当前仓库是 Server，核心库实际在 `src/SonnetDB.Core`。

当前过程参数类型只有 Int64、Float64、Boolean、String。不能因为普通 SQL/表列支持 decimal、DateTimeOffset、Duration 就宣称过程参数也覆盖。初始实现应把支持范围和不支持的 DbType 检查写清；扩展类型须有原生转换、目录与跨协议证据。

## 建议的实现合同（尚未实现）

1. 在进入统一执行租约后，冻结本次调用的过程名、参数名/方向/类型/输入值与输出目标对象。按名字匹配声明，允许既有参数前缀归一，拒绝缺失、额外、重复、方向不符、多于一个 ReturnValue；参数集合顺序无意义。未完成期间不要让修改后的集合决定输出目的地。过程名按现有 catalog 名称合同解析，禁止把任意 CommandText 当多语句执行。
2. 新的原生调用入口接受类型化参数，复用现有执行上下文、调用深度、语句/行预算、访问权限和事务 savepoint。IN 只读，OUT 从 NULL 开始，INOUT 从输入开始；只在完整成功后形成不可变输出结果。现有 Text/CALL 的普通 IN 语义保留。嵌套 OUT 调用和外层变量赋值必须明确绑定；尚不支持的形态在绑定时拒绝，不能静默丢弃。
3. 草稿采用 `SET @p_state=...` 和 `RETURN 7`；它们是提议语法。SET 与已有 trigger `SET NEW.column` 必须分开解析/验证；RETURN 结束当前过程，返回 Int64 状态，不能成为一个额外 SELECT。无 RETURN 的默认值、只读 IN 赋值拒绝、溢出、NULL 与类型错误都须补正反用例。
4. 每次过程只执行一次，结果按程序顺序暴露。Scalar 返回首个结果的首值并完成余下结果；NonQuery 按明确的影响行数规则消费完成；Reader 提前关闭、遍历完、多次 Close/Dispose、DisposeAsync 均收尾一次。Reader 关闭之前，输出参数保留调用前值；成功关闭后一起回写，SQL NULL 转为 `DBNull.Value`。失败、超时、取消、读取或清理失败不能部分发布输出。
5. Reader 对执行资源和完成回调持有独立引用，调用方提前 Dispose Command 仍能正确关闭 Reader（真实 SqlSugar 就如此使用）。关闭顺序须兼顾输出回写、租约释放和 `CommandBehavior.CloseConnection`；不得为写回输出重新调用过程。
6. 调用方已有事务继续使用同一事务状态/远程 session。过程失败回滚到 savepoint；过程成功不替调用方提前 commit。没有外层事务的写过程沿用原生事务行为。远程响应丢失后的提交状态仍可能未知，不引入自动重试来冒称 exactly-once。
7. 远程请求携带过程名与类型化参数，完成响应携带结果/输出，不把用户值拼到 SQL。REST/Frame DTO 同时注册 source generation，保持 AOT/Trim 0 告警；旧服务端显式能力拒绝，不能回退为忽略输出的 CALL 文本。大型结果沿用既有有界读取、预算和截断合同。
8. routine 目录当前 v4；实际落地必须在 #249/#248 合入后重新核版本。旧目录按 IN 迁移，方向/body 同步持久化，新格式旧引擎明确拒绝。此准备不预占格式号，也不修改 FileHeader 布局；若最终触及已发布二进制布局，按 AGENTS 同步版本、迁移/拒绝和 CHANGELOG。

## SqlSugar 实际调用链

固定原始 `SqlSugar.SonnetDBCore/SonnetDB/SonnetDBProvider.cs` 直接构造真实 `SndbCommand`，设置 CommandType/timeout/transaction；`ToIDbDataParameter` 把 `SugarParameter.Direction` 复制到原生参数，并记录非 Input 的 OutputParameters。无需建立伪 SqlSugar 桥或绕过 `UseStoredProcedure()`。

但基础 `SqlSugar/Abstract/AdoProvider/AdoProvider.cs` 的 `GetDataReader`/`GetDataReaderAsync` 在返回 Reader 之前调用 `ExecuteAfter`，当场把原生参数 Value 复制到 SugarParameter 并从 OutputParameters 移除，然后可能立即 Dispose Command。其泛型多结果路径虽另有 DataReaderParameters 回拷，也不能推定直接 GetDataReader 的关闭回调已成立。

因此原生驱动遵守 Reader 关闭后回写，还不足以证明 SugarParameter 在直接 Reader 路径可见最终输出。实施阶段需要真实上游局部候选修正，在不改变数据库输出时机的前提下，于 Reader 完成后传递输出；覆盖同步/异步/提前关闭/命令先释放、重复关闭、异常和多个结果。现阶段原始适配器未改，三个 Direction 消费者用例停在未支持的声明，尚未触及上述回写分支。

## 后续验收与继续顺序

先在 #249→#248 的实际提交上接续，复审共享 AST/元数据/传输与目录变化，再实施 native runtime/目录、ADO 生命周期、三协议、真实 SqlSugar，最后扩大回归和 AOT。现有正向草稿不是全量验收，还需补：严格命名与注入、方向/类型/NULL/边界、缺参/重复参、嵌套调用与 RETURN、目录旧版/损坏/强制终止恢复、运行中 Cancel/timeout、并发同命令/连接关闭、读取错误和老客户端/老服务端组合。

保留所有首次失败，不通过关闭审计、降低告警或绕过真实接口取得 PASS。固定上游的已知 NU1902/NU1903 依赖问题沿用单独边界；本轮直接引用完整冻结 DLL 的编译成功不代表其 NuGet 审计通过。准备阶段没有 NativeAOT 发布执行、正式 NuGet/Server 发行、SqlSugar 上游合并或正式消费者验证，因此 #247 不具备关闭条件。

根监督代理统一维护 HANDOFF/CHANGELOG、最终完整 restore 和规定 Format Check、提交推送及外部回复。本分支只交付新增准备文件与证据清单，不把当前故意失败的草稿直接当作可合入生产测试。


## 2026-10-17 人工整合补记

本准备交付已纳入主线保存。原24项正向草稿完整移动并仅统一 LF 行尾为 `eng/probes/issue247-stored-procedure/StoredProcedureCommandContractTests.cs.txt`，避免尚未实现的合同进入正常CI；不修改其断言或声称通过。#247仍未实施。本段取代本报告开头的当时“本轮不提交”状态，不改变原始基线和失败证据。定时监督继续PAUSED。
