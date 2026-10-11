# main 后续三切片实现与本地验证

基线：`a0e1dff4679f4450fc10fec87cd527515777c700`；本地 main 起始工作树干净。没有创建 worktree、切换分支、推送、发布、部署或变更远程状态。

## 真实缺口核对

读取仓库 AGENTS.md、ROADMAP.md、总里程碑、2026-09-30 测试/兼容审计及 2026-10-02/03 最近增量审计。最新代码已有 TVF 预算、持久会话/任务目录、单分区 CDC→流桥接及自动投递，本轮均复用。M20 仍沿用 2026-09-25 的线上回读，没有请求远程或由本地结果推断七天证据。M19/M25/M29/M35/M40 等固定硬件、模型、安装及长期验收没有新结果。

代码实查确认三个独立残余：measurement 预算预检拒绝 ORDER BY；已有多分区 replica topology 没有 CDC→流 topology；持久 task catalog 与单订阅 dispatcher 没有任务运行接线。

## 独占分工与审查

| 作者 | 独占实现 | 核对方式 |
| --- | --- | --- |
| measurement_sort | SelectExecutor 与 MeasurementOrderMaterializationBudgetTests | 静态核对；审查 bridge topology |
| bridge_topology | 新 CdcStreamingBridgeTopology 与测试；已有 bridge 仅内部源/目标访问器 | 静态核对；审查 task runner 和组合样例 |
| task_dispatch | 新 FileStreamingTaskRunner 与测试 | 静态核对；审查 measurement 排序 |
| 主智能体 | CdcEventSpool O(1) 位点访问器、旧拒绝 fixture、公开选项说明、真实样例、组合测试、文档及证据 | 集中编译/回归/AOT/格式检查和提交 |

三个子智能体不构建共享 obj、不切换分支、不提交、不创建临时资源。主智能体串行启动长命令，限制两个逻辑处理器、一个 MSBuild worker并禁用 build server/node reuse。

交叉审查修正了星号展开后的同名别名排序、now() 时间下推预检，以及源分区检查复制完整字典的问题。源位点现锁内 O(1) 读取。首次测试项目编译发现新增样例没有检查 SqlExecutor 可空结果，主智能体改为显式类型检查并拒绝异常结果，未压制警告。

## 最终合同

- [measurement 排序](../benchmarks/m42-measurement-order-bounds.md)：直接 raw SELECT 标量多键、未投影键、别名、稳定 ties；候选、键和分页输出累计收费，LIMIT 不跳过阻塞输入准入。
- [本地多分区和任务接线](../m43-local-task-topology.md)：公平轮转与独立恢复、路径冲突拒绝；任务定义快照绑定 caller-owned 订阅，复用 dispatcher lease 与 HandlerCompletion。
- [真实组合入口](../../samples/SonnetDB.CdcStreamingJourney/README.md)：`--topology` 实际捕获两分区三次文档变更，窗口持久提交后处理失败且未 ACK，重开追平、稳定身份重投去重和再次重开对账；同时核对持久 measurement 排序预算。

## 验证结果

执行原始日志和带 PID/创建时间/命令/父链的记录保存在[证据目录](roadmap-continuation-evidence-20261003/)。首次编译失败记录保留。

| 检查 | 实际结果 |
| --- | --- |
| 修正后测试项目 Release 构建 | PASS，0 警告 / 0 错误 |
| 最小输入回归 | 5/5，0 跳过 |
| 新切片与已有 CDC/Streaming/measurement 预算专项 | 528/528，0 跳过 |
| 完整 Core Release 回归 | 6140/6140，0 跳过，测试时长 5 分 5 秒 |
| 完整解决方案 Release 构建 | PASS，0 警告 / 0 错误 |
| Server SQL/Frame/Graph/配置专项 | 首轮 69/70（1 个既有本地 HTTP 连接瞬态）；按类重跑 `SqlResultBoundsEndpointTests` 12/12 通过 |
| 托管 `--topology` | PASS_LOCAL_ONLY，2 分区、3 事件、窗口 3、重投 attempt 2、排序首时刻 1 |
| win-x64 NativeAOT 发布 | PASS，0 警告 / 0 AOT 警告；本地入口输出同上；二进制 SHA-256 `FD8861C6CFE6537E671589FBC5A3173BD1038160F06F9669EB984E0896F5B9EB` |
| 最终 restore | PASS，39 项解决方案项目，单 worker |
| CI 同级 format | PASS，0 文件需修改（2163 文件）；诊断日志仅报告 `.dcproj` 无关联语言的工作区警告 |

新增实例为 measurement 排序 37、bridge topology 16、task runner 14、真实组合 2，共 69。旧 ORDER BY 预检拒绝 fixture 的一个实例随能力扩展退出；基线完整 Core 6072，本轮净增 68。专项未调用外部服务；其文件重开与模拟中断不计掉电或硬杀。

## 证据与资源边界

本地合同、真实嵌入式文件重开、Release/AOT 构建均属于开发机证据；远程 parity、固定目标硬件、断电/硬杀、十四能力权限矩阵、七天 scheduled、168 小时 mixed workload和生产门禁分别待补。M42/M43 保持进行中，不把三个切片写成整项 PASS。

复用前轮执行器并人工核对所有退出比较条件，先运行一秒超时的小输入试验，按预期超时且最终 remaining=0。每个正式命令有最大轮询次数、墙钟超时、取消标记、两秒等待和定期进度；结束核对归属身份并仅回收任务进程。共享 bin/obj 和依赖缓存保留且 Git ignored。没有独占 MCP、后台服务、下载或安装任务；样例/测试的专属目录通过 finally 释放句柄并回收。
