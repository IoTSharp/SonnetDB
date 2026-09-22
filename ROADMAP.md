# ROADMAP

本文件只保留当前仍需交付、补证或继续深入的工作。已完成实现、测试和本地门禁的详细变更已归档到 [`CHANGELOG.md`](CHANGELOG.md#roadmap-completed-archive-2026-09-21)，历史 PR 拆分与背景见 [`docs/roadmap-history.md`](docs/roadmap-history.md)。产品级矩阵和 M43 队列见 [`docs/roadmap-total-milestone.md`](docs/roadmap-total-milestone.md)。

## 完成判定

2026-07-14 起，里程碑只有同时满足以下条件才标记为完成：

1. 代码存在，并由真实产品入口调用；占位类型、未调用服务或 UI 原型不算完成。
2. 自动化测试覆盖主要合同，并至少完成一次与声明相符的运行验证。
3. CI、nightly、容量、发布或 Marketplace 声明必须有对应 workflow、报告或已发布产物。
4. 文档描述与实际依赖、调用链和限制一致；“计划采用”不能写成“已经基于”。

最新专项核查基线为 2026-09-05/06，当前增量核对到 2026-09-21：
[综合审计](docs/audits/2026-09-05_project-SonnetDB-report.md)、[九模型证据](docs/audits/nine-model-capability-evidence-20260905.md)、[gap catalog](docs/audits/nine-model-gap-catalog-20260905.json)、[十四能力索引](docs/audits/fourteen-capability-evidence-index.json)。已撤回的系统性能原始报告不作为验收依据。

图例：✅ 已完成 / 🟡 本机或配置级完成、外部真机或发布门禁待验证 / 🚧 进行中、仍有实现残余或部分闭环 / ⏳ 尚未执行或明确后置 / ❌ 已执行但未通过 / 📋 计划中 / ⏸️ 暂停 / ➡️ 移交。

## 里程碑总览

| Milestone | 主题 | 状态 | 当前边界 |
|---|---|---|---|
| 0~13 | 引擎、SQL、服务端、函数、向量底座 | ✅ | 详细实现已归档。 |
| 14 | SonnetDB Copilot | 🚧 | 当前为 `Microsoft.Extensions.AI` + 自研 `CopilotAgent`；Agent Framework、真实模型证据和双网流程未闭环。 |
| 15~17 | GEO/轨迹、Copilot UX、可观测性 | ✅ | 功能与本地测试已归档；服务端会话是权威来源。 |
| 18 | SonnetDB for VS Code | ✅ | `0.4.1` 发布及产物校验已归档。 |
| 19 | 生态适配底座 | 🟡 | #125 runner/verifier 已完成；四档固定目标硬件报告待执行。 |
| 20 | 多模型 Parity | ❌ | 最新七次 scheduled 窗口全部失败；启动修复已本地通过，修复后的连续七次远程窗口待重跑。 |
| 21 | Document Store 单机能力 | ✅ | 单机子集已归档。 |
| 22 | 上层应用/示例候选 | ⏸️ | 不作为 SonnetDB 内置里程碑；通用缺口另行回收。 |
| 23 | 搜索与向量引擎合并 | ✅ | DotSearch/DotVector 已归档。 |
| 24 | Document 管理面 | ✅ | Explorer、Validator、导入导出和维护入口已归档。 |
| 25 | Document 验收与发布治理 | 🟡 | verifier 已完成；million/ten-million 固定硬件与 attestation 待执行。 |
| 26 | 连接器路线 | ✅ | C ABI、多语言入口和 release workflow 已归档。 |
| 27 | AI / Agent 数据访问与治理 | 🚧 | MCP、工业 Demo、在线 provider 和 ONNX profile 有代码；真实模型质量、成本、双网和 Studio broker仍缺。 |
| 28 | 可靠性、并发与热路径加固 | ✅ | 本地 P0~P5 与 SDK 补口已归档。 |
| 29 | 多模型统一管理工作台 | 🟡 | Web/Studio/VS Code、bundle/MSI 和宿主合同已完成；干净 Windows 安装、WebView2、升级/卸载及端口冲突待真机。 |
| 30 | Sparkplug B / CoAP / UDP 接入 | ✅ | 协议入口、生命周期、安全、parity 和基准已归档。 |
| 31 | 时序聚合类型语义 | ✅ | selector/categorical aggregates 已归档。 |
| 32 | Document MongoDB-like 易用性 | ✅ | SDK、查询/更新、索引、aggregation、Bulk、迁移和 Workbench 已归档。 |
| 33 | 时序聚合执行与下推 | ✅ | 正确性、多聚合复用、流式化和 LIMIT/latest-N 已归档。 |
| 34 | Modbus TCP 内建映射表 | ✅ | DDL/catalog、codec、TCP master/slave、受限写、治理和管理面已归档。 |
| 35 | 语义内容与多模态检索 | 🚧 | 代码合同、持久摄取、provider 治理和媒体/视觉入口已交付；真实质量、容量、模型换代和固定硬件待补。 |
| 36 | 九模型专用品类易用性闭环 | 🚧 | #311~#326 代码合同与本地回归完成；九模型真实旅程、远程 parity、恢复和长期证据待验。 |
| 37 | 视图与物化视图 | ✅ | #327/#328 已归档。 |
| 38 | SQL 存储过程与触发器 | ✅ | #329~#332 已归档；外部脚本运行时保持暂停。 |
| 39 | SQL 触发器第二版 | ✅ | #333~#339 研发闭环；生产混合负载和长期 SLO列入真机计划。 |
| 40 | 原生属性图数据库 | 🟡 | #341~#367 步骤 1~7 本地闭环；外部对拍、固定硬件、AOT、Couplet 和 7 天 gate 待真机。 |
| 41 | 关系查询规划与执行性能加固 | 🚧 | #368~#374、#376~#380 本地合同完成；#373/#375~#381 发布证据和统一语料待补。 |
| 42 | 九域与规划器系统性能深化 | 🚧 | 统计/CRC、SQL 指标、KV/向量局部切片已完成；九域容量、跨架构、168 小时和生产门禁未执行。 |
| 43 | 十四套能力与生态发布总收口 | 🚧 | #382~#384 已完成；#385~#395 已有首批本地合同（CDC 编解码与 append/replay/ack spool、订阅与文件 checkpoint），#396 已有索引门禁，#385~#402 的远程拓扑、旅程、报告和生态资料仍在队列。 |
| MM9 | 多模型备份恢复第一批 | ✅ | `BackupService` 与 `sndb backup` 已归档。 |

## 当前推进顺序

主路线按“代码实现与能力补全 → 性能优化 → 验证、测试与论证”执行，完整当前队列见[总里程碑 D 节](docs/roadmap-total-milestone.md#existing-pr-execution-order)。

1. **代码与功能补全：** GH-Issue #177/#180/#193 的远程/Frame/parity 或外部 closure 收口；M27 #340 的可信双网/StudioNative 残余；M35 #298/#302/#303/#305 的真实模型与质量门禁实现；M36 #310/#311/#326 的旅程工具和跨端缺口；M41 #375 与 M42 的冷启动、向量和结果内存残余（KV state 读预算切片已完成）。
2. **性能优化：** 统一语料下的页感知成本、独立 I/O 预算、向量有界 Top-K、对象分页、covering/index-only 和受控并行边界；保持正确性、事务、取消和恢复合同。
3. **后置验证与发布论证：** #125 → #174 → #184~#185 → #187 → #258 → #352/#367 → #373/#381 → M42；最后执行 M20 七次 scheduled、M43 总验收和生态提交。固定硬件、真机、nightly、长稳和外部对拍都属于本阶段。

外部 GitHub backlog 使用 `GH-Issue #N` 标识，按[路线快照](docs/github-issues-roadmap.md)归入 M43 Step 2；同号内部 PR 不得混淆。已标记 `closed_implemented_scope` 的 issue 只表示对应有界实现和本地证据完成，不替代远程 parity、固定硬件、部署或生产证据。

## 真机验证待办

这些事项属于部署后的设备与生产环境验证，不阻塞对应代码合同完成。每次执行需记录项目/服务器、CPU/内存/磁盘、SonnetDB commit、配置、命令、持续时间、吞吐、P50/P95/P99、working set、分配/GC、WAL/磁盘写放大、恢复结果和原始报告路径。

| 来源 | 待验证内容 | 触发时机 | 状态 |
|---|---|---|---|
| M36 #314/#315 | 时序远程 Frame/REST parity、真实服务取消与重开、固定硬件吞吐和预检边界 | 服务与固定目标机可用后 | ⏳ |
| M36 #317 | 大 keyspace continuation、pipeline 背压、TTL/热点诊断和容量 | 大规模数据集与目标机可用后 | ⏳ |
| M36 #318/#319 | FullText 远程 Search/设置 parity、analyzer/relevance/rebuild 和容量 | 真实全文语料可用后 | ⏳ |
| M36 #320/#321 | Vector typed search/lifecycle、ANN/scan 解释、Recall@K 和容量 | 向量语料、模型 profile 与目标机可用后 | ⏳ |
| M36 #323 | 对象高变更率分页、文件流、固定硬件容量和跨进程恢复 | 目标机与对象服务可用后 | ⏳ |
| M36 #325/#326 | MQ nack/reset/dedup、实例 snapshot/restore、consumer offset 和九模型重开 | 真实 Server、远程客户端和恢复目标可用后 | ⏳ |
| M39 | 过程/触发器混合 DML、批量提交、失败回滚、deferred/outbox 尾延迟和 SLO | 实际项目服务器可用后 | ⏳ |
| M39 #339 | Document patch/bulk/TTL、measurement 1/100/10,000 点、高基数写入、WAL/compaction、backup/restore、crash/replay | 真实项目数据可用后 | ⏳ |
| M19 #125 | 四档容量与恢复：1,000,000 series、10,000 segment、20 次 kill/reopen、10,000 measurement | 受保护固定 Linux x64 目标机和 workflow 可用后 | ⏳ |
| M20 #136 | 修复后的 light/full 连续七次 scheduled、artifact 对账和失败日志 | 启动修复远程窗口可用后 | ❌ |
| M25 #174 | million/ten-million Document 写入、查询、重建、TTL、恢复、备份和内存曲线 | 认证目标硬件与 artifact bundle 可用后 | ⏳ |
| M27 #184/#185/#187/#340 | 真实 broker/provider、目标模型质量/成本、双网认证/续流和 StudioNative | 真实 provider、凭据和部署环境可用后 | ⏳ |
| M29 #258 | 干净 Windows 安装、WebView2、升级/卸载保留、宿主生命周期和端口冲突 | 干净目标机可用后 | ⏳ |
| M40 #352/#367 | Graph 外部对拍、LDBC/Graphalytics、1m/10m、Native AOT、Couplet、kill/reopen 和 7 天 mixed workload | 目标机、外部数据库或联合环境可用后 | ⏳ |
| M40/Couplet C1 | `Tsdb.Generations` 跨进程 cursor/root 竞争、hard-kill CAS、双客户端恢复和长稳 | Couplet source lane 与目标机可用后 | ⏳ |
| M41/M42 | 固定 x64/ARM64、木垒同语料、I/O/冷启动、恢复、backup/restore、168 小时 mixed workload | 现场分析窗口可用后 | ⏳ |

现场执行计划不得把 quick、mock、fixture、本机短跑或缩规模报告写成生产 PASS；未执行项目统一写 `NOT_RUN`/`NOT_READY`/`DEFERRED`。

## 待补验收证据

### M20 — Parity nightly

Parity 场景、适配器、compose 和 verifier 已完成；MinIO pinned tag 已切换到可用镜像并通过本地 PowerShell 7 contract。最新七次 scheduled 窗口全部失败，须重跑完整 light/full，保留容器日志、测试报告、commit SHA 和 raw artifact；失败不能以 `No summary was produced for this run.` 代替证据。

### M19 / M25 — 容量与发布

runner、schema 和 verifier 已完成，但固定目标硬件、受保护 artifact attestation 和恢复报告尚未执行。对外只声明“profile 可执行、规模尚未在目标硬件验证”，缩规模 PASS 不替代发布证据。

### M29 — Studio 安装

研发交付已完成；剩余只保留干净 Windows 首次安装、升级/卸载数据目录策略、WebView2、托管 Server 启停/异常退出、端口冲突和日志健康状态。

### M27 / M35 / M36 / M40 / M41 / M42

代码与本地回归细节已经移到 CHANGELOG；当前只追踪真实模型质量、远程 parity、九模型旅程、Graph 外部 gate、统一语料性能、跨架构 AOT、恢复和长期 SLO。具体合同链接见[总里程碑](docs/roadmap-total-milestone.md)及各专页：
[M27 provider profile](docs/benchmarks/m27-provider-model-profile.md)、[M35 查询预算](docs/m35-filtered-search-budgets.md)、[M36 对象合同](docs/object-client-contract.md)、[M40 Graph gate](docs/m40-graph-367-production-gate.md)。

## Milestone 43 — 十四套能力与生态发布总收口

M43 只保留未实施或待外部动作的队列：

| 步骤 | 范围 | 状态 |
|---:|---|---|
| 1 | #382~#384 能力索引与成熟度口径 | ✅ 本地完成，证据状态继续更新 |
| 2 | 既有 PR 代码与性能残余、GH-Issue 外部兼容队列 | 🚧 |
| 2a | 固定硬件、nightly、安装、AOT、恢复和长稳 | ⏳ |
| 3 | #385~#390 CDC 与边缘同步 | 🚧 |
| 4 | #391~#395 流处理与订阅 | 🚧 |
| 5 | #396 十四项能力旅程与总索引 | 🚧 |
| 6 | #397 生产证据汇总 | ⏳ |
| 7 | #398~#402 榜单资料、提交、生态案例与最终验收 | 📋/⏳ |

退出条件、依赖和每个 PR 的单项边界见[总里程碑](docs/roadmap-total-milestone.md)。外部提交不由文档变更自动完成，提交、审核和排名必须分别记录事实。

## 性能观察项

以下不是已完成里程碑的遗留验收，只有取得独立基准后才排期：

| 编号 | 方向 | 进入条件 |
|---|---|---|
| PF1 | 级联删除按选择率切换二级索引或单次哈希扫描 | 1/10/50/100 父键矩阵证明替代路径稳定收益且事务回滚等价。 |
| PF2 | 高活跃词基数 fuzzy 词典结构 | 100k/500k 活跃 term 场景线性枚举成为主要 CPU 成本，且新结构稳定至少 2 倍收益。 |
| PF3 | ANN tombstone gate/区间索引 | 高墓碑基数下区间扫描成为主要成本，且不降低 ANN/精确扫描召回。 |

## 已完成范围索引

完成项不在本文件重复展开：

- M0~M13、M15~M18、M21、M23、M24、M26、M28、M30、M31、M33、M34、M37~M39、MM9：见 [CHANGELOG 归档](CHANGELOG.md#roadmap-completed-archive-2026-09-21)；M14 Copilot 继续按 M27 未闭环队列推进。
- M35 #297、#299~#301、#304、#306~#309；M36 #311~#326 代码范围；M40 #341~#367 步骤 1~7；M41 #368~#380 本地合同：见 [CHANGELOG 归档](CHANGELOG.md#roadmap-completed-archive-2026-09-21) 和各专页。
- 历史正文仅用于追溯；若历史文档与当前实现冲突，以代码、可执行测试和本文件的证据边界为准。

## 历史链接兼容锚点

以下锚点仅保持旧文档链接可达，不代表主路线图恢复已完成里程碑的详细正文；当前状态以本文件总览、真机验证待办和 CHANGELOG 归档为准。

<a id="milestone-12--函数与算子扩展pid--forecast--udf"></a>
<a id="milestone-17--可观测性与运行时可见性-observability--runtime-visibility"></a>
<a id="milestone-18--vs-code-数据库扩展sonnetdb-for-vs-code"></a>
<a id="milestone-19--生态适配底座能力关系--kv缓存--对象桶--大量-measurement"></a>
<a id="milestone-19--生态适配底座能力关系--kvcache--对象桶--大量-measurement"></a>
<a id="milestone-20--多模能力对齐与平移测试-parity"></a>
<a id="milestone-24--sonnetdb-studio-管理体验升级document-管理面"></a>
<a id="milestone-25--document-store-验收文档与发布治理"></a>
<a id="m40-修复与发布执行顺序2026-08-23-复盘"></a>
