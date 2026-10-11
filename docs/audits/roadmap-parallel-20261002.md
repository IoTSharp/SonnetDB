# 2026-10-02 并行实现与验证

本轮从干净 `main` 的 `54e75dd6` 开始，在 `codex/roadmap-parallel-20261002` 上实现三项最新路线图缺口。三个子智能体分别独占 TTL 文档 SQL、窗口和 DLQ 文件，主智能体集中处理公共文档、组合恢复、验证和资源回收。初次实现完成后，用户另行授权按任务提交并合并本地主分支；本轮交付范围不包含推送、发布或部署。

| 切片 | 交付与实际入口 |
| --- | --- |
| M42 TTL 文档预算 | `SqlExecutor.Execute` 的显式结果预算逐文档判断 TTL，查询固定时刻、过期候选推进游标、过滤/分页后保留计费；预算冷开及 EXPLAIN 跳过 TTL 全量回收，修复时间相加溢出。 |
| M43 持久滑动窗口 | 四种显式滑动定义支持 COUNT、decimal、字符串分组及组合模式，size/slide 最多 128 重叠；复用全批提交、watermark、容量、分页与提交后 ACK 去重。 |
| M43 DLQ 重放/删除 | `ReplayDeadLetterAsync` 持久领取同一重放身份，`DeleteDeadLetterAsync` 按目录 revision、原身份、重放身份/次数完成或丢弃；删除回收容量，序号高水位保留。 |

合同见[TTL 文档](../benchmarks/m42-document-ttl-result-bounds.md)、[滑动窗口](../m43-sliding-window-contract.md)和[DLQ 运维](../m43-streaming-dlq-operations.md)。新增滑动定义为独立 v4，旧窗口 v1～v3 字段和原哈希保持；DLQ 校验旧 v1 原哈希后迁移至 v2，原订阅格式及公开主构造、解构和 `OpenAsync` CLR 签名保持。没有新增运行时依赖、修改固定引擎二进制格式或关闭 AOT/trim 分析。

主智能体的 `FileStreamingSlidingDeadLetterJourneyTests` 通过真实本地文件 API，将隔离、重放领取、重开同一 ReplayId、独立目标窗口提交后未 ACK、重开去重、条件删除及原流尾批次显式基线组合起来。消费者目标目录单独保存原事件；没有声称业务副作用与源 DLQ 删除之间存在事务。

交叉审查补强三个入口：预算 cold-open 和 EXPLAIN 不走全量 TTL 回收；DLQ 的旧状态读取句柄在 Windows 原子迁移前关闭；恢复同时校验目录高水位与 revision 最低不变量。合法 SHA 下的结构损坏负例继续保留。

## 验证

使用 PowerShell 7.6.6、.NET SDK 10.0.401，专属构建与 TEMP 目录为 `C:\Temp\sdbp-81bfff26`。外部命令有墙钟超时、最大轮询次数、取消标记和进度，记录 PID、创建时间、完整命令行及父进程；构建并发为 2、运行时可见处理器为 4，关闭共享编译器和 MSBuild 节点复用。既有 VS Code 后台进程不属于本任务。

| 检查 | 最终结果 |
| --- | --- |
| 标准及隔离方案 restore | 通过。 |
| 全方案 Release build | 通过，0 warning / 0 error；生产 AOT/trim 分析保持启用。 |
| 小规模边界试运行 | 5 / 5 通过，先验证 TTL 等号、两事件重叠窗口及单批 DLQ 领取/删除。 |
| 新增定向回归 | 98 / 98 通过：TTL 32、滑动窗口 38、DLQ 27、组合恢复 1。 |
| 完整 Core | 5815 / 5815 通过，0 failed / skipped / notExecuted。 |
| 真实服务相关合同 | 64 / 64 通过，包含结果边界、REST 预览、注册表预算、JSON 与 Document 端点。未新增远程预算请求。 |
| 持久订阅强杀恢复 | 1 / 1 通过，重投稳定 ID、ACK 及再次重开；执行程序集 Core SHA-256 与最终构建和 Core 测试一致。 |
| CI Format Check | `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/` 通过，exit 0。 |
| 索引及差异 | 十四能力 validator 和 `git diff --check` 通过，4 项 composed journeys。 |

首轮构建在子进程退出后遇到回收身份检查竞态，日志保存中断，未计为有效构建证据；修复任务 runner 后重新执行方案构建取得 exit 0。强杀首轮虽返回通过，但程序集核对发现 `CopyCrashTestsChildOutput` 的固定旧 `Child/bin` 路径覆盖了隔离输出，故明确排除。仅在任务专属构建目录复制本次 child 输出、核对 Core/child 哈希后，重新运行取得上表最终结果；没有改动范围外的项目构建规则。Format stderr 保留工作区加载警告，未隐藏，格式命令仍正常返回 0。

证据目录 [validation.json](roadmap-parallel-evidence-20261002/validation.json) 保存命令状态、TRX counters/哈希、16 个最终源码/测试文件 SHA-256、新增用例分组与后置门禁；同目录保留实际配置、stdout/stderr 和进程记录。[crash-artifacts.json](roadmap-parallel-evidence-20261002/crash-artifacts.json) 区分被排除的历史 DLL 和最终执行程序集。[cleanup.json](roadmap-parallel-evidence-20261002/cleanup.json) 核对 94 条验证进程身份、0 个任务存活进程，并记录专属目录 5091 条文件/目录回收。构建产物和完整 TRX 在摘要/哈希保存后回收；共享缓存、用户数据、既有进程及交付证据保留。

最终只读复核确认 16 个源码/测试哈希匹配、cleanup 子进程已退出、专属构建目录不存在。自动审批审核拒绝了删除仓库内 `artifacts/parallel-20261002` 的任务辅助文件，工具原因仅为 `blocked by policy`；没有改用其他方式绕过。该目录保留 `Run-Bounded.ps1`、`Collect-Evidence.ps1`、`Cleanup-Task.ps1` 和 `task-root.txt` 四个小文件，构建/测试数据及临时进程已回收。

## 验收边界

TTL 预算限制保留输出的累计行数及估算字节；底层 KV、cold-open 索引恢复/修复、单文档解析、heap 和首行仍有独立边界。排序/聚合、Document DML、其余模型预算、远程请求和后台 TTL 有界回收没有由本轮完成。

滑动窗口与 DLQ 均为本地单执行者文件合同。旧程序不能读取新窗口 v4 或 DLQ v2；升级不支持直接降级打开新格式。DLQ 迁移增加元数据，贴近原字节容量的 v1 可能在保留原文件的同时拒绝迁移；当前没有扩容入口。

会话窗口、任务目录、自动重发、远程 Frame/REST parity、跨机器租约、业务副作用事务、真机掉电、固定硬件、NativeAOT publish、真实模型、nightly 和长稳继续为未执行或后置，不因本轮本地合同完成而标记为生产 PASS。

## 本地交付

后续按用户授权拆分三个实现提交：TTL 文档预算 `1cb54f97`、持久滑动窗口 `73c2d2a8`、DLQ 运维 `1579cdc0`；组合恢复测试、公共路线图及验收证据单独提交。四次提交分别执行标准 `dotnet restore SonnetDB.slnx` 与完整 CI Format Check，均返回 0；未降低检查级别。提交前再次确认原 16 个源码/测试 SHA-256 全部匹配，原构建与回归结果仍适用。

新增交付门禁使用独占临时目录 `C:\Temp\sdb-delivery-9bdceac1`，命令配置、stdout/stderr、进程身份和四组结果保存在 [delivery/validation.json](roadmap-parallel-evidence-20261002/delivery/validation.json) 及同目录。首次 Git 可执行文件发现返回两条 PATH 结果，runner 在启动前失败；选择第一条明确路径后提交成功，失败记录保留且未计为成功执行。九个既有证据文本统一为 LF，JSON 内容及验证结论保持。交付命令同样有超时、最大轮询次数、取消和仅限自身的进程回收；验收提交前核对任务存活进程为 0，提交完成后回收该临时目录。

本地 `main` 与实现起点同为 `54e75dd6`，交付采用快进合并；远程分支、发布和部署仍在本轮授权范围之外。下一轮候选为 Document 排序物化预算、持久订阅任务目录和持久会话 COUNT 窗口，由新会话核对实际缺口及独占文件范围后并行实施。
