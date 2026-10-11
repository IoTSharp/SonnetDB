# SonnetDB 4.5 路线规划与完成范围核查

规划基线日期：2026-10-03（Asia/Shanghai）；最终文档核查：2026-10-04。基线提交：`4b004946`，开始时工作树干净。本轮只分析、编写与整理文档，没有实现新功能、修改包版本、运行构建/测试、提交、推送、部署或发布。

## 用户范围与交付

本轮按用户要求研究数据库和相关产品的 AI、通用聚合/持续计算、存储编码及其它能力/性能差距，结合九模型规划 4.5；新增 AI 里程碑，将已完成实现归入 CHANGELOG，TsFile 明确排除。三个独立专题由并行子智能体整理，主智能体整合根路线、既有索引、归档与状态审查。

| 文件 | 内容 |
|---|---|
| [根路线](../../ROADMAP.md) | M44/M45/M46、九模型 AI 角色、V45-X 跨模型队列、必选/条件范围、依赖及发布判定。 |
| [总索引](../roadmap-total-milestone.md) | 既有 PR/十四能力/M43 剩余归属，与根路线对齐。 |
| [AI 专题](../design/sonnetdb-45-ai-applications.md) | 模型应用、治理、任务、真实质量/成本与九模型旅程。 |
| [聚合专题](../design/sonnetdb-45-aggregation-continuous-compute.md) | 聚合 state、merge/retract、TAG/时间分组、增量物化/rollup 与批流。 |
| [存储专题](../design/sonnetdb-45-storage-performance.md) | 编码/默认策略、无损回退、范围解码、统计精度与维护成本。 |
| [CHANGELOG](../../CHANGELOG.md#roadmap-completed-archive-2026-10-03-45) | 已完成切片与证据边界归档；新功能仍是计划，只记录本轮文档变更。 |

新增 ID 是规划编号，没有据此创建远程 issue/PR。P0/P1 必选与 P2/条件范围分别定义；未校准门槛不预写 PASS，计划文档不更新能力索引的实际状态。

## 基线事实与纠偏

- 预测/异常/变点、在线 chat/embedding、显式 ONNX profile、持久摄取/resume、混合检索、MCP、图合同和本地 CDC/持久窗口均已有实现。新里程碑只补增量，不重新包装已有能力。
- 当前 measurement 时间桶不能直接按 TAG 分组；关系 GROUP BY、HAVING、DISTINCT 子集及部分窗口/spill 已有，不能称所有模型都没有聚合。
- accumulator 的 Add/Merge 与投递幂等、retract 是不同合同；新增计划要求分别证明结合/交换/顺序、去重及更新删除修正。
- `SegmentWriterOptions` 默认时间戳/值编码为 None。编码器存在不等于默认启用；V2 Int64 raw 8B，v6 内嵌索引/sketch 已有。
- 解码缓存按 reader，HNSW 缓存为共享静态缓存，KV 独立 I/O 预算已有；总预算深化不能从零重建现有机制。mmap 读取仍有复制，不宣称 zero-copy。
- Int64→double 统计精度边界列为待差分验证的风险及 guard 计划，不在未跑复现时定性为已确认 bug。
- 物化预算、编码切帧与单表预览不等于执行器到客户端全链路有界或整个 CLR heap 上限；本地副本不等于 HA；MQ 实例恢复不由单库备份推出。

## 已完成范围与原始证据复核

根路线旧“已完成范围索引”合并移入 CHANGELOG，保留原报告链接与限定范围；历史基础实现继续引用既有归档。M42/M43 未整体关闭，M35/M36/M40/M41 的真实质量、远程、容量、安装与长期任务保留。

最近两轮原始证据已作只读复核：

| 证据 | 原始结果 | 本轮复核 |
|---|---|---|
| [TVF 增量报告](roadmap-parallel-next-20261003.md)及[摘要](roadmap-parallel-next-evidence-20261003/validation-summary.json) | 完整 Core TRX 6072/6072、0 failed、0 notExecuted | 原始 XML 计数一致；SHA-256 `0EC5C7377DB9AEB7BE3E0B7FCABF31673B0F8AAE204B8351CEAF315C971AB4AD` 与摘要一致。 |
| [后续三切片报告](roadmap-continuation-20261003.md)及[摘要](roadmap-continuation-evidence-20261003/validation-summary.json) | 完整 Core TRX 6140/6140、0 failed、0 notExecuted | 原始 XML 计数一致；SHA-256 `53C2033E4A26695DF7D37BB78244E6EB0833F69EE352ADB9714073B5627B487E` 与摘要一致。 |
| 后续增量源清单 | 13 个源码 SHA-256 | 与当前规划基线逐项一致；核查最多 40 文件/30 秒。 |
| 最小 TRX 核查试运行 | 5/5 | 先读取一个小输入，再复核完整记录。 |

没有本轮重新运行测试。首次失败和重跑保留在原记录；例如 Server 原 69/70、后按类 12/12，不能合并改写为首次全绿。原记录中的本地 AOT/format 通过属于其当时提交，不自动成为本轮源码或生产 gate。

M20 沿用保存的 9/25 回读及 9/26 候选复验；本轮未查询最新线上 workflow。固定硬件/真实模型/安装/远程/长期报告没有新增结果。

## 外部研究的证据口径

官方资料、许可、公开源码/文档和部署版本分别记录在专题。研究按代表产品家族组织，不宣称所有产品全部功能已验证，也不将商业托管能力记作开源能力。

主智能体额外读取了 [PostgreSQL 统计](https://www.postgresql.org/docs/current/monitoring-stats.html)、[SQLite WAL](https://www.sqlite.org/wal.html)、[Redis 持久性](https://redis.io/docs/latest/operate/oss_and_stack/management/persistence/)及[Materialize 增量物化](https://materialize.com/docs/sql/create-materialized-view/)。Materialize replacement preview 不作为成熟基础；MySQL 官方正文受 robots/403 限制，该页不作为已核实能力依据。

专题智能体额外取得 Qdrant Hybrid Queries、Snowflake Cortex AI Functions、Databricks AI Functions、ClickHouse 压缩、Parquet 编码规范与 WiredTiger compressors 的官方正文。DuckDB storage 请求仅返回重定向页，其具体存储细节保持待核。各专题保留来源与适用边界，未取得正文的链接不据此升级为已核实结论。

算法和功能借鉴要求结合 SonnetDB 当前合同独立实现；许可、Native AOT、安全内存、零核心依赖、格式版本与名称规则不因对标弱化。没有采用 Graphify。

## 文档验证与资源

七份交付文档完成静态核查；CHANGELOG 的新增归档块独立检查，不把历史全文件的内容重新认定为本轮核实。

| 检查 | 结果与范围 |
|---|---|
| 本地 Markdown 链接 | 133 处引用、88 个唯一目标均存在；12 处片段引用按显式 HTML 锚点或标题 slug 核对通过。 |
| 规划编号与范围 | 根路线 37 个唯一工作包：32 必选、4 条件、1 后续；三个专题共 29 个 M44/M45/M46 定义的优先级与范围逐项一致。 |
| 历史显式锚点 | 根路线 9 个、总索引 1 个原有 HTML 锚点全部保留；新增 CHANGELOG 归档锚点可达。 |
| 文档质量与变更范围 | `git diff --check` 通过；新文档尾部空白核查通过。工作树仅修改 3 份、新增 4 份 Markdown，没有源码、依赖、包版本或能力 JSON 状态变更。 |
| 交叉审阅 | 已收紧 GraphRAG 为上层合同及 typed SDK 投影；A06 区分评测合同先行与最终真实评测，统计剪枝禁止依据不可靠元数据。TsFile 仅作明确排除。 |

链接、工作包和锚点检查均先用一个目标试运行，再运行完整有界输入；上限分别为 8 文件/500 链接/45 秒、50 定义/30 秒和 2 文件/50 锚点/30 秒，实际均在一秒内结束。代码提交前的 restore/format 铁律继续适用；本轮没有提交，也没有将静态文档检查当作源码 Format Check、构建或测试通过证据。

全程使用 PowerShell 7.6.6；工具调用和批处理设置项目上限/墙钟边界，外网请求失败按机器规则使用单次代理回退，拒绝访问的资料保持未核实。没有安装工具、启动服务、创建 worktree 或长期进程；终端命令均已返回，三个专题智能体确认无临时文件或后台资源待回收。只保留交付文档，不清理历史/共享构建输出及其它任务的临时资源。
