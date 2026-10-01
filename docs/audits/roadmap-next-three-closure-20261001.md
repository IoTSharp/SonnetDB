# 2026-10-01 第四批并行实现

本轮从 `codex/roadmap-four-closures-20261001` 的干净工作树继续，基底为 `1f900c7c2d4031da09fc0d520eb29834c8bcaa21`。先阅读主路线图、总里程碑和第三批闭环报告，再以实际代码确认三个缺口。三个子智能体分别独占文档 SQL、窗口及订阅/DLQ 文件；主智能体负责共享选项注释、两项组合恢复、路线图、能力索引、统一验证和清理。此前被拒绝删除的临时目录未被接管。

实现分为三个独立提交：`7606f366`（文档 SQL 累计物化预算）、`bddad488`（持久字符串分组窗口）、`6876812c`（条件持久 DLQ）。本报告、共享路线图和两项组合恢复测试在随后独立的验收记录提交中交付。

| 切片 | 真实缺口与交付 |
| --- | --- |
| M42 文档 SELECT 预算 | 文档模型此前被 opt-in 预算拒绝。直接无 TTL 集合按 ID 逐条读取，过滤和分页后、保留完整投影行前计入根累计行/估算字节预算。锁内复检 TTL 避免并发建索引后误入全量清理。 |
| M43 分组窗口 | 原持久窗口只有非分组 COUNT/decimal。显式顶层 JSON string 键按 Ordinal 原值分组，支持 COUNT 及数值聚合；限制键数、结果数、批次/状态字节，使用起点加键的排他分页游标。 |
| M43 条件持久 DLQ | 耗尽批次此前只能 reset/ACK。管理员显式匹配 delivery ID、state revision 和 attempt 后，先保存完整事件及原因，再推进 checkpoint、回收 spool 并完成隔离意图；重开校验并协调中断阶段。 |

合同分别见[文档预算](../benchmarks/m42-document-result-bounds.md)、[分组窗口](../m43-grouped-windows.md)、[条件 DLQ](../m43-subscription-dead-letter.md)。公开记录保持已有主构造/解构，订阅保留原四参数 `OpenAsync` 的 CLR 签名，新增独立 overload。分组状态显式使用 v3，COUNT v1 与数值 v2 原形状及 SHA-256 保持；DLQ 使用独立文件，订阅 v1~v3 合同不改变。未修改引擎固定二进制格式，未新增核心运行时依赖，JSON 使用源生成或手写 reader。

两项 `FileStreamingGroupedDeadLetterJourneyTests` 使用真实本地文件 API。第一项在分组数值提交后、ACK 前关闭，重开核对原 delivery ID 和重投去重，处理尾批次并分页对账 `Pump`/`pump`。第二项让非 string 分组键整批拒绝，再隔离原事件并重开，核对 payload、headers 和原因。旧聚合器拒绝已跳过的 checkpoint；调用方显式用另一状态文件和隔离后的 checkpoint 创建新基线，才消费正常尾批次。没有自动改变既有窗口结果或宣称隔离事件已被聚合。

## 验证

验证使用 PowerShell 7.6.6 和固定 .NET SDK 10.0.401，隔离 artifacts 与短 TEMP 位于本任务专属 `C:\Temp\sdb4-5115d3fe`。执行 wrapper 每次设置墙钟期限和最大检查次数，记录 PID、创建时间、完整命令行及父进程；支持取消标记，最多跟踪 256 个任务进程、16 层树。编译并发为 2，关闭共享编译器及节点复用，不接管 VS Code 的后台构建。

| 检查 | 最终结果 |
| --- | --- |
| 标准与隔离 solution restore | 均通过。 |
| 全方案 Release build | 0 warning / 0 error；生产 AOT/trim 分析启用。未执行 NativeAOT publish。 |
| 完整 Core | 5717 / 5717，通过；0 failed / skipped / notExecuted。 |
| 本批新增 | Document 36、分组窗口 48、DLQ 24、组合恢复 2，共 110 / 110，通过。 |
| 相关既有与新增合同 | 最终完整 TRX 的过滤视图 372 / 372，通过。 |
| 状态 scanner 小规模试跑 | 重复属性 5 项及缺失默认字段 4 项，共 9 / 9，通过。 |
| 真实服务 SQL | REST/Frame/ADO 预览、measurement、文档 JSON 和注册表配置，共 67 / 67，通过。不代表远程预算接口。 |
| 持久订阅硬杀恢复 | 1 / 1，通过；新建子进程输出的 Core DLL SHA-256 与最终构建一致。 |
| CI Format Check | `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/` 通过，exit 0。 |
| 十四能力索引及差异检查 | validator 和 `git diff --check` 通过；14 capabilities、14 journeys、3 composedJourneys。 |

首轮新增定向回归为 94 passed / 4 failed / 98 total：三项测试错误地将已有 `json_value` 数字 `Double` 强转为 `Int64`，一项使用 `DOCS` 引用按 Ordinal 保存的 `docs` 集合。修正测试的类型与集合原名假设，生产合同未改变；后续全部 110 个新增用例通过。首轮编译后又增加状态完整性用例，98 不是最终新增数量。Format stderr 仍有加载工作区警告提示，没有格式错误。

交叉审查补强两处严格恢复边界：v3 分组状态在反序列化前拒绝重复 JSON 属性及缺失定义/容量字段；独立 DLQ 拒绝 checkpoint 中缺失的默认 revision 等字段，避免以反序列化默认值补全后通过原哈希。回归保留或重算合法 SHA-256，证明拒绝来自结构检查。DLQ 首次空 journal 在专属目录写入后原子发布，发布前失败可重试，正式目录缺失状态仍 fail closed。

证据目录：[validation.json](roadmap-next-three-evidence-20261001/validation.json)、[新增结果](roadmap-next-three-evidence-20261001/new-tests.json)、[相关结果](roadmap-next-three-evidence-20261001/related-tests.json) 保存实际命令状态、最终 TRX counters/哈希、首轮失败、最终源码 SHA-256；同目录保留命令配置、进程身份及 stdout/stderr 文本。暂存检查后统一证据文本的 LF 换行并移除行尾空白，命令、数字和诊断内容不变。完整 TRX 和编译输出在摘要及哈希留存后回收，证据对应提交前最终源码快照。

[cleanup.json](roadmap-next-three-evidence-20261001/cleanup.json) 核对 149 条进程身份记录，没有本任务存活进程；已回收专属目录内 5103 个文件/目录条目及任务指针。仅回收本任务对象，未触及共享缓存、用户文件或此前被拒绝删除的目录。

## 验收边界

文档预算是 Core 调用的 opt-in 合同；REST/Frame 未新增预算请求。排序、聚合、全文/向量、JOIN、嵌套文档来源及 TTL 集合在扫描前拒绝。保留行计费不涵盖底层 KV overlay/segment 快照、单文档反序列化或表达式瞬时分配，不代表 CLR heap 硬上限、快照分页或固定硬件性能完成。

分组仅支持顶层 string 原值及固定 UTC 滚动窗口；数值精度、迟到 DROP/REJECT 与 AVG 舍入沿用原合同。滑动/会话窗口和自动 schema 演进未由本轮完成。

DLQ 与订阅是单目录、单执行者的本地运维入口，权限由宿主控制；没有远程运维、分布式租约、外部副作用事务或 exactly-once。隔离整批而非单个坏事件；容量满时拒绝，未提供重放或删除。任何持久提交后的异常都可能结果未知，必须重开检查。新 DLQ 文件需要随订阅目录一起保留，旧实现不能处理未完成隔离意图。

本轮没有执行真实掉电、固定 x64/ARM64、NativeAOT publish、真实模型质量/成本、远程 CDC/流协议、安装、nightly 或 168 小时门禁；这些继续为 `NOT_RUN`/`DEFERRED`。M43 十四项总验收不由本地切片完成。
