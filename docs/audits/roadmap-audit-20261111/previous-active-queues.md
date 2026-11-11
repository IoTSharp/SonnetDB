# 旧当前队列快照（2026-11-11 整理前）

来源：`main@92d760077ce0e38525e41e8cfd2a8694e786f029` 的根 ROADMAP。以下按原文保留候选/日期与当时状态，**不再作为当前执行顺序或实时 issue 状态**。当前顺序见 [根路线](../../../ROADMAP.md#当前推进顺序)；更早历史仍见原收据。

# ROADMAP — SonnetDB 4.5 与后续里程碑

## 2026-11-10 KV-LSM 接续

K1～K8 的阶段源码已随 `ca495cae` 进入 main；K7 为部分完成。该提交的 [CI](https://github.com/IoTSharp/SonnetDB/actions/runs/42754412252)（含 Ubuntu/Windows Core LSM 轮次及三个 AOT Publish 作业）、CodeQL、Docker Publish、Connectors Release、Docs Pages 已成功。该证据只绑定 `ca495cae`，不代替后续提交、公开包、生产或固定硬件验收。默认 LSM、打开时自动迁移、旧版本拒绝 v3 布局的合同见 [设计 §7.6](docs/design/kv-lsm-storage-engine.md#76-k6k8-实现说明与上文设计的差异以本节为准)及[迁移运维](docs/operations/kv-lsm-migration.md)。

`f71e8a80` 已提交 10 万／100 万条有界本机基线、基准入口和 13 阶段迁移真实强杀，原始证据见 [KV-LSM 基准与恢复证据](docs/benchmarks/kv-lsm-capacity-20261110.md)。本轮接续已实现 K7 同实例多表原子批次、维护元数据、共享表持久行数、离线回滚 CLI 及回滚中断恢复；并增加共享 SST v3 StorageId 区间／前缀布隆、Version 备份、后台压力暂停和大行恢复 Span 投影。分层验证与重复测量见[本轮接续证据](docs/benchmarks/kv-lsm-followup-20261110.md)；本批提交和双端推送的精确状态见根 HANDOFF 及对应 Git 读回，不由本段推断发行或生产验收。

K7 的完整性能／容量门禁仍分别验收：千万条、1000 表 × 1 万行、随机强杀 200 轮、真实旧版二进制、一亿条／168 小时固定硬件不由局部代码或本机基线自动通过。时序 P0-3～P0-5 的[独立设计与具体决策问题](docs/design/time-series-write-partition-duplicates-decisions.md)等待用户决定，本轮未改变时序行为。

## 2026-11-07 分支整合后的执行顺序

2026-11-07 上午状态更新：用户明确普通代码 issue 以代码完整实现及必要代码层验证为关闭依据，公开包、上游维护者合入、部署与外部宿主验收分别跟踪；本身属于发布、部署或环境验收的问题仍按其实际合同处理。#230/#392/#394/#395/#403/#404 已回复并关闭为 completed，公开/上游/宿主事项继续保留原始 NOT_RUN 或未完成状态。#396/#397/#398/#399/#401/#402 仍 OPEN，具体缺口已逐条回复。下方较早记录中的“母 issue 继续 OPEN”是当时的关闭口径，不作为当前状态。GetAndDelete 后续修复 cdaebfa5 已拒绝缺失旧记录却带变更版本的确认，保留未知结果且不重试；完整 Client 274/274 通过，准确推送与回读见根 HANDOFF。

PR #413 已修复审查发现的超时后提交、Windows 挂载卷及只读容量查询重建目录问题，并在最终精确源码验证后合入 main；已合入切片不重新立项。下一阶段由新监督会话按[共同 Provider 三线并行合同](docs/handoffs/provider-next-phase-20261107.md)接续第一顺序，逐项状态及后续见 [PR #413 审查与 issue 矩阵](docs/audits/pr413-integration-20261107.md)。

2026-11-07：三位独立worker完成共同候选`5.1.0-rc.20261107.1`/source`eb5c0e5f`的消费者增强和独立复核；本地四包/JIT与实际CI四包/NativeAOT Server各69步骤PASS，三协议、after-ACK强杀/Down/Up及Server重开齐全，12类发行readiness READY。证据见[发行评审/机器清单](docs/releases/provider-candidate-20261107.md)及[迁移](docs/audits/provider-candidate-20261107-migrations.md)、[JSON](docs/audits/provider-candidate-20261107-json-update.md)、[集合](docs/audits/provider-candidate-20261107-ownsmany.md)专题；正式发行延期及公开门保持，母issue不提前关闭。下一独立SDK切片冻结为typed GetAndDelete。

| 顺序 | 接续范围 | 下一份可验收交付 |
| --- | --- | --- |
| 1 | #392 / #395 / #394：共同 Provider 候选 | 从全部整合后的最终 source 冻结包与匹配 Server，重验库存迁移/回退、补货 JSON 更新及 OwnsMany 生命周期。正式发布已延期；恢复发行后以公开新包及独占空缓存复验，满足后关闭。 |
| 2 | #397：M49 SDK | 在 typed KV Get/Put/Scan 基础上冻结下一操作/模型/语言包，补真实原生 HTTP/2、TLS/ALPN、取消/未知写入、ABI、重开及公开消费者。 |
| 3 | #230 / #404：上游 Provider | 接续 FreeSql #2291、XCode #104→#105 的实际审查/合入，公开 Core/Provider 后验证全公开组合；候选与上游发行分别记账。 |
| 4 | #396 / #402：AOT 与发行恢复 | EF 固定静态查询应用取得预编译/NativeAOT 的首个具体缺口；恢复流程先补可信 attempt 来源与双 verifier，再做受控预发布失败恢复。 |
| 5 | #398 / #399 / #403：独立环境验收 | 先落实干净 OS、固定硬件/阈值、正式 HTTPS/WorkBuddy 宿主与平台入口，再分别执行安装生命周期、原规模容量/长稳、九模型及送审公开闭环。 |
| 6 | #401：M50 高精度时序 | T01 冻结独立版本/API/矩阵→T02 精确换算→T03–T06 持久元数据/格式/WAL/恢复→T07–T11 查询/分桶/retention，再接协议、语言、迁移与固定硬件。 |

2026-11-07下一独立切片：typed GetAndDeleteKvAsync复用既有op6并保留旧记录及变更版本，新增codec44例、完整Client272例及真实产品Server7例本地通过；source336f9aa8的Client/Protocol两包候选及仓库外独占缓存应用、两代同目录Server重开均PASS。TLS/ALPN、公开消费和其余M49范围不外推，#397继续OPEN。详见[#397专题及机器收据](docs/audits/issue397-get-and-delete-20261107.md)，不修改已冻结的Provider候选。

这 12 个母 issue 仍有未完成的公开、上游或实机门禁；#367/#393/#400/#407 已关闭。原 `0d687bd9` 5.1 候选的 PASS 不外推到新源码。Mulei 后续仍保留 FIFO 长尾、中心超时/积压、统计刷新取消、历史缺图及完整长稳；本次不执行生产操作。

## 路线图状态图例

| 标记 | 含义 |
|---|---|
| ✅ | 已完成：该任务声明范围的实现与适用证据齐全。 |
| 🟡 | 局部完成：已有本地/单片证据，外部、发布、硬件或长期证据仍待补。 |
| 🚧 | 进行中：已开始实施或验收，仍有明确剩余工作。 |
| 📋 | 未开始：已规划，尚未实施。 |
| ⏳ | 待验证：已有实现或材料，但关键验证尚未运行。 |
| ❌ | 失败/阻断：最近执行失败，需修复后重试。 |
| ⏸️ | 暂停：明确暂停，保持现状等待恢复条件。 |
| ❓ | 未知：证据不足，不能推断完成情况。 |

状态只描述对应任务声明的范围；局部代码、mock、fixture、单机短跑或历史回执不自动升级为远程、固定硬件、生产、长期运行或正式发布完成。
2026-11-07上游接续：XCode #105新增两条late review已实际复现并由source9fc590a78最小修复，两个原测试从2 FAIL到2 PASS；六目标构建、source-linked及新两包66/66、三协议9进程/6次Server生命周期均通过，review已回复并读回resolved。新head CI为13 SUCCESS/3 FAILURE（OIDC/iOS工具链/s390x NovaDb），保留FAIL；#104→#105合入/公开Core-Provider/全公开新缓存链尚未完成，#404不关闭。FreeSql#2291仍OPEN、Provider索引404，#230不关闭。详见[上游review收据](docs/audits/issue404-review-20261107.md)。

2026-11-04：**[M51：WorkBuddy生态集成](#milestone-51)**当前连接器/Skill合同更新为0.2.0，面向SonnetDB 5.0的43工具发现、默认Read和用户明确授权的Write。生产切片49731955保持冻结，不重复开发；历史Docker/r2九工具证据保留source。平台解析、HTTPS、宿主验收与正式送审仍未完成，由统一发布流程逐项整合。

2026-11-02：新增 **[M50：时序时间戳精度与无损时间链路](#milestone-50)**，按库真正保存 `ms/us/ns`、默认 `ms`，规划 30 项必选与 2 项条件任务；全部 `📋 planned`。完整实现思路、源码归属、依赖/验收、迁移/旧端拒绝与初步工作量见 [M50 专题](docs/design/m50-timeseries-timestamp-precision.md)。M50 独立冻结版本与批次，本次仅更新规划，不启动源码改造或变更现有 4.5/预览/连接器批次范围。

2026-10-16：按用户确认修订 **[M49：连接器/嵌入式拆分、九模型访问与有界批处理](#milestone-49)**。远程各语言原生 HTTP/2 二进制 Frame，嵌入式 .NET 托管与非 .NET 共用 AOT；VB6/PureBasic 仅远程。首批只做九模型数据访问、先64位、Java8兼容、直接重构不留旧兼容层，允许分批正式发行。D01/F01/C01～C08/R01～R05 共15包，首片已实施、均未整体完成；详见 [M49 专题](docs/design/m49-connector-analytics-bounded-batches.md)与[确认需求](docs/design/connector-embedded-split-requirements-20261016.md)。

2026-10-17：F01 关系 SQL 写入首片已接通新 Client→真实 HTTP/2→Server：Execute=2/payload v1、精确参数、类型化 NULL、单条自动提交、完整终态与未知提交无重试。旧 ADO 迁移等待多结果/事务合同；九模型 F01 与 R01～R03 仍未关闭。见[协议](docs/sql-execute-frame.md)和[换机入口](docs/handoffs/m49-next-computer.md)。

M49-D01 共享协议与原生 .NET Frame Client 首片已完成：Protocol 程序集脱离 Core，Client 只依赖 Protocol；真实 h2c、取消、关闭、预算和干净包消费者证据见 [D01 审计](docs/audits/m49/d01-20261016/README.md)。D01/F01/R01～R03 仍未整体完成，换机接续见 [M49 交接](docs/handoffs/m49-next-computer.md)。

2026-10-16：新增 **[M48：单机日志与事件分析](#milestone-48)**，将 ELK 核心日志场景落实为可靠摄取、联合检索/统计、磁盘访问、生命周期、日志探索和规则告警的增量规划。`M48-L01`～`L20` 为 R1 必选，`L21`～`L24` 为条件候选，当前全部 `📋 planned`；完整字段/确认/查询/恢复合同、24 个工作包、阶段退出与验收矩阵见 [M48 详细设计](docs/design/m48-single-node-log-event-analytics.md)。R1 的目标版本与日期在 L01 冻结，不将全部 M48 自动追加为既有 4.5 正式版或已发布 Workbench 预览的门槛。

2026-10-16：第二批 [十项产品切片](docs/audits/implementation-batch-02-20261016.md)已完成本地实现：GH-Issue #226 存量主键列重命名，#231 持久序列/原子取号/逐行插入，#237 表列注释，#240 字符串长度声明/演进/元数据，以及 #243 有主键和复合主键有界删除。扩大 Core 3157 项、Server SQL/ADO/Frame 412 项（含真实 REST/h2c，本批新增 18 项中 17 项为真实网络）及实际强制终止恢复 2 项回归通过，最终冻结复核与本地提交见根 HANDOFF。表目录含新属性时写 v12，旧引擎拒绝；未改行或键编码。#226 主键类型/定义、#227 DDL 事务、#232/#233/#236 及无主键依赖继续排队；正式发布及 ORM 消费者验收另计，不能由这些切片关闭整个 Issue。

2026-10-15：接收最新 main `c7b307f6`，首次核对 46 条开放 Issue，最终快照 48 条（新增 #312/#313/#314，关闭 #273），清理两个已合并本地分支。本批 [十项实现任务](docs/audits/implementation-batch-01-20261015.md)已完成本地实现：GH-Issue #224 的 HAVING/复合投影/排序分页，#229 多结果 ADO，#257 全表关系 UPDATE，以及 M45/M46 的 Int64 众数/近似去重和三项聚合状态校验。最终 Core 2938 项、真实 REST/h2c 167 项回归通过；实际提交和远端 Issue 闭环见根 HANDOFF，不重做已合入的 #219～#223/#225/#228，不把局部任务提升为整个里程碑、正式发布或下游 ORM 完成。

更新日期：2026-11-03（Asia/Shanghai）。**Workbench 继续按 Web 预览后续、Studio 与 VS Code 各自门禁推进；M48 单独冻结日志 R1 范围。** 首版预览不代表 SonnetDB 4.5 正式版或 M47 三面整体完成，候选12的 P07/P08 已完成；[4.5.0-preview.1.12](https://github.com/IoTSharp/SonnetDB/releases/tag/workbench-preview-1.12) 已公开，匿名下载与Chrome/Edge本机首跑通过。

根路线图只保留剩余交付、依赖和验收顺序。已完成实现、受限真实旅程以及旧窗口记录已归入 [CHANGELOG：2026-10-10 完成范围归档](CHANGELOG.md#roadmap-completed-archive-2026-10-10-workbench)；详细原始证据仍在 [M47 验证记录](docs/design/m47-unified-management-workbench/validation-report.md)。后续不按 WB 编号重新实施已完成功能。

## 当前执行顺序：Workbench Preview 1

| 顺序 | 交付 ID | 状态 | 目标 | 依赖 / 当前状态 |
|---|---|---|---|---|
| 1 | M47-P01 | 🟡 | 冻结预览范围、已提交候选、版本与发布物白名单 | **范围冻结完成，产品仍 NOT_READY**。[范围/入口控制](docs/design/m47-unified-management-workbench/preview-1-scope.md)、[候选01](docs/design/m47-unified-management-workbench/preview-1-candidate-01.json)、[独立分发合同](docs/design/m47-unified-management-workbench/preview-1-distribution.md)已记录；入口与分发实现仍阻断。 |
| 2 | M47-P02 | 🟡 | 定位 Web smoke 失败并取得候选通过证据 | **本地验收完成；远端门禁 NOT_RUN**。[P02报告](docs/design/m47-unified-management-workbench/preview-1-p02-smoke.md)、[候选02](docs/design/m47-unified-management-workbench/preview-1-candidate-02.json)：build、448合同、307fixture与12宿主回归通过，25默认skip分列；产品NOT_READY。 |
| 3 | M47-P03 | 🟡 | 首版真实登录、授权、预算和终态 | **R1本地完成；完整模式四片307通过/25原skip**：[P03报告](docs/design/m47-unified-management-workbench/preview-1-p03-controls.md)、[范围修订](docs/design/m47-unified-management-workbench/preview-1-p03-scope-revision.md)。七模型专用读取延期，原九模型承诺未全部验收；KV/measurement视口渲染与完整/选中导出回归通过；[候选04](docs/design/m47-unified-management-workbench/preview-1-candidate-04.json)固定`57222f57e4cc` / 4.5.0-preview.1.4，产品NOT_READY。 |
| 4 | M47-P04 | 🟡 | 生成来源明确的 Web/Server 预览发行物 | **候选12 P04通过**：4.5.0-preview.1.12/source5bcd3b81、38145580702 attempt1 success，290项库存/hash/NativeAOT身份独立通过；旧失败保留。 |
| 5 | M47-P05 | 🟡 | 用实际发行物验首次启动、部署与用户旅程 | **候选12本机R1实物通过**：四阶段37记录、五类拒绝、快照恢复/导出和17回执244身份非空审计通过；[冻结记录](docs/design/m47-unified-management-workbench/preview-1-candidate-12.json)。 |
| 6 | M47-P06 | 🟡 | 完成预览说明、已知限制、升级/回退和反馈入口 | **候选12真实四附件prepare通过**：独立原archive digest、附件/ZIP核验PASS；[分发冻结](docs/design/m47-unified-management-workbench/preview-1-distribution-12.json)保持原P04包字节。 |
| 7 | M47-P07 | ✅ | 按现有发布门禁核同一候选并作放行评审 | **完成：更新分发工具后P07 READY，12/12门禁、8/8验收**：[候选12评审](docs/design/m47-unified-management-workbench/preview-1-p07-12.json)，原工具/审批策略历史保持。 |
| 8 | M47-P08 | ✅ | 发布、独立回读下载结果并归档首版 | **已发布，匿名四附件/290项库存及Chrome/Edge本机首跑通过**：[4.5.0-preview.1.12](https://github.com/IoTSharp/SonnetDB/releases/tag/workbench-preview-1.12)、[P08记录](docs/design/m47-unified-management-workbench/preview-1-p08-12.json)。maikebing已实际自批，正常停止/独立进程审计通过；只限Windows R1 Web预览。 |

具体范围、每步退出条件、宿主后续与失败处置见下方 [M47 首个预览版路线](#workbench-preview-1)。P01候选01来源固定为 `c40fa670e3a170c5fac8cefa181d618631862270`，保留版本 `4.5.0-preview.1.1`；仅为已提交实现起点，未构建/验收/打标签。后续代码、配置或打包变更须新增候选与版本，不覆盖该清单、不用 dirty 工作树发包。首版限定 win-x64 同源环回 Web＋Server；旧 `workbench` 定时保持 `PAUSED`。 后继候选02已绑定修复提交 `ed4f48242c5d5b4e7e16861504d03cc845146402` 与保留版本 4.5.0-preview.1.2；本地验收通过，未打包/推送/发布，P07须重核最终候选的全部门禁。

4.5 后续仍推进 AI 应用、聚合/持续计算、存储编码及跨模型成本、恢复、容量与真实质量，按本文件 M44～M46 和 V45 工作包执行；这些增量不自动成为单独 Workbench Web 预览的先决任务。已撤回的系统性能原始报告不作为验收依据。完整背景见 [路线历史](docs/roadmap-history.md)、[4.5 规划核查](docs/audits/sonnetdb-45-roadmap-planning-20261003.md)、[九模型证据](docs/audits/nine-model-capability-evidence-20260905.md)和[十四能力索引](docs/audits/fourteen-capability-evidence-index.json)。



## 被替代的另一组全局执行顺序

1. **基线与合同冻结：** M44-A01/A02/A06、M45-C01/C02/C09、M46-S01/S09、M49-C01/C02、V45-X06；冻结支持矩阵、语料与门槛，不重做归档实现。
2. **基础实现与成本治理：** M45 分组/state、M46 编码/精度 guard、V45-X01/X02；读取按 M49-R01/R02/R03+C03 接通公共链路，再逐语言 C04～C07；每包有必要定向回归、Release 和适用 AOT。
3. **持续计算与恢复：** M45-C03/C04/C05/C08 接既有 M43，V45-X03/X04/X08；不支持源/操作及未知结果明确拒绝或进入恢复。
4. **AI 应用与九模型旅程：** M44-A03/A04/A05/A07、M45-C06/C07、M46 深化和 V45-X05；条件项独立证明收益后决定。
5. **性能、质量与发布论证：** 执行既有目标机队列和新应用/聚合/编码 gate，绑定同一候选；本机合成数字不升级生产结论。
6. **统一管理工作台与三面发布：** M47 先评审完整导航、五区壳、共享对话框/提示和一级/二级任务，再逐页细化；用户确认原型后推进共享合同、九模型工作台与生产迁移。Web Admin/Studio/VS Code 旅程、AI/MCP 入驻和版本兼容矩阵分别通过对应宿主证据；单端或原型 PASS 不升级为三面整体发布。

独立切片可并行；共享源码、构建输出和资源验证按所有权协调。单 PR 只做一个可审查切片。

<a id="milestone-50"></a>
