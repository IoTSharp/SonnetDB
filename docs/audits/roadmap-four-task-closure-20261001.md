# 2026-10-01 第二批四任务闭环

本轮以当前 `ROADMAP.md`、总里程碑和已有代码为依据，选择可以独立复现的四项实现。主智能体负责组合旅程、集成与统一门禁，三个子智能体分别实现 CDC、窗口和 KNN，并交叉审阅恢复及路由合同。工作基线为 `cf8c70e0032e2015590eef32300584b077831396`，分支为 `codex/roadmap-four-closures-20261001`；以下交付以该基线上的当前工作树为准，不表示已发布或已推送。

## 选题与完成范围

| 任务 | 选择依据 | 交付及验收合同 |
|---|---|---|
| M43 #386 多分区本地 CDC | 单分区泵和真实源已具备，多分区协调仍是路线图缺口。 | 最多 64 条独立路由，公平轮转、次数/时间/行/字节边界，真实两集合快照中变更、全资源重开、增量续传及位点/结果对账；失败路由不提前 ACK。 |
| M43 #391~#395 持久滚动 COUNT | 已有持久事件和订阅状态，不存在持久窗口入口。 | 原子保存 COUNT 与已应用批次，提交后 ACK，重投核对内容并去重；显式 watermark、迟到策略、有界分页/结果回收、损坏拒绝恢复及固定待提交文件的所有权回收。 |
| M42 measurement KNN 有界 Top-K | Document 默认排序已有有界堆，但 measurement 仍先保留全部扫描候选。 | 扫描期共享 O(K) 候选、稳定并列排序、SQL 内存/worker 预算、根取消、墓碑与 ANN 时间窗补偿；固定 K 的 100,000 次替换分配回归。 |
| M43 #396 真实组合恢复入口 | 现有十四能力索引无法实际运行一个 CDC/订阅/窗口组合旅程。 | 新可执行样例调用真实入口，快照一页后关闭并重开，再更新/删除并对账；固定副本导出到订阅，在窗口提交后 ACK 前关闭重开，最终 COUNT=3、pending=0、位点一致。 |

每项均有独立代码与合同文档：[CDC](../m43-local-cdc-topology.md)、[窗口](../m43-persistent-windows.md)、[KNN](../benchmarks/m42-measurement-knn-bounds.md)、[样例](../../samples/SonnetDB.CdcStreamingJourney/README.md)。

## 统一验收

当前已取得的结果：

| 检查 | 结果 |
|---|---|
| 全方案 Release 构建 | PASS，0 warning / 0 error；生产程序集启用 AOT/trim 分析。 |
| CDC/Streaming/KNN/Hybrid/向量参数/组合入口定向回归 | PASS，250/250，无跳过。 |
| 本轮新增案例 | PASS，60/60：CDC 23、窗口 21、候选堆 9、SQL KNN 4、组合旅程 3。 |
| 可执行组合样例 | `PASS_LOCAL_ONLY partitions=2 rows=3 window_count=3 redelivery_attempt=2 pending=0 closed=True`。 |
| 固定候选分配回归 | K=8，预热后连续替换 100,000 次，额外托管分配 0 bytes。仅针对候选处理。 |
| 十四能力索引校验 | PASS，14 项能力和 14 项旅程索引；整体成熟度仍为 partial/Beta。 |
| 完整 Core 回归 | PASS，5,529/5,529，0 failed / 0 skipped，约三分三十五秒。 |
| CI 相同 Format Check | PASS，`dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/` 退出 0；首轮发现样例对象初始化的六处换行问题，修正后全方案复验通过。 |

复现命令：

```powershell
dotnet restore SonnetDB.slnx --disable-parallel
dotnet build SonnetDB.slnx -c Release --no-restore -m:2 -p:UseSharedCompilation=false -nodeReuse:false
dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~SonnetDB.Core.Tests.Cdc|FullyQualifiedName~SonnetDB.Core.Tests.Streaming|FullyQualifiedName~Knn|FullyQualifiedName~HybridSearch|FullyQualifiedName~SqlVectorParameterTests|FullyQualifiedName~CdcStreamingRecoveryJourneyTests"
dotnet run --project samples/SonnetDB.CdcStreamingJourney -c Release --no-build
pwsh -File eng/validate-fourteen-capability-index.ps1
dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/
```

首轮定向回归有一个测试断言误把快照 offset=0 的合法补确认要求为空。已改为检查 ACK 保持 offset=0、失败增量 offset=1 未确认，最终 250 项全部复验通过。没有通过修改生产代码弱化失败位点合同。

[可回读验收证据](roadmap-four-task-evidence-20261001/validation.json) 保存实际命令、退出状态、PID/创建时间/命令行/父进程记录、构建和测试原始控制台输出、250 项定向结果及分配样本、Core 总计数和本轮代码 SHA-256。原始 TRX 的摘要也保留用于对应本轮采集；临时 TRX 和构建输出目录不作为仓库源码交付。

## 审查修正与证据边界

交叉审查后，窗口提交从随机临时文件改为固定 `.pending` 和路径绑定的所有权标记。重开先校验正式状态，再回收带有有效 owner 的待提交文件；未知文件保留并拒绝恢复，避免错误清理或反复硬杀后的文件数量增长。回归只注入待提交残留，没有实际执行窗口进程硬杀。

CDC 拓扑校验 view/replica 的精确身份，以及 spool 的路径/分区。调用方必须使用匹配身份的源捕获器生产专用 spool；已确认正文或快照前缀不能用于证明 spool 来源。快照后的增量由接收端校验 source/entity/schema，错配不推进位点也不 ACK。独立分区不形成跨分区事务，追平仅指已捕获 spool。

KNN 的 O(K) 只针对候选保留状态；MemTable 快照、块解码、ANN 内部状态、series 列表和 SQL 结果物化继续有各自分配，不宣称整个 CLR heap 峰值有界。候选字节预算是保守准入估算。

组合旅程是本地有序关闭/重开的确定性恢复，固定副本导出不代表持续、事务化 CDC 到流桥接。COUNT 以外聚合、任务运维、远程 Frame/REST、跨分区原子性、权限矩阵、真实模型、固定 x64/ARM64、掉电和长稳仍未通过。本轮不改变 M20 七天 scheduled、M19/M25 容量、M29 干净安装或 M43 十四能力总完成状态。

所有执行使用 PowerShell 7.6.6，.NET SDK 10.0.401；长期命令采用单独 PID/启动时间/父进程/命令行记录及明确超时，限制 MSBuild 并发为 2，关闭共享编译器/节点复用。仅回收本次任务创建且身份匹配的子进程和临时目录；交付代码与证据保留。

[最终清理记录](roadmap-four-task-evidence-20261001/cleanup.json) 核对了 160 条进程身份记录，没有遗留任务进程、编译器目录或本轮新增测试目录；任务专属临时日志、TRX 和执行包装器已回收，验收 JSON 留在仓库。
