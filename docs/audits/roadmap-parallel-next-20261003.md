# M42 表值函数物化准入并行增量

基线：本地 `main` 的 `4ad6c47ddd327d6a66f7ecdb34b0f20ec59e41da`，开始时工作树干净。
工作分支：`codex/roadmap-parallel-next-20261003`，使用原目录，不新建 worktree、源树或会话。

## 文件归属与真实缺口

预算模式在 `SqlMaterializationContract` 与 `SqlExecutor.EnsureMaterializationSourceSupported` 中统一拒绝除 Graph 外的表值函数。
已有 KNN O(K) 候选、文档向量搜索、文件查询，以及上一轮 M43 桥接/自动投递均不重复交付。

| 作者 | 独占生产代码 | 独占新增测试 |
| --- | --- | --- |
| knn_budget | `Sql/Execution/TableValuedFunctionExecutor.cs`、`Query/KnnExecutor.cs` | `Sql/KnnMaterializationBudgetTests.cs` |
| vector_budget | `Sql/Execution/DocumentVectorSearchExecutor.cs` | `Sql/DocumentVectorMaterializationBudgetTests.cs` |
| json_budget | `Sql/Execution/JsonFileSqlExecutor.cs` | `Sql/JsonFileMaterializationBudgetTests.cs` |
| 主智能体 | `SqlExecutor`、`SqlMaterializationContract`、`SqlTableFunctionMaterialization`、`SelectExecutor`、`SqlExplainPlanner`、公共选项注释、样例与文档 | 组合旅程、公共准入回归与既有拒绝 fixture 更新 |

生产路径均相对 `src/SonnetDB.Core/`；测试相对 `tests/SonnetDB.Core.Tests/`。
三位子智能体同时实施，不自行构建共享 obj、切换分支或提交。新增文件归属须先协调。

## 实现与交叉审查

- KNN：复用 O(K) 候选队列，实际入堆、替换、有序快照及标量输出累计计费；只回填引用字段的命中时间戳。替换锁等待最多 3000 次、每次 100 ms，并共用五分钟截止与根取消。
- 文档向量：单文档游标、元数据过滤、Top-K 保留及排序快照计费；已知 TTL 使用固定查询时刻的只读扫描。非 TTL ANN 的 hit 转 SQL 候选时计费，内部索引工作集另有存储边界。预算 EXPLAIN 不读取全集计数，以 `budgeted_estimate_omitted` 标注。
- 文件：UTF-8/BOM 逐记录读取，规范化 JSON/ID/ordinal 与投影累计计费；256 MiB 文件、4 MiB 记录/NDJSON 物理行、100 万候选、五分钟及根取消共同限定。LIMIT 只验证已消费前缀，完整读才验证尾部。
- 共享入口：直接来源许可、注册同名 TVF 回调及间接来源拒绝、标量 arity/CAST 预检、避免二次分页。`--budgets` 实际读取三条文件记录，保存到持久文档与 measurement，检查三种拒绝、正常查询和重开。

循环审查依次为 KNN→文档向量、文档向量→JSON、JSON→KNN；主智能体复核共享入口、样例与证据。
审查推动修正 JSON TVF 分发、候选快照漏计费、锁等待取消、并行共享预算、排序取消包装以及 JSON 标量/布尔与 CAST/参数个数预检。
首轮定向回归失败后，三位原作者分别修正各自文件，未产生共享构建写入。

## 回归发现与修复

初次 Release 构建暴露局部变量重名、索引列表的 `FirstOrDefault` 分析器警告以及锁测试的 nullable/xUnit 同步等待警告，均直接修正，未压制警告。
初次定向回归 464 项中 17 项失败、447 项通过，原始日志与 TRX 保留。
其中 KNN 的旧候选 256 字节预留与新增累计 160 字节预留同时占用共享阈值，导致精确字节边界及分页被过早拒绝；预算模式改由根预算统一预留，默认路径仍使用原候选预留。另增加实际预留量 160→320 与默认 256→256 回归。
其余新 fixture 使用了现有 TVF 解析器不接受的来源别名，或误把稳定取消错误/JSON 派生异常当作不同异常合同。改为来源 measurement 限定符、JOIN AST 预检以及既有取消错误码/内因断言；不扩展解析器或修改默认异常合同。
第二轮 466 项中 2 项失败、464 项通过：解析器生成的空 `Joins` 优先于 legacy `Join`，手工 AST 改设置 `Joins` 并先断言有效；带 BOM 的默认 JSON `auto` 不跳过 BOM，会把单行数组探测为一条 JSONL 记录，新增预算路径正确识别数组元素的独立回归，并明确保留默认行为差异。
最终新增测试实例为 KNN 44、文档向量 43、文件 46、共享准入 11、组合旅程 2，共 146；均为本地合同或持久重开语料。

## 验证与边界

执行证据将集中保存于 [本轮目录](roadmap-parallel-next-evidence-20261003/)。
修正后完整 Release 解决方案及最终测试项目构建通过，0 警告、0 错误。

| 检查 | 实际结果 | 原始证据 |
| --- | --- | --- |
| 完整 Release 解决方案 | PASS，0 警告/0 错误 | `release-build-corrected.out.log` |
| 最终测试项目构建 | PASS，0 警告/0 错误 | `test-build-final.out.log` |
| 定向回归 | 467/467，0 跳过 | [TRX](roadmap-parallel-next-evidence-20261003/targeted-final.trx) |
| 完整 Core Release 回归 | 6072/6072，0 跳过 | [TRX](roadmap-parallel-next-evidence-20261003/core-full.trx) |
| 托管真实 `--budgets` 入口 | PASS，导入 3、向量 ID=a、KNN time=0、拒绝 3、reopened=true | `managed-journey.out.log` |
| win-x64 NativeAOT 编译 | PASS，日志 0 警告/0 错误、0 IL/AOT 警告 | `aot-publish.out.log` |
| 原生真实 `--budgets` 入口 | PASS，与托管入口一致 | `aot-journey.out.log` |
| 最终完整 solution restore | PASS，39 个项目，单 worker | `final-restore.out.log` |
| CI 同级格式检查 | PASS，退出 0，2156 文件中 0 文件需修改 | `final-format*.out.log` |

AOT EXE 为 29,140,992 字节，SHA-256 为 `AE9CED4478642B78585B62E2FED1851ACB167D6E222F26274699CA07C35B36C7`，仅作本机验证，没有外部发布。
格式检查使用 `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/`，未降级检查。
通用工作区警告经 diagnostic 重跑核实，仅为 `docker-compose.dcproj` 不属于可格式化语言；Core、Core.Tests 和样例均已加载并运行分析器。两次检查均退出 0，没有修改代码。
命令参数、TRX 计数、源文件 SHA-256 与最终资源核查见 [validation-summary.json](roadmap-parallel-next-evidence-20261003/validation-summary.json)。
本地嵌入式文件/数据库重开与合同 fixture 不替代远程 parity、固定硬件、七天 scheduled、168 小时或生产门禁。
M42/M43 保持进行中。没有推送、远程合并、发布或部署。

## 资源执行规则

本轮使用新的 [执行器](roadmap-parallel-next-evidence-20261003/run-owned.ps1)。
所有长命令同时设最大轮询次数和墙钟超时，提供取消标记、两秒阻塞等待和进度报告。
每个归属链来自同一当前进程快照，核对根与 launcher 的 PID、创建时间、完整命令及父 PID；父创建时间晚于子则拒绝认领。
清理前重新取快照并核对完整父链，逐 PID 回收并记录动作；身份缺失或根已退出后的孤儿不推断归属、不终止。
单 MSBuild worker、两颗逻辑处理器、禁用 build server/node reuse。原 `.codex-temp/parallel-next-20261002` 保留，不复用、不再次删除。

本轮 NativeAOT 使用此前不存在的 `.codex-temp/parallel-next-20261003`，先记录 `ownership.json`，再输出到 `aot/`。
目录仅有所有权记录和已核对名称的三个 AOT 输出文件，二进制摘要已保存于 `aot-binary.json`。
自动审批审查在命令启动前拒绝了删除这三个文件并移除空目录的非递归清理动作，仅返回 `blocked by policy`；没有执行删除或尝试绕过。目录现保留，见 `temporary-cleanup.json`。
标准 `bin/obj` 与依赖缓存作为共享构建输出保留并保持 Git ignored，不纳入提交或宽泛清理。
最终执行器 SHA-256 为 `47C411907A4D2BB2BD7DE90D492B9E666827B53DB5BE97DAA0758403DB0963CE`。小输入试运行 `runner-owned-final-trial` 按预期退出 124，清理两条已核实归属进程，最终快照可用且 remaining=0。
使用该执行器的正式构建、测试、AOT 与格式检查最终快照可用且 remaining=0；短命令样例在首次 CIM 前已经退出，保留启动/退出记录，不把缺失的根 CIM 身份当作完整归属证明。
汇总脚本另有 32 次记录、每条 1000 个样本/128 条链/64 层、4096 个身份与当前进程、200 个文件、60 秒及取消标记边界；先完成单记录/单源文件/单测试 TRX的小输入试运行，再做完整只读核查。不创建独占 MCP、服务或下载任务。
