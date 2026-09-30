# 测试与兼容代码瘦身审计（2026-09-30）

审计发现：存在优化路径调整后未回收的内部旧实现，也存在只被兼容测试维持的废弃公开 API。应该一起处理生产代码和测试，不能只减少测试计数。本轮已按高置信度清单实施第一批清理，并保留仍承担持久化格式职责的兼容路径。

最明确的生产清理项是 5 个无调用内部方法；最明确的旧公开实现是 `WalTruncator.SwapAndTruncate`，仓内只有 7 个废弃接口测试调用。另有窗口函数的重复算法，以及 3.0.1 的构造/解构兼容成员。没有证据将其余数千测试整体认定为历史残留。

## 审计范围与证据边界

- 工作目录：`D:\source\SonnetDB`；分支：`codex/test-compat-cleanup-20261001`。
- HEAD：`33e116a6c8a890ed3e2aacb50ea8f7d877a17ecd`；分析包含当前未提交工作树，尤其 GH #211 标识符合同变更。
- 静态盘点 `tests/` 中 736 个 C# 文件；重点人工复核兼容、恢复、IO、窗口函数、SQL、Server fixture、发布及证据测试。工作树在审计期间继续变化，规模表采用最后一次扫描结果。
- 调用检查覆盖 `src/`、`tests/`、`eng/`、`samples/`、`connectors/`、`extensions/` 中的 C#；区分同名类型的不同方法、不同重载及条件编译。
- 初始审计没有运行构建、测试、覆盖率或性能基准；删除后的验证结果见本报告末尾。无调用是仓内静态证据，不代表知道所有外部消费者。
- 本轮已修改清单中的生产代码和测试；没有修改 AGENTS 或既有 CHANGELOG 条目。清理后的定向构建、测试和格式检查结果在本报告末尾补记。所有任务命令均已结束，无任务创建的后台进程或临时文件需要回收。

## 本轮已实施的清理

- 删除 5 个无仓内调用的内部残留方法：KV 全量旧加载器、两个字典保存重载、旧磁盘分页包装，以及旧覆盖索引包装。
- 删除已废弃 `WalTruncator.SwapAndTruncate` 及其 7 个专属测试；旧 `active.SDBWAL` 数据升级路径保留。
- 删除 3.0.1 专属的 5 个 API 兼容测试、对应旧构造/解构/`TableSchema.Create` 转发成员，以及对象读取结果五参构造/五元素解构和专属测试。4.0 主版本重新编译要求由发布说明承担。
- 删除静态 IoTSharp 兼容矩阵及其条件编译文档自检；矩阵已迁出 SonnetDB，真实 EF provider 测试仍保留。
- 删除重复 IO、WAL 文件名、Admin 路由和窗口函数自比测试；把 VarUInt 长度断言并入编码测试，收敛固定宽度 Position 参数。
- 删除仅检查脚本/README 字符串的 Studio release 测试，继续使用实际 release artifact verifier。

## 测试规模：定义数与执行用例数分别计算

下表按源码 `[Fact]` / `[Theory]` 统计。Theory 的每行数据可展开为一个执行用例；条件编译、动态数据及实际 test discovery 会改变执行数量。

| 项目 | 含测试文件 | Fact | Theory | 测试方法定义 |
|---|---:|---:|---:|---:|
| SonnetDB.Core.Tests | 405 | 4172 | 314 | 4486 |
| SonnetDB.Tests | 108 | 747 | 123 | 870 |
| SonnetDB.Benchmarks.Tests | 10 | 58 | 3 | 61 |
| SonnetDB.EntityFrameworkCore.Tests | 1 | 56 | 1 | 57 |
| SonnetDB.Studio.Tests | 4 | 25 | 11 | 36 |
| SonnetDB.Parity | 6 | 23 | 0 | 23 |
| SonnetDB.CrashTests | 2 | 21 | 3 | 24 |
| SonnetDB.IoTSharpCompat.Tests | 3 | 9 | 0 | 9 |
| SonnetDB.Accuracy.Tests | 2 | 9 | 0 | 9 |
| SonnetDB.Media.Tests | 1 | 8 | 3 | 11 |
| SonnetDB.LanguageServer.Tests | 2 | 6 | 0 | 6 |
| 合计 | 544 | 5134 | 458 | 5592 |

清理后的 Core 有 314 个 Theory；IO Position 参数已经收敛为 Fact，另有一处 MemberData。用户看到的五千多个执行用例并不等于五千多个独立测试方法；本轮仍需用 test discovery 和 TRX 复核实际展开数。

初次区域盘点中，Core 最大区域是 SQL：101 个文件、1433 个方法；其次为 Engine 291、Query 259、Storage 254、KV 189、Vector 189。末次扫描时 Core 增加了 2 个 Fact，见上表。数量较多主要来自当前功能和边界，而非单一历史兼容套件。

普通 CI 在 Windows/Linux 对整个 `SonnetDB.slnx` 执行测试，见 `.github/workflows/ci.yml:74`；没有过滤 `Category=Documentation`。Parity 不在该 solution 中，由单独的 `parity.yml` 调度，不能把它的参考服务测试全部计入普通 CI 的负担。

## 第一批：已删除的内部生产残留

| 生产位置 | 已删除项 | 替代路径与调用证据 |
|---|---|---|
| `src/SonnetDB.Core/Kv/KvStateFile.cs:126` | `Load(string)`，约 70 行的全量 value 内存加载实现 | 仓内未找到调用。`KvKeyspace.LoadLatestState` 在 `KvKeyspace.cs:2877` 统一调用 `OpenDiskState`；旧 state v1-v4 与当前 v5 都在此路径解码。 |
| 同文件 `:21` | `SaveSnapshot(IReadOnlyDictionary<...>)` 的排序重载 | 未找到该重载调用。checkpoint `KvKeyspace.cs:3110` 和现有测试使用 `IEnumerable + count` 重载。 |
| 同文件 `:36` | `SaveSegment(IReadOnlyDictionary<...>)` 的排序重载 | 未找到该重载调用。checkpoint `KvKeyspace.cs:3101` 使用流式重载。 |
| 同文件 `:474` | `KvDiskState.ScanPrefixAfter` 旧包装 | 未找到调用，当前磁盘索引扫描使用 `ScanRange`。这里不能误删仍被 Server、Document 等使用的公开 `KvKeyspace.ScanPrefixAfter`。 |
| `src/SonnetDB.Core/Tables/TableStore.cs:1187` | `EnumerateCoveredIndexEquality` 旧包装 | 未找到调用；SQL 在 `TableSqlExecutor.cs:2965` 与覆盖索引测试直接使用 `EnumerateCoveredIndex`。 |

这些方法所在类型或方法是 internal；删除无需改变公开 API，也不改变持久化格式。新的磁盘读取路径仍须保留版本、CRC、TTL、generation 和恢复验证。

不能把 `KvStateSnapshot.Values` 连带删掉：它仍用于 WAL replay 的内存增量，`KvKeyspace.cs:2922/2964` 会写入，checkpoint 会合并它与磁盘 state。全量旧加载器废弃不意味着所有内存 overlay 都废弃。

由于上述方法没有调用，删除的直接收益是减少维护、审查和误用入口，不应宣称查询吞吐因此提高。源码不能证明当初方案是否错误，但能证明旧实现当前已无调用价值。

## 第二批：明确废弃的公开 API，与测试一起退役

### WAL 旧截断策略

`src/SonnetDB.Core/Wal/WalTruncator.cs:32` 已标记 `[Obsolete]`，注释明确要求改用 `WalSegmentSet.Roll + RecycleUpTo`。它仍保留完整 rename、dispose、删归档、重建 active WAL 的另一套实现。

仓内只有 `tests/SonnetDB.Core.Tests/Engine/WalTruncatorTests.cs` 的 7 个 Fact 调用；测试还专门压制 CS0618，理由是验证向后兼容。没有找到生产、样例或连接器调用。

本轮已在 4.0 公共 API 收口中删除该类及这 7 个测试。旧 `active.SDBWAL` 的数据升级继续由 `WalSegmentLayout.UpgradeLegacyIfPresent` 和 `Tsdb.Open` 负责；退役旧截断 API 不等于退役旧 WAL 数据读取。

### 3.0.1 的公开签名合同

`src/SonnetDB.Core/SonnetDB.Core.csproj:12/13` 明确允许首个 4.0.0 打破 3.x 公共 API，后续应以 4.0 为 baseline。允许主版本变更提供了清理时机，并不表示所有旧成员已经退役。

`tests/SonnetDB.Core.Tests/Compatibility/PublicApiCompatibilityTests.cs` 中 7 个 Fact 有 5 个专门维护 3.0.1：

| 测试起始行 | 合同 | 对应生产兼容代码 |
|---:|---|---|
| 94 | `TokenKind_NewMembers_PreserveVersion301NumericValues` | 旧 enum 数值合同。没有找到 token 数值写盘/wire；但 `SqlFingerprint.cs:59` 依赖数值顺序，不能随意重编号。 |
| 107 | CreateTable 五参数构造与五元素解构 | `Sql/Ast/Statements.cs:58/76`，旧 ctor 转发主 ctor，旧 Deconstruct 只读属性。 |
| 131 | Select 十四参数构造与十四元素解构 | 同文件 `:985/1036`，转发及属性读取。 |
| 186 | Explain 十四参数构造与十四元素解构 | `Sql/Execution/SqlExplainPlanner.cs:54/105`，转发及属性读取。 |
| 238 | `TableSchema.Create` 七参数签名 | `Tables/TableSchema.cs:99`，转发八参数重载，`checkConstraints=null`。 |

可以在明确的 4.0 API 变更清单中退役这 5 个历史测试及 7 个兼容成员（3 ctor、3 Deconstruct、1 Create）。旧短参数源码调用通常仍能绑定带默认参数的当前重载；旧已编译程序集需要重新编译。

测试并非都能证明旧二进制签名存在：短参数构造/Create 调用在删除旧重载后仍可能编译；真正继续锁住旧 arity 的是三条 Deconstruct 测试。

同文件前两条当前 FullText/Generation API 测试另行评估，不能随历史合同一起批删。Generation 的 ResourceKind 数值写入 `DatabaseGenerationCodec.cs:27`，属于持久化格式。

另一个已实施的退役项是 `SndbObjectModels.cs:93/106` 的五参数构造/解构，以及 `SndbObjectStoreTests.cs:163` 的 1 个 LegacyFiveParameterApi Fact。当前公开实现明确声明服务旧消费者，4.0 升级需重新编译直接消费者。

## 第三批：重复算法应收敛，而非只删对照测试

窗口函数 CumulativeSum、RunningExtreme、MovingAverage 同时维护 typed batch、boxed batch、streaming state：

- `Query/Functions/Window/RunningFunctions.cs:97/180` 的两个 `ComputeObjectCore`。
- `Query/Functions/Window/SmoothingFunctions.cs:68` 的 `ComputeObjectCore` 和 `:130` 的 object 输出计算重载。

SQL 在 `Sql/Execution/SelectExecutor.cs:1134` 对这些 evaluator 使用 typed batch；boxed Compute 不在这三类的 SQL 路径上，仓内主要由测试直接调用。它仍通过公开 `IWindowEvaluator.Compute` 暴露，所以不能断言没有外部调用。

建议保留公开 Compute 接口，让 boxed 调用复用 `DoubleWindowEvaluatorBase.ComputeObjectCore` 的 streaming state，删除独立 boxed 算法；用预期值、NULL、边界和必要的性能验证确认等价。typed batch 仍是混合非 streaming 窗口时的活路径，不能删。

EWMA 与 HoltWinters 已经复用此基类，`WindowFunctionTests.cs:351/470` 的两个 `MatchesCompatibilityCompute` 测试实际对同一 state.Update 算法做自比，本轮已删除。其它三类的类似测试目前比较不同算法，应先收敛实现再收敛测试。

## 可审查的测试精简清单

以下数量是展开后的执行用例减少数；包括删除、断言合并、参数收敛，不是单纯删除测试方法。

| 已处理位置 | 处理 | 可减少用例 |
|---|---|---:|
| `IoTSharpCompat.Tests/CompatibilityMatrixBaselineTests.cs:5` | 删除静态文档矩阵自检，将矩阵保留为文档。1 Fact + 46 InlineData 不运行数据库，注释已要求从通过数排除。 | 47 |
| `Studio.Tests/StudioReleaseContractTests.cs:7` | 删除源码/说明文字 Contains 检查，采用实际 release artifact 检查。现有 `eng/verify-release-artifacts.ps1` 与其合同测试已覆盖 Studio 包 Server 内容；实机升级/卸载仍需实际验证。 | 1 |
| `SonnetDB.Tests/AdminUiEndToEndTests.cs:125` | 删除重复的无 Authorization GET `/admin/login`；`:97` 已验证同一响应。 | 1 |
| `Core.Tests/IO/SpanReaderTests.cs:231/250` | 删除与 SpanRoundTrip 同输入、同写读路径的两个 VarUInt Theory；后者还断言 IsEnd。 | 15 |
| 同文件 `:263/274/285/296` | 删除已被 String round-trip Theory 覆盖的 null/empty/ASCII/中文四个 Fact。 | 4 |
| `Core.Tests/IO/SpanWriterTests.cs:98` 起的 8 个数字 Position Theory | 38 个 case 只断言固定宽度 Position；每类型留一个 Fact，数值边界由 round-trip 覆盖。 | 30 |
| `Core.Tests/IO/SpanRoundTripTests.cs:381` 起的 MeasureVarUInt32/64 | 将 Measure 断言并入 Writer 的编码长度 Theory，移除其数据子集的两个方法。 | 11 |
| `Core.Tests/Query/Functions/WindowFunctionTests.cs:351/470` | 删除 EWMA/HoltWinters 同算法自比，保留具体期望值与当前 streaming 行为测试。 | 2 |
| `Core.Tests/Engine/LegacyWalUpgradeTests.cs:44/87/111` | 3 Fact 合成 1 个真实 Tsdb.Open 升级测试，加入非默认 startLsn=42 与目标文件名断言；普通启动已被其它正常路径覆盖。 | 2 |
| `Core.Tests/Wal/WalSegmentLayoutTests.cs:32` | 删除仅验证十六位大写文件名的 Fact；`:25` 精确字符串 Theory 已覆盖大小写、补零、0 和最大值。 | 1 |
| 合计 | 不含需要撤销公开 API 合同的测试 | **114** |

本轮源码盘点显示定义方法从 5625 降为 5592，Fact/InlineData 近似展开数从 6991 降为 6863，约减少 128 个展开用例；MemberData 与条件编译仍需以 test discovery 和 TRX 为准。

条件 IoTSharp 源码测试中的纯说明文本 Fact 与 `IoTSharpCompatMatrix` 也已删除；`ApplicationDbContext` 的真实 provider 行为测试和 `ShardingCore` 测试仍按外部源码条件编译。

API 退役批次另外涉及 WAL 7 个、3.0.1 合同 5 个、对象旧签名 1 个，共 13 个测试执行项；它们不计入上表的 IO/重复测试清单。

## 比计数更优先的执行质量问题

1. `CopilotInfrastructureTests.cs:26` 的 17 个 Fact 每项启动一个 Kestrel Server，只有 `HealthEndpoint_ReturnsCopilotFlags:536` 使用这套 Server。其余 16 项可拆成直接测 readiness/provider/DI 的单元测试，减少 16 次无必要的 Server 启停，保留行为覆盖。
2. `InfluxDbAccuracyTests.cs` 的 8 个 Fact 在设施不可用时直接 return；fixture 会吞下启动/seed 异常并设置原因。它们被计为 Passed，而非 Skipped。应迁入明确准备 Docker/Influx 的集成作业，设施缺失显式 skip，要求执行的作业遇到启动/seed 失败则失败。
3. `AdminUiEndToEndTests` 的 5 个 UI Fact 遇到 503 直接 return，favicon 遇到 404 也 return；普通构建默认 `BuildAdminUi=false`。应使用已发布 SPA fixture 或独立 UI smoke 作业，避免未执行却通过。API 鉴权与 llms 路由仍保留。
4. `USearchNativeIndexTests` 的 6 个 Fact 在不支持平台直接 return，建议显式 skip 并保留支持平台的真实原生能力测试。

没有取得当前工作树全量耗时基线。现存 `artifacts/m39-production/m39-full-core.trx` 是 2026-09-06 的 4217 用例、175.4 秒，不能当作当前五千多用例的运行耗时；文件名 `core-final.trx` 等还可能是定向运行，必须看 Counters 与命令过滤。

## 应保留的兼容边界与可优化的真实开销

- KV state v1-v4、WAL v1/v2 在 `docs/kv-keyspace.md:119/131` 明确承诺读取兼容。新读取实现已经负责兼容，不需要因此保留旧全量加载器。
- TableSchema 的旧格式测试覆盖索引、JSON path、ROWVERSION/FK、CHECK/default、AUTO_INCREMENT 的不同布局；不能仅因 v1-v7 字样视作重复。Measurement schema 同理。
- KV generation 旧清库恢复的 5 个用例防止已清数据复活。时间戳 fallback 仅用于旧 metadata 缺 ResetSequence，当前 v2 使用显式序号；保留恢复与拒绝误判。
- `TableStore.cs:45` 无 migration marker 时调用 `MigrateLegacyRowsLocked`，新库也会进入一次扫描。它是活路径，可让新建现代布局库直接初始化标记，并将旧库迁移边界单独处理；不能当无调用死代码删除，中断恢复仍需验证。
- 表/measurement 旧 catalog 大小写冲突处理是用户明确要求的 GH #211 合同，当前相关变更不在此次删除范围。
- 对象旧预签名 token 两条测试分别验证原桶有效与删桶重建后失效，守护实际权限边界。
- Vector facade 的 L2 开方和内积负号不同于底层 compute 的平方 L2/正点积，相关测试不是重复。
- Returning194、窗口/Join/EXPLAIN 等历史追加文件多数验证级联、并发旧镜像、回滚、类型或执行计划等不同当前边界；文件名带 Issue 编号不能作为删除依据。
- M27/M36/M40/M41 等证据报告测试会拒绝伪造结果、错误 schema、hash fallback 与本机 smoke 冒充生产通过，属于当前证据工具行为。

## 推荐实施顺序与收口规则

1. 单独清理第一批 5 个 internal 死方法，运行现有 KV state、checkpoint、generation/recovery 和 covered-index 定向回归；不新增镜像实现的测试。
2. 按清单合并重复测试、收敛参数、移走文档自检，并修复上述无执行却 Passed 的路径。分别记录方法数、展开 case 数、耗时及覆盖变化，避免只报总数。
3. 在 4.0 公共 API 变更中记录已退役的 WalTruncator 与旧构造/解构签名，要求直接消费者重新编译；保持当前功能、网络协议和持久化格式验证。
4. 收敛窗口 boxed 算法并验证外部 batch 性能；独立优化新库 migration marker，避免把维护瘦身与吞吐改进混报。
5. 提交代码前，必须在最终工作树完成仓库要求的 restore 与完整 Format Check。

建议后续优化收口遵循：内部实现路径变更后，旧实现若无当前调用、无已发布数据迁移责任，就删除，不以“兼容”名义永久保留；兼容责任尽量由一条当前实现承担。回归测试应断言当前正确行为及数据边界，不能仅把旧实现作为正确性 oracle。公开 API、wire 和磁盘格式分别记录支持范围及退役理由。

## 2026-10-01 实施验证

- `dotnet restore SonnetDB.slnx --nologo`：通过，所有项目已是最新状态。
- `dotnet build tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj -c Release --no-restore /warnaserror`：通过，0 警告、0 错误。
- `dotnet build SonnetDB.slnx -c Release --no-restore /warnaserror`：通过，0 警告、0 错误。
- Core 受影响定向回归：342/342 通过；Core 全量：5456/5456 通过。
- Server Admin UI：8/8 通过；IoTSharpCompat：1/1 通过；Studio：57/57 通过。
- 强制格式检查 `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/`：退出码 0。工作区加载时输出一条警告，但没有格式差异。
- `git diff --check`：通过。

本轮没有运行整个 solution 的全量测试，也没有运行覆盖率、NativeAOT、Parity、Docker、真实外部 IoTSharp 源码或性能基准；这些不属于本次删除项的必要验证，后续发布门禁仍需独立执行。
