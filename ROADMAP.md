# ROADMAP

本文件只保留当前仍需交付、补证或继续深入的工作。已经完成的里程碑压缩为结果摘要；历史 PR 拆分与设计记录见 [docs/roadmap-history.md](docs/roadmap-history.md)。

图例：✅ 已完成 / 🟡 本机或配置级完成、外部真机或发布门禁待验证 / 🚧 进行中、仅完成部分切片或仍有实现残余 / ⏳ 尚未执行或明确后置 / ❌ 已执行但未通过 / 📋 计划中 / ⏸️ 暂停 / ➡️ 移交。历史条目中的“✅（待验证）”按 🟡 理解。

## 完成判定

2026-07-14 起，里程碑只有同时满足以下条件才标记为完成：

1. 代码存在，且真实产品入口已经接到该实现；占位类型、未调用服务或仅有 UI 原型不算完成。
2. 自动化测试覆盖主要合同，并至少完成一次与声明相符的运行验证。
3. 涉及 CI、nightly、容量、发布或 Marketplace 的声明，必须有对应 workflow、报告或已发布产物证据。
4. 文档描述与实际依赖、调用链和限制一致；“计划采用”不能写成“已经基于”。

本轮核查基于 SonnetDB `424f61ad16e883d6b9050a9eb29a352105d28cef + dirty`（`3f362d1c387149524c8d08f536a687c135aa45eb` 为祖先）以及外层 TOLNSD `770a139f6f00512d7725458cb7ca43bb1ad75620`（直接父提交为请求基线 `988ed78df18b46a399d5b544fd78904452d2405f`）。九域与规划器性能证据见 [2026-09-01 系统性能报告](docs/benchmarks/system-performance-20260901.md)；旧 gitlink `0be6898` 不属于当前基线。

## 里程碑总览

| Milestone | 主题 | 状态 | 核查结论 |
|---|---|---|---|
| 0~13 | 引擎、SQL、服务端、函数、向量底座 | ✅ | 实现与测试已落地，详情归档。 |
| 14 | SonnetDB Copilot | 🚧 | MCP、知识库、skills 和自研 `CopilotAgent` 已落地；Microsoft Agent Framework 未接入；本地 ONNX 的显式 profile 执行合同已转入 M27 推进，真实目标模型证据仍未完成；在线 provider-neutral 接线也转入 M27。 |
| 15~17 | GEO/轨迹、Copilot UX、可观测性 | ✅ | 功能与测试已落地；会话以服务端持久化为准，不回退 `localStorage`。 |
| 18 | SonnetDB for VS Code | ✅ | `0.4.1` 已发布；smoke、隔离 VSIX 安装和本地/Marketplace SHA256 对拍通过。 |
| 19 | 生态适配底座 | ✅（待验证） | #109~#124、#126/#126.1 与 #125 runner、workflow、报告 verifier 已实现；四个默认容量档固定目标硬件报告待后续真机验证。 |
| 20 | 多模型 Parity | ✅（待验证） | 套件、宿主 readiness、失败路径结构化 summary 和 nightly verifier 已实现；7 天 scheduled 连续证据待后续运行验证。 |
| 21 | Document Store 单机能力 | ✅ | 常用单机 Document 子集已落地。 |
| 22 | 上层应用/示例候选 | ⏸️ | 不作为 SonnetDB 内置里程碑；通用能力缺口再回收。 |
| 23 | 搜索与向量引擎合并 | ✅ | DotSearch / DotVector 能力已收编。 |
| 24 | Document 管理面 | ✅ | Explorer、Validator、导入导出和维护入口已接入共享工作台。 |
| 25 | Document 验收与发布治理 | ✅（待验证） | parity、runner、schema v2 报告和发布 verifier 已实现；#174 百万/千万固定目标硬件档待后续真机验证。 |
| 26 | 连接器路线 | ✅ | C ABI 与多语言入口已交付，连接器 release workflow 通过。 |
| 27 | AI / Agent 数据访问与治理 | 🚧 | 产品定位与 MCP 合同已校准；工业 Demo 和 eval 已完成研发闭环（真实 provider 运行待验证），本地 ONNX 真实目标模型语义证据与双网客户端 Copilot 仍有研发缺口；#185 profile 合同与证据门禁见 [专页](docs/benchmarks/m27-provider-model-profile.md)。 |
| 28 | 可靠性、并发与热路径加固 | ✅ | P0~P5 与 SDK 补口已收官。 |
| 29 | 多模型统一管理工作台 | ✅（待验证） | Web/Studio/VS Code 功能、Studio 独立 bundle/MSI、宿主生命周期合同和自动化测试已落地；干净 Windows 安装、WebView2、升级/卸载保留及端口冲突仍待真机验收。 |
| 30 | Sparkplug B / CoAP / UDP 接入 | ✅ | 协议入口、生命周期、安全、parity 和基准已落地。 |
| 31 | 时序聚合类型语义 | ✅ | selector / categorical aggregates 已落地。 |
| 32 | Document MongoDB-like 易用性 | ✅ | SDK、查询/更新、multikey/wildcard 索引、aggregation、mixed Bulk、迁移 CLI、Workbench、Quickstart 与结构化 gap report 已闭环。 |
| 33 | 时序聚合执行与下推 | ✅ | Geo 正确性、多聚合复用、残差流式化、count(*)、LIMIT/latest-N 下推已落地。 |
| 34 | Modbus TCP 内建映射表 | ✅ | #288~#296 已完成 DDL/catalog、地址/codec、TCP master/slave、受限 Source 写、Endpoint 外部写治理、管理面、审计与文档。 |
| 35 | 语义内容与多模态检索 | 🚧 | #297/#299/#301 已完成，#298/#300/#302 已交付部分能力；RAG Core 首切片已落地，CLI、持久化 writer/retry/resume、实际派生索引应用与剩余质量/媒体/治理项仍待完成。 |
| 36 | 既有八模型专用品类易用性对齐（原范围） | 🚧 | #316 嵌入式 KV 首切片已落地；#310/#311、远程 parity、golden journey、产品入口及其他模型专用工作流仍待完成。 |
| 37 | 视图与物化视图 | ✅ | #327 逻辑视图与 #328 显式全量刷新物化视图均已实现。 |
| 38 | SQL 存储过程与触发器 | ✅ | #329~#332 已完成 SQL 过程、关系表 AFTER ROW 触发器及治理收口；外部脚本运行时保持暂停。 |
| 39 | SQL 触发器第二版 | 🚧 | #333 证据 runner、三条关系表 journey、三种 DML 成本/回滚矩阵和真进程 crash 场景已接入；固定目标硬件矩阵仍待归档，再决定高级语义与多模型范围。 |
| 40 | 原生属性图数据库 | 🚧 | Phase 0 已完成；修复与发布步骤 1~5 已关闭。步骤 6 继续加固：Expand/traversal/weighted-path 已按剩余预算读取且最多增加一条 probe，避免预算外邻接解码；步骤 7 已修复 typed point read 将结构化 `graph_not_found` 误判为元素缺失 `null` 的远程 parity 问题；generation 新增 exact-revision lease，供 orderly reopen 的分页链固定 retired revision。Phase 1 仍缺 #352 正式准入证据；固定硬件、PostgreSQL/Neo4j、LDBC/Graphalytics、Couplet C2~C4、Native AOT journey 与 7 天生产证据均保持 `NOT_RUN`。 |
| 41 | 关系查询规划与执行性能加固 | 🚧 | #368~#374、#376~#380 与 #381 本地合同已收口；#375 的统计结构、持久化、显式 `ANALYZE` 和分配优化已完成，但自动刷新仍可能在首个业务规划线程同步采样，未满足“不长时间阻塞业务”的原合同。固定硬件、木垒同语料、7 天 mixed workload 与现场发布观察均未执行。 |
| 42 | 九域与规划器系统性能深化 | 🚧 | ✅ 九域矩阵、竞品入口和统一指标已建立；🟡 统计/CRC 本机切片、SQL 指标上界、三域读取 smoke、Rebirth 合同及 win-x64 AOT 已取证；🚧 九域容量闭环和 P0~P3 残余仍在推进；⏳ 固定 x64/ARM64、木垒同语料、168 小时与生产门禁未执行。 |
| MM9 | 多模型备份恢复第一批 | ✅ | `BackupService` 与 `sndb backup` 已落地。 |

## 当前推进顺序

1. M41 #368~#381 的既定本地合同除 #375 自动刷新边界外已收口；M42 先完成“自动统计刷新移出首读”和 ARM64 可执行/AOT 两项 P0，再推进无偏采样、页感知成本、参数敏感计划、独立 I/O 预算及九域 benchmark。木垒同语料、固定硬件、7 天 mixed workload、真进程 crash/replay、部署 Native AOT 和生产 gate 均保持 ⏳，不得用本机数字或增加 permit/内存/索引数量代替根因修复。
2. M20 Parity nightly、M19 #125 与 M25 #174 的研发 runner/verifier 已收口；连续 nightly 与固定目标硬件容量报告作为后续现场验证，暂不阻塞其他研发。
3. 收口 M27 的真实 provider/Agent 接线与双网客户端 Copilot；#184 工业 Demo、#187 eval 已完成研发闭环，真实 provider 运行证据后续补验。
4. 收口 M29 Studio 安装包/宿主生命周期实机验收。
5. M34 已完成 TCP master/slave runtime、受限 Source 写、Endpoint 外部写治理与管理面闭环；M35 在过滤 ANN 与内容生命周期地基完成后再做媒体场景。
6. M36 先完成其原八模型范围的 golden journey 与 gap catalog；实现顺序为高频客户端工作流 -> 查询诊断 -> 高级治理，Document 复用已完成的 M32 结果，向量高级项复用 M35 地基。
7. M39 先执行 #333 触发器 V2 证据门禁；未证明 V1 在真实 journey 上存在缺口前，不直接扩展 BEFORE、statement-level 或多模型触发器。
8. M40 按本节新增的“修复与发布执行顺序”推进：步骤 1~5 已关闭；步骤 6 已补剩余预算读取/单 probe 和 exact-revision generation lease，仍需固定 workload 性能证据及步骤 7 的恢复/产品 parity。所有前置门禁通过后才运行固定硬件、外部对拍和 7 天发布证据。当前公开定位已将原生属性图以 Graph Beta 计入“九种数据模型，各有原生语义，共享一套引擎”；上述门禁仍是宣称 Graph Production 的前提，不因模型计数变化而放宽。

## 待补验收证据

### M19 — 生态容量证据

#125 runner、workflow、报告 verifier 和缩规模验证已经完成，研发切片可视为完成；容量发布证据仍需在固定规格目标硬件上分别运行并归档：

- `high-cardinality`：默认 1,000,000 series。
- `small-segments`：默认 10,000 segment。
- `maintenance-chaos`：默认 20 次确定性 kill/reopen。
- `many-measurements`：默认 10,000 measurement。

每份报告必须记录 commit、机器/磁盘规格、持续时间、working set/托管内存峰值、查询与恢复 P50/P95/P99，以及 missing/duplicate/unexpected/value mismatch。缩规模 PASS 不能代替发布容量证据。

### M20 — Parity nightly 证据

Parity 场景、适配器和 compose 已存在，但“完成”还需要：

- ✅ workflow 已改为宿主 readiness 探测；restore、build、stack 或 test 失败仍生成带稳定 `gap_reason`、commit SHA 和门禁分类的 schema v2 summary，并保留容器诊断。
- ✅ 2026-08-25～27 的三个 scheduled run 已让 `light` / `full` 完整 compose 在 CI 中健康启动，并实际完成 parity、reliability、summary、artifact 和发布步骤，不再只是 `docker compose config` 证据。
- ✅ 新增只读 nightly evidence verifier，逐次校验双 profile artifact、完整 schema v2 字段与计数不变量、run/commit 绑定，并将每个 summary suite 与 `raw/<runId>/report.json` 一一对账；证据窗口下限固定为 7 次，只能向上扩大。离线 fixture 固定不足 7 次、混入失败、缺字段/原因、计数或 raw 对账不一致与七次成功合同。
- ⏳ scheduled workflow 连续 7 天成功率仍须不低于 95%。[2026-08-29 审计](docs/benchmarks/m20-parity-nightly-evidence.md)为 `NOT_READY`：最近七次只有 2026-08-25～28 四次有效，4/7（57.14%），8 月 22～24 的结构化失败不能计为通过；runner/verifier 研发已完成，但连续运行证据本身尚未完成。
- NATS、VictoriaMetrics 等第三方镜像的健康检查不得依赖镜像内不存在的 shell/wget；探活由宿主 workflow 或可用的原生命令完成。
- 失败 run 必须保留容器日志、测试报告和 commit SHA，不能发布 `No summary was produced for this run.` 作为完成证据。

### M25 — Document 容量证据

- ✅ MongoDB 参考 parity、Document 能力矩阵、迁移边界和 1 万文档 quick profile 已完成。
- ⏳ 在固定目标硬件运行 `million` 与 `ten-million` profile，归档写入、查询、rebuild、TTL、热/冷启动、crash recovery、backup/restore 和内存曲线；runner、报告 schema 和 verifier 已完成，目标硬件运行尚未执行。
- 没有对应 PASS 报告前，对外只声明“profile 可执行，规模未在目标硬件验证”；当前发布证据仅支持 1 万文档级完整治理闭环。

### M29 — Studio 实机验收

Web/Bridge smoke、Server 管理合同、Web Admin、Studio Release build 和 VS Code consumer smoke 已覆盖实现。剩余验收只保留：

- 在干净 Windows 环境安装 Studio 安装包，验证首次启动、升级/卸载和数据目录保留策略。
- 验证托管 Server 的启动、停止、异常退出、宿主退出策略、端口冲突和日志/健康状态。

上述功能性实机验收属于后续现场验证；研发交付已完成，当前标记为 ✅（待验证），不得将本地 build/smoke 宣称为真机 PASS。

## Milestone 27 — AI / Agent 数据访问与治理

目标是在不改变 SonnetDB“九种数据模型，各有原生语义，共享一套引擎”核心定位的前提下，为 Copilot、MCP 和外部 Agent 提供受权限、审计与人工确认约束的数据访问能力。工业数据诊断是验证该能力的示例之一，不是产品类别。当前实现不是 Microsoft Agent Framework：实际为 `Microsoft.Extensions.AI` 抽象加自研 `CopilotAgent`；在线 `/v1/copilot/chat` 已支持云 Gateway 与配置的 `IChatProvider` 两条路径；`LocalOnnxEmbeddingProvider` 已在显式 `ModelProfile` 下执行 tokenizer、批量输入绑定、逐行 pooling/归一化和可配置 ONNX Runtime 线程模式，并在缺少 profile、资源或运行时不可用时显式回退 hash provider；真实目标模型语义质量、性能和现场证据仍未完成。#185 的合同字段、tiny fixture 测试和真实模型证据门禁见 [provider model profile 证据专页](docs/benchmarks/m27-provider-model-profile.md)。文档和报告必须如实描述这些边界。

| 项目 | 剩余交付 | 状态 |
|---|---|---|
| #182 产品定位校准 | README / README.en、文档首页、`llms.txt` 和产品欢迎页统一为“九种数据模型，各有原生语义，共享一套引擎”；原生属性图明确标注 Graph Beta，部署方式、行业场景和 Agent 能力按层表达，不进入一级定位。 | ✅ |
| #183 MCP 合同 | 现有九个只读工具已发布机器可验证 input/output schema、v1 合同版本、稳定错误码与兼容文本，并以端到端测试冻结权限 annotation、既有 required 集合和字段类型；参数、返回、权限、错误与 extend-only 版本规则见 `docs/mcp-contract.md`，未扩大工具面。 | ✅ |
| #184 工业 Demo | 用 MQTT/HTTP 写入温度、电流、振动，演示异常设备查询、维修建议、引用和报告；数据模型、脚本、文档和视频口径一致；新增可运行 sample、结构化状态和不可达 provider 的 `NOT_READY` 门禁。真实 broker/provider journey 后续验证。 | ✅（待验证） |
| #185 Provider 接线 | 配置样例和模型分组已完成；在线 Chat 已按配置接入 `IChatProvider` 或云 Gateway，并复用本地 Agent/权限/会话合同；本地 embedding 已完成显式 profile 的 tokenizer/input/pooling 执行合同、单次 ONNX `Run` 的 `[batch, sequence]` 批处理、逐行 pooling/归一化、`IntraOpThreads` / `InterOpThreads` SessionOptions 接线、tiny fixture 测试及 `m27-local-onnx-evidence-v2` runner/verifier。v2 现在真实执行 batch 并记录 session initialized/applied/effective thread state；缺少目标模型/tokenizer/corpus、固定环境质量/性能报告时仍稳定保持 `NOT_READY`，不得把 tiny fixture 或本机 Native AOT 发布包装为真实模型证据；profile 字段和验收门禁见 [证据专页](docs/benchmarks/m27-provider-model-profile.md)。 | 🚧 |
| M14 纠偏 | 接入最新 Microsoft Agent Framework 并以测试证明，或继续明确标注“自研 orchestrator”；在真实目标模型 profile、质量和可追溯报告归档前，不得宣称 bge-small-zh 已可用。 | 🚧 |
| #186 写审批 | 已移交 M29，共享 staged preview/dry-run/confirm 已完成；M27 只消费。 | ➡️ |
| #187 Eval/成本 | 增加异常设备、慢查询、schema、维修建议和审批场景，记录 provider/model/tool/失败原因/token 成本，并给出可复现报告；已冻结 `m27-copilot-eval-v1` verifier 和诚实的 `NOT_READY` fixture，真实 provider usage/质量门禁后续验证。 | ✅（待验证） |
| #188 上层边界 | IoTSharp 联合样例归 IoTSharp；SonnetDB 只提供授权 MCP、通用引擎和 Agent 素材。 | ✅ |
| #340 双网客户端 Copilot | 在数据库服务器不能访问公网、浏览器或 Studio 同时可访问内网和公网时，由访问端编排外部 AI 与本地授权工具；Web 统一 runtime、BrowserDirect 公网 transport 与本地 typed MCP tool-call loop 已落地。ServerRelay 现提供服务端稳定 run/tool-call ID、sequence/cursor 和单进程有界重放，Web 已消费真实 envelope；断线会停止本次 provider/SQL 工作并封闭为可重放的 interrupted 终态，不在后台继续生成。页面刷新后的中段恢复、跨进程/多实例续流仍未实现。Studio bridge 已完成 origin/header-token 收紧和 NativeWebHost 内存握手前置，但还不是 AI broker；可信 OAuth token 获取、已部署公网 continuation/CSP/CORS、StudioNative transport/系统凭据库和真实双网流程仍未接入。 | 🚧 |

### #340 — 双网客户端 Copilot

目标是支持典型的双网部署：SonnetDB Server 位于不能访问公网的内网，用户通过具备内外网连接的浏览器或 Studio 使用 Copilot。访问端是 Copilot host，分别连接外部 AI 服务与内网 SonnetDB；外部 AI 不直连内网，数据库服务器也不承担公网代理。

架构必须保持以下边界：

- 数据库凭据只发送到当前活动 SonnetDB 连接的受信 origin，外部 AI 的短期凭据只发送到批准的公网 origin；日志、错误和重试不得串用或泄露两类 token。
- 浏览器或 Studio 负责对话与 tool-call 状态机；数据库授权、SQL AST 分类、行数/字节/超时限制、结果脱敏和审计仍由 SonnetDB 服务端执行。
- 本地工具面优先复用 `/mcp/{db}` 的 typed、只读合同；不得让模型直接调用原始 `/v1/sql`，不得增加任意 URL 代理，也不得把本地 MCP 暴露到公网。
- 外部数据出域必须显式启用，并采用按需 schema、聚合优先、结果截断和敏感列脱敏；工具结果只能作为结构化、不可信数据返回模型，不能拼入 system prompt。
- 运行模式显式区分 `ServerRelay`、`BrowserDirect`、`StudioNative` 和 `Disabled`。不同模式代表不同数据路径，不得根据一次探活结果静默切换。
- 当前代码使用 `ai.sonnetdb.com`。若正式入口迁移到 `sonnet.vip`，必须先冻结版本化的认证、模型发现、流式事件、tool call/result、取消、续流和错误合同，不能只替换域名。

分层交付：

| 阶段 | 交付 |
|---|---|
| Client runtime 合同 | 抽取统一 Copilot transport 和事件状态机，保留现有 server relay；加入独立的本地/公网 readiness、run id、tool call id、sequence/cursor、幂等和取消合同。Web 已实现显式四模式、统一状态机、拒绝重定向/截断/终态后事件的 fail-closed 门禁，以及关闭/切会话/卸载取消和服务端会话重同步；BrowserDirect 已从显式 HTTPS 公网 URL 与批准 origin 注册到真实聊天入口，公网 token 仅驻留内存并执行两小时 TTL、数据库 token 隔离、过期/登出清除及不回退门禁。单次 BrowserDirect 运行按稳定 `toolCallId` 和规范化参数缓存本地结果，同 ID 同参只回放缓存，同 ID 冲突在再次执行前拒绝，重复回放也计入有界 loop 预算。ServerRelay 服务端现以 source-generated envelope 发布稳定 `runId`、`sequence`、`cursor`、`toolCallId`，按认证 owner、database 和请求 fingerprint 绑定单进程 active/replay/tombstone，并对等价 tool-call/result replay 幂等、冲突 replay fail closed；Web 已发送 runtime run ID 并拒绝缺失服务端 envelope 的旧 SSE。请求断开会取消 linked provider/SQL 工作并记录 interrupted `error/done`，不会启动后台 continuation；现有 cursor 只在同一 Server 进程 journal 内可用，页面刷新后的新 runtime、中段 sequence 初始化、进程重启和多实例续流仍待实现。Studio bridge 启动配置已改为 NativeWebHost request/event 内存握手，旧 URL/query/storage 凭据会清理；这只关闭 transport 接入前的宿主安全前置。可信 OAuth token 获取和 StudioNative transport 仍待实现。会话、消息和 usage 继续回写 SonnetDB 服务端持久化，客户端只同步状态，不回退 `localStorage` 作为权威来源。 |
| 本地工具会话 | Web BrowserDirect 已基于当前数据库 Bearer 与固定 `/mcp/{db}` 执行 `initialize` / `tools/list` / `tools/call`，要求工具同时声明 read-only、non-destructive、idempotent、closed-world，校验 input/output schema 和 typed contract v1；generic error、typed error、未知/未批准工具、结果超过 64 KiB 或单轮超过 8 次均在向公网 continuation 前 fail closed。数据库 grant、SQL AST、系统库和结果行数仍由服务端每次调用重新约束；公网出域还必须显式开启并配置 allowlist。 |
| Browser Direct | 已接入显式 HTTPS 公网 URL、approved origins、独立公网 readiness、`fetch` 分段流式入口与本地 typed MCP loop；access token 仅驻留内存、最长两小时，并与数据库 token 隔离。每次 continuation 只发送到构造时批准的公网 endpoint，且下一段首事件必须逐字回显对应 `tool_result`。可信 Device Flow/PKCE 获取、已部署公网 continuation 合同、CSP/CORS 部署和真实公网服务联调仍待完成。 |
| Studio Native | 由固定目标、非通用代理的 native broker 访问公网并使用系统凭据库保存 refresh token/BYOK。bridge 安全前置已完成：仅接受配置的 Studio origin、header token 和 CORS preflight，拒绝 query token；endpoint/token 通过 NativeWebHost request/event 只进入当前 WebView 内存，并清理旧 URL/storage 残留。AI broker、系统凭据库、外部 provider transport 和真实双网流程仍未实现。 |
| 治理与写入 | 第一阶段只开放只读 MCP。写工具后续消费 M29 的 staged approval，并增加由服务端签发、绑定 user/database/run/tool-call/规范化参数哈希/expiry 的一次性 confirmation challenge；模型或浏览器的 `confirmed=true` 不构成授权。 |
| 审计与评测 | 记录模式、provider/model、数据库、工具、规范化参数或 SQL hash、行数、字节数、脱敏策略、审批和失败原因，默认不记录原始结果；纳入 M27 eval 与成本报告。 |

#340 验收要求：

- 在 SonnetDB Server 的 DNS 与公网出口均被阻断时，具备双网连接的浏览器和 Studio 均能完成一条真实的只读 grounded Copilot 流程，网络证据确认服务器没有产生 AI 外连。
- 自动化测试证明 SonnetDB token 从不发送到公网、外部 AI token 从不发送到数据库服务器，未知工具、合同版本不匹配和未批准数据出域均 fail closed。
- 覆盖 CORS preflight、分片流解析、取消、断线续流、重复 tool call、客户端代理和四种本地/公网可用性组合；公网不可用时不得自动排队或重放包含数据库结果的请求。
- 本地工具调用保留现有认证与 database grant，并有 SQL AST、系统库、行数、字节、时间和并发门禁；第一阶段不存在可绕过人工确认的写路径。
- 页面刷新或 Studio 重连后可从 SonnetDB 服务端恢复会话；现有 `ServerRelay` 路径继续通过回归测试，客户端模式与服务端模式之间不得静默切换。

验收要求：AI / Agent 文案不得替代多模型引擎的核心产品定位；本地关闭云端外发时仍有一条可运行路径；高风险写入必须经权限和人工确认；外部 Agent 只通过授权 MCP/HTTP 合同访问，不直读目录或系统表。

## Milestone 32 — Document MongoDB-like 易用性

已完成：在既有 update、compound/unique/sparse/partial/TTL 索引、change feed 和管理面基础上，补齐类型化 SDK builder、AOT 友好值、分页 cursor 与稳定错误码；新增 `$mul/$pop`、原子 `findOneAndUpdate`、常用数组/类型/正则查询、递归 `$not` 和基础 ordinal collation。path index 支持 multikey/wildcard、parallel-array 拒绝、typed key、planner/EXPLAIN 与崩溃后派生索引修复；aggregation 支持 computed project/group expression、`push/addToSet` 和完整 unwind。

mixed Bulk 已统一 Core、HTTP、.NET SDK 与 Web/Studio 的 ordered/unordered、逐项结果、单 collection 原子边界、稳定批次上限错误和 24 小时 `requestId` 重放。`sndb document import` 可执行 JSON/NDJSON、常用 Extended JSON 和 mongodump BSON 子集导入，提供 dry-run、checkpoint/resume、确定性批次 ID、索引建议与机器报告；Document Quickstart、OpenAPI、迁移指南和 `docs/document-mongodb-gap.json` 提供可运行入口及 supported/partial/planned/not_planned 证据。

边界保持不变：不承诺 MongoDB wire protocol、BSON command、官方 Driver 直连、replica set、sharding、跨 collection 事务、locale collation、完整 aggregation/BSON 或 positional array update；对外只使用 “Document Store” 或 “MongoDB-like workloads”。

## Milestone 34 — Modbus TCP 内建映射表

SonnetDB 同时支持两个明确角色：主站/client 主动轮询外部 PLC/RTU 并写入表；从站/server 暴露受控寄存器映射供外部主站读取或 staged 写入。协议运行时默认关闭，普通 `SELECT` 只读已采集状态，不同步阻塞访问 PLC。

M34 已完成本地合同与持久化地基、默认关闭的 TCP master/slave runtime、Source 受限写、采集质量与失败策略，以及 Endpoint 外部写治理。Endpoint 支持 `0x05/0x06/0x0F/0x10` 写请求的 `REJECT` 或 durable `STAGED` 入口，审批后按 `STAGE_ONLY/UPDATE_TABLE` 执行；待审批队列、统一审计、REST 管理合同和 Web 管理页均已接线。catalog `ENABLED` 仍不能绕过全局门禁，IoTSharp 只通过公开合同消费能力。

| PR | 交付 | 状态 |
|---|---|---|
| #288 | 定稿 `CREATE MODBUS SOURCE/ENDPOINT`、`FROM MODBUS`、`EXPOSE AS MODBUS` 的 DDL、方向、地址、类型、字节序、缩放、访问和错误策略。 | ✅ |
| #289 | Parser/AST/catalog、版本兼容和 `SHOW/DESCRIBE MODBUS` 元数据。 | ✅ |
| #290 | 地址冲突校验及 BIT、整数、浮点、BCD、STRING 的 Span/BinaryPrimitives 编解码。 | ✅ |
| #291 | 默认关闭的 TCP master runtime、批量读取、轮询、取消、退避、超时、重连和指标。 | ✅ |
| #292 | 受限 SQL 写寄存器、preview/dry-run、权限和审计；远端失败不得伪造本地成功。 | ✅ |
| #293 | 质量位、错误码、source health、latest/history 与 KEEP_LAST/NULL/SKIP/MARK_BAD 策略。 | ✅ |
| #294 | 默认关闭的 TCP slave endpoint、读请求、绑定/白名单/unit id/最大连接数。 | ✅ |
| #295 | 外部写入的 REJECT/STAGED/UPDATE_TABLE 策略、待确认队列和审计；默认 STAGED。 | ✅ |
| #296 | Web/Studio 管理面、模拟 PLC parity、文档，以及 IoTSharp Product/Collection Template/Gateway/EdgeNode 合同边界。 | ✅ |

验收要求：四类寄存器读写与类型转换可对拍；写入不绕过审批、权限和审计；IoTSharp 只通过稳定合同消费，不依赖 SonnetDB 内部 catalog。第一版只做 Modbus TCP，不扩张到 RTU/ASCII、OPC UA、S7 或完整 SCADA。

## Milestone 35 — 语义内容与多模态检索

复用 Object Bucket、Document、FullText、Vector 和 Hybrid Search，建立“原始内容 → 异步提取/embedding → 可重建派生索引 → 检索”的受治理链路。Core 只负责确定性存储和检索，不下载模型、解码媒体或同步调用外部推理。

| PR | 交付 | 状态 |
|---|---|---|
| #297 | Semantic Content 清单、object reference、chunk/segment、状态机和 Embedding Profile 合同。通用内容清单、稳定 chunk/segment、对象引用、派生状态机、profile 隔离、外发策略和 source-generated JSON 合同已落地。 | ✅ |
| #298 | metadata-filtered ANN、精确补偿/回退、similar-by-id 和可解释 EXPLAIN。当前已落地 source bucket、metadata/tag path/wildcard 预过滤、managed HNSW filtered traversal、小候选精确补偿、大候选/不可索引条件分页且可取消的精确回退、HNSW allowed-key 漂移 fail-closed、similar-by-id、自身排除和按需候选解释；USearch filtered API、可配置预算、固定硬件 recall/延迟/容量证据仍未完成。 | 🚧 |
| #299 | 异步摄取、幂等 hash、重试/取消/背压/重启恢复，以及对象覆盖删除后的对账。已落地 KV 持久化任务、幂等对象版本、5 次退避重试、取消/替代、有界 Channel 背压补偿、重启恢复，以及普通删除、批量删除和生命周期过期后的语义索引/缩略图清理。 | ✅ |
| #300 | provider-neutral text/image/object embedding 能力发现、外发策略和调用审计。当前已落地 text/image provider 合同、SigLIP2 ONNX 与状态发现；object embedding、外发策略和调用审计仍未完成。 | 🚧 |
| #301 | 图片搜图片、文字搜图片、缩略图/来源/profile/分数展示和工业图片样例。已落地原图摄取/读取、文搜图、图搜图、WebP 缩略图、来源/profile/分数 REST 契约、managed/USearch 后端、对象桶管理面和可运行工业图片样例。 | ✅ |
| #302 | 通用 RAG 摄取 SDK/CLI、稳定 chunk、增量更新、删除同步和 Copilot 可回滚迁移。Core 已落地严格 UTF-8 的确定性 hash、Unicode 安全稳定分块、完整快照 add/update/delete diff、资源预算、取消和有界 callback executor；CLI、持久化 writer/retry/resume、实际 Document/FullText/Vector 删除应用与 Copilot 可回滚迁移尚未实现。 | 🚧 |
| #303 | RRF/归一化/去重/rerank hook，以及 Recall@K、nDCG、P50/P95、体积和重建评测。 | 📋 |
| #304 | 音视频 transcript、关键帧和 timecode segment；媒体处理留在可选扩展或外部工具。 | 📋 |
| #305 | 管理面、安全、失败恢复、备份重建、模型换代和 10k/100k 容量基线。 | 📋 |
| #306 | 派生目标、区域、track 与 detector profile 模型，保持原对象为唯一主数据。 | 📋 |
| #307 | 默认关闭且受治理的人脸 1:1 验证/1:N 候选，独立权限、审计、删除和 FAR/FRR/TAR 评测。 | 📋 |
| #308 | ReID、步态、姿态/动作的独立 profile、查询和对应 mAP/CMC/precision/recall 评测。 | 📋 |
| #309 | 车辆外观向量与车牌 OCR；号码以标准化精确索引为主，向量不替代相等语义。 | 📋 |

顺序固定为 #297/#298 地基 → #299/#300 摄取/provider → #301/#302 首批场景 → #303 质量 → #304/#305 扩展收口 → #306~#309 专业视觉。完成 #301 前只宣称“具备多模态检索底座”。所有生物特征能力默认关闭，并要求用途、权限、访问/导出审计、保留期限和删除闭环。

## Milestone 36 — 既有八模型专用品类易用性对齐（原范围）

目标是让每种数据模型都保留该品类用户熟悉的高频工作流，同时共享 SonnetDB 的连接、权限、审计、错误和运维边界。M20 回答“能力和结果是否对得上”，M29 回答“管理工具是否有入口”，M32 深化 Document MongoDB-like 易用性；本里程碑只处理从第一次成功调用到分页、批处理、失败恢复和诊断的**产品易用性**，不重复三者已经完成的工作。

参照产品是学习来源，不是兼容承诺。每项能力进入实现前都必须用代码、公开 API、真实产品入口、测试和文档建立 `supported / partial / planned / not_planned` 证据；已存在的能力只补入口或文档，不得重新实现。

### 逐模型分析与取舍

| 数据模型 | 主要参照与学习理由 | 已有基线，不重复建设 | 优先吸收的易用性 | 明确不吸收 |
|---|---|---|---|---|
| Document | MongoDB / MongoDB Compass：文档心智、builder、cursor、局部更新、索引与迁移诊断成熟。 | CRUD、常用 update、分页、索引、aggregation 子集、validator、change feed 和管理面已存在。 | 类型化 builder、常用缺失操作符、multikey/wildcard、混合 bulk、可执行迁移与结构化 gap report，全部归 M32。 | MongoDB wire/BSON command、官方 Driver 直连、replica set、sharding、分布式事务。 |
| 关系 SQL | PostgreSQL：SQL 行为与错误诊断；SQLite：嵌入式零配置；EF Core / DBeaver：.NET 与开发工作流。 | ADO.NET、EF Core、参数绑定、轻事务、主外键/索引/CHECK/ROWVERSION、`INSERT ... RETURNING`、数据库生成整数键回填、EXPLAIN 和关系工作台已存在。 | `UPDATE/DELETE ... RETURNING`、`INSERT ... ON CONFLICT` 等高频 DML；可定位错误与稳定 code/hint；实际行数/耗时诊断；连接、取消、超时和 schema migration 的清晰入口。 | pgwire、完整 PostgreSQL 方言/extension、MVCC 全隔离级别、存储过程全集、HA 管理面。 |
| 时序 | InfluxDB：Point/Line Protocol 与批量 Write API；VictoriaMetrics / Grafana Explore：range query 与排障；TimescaleDB：SQL 连续性。 | measurement/tag/field/time、自动 schema 演进、LP/JSON/Bulk、窗口/填充/聚合、Retention、图表与多协议接入已存在。 | 类型化 Point writer、批量 flush/retry/backpressure 与逐项错误；range/aggregate/gap-fill 查询 builder 和流式结果；precision/schema/cardinality/retention 预检与摄取诊断。 | Flux/PromQL 全语言兼容、分布式集群、无限基数承诺、把采集 Agent 或长期任务调度器塞进 Core。 |
| KV / Cache | Redis：原子 key 操作、TTL 和条件写心智；RedisInsight：namespace、类型与过期诊断；.NET Cache：框架接入。 | bytes get/set、many、prefix scan/delete、TTL、INCR/DECR、CAS、`IDistributedCache`、EasyCaching 和 KV 工作台已存在。 | `NX/XX` 风格条件写、get-and-set/delete；AOT 友好的 UTF-8/JSON codec；异步 cursor/pipeline、批量分项结果、hot key/expiry/容量诊断。 | RESP/redis-cli 直连、List/Set/Hash/Stream 全数据结构、Lua、Pub/Sub、Redis Cluster 或跨 keyspace 事务。 |
| 全文检索 | Meilisearch：单一 Search 请求、typo tolerance、filter/facet 和 task 状态；OpenSearch/Kibana：analyzer 与 relevance explain。 | Document 全文索引、BM25、Unicode/CJK/Jieba、exact/fuzzy/phrase/boolean、Document filter、analyze、rebuild、客户端高亮和 playground 已存在。 | 用类型化 `SearchRequest/Result` 汇总现有 mode/filter/page；补 sort、facet distribution、服务端 matched offset/highlight；增加 searchable/filterable/sortable fields、synonym/stopword/typo 设置、analyzer diff、score explain 和 rebuild progress。 | Elasticsearch Query DSL 全集、聚合分析平台、分片副本集群、把全文索引变成第二份主数据。 |
| 向量检索 | Qdrant：point/payload/filter 与 collection UX；`Microsoft.Extensions.VectorData`：.NET 抽象；pgvector：SQL 组合能力。 | Measurement/Document 向量、HNSW/IVF/IVF-PQ/Vamana、精确回退、Hybrid Search、VectorData adapter 和 playground 已存在。 | 以 VectorData 为 .NET 默认入口，补齐 batch/filter/threshold/include 与 SonnetDB extension options；Measurement 仅补其不能表达的原生请求；增加 fast/balanced/accurate preset、维度/模型预检、index health、ANN/scan fallback explain 与质量报告。filtered ANN、similar-by-id 和 Embedding Profile 复用 M35 #297/#298。 | Qdrant/Milvus wire、第二套 collection/vector catalog、自动偷跑 embedding、分布式 shard/replica、用向量替代精确等值语义。 |
| 对象存储 | S3 SDK：stream、conditional request 与 Transfer Manager；MinIO Console：bucket/prefix、multipart 和治理渐进呈现。 | bucket/object、stream/range、continuation、multipart、version/lifecycle/retention/legal hold/audit/quota/presign 和工作台已存在。 | 自动 multipart 的传输管理器、并发/重试/校验和/断点续传/进度；`If-Match/If-None-Match`、metadata/content type；异步分页和带 dry-run 的 `cp/sync`。 | SigV4/S3 wire 全兼容、跨节点复制、纠删码集群、把 prefix 伪装成真正目录或承诺 POSIX 文件系统语义。 |
| 消息队列 | NATS JetStream：简洁 producer/consumer 与 drain；RabbitMQ：ack/nack/retry/DLQ；Kafka：offset/time/lag 与批处理运维。 | publish/batch、pull/ack、consumer offset、持久 replay、push stream、retention、MQTT 桥接和 MQ 工作台已存在。 | 高层 producer/consumer builder、`IAsyncEnumerable`、manual/auto ack、prefetch/backpressure 与 graceful drain；nack/redelivery/max-delivery/DLQ、message-id 去重、offset reset 和可解释 lag。 | Kafka/AMQP/NATS wire、partition rebalance、broker 集群、跨节点 consumer group、未经证明的 exactly-once。 |

### 交付拆分

| PR | 交付 | 状态 |
|---|---|---|
| #310 | 原八模型范围 usability gap catalog 与可执行 golden journey：记录每个常用任务的当前入口、证据、手写样板量、失败恢复和 `supported/partial/planned/not_planned`；与 M20 capability report 分开。 | 📋 |
| #311 | 统一新客户端合同：连接/鉴权、取消/超时、分页、批量分项错误、correlation id、仅对可安全重试操作启用的 retry/idempotency 元数据；不强行抹平各模型概念。 | 📋 |
| #312 | SQL 高频 DML：关系表 `INSERT ... RETURNING`、ADO.NET 语句级 last-insert-id 与 EF Core 数据库生成整数键回填已落地；仍需 `UPDATE/DELETE ... RETURNING`、SonnetDB-native `INSERT ... ON CONFLICT` 子集和稳定冲突结果。 | 🚧 |
| #313 | SQL 开发诊断：带位置/code/hint 的解析与执行错误、`EXPLAIN ANALYZE` 实际行数/耗时/回退原因，以及取消和超时闭环。 | 📋 |
| #314 | 时序类型化 Write API：Point builder、precision、batch/flush、限界背压、传输级重试、逐项错误和 dispose/drain；嵌入式与远程语义一致。 | 📋 |
| #315 | 时序 Query API 与建模诊断：range/aggregate/window/gap-fill builder、流式结果，以及 schema/cardinality/retention/坏点预检；不新增第二套查询引擎。 | 📋 |
| #316 | KV 条件与类型化 API：嵌入式 Core 已落地 NX/XX、原子 get-and-set/delete、namespace 视图、严格 UTF-8 与基于 `JsonTypeInfo<T>` 的 AOT JSON codec，保持 raw bytes 为底层权威语义。#310/#311、远程 parity、golden journey 和产品入口尚未完成。 | 🚧 |
| #317 | KV 大 keyspace 工作流：异步 cursor、pipeline/batch 分项结果、取消/背压和 hot-key/expiry/容量诊断；现有 many/prefix/TTL 不重做。 | 📋 |
| #318 | FullText 高层 Search API：复用现有 query kind、Document filter 和分页，形成 query/filter/sort/facet/highlight/page typed contract；补服务端 matched offsets/terms 与稳定 score metadata。 | 📋 |
| #319 | FullText 设置与诊断：searchable/filterable/sortable fields、synonym/stopword/typo policy、analyzer diff、relevance explain 和可观察 rebuild task。 | 📋 |
| #320 | Vector 高层 Search API：以 VectorData adapter 为默认入口补 batch/filter/threshold/include/exact 与 fast/balanced/accurate preset；SonnetDB-specific 能力用 extension options 表达，不另建 collection API。 | 📋 |
| #321 | Vector 生命周期与解释：dimension/metric/Embedding Profile preflight、index health/rebuild progress、ANN/scan/补偿原因与 recall report；依赖 M35 #297/#298 的部分不得提前复制实现。 | 📋 |
| #322 | Object Transfer Manager：自动 multipart 阈值/part size/并发、checksum、retry、resume、progress、取消和资源释放，基于现有 `SndbObjectStorageClient`。 | 📋 |
| #323 | Object 日常文件流：conditional put/get、metadata/content type、异步 continuation，以及 CLI `cp/sync --dry-run`、冲突与删除保护。 | 📋 |
| #324 | SonnetMQ 高层 consumer：producer/consumer builder、push/pull `IAsyncEnumerable`、prefetch、manual/auto ack、限界背压、取消和 graceful drain。 | 📋 |
| #325 | SonnetMQ 投递失败治理：nack/redelivery/max-delivery/DLQ、message-id 去重窗口、offset earliest/latest/time/explicit reset、lag 与丢弃原因诊断。 | 📋 |
| #326 | 原八模型范围收口：每模型一个嵌入式/远程同代码或最小差异样例，SDK/API/Workbench/CLI 能力矩阵、结构化 gap report 和用户任务 e2e；Document 结果汇总自 M32，不复制任务。 | 📋 |

### 顺序与验收

顺序固定为 #310 先建立证据，#311 固化共享合同；随后优先 #314/#316/#322/#324 四条高频客户端路径，再推进 SQL/全文/向量的查询与诊断，最后以 #326 收口。M32 可独立推进；#321 中的 filtered ANN、similar-by-id 与 Embedding Profile 必须等待 M35 #297/#298，不得另建旁路。

完成要求：每个模型至少有一个 20 行左右的最小成功样例和一个生产化样例；嵌入式/远程对同一合同做 parity；分页或流式读取内存有界；取消可停止真实工作；重试不会把非幂等写静默执行两次；错误包含稳定 code、操作与可行动建议且不泄露数据；对应产品入口和自动化测试真实接线。UI 控件只能在引擎/SDK 语义完成后开放。M36 不以“拥有与参照产品同名功能”判定完成，而以 golden journey 可运行、gap 可解释、失败可恢复来判定。

总边界：不新增任何竞品 wire protocol，不宣称完整替代专用数据库，不引入分布式复制/分片/集群，不为统一表面 API 混淆各数据模型的原生语义，也不把外部采集、媒体推理或长运行工作流塞进 Core。

## Milestone 37 — 视图与物化视图

目标是在不复制查询引擎的前提下提供可持久化的查询抽象，并把普通视图与具有物理存储、刷新生命周期的物化视图明确分开。

| PR | 交付 | 状态 |
|---|---|---|
| #327 | 逻辑视图：`CREATE VIEW [IF NOT EXISTS] ... AS SELECT ...`、`DROP VIEW [IF EXISTS]`、`SHOW VIEWS`、`DESCRIBE VIEW`、`information_schema.views`；定义以独立版本化目录持久化，查询通过派生表 AST 展开复用现有表/measurement/document/子查询/JOIN 执行器；拒绝参数化定义、未知依赖、循环/过深展开，并阻止删除或修改被引用基础对象。 | ✅ |
| #328 | 物化视图：定稿 `CREATE MATERIALIZED VIEW`、`REFRESH MATERIALIZED VIEW`、`DROP`、`SHOW/DESCRIBE`；首版仅显式全量刷新，结果写入独立派生存储并用临时代际 + 原子切换发布，刷新失败保留上一可读版本；记录定义版本、刷新时间、状态、错误和依赖。增量刷新、定时调度、后台自动刷新须经独立正确性和容量证据后再排期。 | ✅ |

验收要求：普通视图不保存数据，读取时使用当前基础对象数据；物化视图不得把刷新中间态暴露给读者。两者都不得绕过现有数据库权限、SQL 参数绑定和审计入口。首版不支持 `OR REPLACE`、`CASCADE` 或跨数据库依赖。

## Milestone 38 — SQL 存储过程与触发器

首版只实现 `LANGUAGE SQL`。Catalog 可为未来语言类型保留显式字段，但当前不得加载 C# 脚本、.NET DLL、JavaScript、Python、WASM、Shell 或任意外部进程；这些运行时继续暂停，恢复评估时必须单独完成隔离、权限、资源限额、AOT 和供应链威胁模型。

| PR | 交付 | 状态 |
|---|---|---|
| #329 | SQL 过程合同与目录：定稿 `CREATE PROCEDURE` / `DROP PROCEDURE` / `SHOW PROCEDURES` / `DESCRIBE PROCEDURE` / `CALL`，支持有序 IN 参数、SQL body、创建时间和 `LANGUAGE SQL`；使用独立版本化目录持久化，禁止重载、默认参数、OUT/INOUT 和动态 SQL。 | ✅ |
| #330 | SQL 过程执行：复用现有 parser、binder、executor 和轻事务；参数按 AST 绑定而非文本替换，定义时完成语法与对象依赖校验；明确多语句结果合同、调用方权限、失败回滚、取消、语句数/嵌套深度/结果行数上限与递归拒绝。 | ✅ |
| #331 | SQL 触发器第一版：定稿关系表 `AFTER INSERT/UPDATE/DELETE` 的 `FOR EACH ROW` 语义、`OLD`/`NEW` 只读行上下文、确定性触发顺序和 `WHEN` 条件；触发动作只允许受限 SQL body，与发起 DML 同一提交边界，任一失败回滚原操作。Document/measurement 触发器在写放大、批量语义和恢复证据完成前不宣称支持。 | ✅ |
| #332 | 过程/触发器治理收口：依赖图与 DROP/ALTER 阻断、调用链审计、稳定错误码、执行耗时和失败指标、备份恢复、崩溃恢复、权限越权测试、递归/语句炸弹/大结果防护，以及嵌入式与远程 parity。 | ✅ |

顺序固定为 #329 先冻结目录与公开合同，#330 完成可调用 SQL 过程，再以同一运行时实现 #331 触发器，最后 #332 做治理与恢复收口。不得在 #329/#330 中顺带引入外部语言宿主。

## Milestone 39 — SQL 触发器第二版

第二版从 V1 的可执行证据出发，不以 PostgreSQL/MySQL 语法全集为目标。每项高级语义必须先说明工业 journey、事务边界、批量复杂度和恢复行为；外部语言宿主仍不在本里程碑范围内。

| PR | 交付 | 状态 |
|---|---|---|
| #333 | V2 gap baseline：固定审计 outbox、派生汇总、状态流转保护三条关系表 golden journey；建立 1/100/10,000 行 DML 下无触发器、V1 row trigger 与候选 statement trigger 的吞吐、WAL、内存和回滚成本矩阵；加入触发动作中途失败、提交失败、进程终止、重启 replay 的 crash-injection 证据，并据此确认后续条目的优先级。`tests/SonnetDB.Benchmarks --m39-trigger-evidence` 的 v3 报告现已覆盖 INSERT/UPDATE/DELETE 三种 DML、三条路径及精确回滚状态，并接入 xUnit quick-contract、`m39-trigger-evidence.yml` 和 CrashTests；固定目标硬件复测仍需归档，不以本地 quick 结果代替语义或容量准入结论。 | 🚧 |
| #334 | 生命周期与确定性顺序：设计 `ALTER TRIGGER ... ENABLE/DISABLE`、原子替换或重命名，以及显式 `FOLLOWS` / `PRECEDES` 顺序合同；目录更新必须保持落盘后发布、依赖安全和备份恢复兼容，禁用状态不能改变历史创建顺序。 | 📋 |
| #335 | 语句级触发器与 transition tables：在 #333 证明逐行写放大是主要瓶颈后，实现 `FOR EACH STATEMENT` 及只读 `OLD TABLE` / `NEW TABLE`；固定空影响集、批量 UPDATE/DELETE、同语句多行、触发器链和失败回滚语义，避免把 transition set 无界复制到内存。 | 📋 |
| #336 | 受控 BEFORE 语义：仅面向关系表 `BEFORE INSERT/UPDATE`，先冻结校验/改写顺序、生成列/ROWVERSION/主外键/CHECK 交互和只读 OLD 规则；若允许修改 NEW，必须使用受限赋值合同，不允许任意递归 DML 或绕过约束。`INSTEAD OF` 与可写视图另行评估。 | 📋 |
| #337 | 诊断与治理：提供按触发器过滤的执行/失败/回滚原因与延迟分布、最近调用链查询、定义级 `EXPLAIN`/dry-run，以及不记录参数值和行内容的可持久审计导出；指标标签必须有界，提交失败与已回滚动作不能被报告为已提交成功。 | 📋 |
| #338 | 高级事务语义准入：评估 deferred trigger、constraint trigger 和显式 order group 是否解决 #333 的真实场景；给出死锁、取消、保存点、调用深度和提交阶段错误合同。`AFTER COMMIT` 异步动作优先建模为 durable outbox worker，不伪装成与原 DML 原子的普通触发器。 | 📋 |
| #339 | Document / measurement 准入：分别量化批量摄取写放大、乱序/重放、幂等键、保留策略、compaction、备份恢复和高基数影响；只有模型原生事件合同和 crash/replay 对拍通过后才实现。不得把关系行 `OLD`/`NEW` 生硬套到 document patch 或 measurement batch。 | 📋 |

执行顺序为 #333 -> #334/#337；#335、#336 和 #338 由 baseline 证据决定是否进入实现，#339 始终独立过模型语义与容量门禁。V2 不默认包含多事件合并语法、异步网络调用、外部脚本、跨数据库触发器或分布式 exactly-once。

## Milestone 40 — 原生属性图数据库

目标不是把 `MATCH` 重写为关系 JOIN，而是在现有 KV/WAL/checkpoint/backup 地基上增加一级 Vertex、Edge、Label、Property 和双向 adjacency 存储，同时提供原生 Graph API 与可组合 SQL/PGQ。关系表映射图和原生图共享 Graph Logical Plan 与流式执行算子，但使用不同底层 accessor，`EXPLAIN` 必须如实显示 native adjacency、relation index seek 或 scan fallback。

详细的复用矩阵、key layout、事务不变量、SQL 双入口、逐 PR 验收和容量门禁见 [docs/native-graph-database-roadmap.md](docs/native-graph-database-roadmap.md)。

代码知识与 Agent 产品已在独立仓库 [IoTSharp/Couplet](https://github.com/IoTSharp/Couplet) 建立；仓库与路线基线完成不表示 Couplet 功能或 SonnetDB 图能力已经完成。依赖方向固定为 `Couplet -> SonnetDB.Core`：Couplet 负责工作区/Git、语言解析、代码领域 schema、增量协调、本地 embedding、上下文组装、首版只读 typed MCP、CLI/Agent 接线和产品评测；SonnetDB 负责通用 Graph/KV/Document/FullText/Vector/Hybrid Search、事务、快照、恢复、执行计划和性能。两个仓库保持独立，不互相作为 Git submodule；跨仓开发与联合门禁由 Couplet 显式 `ProjectReference` 最新 SonnetDB 源码，默认固定 package lane 继续作为可独立构建的兼容基线。两条 lane 必须分别验证，package lane 不能替代最新源码联合回归。Couplet 不复制 Core、不读取内部 key layout，也不得以关系边表、应用层遍历、第二套图存储或隐藏全扫补缺。

凡 Couplet golden journey 暴露 Graph/KV/事务/快照/恢复缺口，必须在 M40 先修复；暴露 Document、FullText、Vector 或混合检索缺口，必须回收到 M32、M35、M36 或对应公共执行里程碑并优先排期。声明支持的上层阶段在阻塞缺口关闭并取得回归/容量证据前不得发布，也不得通过产品侧旁路绕过 Core。Couplet 的完整路线以其仓库 [ROADMAP.md](https://github.com/IoTSharp/Couplet/blob/main/ROADMAP.md) 为准，SonnetDB 只维护跨仓门禁，不复制产品待办。

| Couplet 阶段 | 产品边界 | 联调/开发开始条件 | 联合退出/发布门禁 | 当前状态 |
|---|---|---|---|---|
| 仓库/路线基线 | 独立仓库、ADR、MCP 合同语义、golden journeys、SLO 与 C0-C4 路线 | 无 | 把相同 workload/SLO 输入 M40 #341 | ✅ 已建立；不表示 C0 或图能力完成 |
| C0 基础与合同 | 可运行骨架、schema/capability handshake、fixture/eval runner | 基线已建立 | 与 #341 同步冻结合同与证据 | ✅ 已完成；不表示 C1 或图能力完成 |
| C1 增量代码索引 | 工作区/Git、语言适配、Document/FullText、基础只读 MCP | `Tsdb.Generations` public contract 已可从最新 SonnetDB 源码联调 | source lane revision/crash/cursor/cleanup/capacity gate 全部 PASS | 🚧 Core exact-revision lease 与 Couplet source publish/query、database-root 单 live store、orderly reopen cursor terminal cleanup 和 watcher 本地回归已通过；真实跨进程 cursor/root 竞争、hard-kill CAS、双客户端、固定硬件和长稳未运行，`CG-005` 保持 verifying |
| C2 原生图代码智能 | 关系、路径、影响与测试选择 | #347~#351 目标 public API 可联调 | #352 与 Couplet C2 两个 gate 同时 PASS | 📋（Preview 发布受联合门禁约束） |
| C3 混合检索与 context pack | 本地 embedding、shared FullText/Vector/Graph typed plan 和 Agent eval | #353~#358 与相关 M35/M36 API 可联调 | #359 与 Couplet C3 gate 同时 PASS | 📋（Beta 发布受联合门禁约束） |
| C4 生产与 Agent 体验 | 长稳、恢复、安全、分发和 Codex/Claude Code 验收 | M40 修复顺序步骤 1~7 全部通过后取证 | #367 与 Couplet C4 生产门禁同时 PASS | 📋（1.0 发布受联合门禁约束） |

| 阶段 | PR 范围 | 交付边界 | 状态 |
|---|---|---|---|
| Phase 0：公共地基 | #341~#346 | ADR/golden journey、共享 sortable codec、KV snapshot cursor、Graph Catalog、单 graph 原子事务、backup/invariant/crash 骨架；无对外 Graph 能力宣称。 | ✅ 已完成；仅公共地基，不代表 Native Graph Preview |
| Phase 1：Native Graph Preview | #347~#352 | 原生 GraphStore、双向邻接、属性索引、流式 Expand/BFS/DFS/shortest path、Server/SDK/import 和 correctness/performance gate。 | 🚧 步骤 1/3 的 #348/#351 功能合同已关闭；#352 固定硬件、外部对拍和正式准入证据未运行 |
| Phase 2：SQL/PGQ Graph Beta | #353~#359 | 共享 Graph Logical Plan、原生 graph SQL DDL/DML、SQL/PGQ 关系映射、`GRAPH_TABLE MATCH`、planner/EXPLAIN、跨模型 SQL 组合与 M35/M36 Hybrid Search 候选合同复用。 | 🟡 #353~#359 功能与本地自动化门禁已完成；外部语义/容量和联合发布证据仍 `NOT_RUN` |
| Phase 3：生产级单机图数据库 | #360~#367 | statement snapshot、supernode/维护、按证据准入的高级路径/算法、可选 GQL 风格入口、知识图谱组合、运维产品面和发布门禁。 | 🚧 #360~#366 已有功能切片，#367 strict evaluator 已完成；性能/恢复加固及正式发布证据未完成 |

2026-08-25 的 Couplet C1 审计确认，#343 只固定单个 KV keyspace 内的 read snapshot/range cursor，#346 只固定已知模型（尤其 Graph）的 checkpoint、backup 与 crash/invariant；两者都没有一个覆盖 KV、Document、FullText 的 active generation 指针、跨分页 query lease、generation-bound cursor 或 lease-aware retired cleanup，因此“#343/#346 已完成”不能关闭 `CG-005`。本次新增通用、extend-only 的 `Tsdb.Generations`：发布前 checkpoint 并校验独占资源，内部 KV 条件批次原子发布 descriptor/ownership/active revision，查询租约固定 revision，清理等待全部租约释放；A/B reopen、publish 前后 fault、真实 Document+FullText、backup/restore、public API、package consumer 和 Core 回归均已通过。2026-08-31 又增加 `Acquire(stream, revision)` 的原子 exact-revision lease；Couplet 已用它完成持久 cursor 的 orderly store reopen、database-root 独占 lease、terminal cursor 的 version-CAS/snapshot/delete/snapshot 恢复窗口，以及本地 watcher/revision provenance 回归。本轮证据只覆盖本机同进程 open race、Windows extended-path alias、orderly dispose/reopen 和注入式恢复故障；真实进程重启/跨进程 cursor/root 竞争、hard-kill CAS、双客户端、固定硬件、随机故障和长稳仍未取得，C1/`CG-005` 继续保持 verifying/FAIL。

### M40 修复与发布执行顺序（2026-08-23 复盘）

以下顺序是依赖顺序，不是可任意并行勾选的清单。前一步的退出门禁未通过时，不得把后一步的 quick、本机结果或文档摘要计为阶段完成证据。

| 顺序 | 工作项 | 完成门禁 |
|---|---|---|
| 1 | ✅ **正确性阻塞项（#348/#349）已关闭**：`Expand(Both)` 保留跨方向底层页的未消费条目；无权 shortest path 以独立 `MaxPaths` 和一条额外探测区分不可达与预算截断。 | page size 3 的 Out/In/Both、self-loop/parallel edge、BFS，SQL 内部 page size 256，以及远程 typed SDK 回归通过；预算耗尽抛 `GraphTraversalLimitExceededException`，HTTP/SDK 返回稳定 `graph_budget_exceeded`。 |
| 2 | ✅ **#367 证据门禁已加固**：`m40-graph-production-input-v2` 分别按 dataset/environment/soak/journey/check schema 解析原始 artifact，并从三轮逐样本列、oracle assertion、checkpoint/kill/reopen/resource 样本独立重算 manifest 摘要；Git commit 对象、HEAD、clean worktree 和无 shell 的结构化复现命令/退出码均强制校验。 | 伪造 `{ "status": "PASS" }`、脏工作树、无效 commit、缺少原始样本、摘要漂移或复现失败回归均稳定 FAIL；allocation P95、Gen0/1/2 rate 和 GC pause P99 阈值由原始样本判定，判定器测试通过。Production evidence 仍须等待步骤 4~7。 |
| 3 | ✅ **Phase 1 合同（#348/#351）已补齐**：Expand 以 `GraphVertexPredicate` 落实目标 label/property 等值过滤；HTTP/SDK 扩展同构字段，带过滤请求不修改 Frame v1 而走 HTTP 流。Importer 同时执行 10,000 元素、8 MiB batch 和默认 1 MiB CSV 单行预算，完整输入校验后才发布批次。 | 1,000 度邻接跨页过滤、分页分配上界、按 UTF-8 字节分批、后续超长 CSV 无部分发布、未知长度 HTTP 413、嵌入式与 Frame 配置 typed SDK 回归通过；稳定错误为 `GraphImportLimitExceededException` / `graph_import_budget_exceeded`。#352 证据仍未运行。 |
| 4 | ✅ **Phase 2 共享架构（#353/#355/#359，依赖 M41 #373/#374）已关闭**：原生 API、原生 SQL 和关系映射消费共享 logical plan/pull operators；关系 scan/filter/project/JOIN/Top-N 改为逐批消费，完整等值 covering index 可不解码基表行；关系图在一次捕获窗口固定全部映射表快照。 | Graph SQL 不再整游标物化或逐 match 创建 binding dictionary；原生与映射图回归通过，`EXPLAIN [ANALYZE]` 报告 `paged_cursor`、`fixed_slots`、阻塞内存行为、`statement_snapshot` 及各表实际 sequence。 |
| 5 | ✅ **SQL 与 planner（#354/#358）已关闭**：冻结 `graph_sql_v1`，明确全部 label/非空 property 自动等值索引而不增加无物理差异的命名 DDL；实现属性 INSERT、显式 version UPSERT、部分 UPDATE、DELETE 与 `ANALYZE GRAPH`；原生 value cardinality 可选择 property anchor。 | `EXPLAIN [ANALYZE]` 报告实际 `native_property_index_seek`、索引、统计 sequence/freshness、anchor/expand 顺序和 fallback；高选择性右端属性驱动 incoming 计划，SQL mutation 全部复用单个 `GraphTransaction`，版本冲突整句不发布。合同见 `docs/m40-graph-354-358-sql-planner.md`。 |
| 6 | **性能加固（#348/#349/#358/#361~#363）**：已落地 parent-linked path（延迟数组物化）、file-backed offline vector 的有界 little-endian page cache/批量 flush，以及 Expand/traversal/weighted-path 按剩余 edge/result 预算读取、最多一条 probe；预算截断后不再解码后续邻接。邻接、weighted-path 和 spill 的固定 workload 证据继续独立采集。 | 固定 workload 同时满足 latency、allocation、Gen2、pause、working set 和 spill I/O 阈值；不以提高预算、关闭耐久或减少能力换取数字。Graph 定向 142/142、相关 generation/API 167/167 与 Core Release 3882/3882 已通过；固定硬件 gate 仍待运行。 |
| 7 | **恢复与产品闭环（#346/#359/#361/#366）**：Server 与 embedded SDK 已统一 `applying` 审计恢复和 torn NDJSON tail 规则，#367 quick 已加入真实子进程 kill/reopen；typed point read 仅将空 404 映射为元素缺失 `null`，缺失 graph 的结构化 404 保留稳定 `graph_not_found`；generation exact-revision acquire 与 cleanup 使用同一生命周期锁序，已清理/资源已删除的 revision 稳定返回 `generation_revision_unavailable`。Server/SDK/CLI/Studio parity 的固定发布证据继续独立采集。 | 审计与维护在进程终止后具有确定终态或可恢复状态，损坏尾部不会被静默接受；Core、远程入口和管理面的结果、权限、错误及审计一致。typed vertex/edge point read 与 orderly reopen 小型回归已通过，真实跨进程 cursor、7 天/每日 kill matrix 仍待运行。 |
| 8 | **最后采集发布证据（#352/#367 + Couplet C2~C4）**：依次运行 Neo4j/PostgreSQL 语义对拍、LDBC/Graphalytics、固定硬件 1m vertex/10m edge、Native AOT journey、Couplet 联合门禁和 7 天 8+1 mixed workload。 | correctness/recovery 与 performance/capacity 双 gate、Couplet C2~C4 对应 gate 全部 PASS，报告含原始样本、commit、硬件、命令、退出码和 access path；任一失败即保持 M40 🚧。 |

准入规则：步骤 1~5 已完成；步骤 6~7 未完成前可以运行用于设计决策的 microbenchmark/quick，但不得启动或累计 #352/#367 固定硬件、外部对拍和 168 小时发布证据。只有步骤 1~7 的阻塞项全部关闭后，步骤 8 才可开始；在两个生产 gate 与 Couplet 联合门禁全部通过前，不得宣称 Production。九模型定位只把原生属性图纳入 Graph Beta 产品范围，不放宽上述准入规则。

固定边界：一个 graph 一个 keyspace，第一阶段不支持跨 graph/跨模型原子事务；vertex 删除先用 `RESTRICT`，不以静默拆批伪装超大 `DETACH DELETE` 原子性；Graphify/实体抽取/LLM/GraphRAG job 留在 importer、Server 或 SDK；不引入第二套 WAL、SQL 表达式系统、向量/全文索引、权限和备份格式；不承诺 Bolt、完整 Cypher/GQL、RDF 推理或分布式图能力。

阶段名称也是产品宣称门禁：Phase 1 gate 通过后才可发布 Native Graph Preview，Phase 2 gate 通过后才可发布 SonnetDB Graph Beta。当前公开定位已将 Graph Beta 计入九模型；只有 #367 的 LDBC/Graphalytics 子集、7 天 mixed workload、crash/backup/Native AOT 和固定硬件报告通过后，才可称“生产可用的单机原生属性图数据库”。

## Milestone 41 — 关系查询规划与执行性能加固

目标是在不减少 SQL 能力、不改变事务/持久化/恢复语义的前提下，消除关系查询中的扫描、物化、复制、长锁和 GC 放大，并从当前规则式访问路径选择演进到可解释、可回退的轻量成本优化器。本里程碑不追求 PostgreSQL/MySQL 方言或优化器全集，也不通过降低正确性、关闭审计、放松 fsync、限制既有查询能力或提高无界并发换取基准数字。

### 生产触发证据与目标

2026-08-05 木垒 ARM64 生产只读采样已经满足排期条件：主机 48 核、250 GiB 内存且约 190 GiB 可用，采样期 CPU idle 72%~89%、I/O wait 为 0；SonnetDB RSS 约 27~28 GiB，在约 72.33 SQL QPS、322.60 返回行/秒下产生约 282.39 MiB/s 逻辑读取而物理读取为 0。`GovernanceAudits` 幂等键/`EXISTS`、普通 `IN`、nullable `OR`、多表 JOIN 和倒序分页出现 12~61 秒延迟，简单点查和 `COMMIT` 也受排队、锁等待或 GC 连带影响。该采样是生产问题基线，不代替可重复基准和 profile。

### 已确认缺陷：`EXISTS` 绕过表索引访问路径（#369）

木垒“超限车辆核验”请求超时已经定位到 SonnetDB SQL 执行器，而不是索引损坏或单纯的选择率估算错误。同一张表按唯一二级索引 `IdempotencyKey` 直接等值查询约为 2 ms；原始复合 `EXISTS` 查询耗时 25,465 ms，生产 Top Queries 中同一参数化查询在 61,143 ms 后失败。生产镜像基于 commit `1d94b96`，相关执行器到本次核查的 `cbea6ed` 仍保持相同行为。

当前两条实际执行路径不同：

```text
普通 SELECT ... WHERE IdempotencyKey = ?
  -> TableSqlExecutor.LoadSelectCandidateRows
  -> ChooseBestIndexAccessPlan
  -> 唯一索引点查 + 残余谓词

SELECT EXISTS (...)
  -> SqlExecutor 发现子查询
  -> RelationalSelectExecutor
  -> LoadTable(... where: null)
  -> TableStore.Scan() 并物化整表
  -> 在内存中执行 WHERE
  -> EvaluateExists 检查 Rows.Count != 0
```

代码证据集中在 `Sql/Execution`：`SqlExecutor.cs` 的关系路径分派会把包含子查询的语句交给 `RelationalSelectExecutor`；`RelationalSelectExecutor.LoadTable` 以 `where: null` 调用 `TableSqlExecutor.LoadSelectCandidateRows`，随后才过滤已物化的全部行；`EvaluateExists` 执行完整子查询后才检查 `Rows.Count`，没有在首个匹配行处停止。相比之下，`TableSqlExecutor.ChooseBestIndexAccessPlan` 已支持复合条件中的索引前缀和残余谓词，所以问题不是“SonnetDB 不会用现有索引”，而是 `EXISTS` 所走的关系执行路径根本没有把内层谓词交给这套索引规划。相关 `EXISTS` 每处理一条外层候选行都可能再次完整扫描并物化内表，相关子查询最坏会退化到接近 `O(N x M)`。此外，`SqlExplainPlanner` 目前可能按普通表路径报告索引访问，但运行时实际已经分派到关系路径，存在 EXPLAIN 与真实执行不一致的风险。

#369 按以下顺序修复，不依赖 P2 成本模型：

1. 对可证明等价的非相关、单表 `EXISTS`，让内层 SELECT 复用正常表候选行规划，并把“找到一行即返回”作为执行合同，而不是先构造完整 `SelectExecutionResult`。
2. 在关系执行器中完成 #369 所需的最小单表谓词下推，将可索引谓词传入 `LoadSelectCandidateRows`，同时保留顶层残余谓词；通用 JOIN/视图/外连接谓词下推仍由 #372 收口。
3. 对相关 `EXISTS`，安全绑定外层值后优先执行内表主键/唯一索引点查或普通索引候选读取；不能证明相关性、NULL 或事务语义等价时显式回退。
4. 统一 EXPLAIN 与运行时分派，使计划中的 access path、index、residual predicate、early-exit 和 fallback reason 来自实际会执行的路径。

#369 的合并门禁必须包含以下自动化回归：

- 唯一二级索引等值条件位于 `EXISTS` 内时，执行计数证明全表扫描为 0，并且 examined rows 随命中数而不是表总行数增长。
- “索引等值条件 + 一个或多个残余条件”的复合谓词仍使用索引，残余条件结果与当前执行器差分一致。
- 非相关和相关 `EXISTS` 都在首个匹配行停止；无匹配时完整检查必要候选且结果正确。
- 可索引的相关 `EXISTS` 按外层行执行内表索引探测，不重复物化整个内表；不可索引相关谓词保留可取消、可观测的正确回退。
- 增加 EF Core 生成的参数化 `SELECT EXISTS (...)` 回归语料，覆盖幂等键、附加状态/时间条件、命中、未命中和 NULL。
- `EXPLAIN` 声明的访问路径与真实执行计数一致，不允许计划显示 index seek 而运行时发生 full scan。

实现必须继续保持 SQL NULL 三值逻辑、参数绑定、事务 overlay/read-your-writes、稳定错误和取消语义，并满足 Core 零第三方运行时依赖、Safe-only 与 Native AOT 约束。TOLNSD 应保留当前应用层超时规避方案，直到修复版本完成生产同语料复测；应用 workaround 与 SonnetDB 根因修复互不替代。

阶段目标按以下顺序收敛：

1. P0 先让访问路径、examined/returned rows、队列/锁等待和分配可见，并消除已确认的 `EXISTS/IN/OR/倒序 Top-N` 扫描放大。
2. P1 将谓词、投影和 LIMIT 推入各关系输入，以流式 cursor、延迟物化和短锁快照替代完整行列表物化。
3. P2 建立统计信息、基数/行宽估算、轻量成本模型和可解释的逻辑/物理计划选择。
4. P3 在工作量已经缩减且内存有界后，再增加 JOIN 算法、spill、受控并行和运行时反馈。
5. 每一阶段都以差分正确性、故障恢复、固定硬件基准和木垒查询语料回归为准入门禁，不等到全部实现后才验证。

### 交付拆分

| 优先级 | PR | 交付 | 状态 |
|---|---|---|---|
| P0 | #368 | 性能合同与可观测性基线：固化木垒慢查询语料和合成数据集；按规范化 query fingerprint 记录访问路径、候选/检查/返回行数、SQL permit 队列等待、表/KV 锁等待、执行时间、分配量、GC、逻辑/物理读写及 fallback reason。指标标签必须有界且不得记录参数值或行内容；慢查询环满载不得丢失聚合计数。 | ✅ |
| P0 | #369 | `EXISTS`/`Any`、semijoin 与标量 `IN` 快速路径：唯一键/主键条件使用直接探测，普通主键或索引 `IN` 使用去重后的批量 MultiGet；保留残余谓词、NULL 三值逻辑、事务 overlay、稳定输出和参数绑定，无法证明等价时回退现有执行器。 | ✅ |
| P0 | #370 | `OR` 与多索引候选集合：为可索引分支实现主键集合 union，并按证据准入 intersection；支持 nullable 时间条件等常见形式，统一去重、残余过滤、排序/分页边界和内存阈值，超阈值或不可索引分支使用可解释 fallback。 | ✅ |
| P0 | #371 | 双向索引 cursor 与早停 Top-N：支持满足排序合同的升/降序索引遍历，将 `LIMIT/OFFSET` 安全下推到候选读取；多列方向、NULL 顺序、非覆盖谓词或事务 overlay 无法保证顺序时继续走现有排序路径。 | ✅ |
| P1 | #372 | 关系输入谓词与投影下推：在 JOIN 前按绑定列归属拆分并下推单表 WHERE、所需列和安全 LIMIT；顶层残余谓词始终保留，外连接、相关子查询、聚合和视图展开必须有独立等价性测试。 | ✅ |
| P1 | #373 | 流式关系算子与延迟物化：定义公共 row/candidate cursor，使 scan/filter/project/Top-N/JOIN 可逐批消费；增加 covering/index-only scan，仅在输出或残余谓词需要时读取并解码基表全行；所有阻塞算子必须声明内存行为。 | 🟡 本地完成；发布证据后置 |
| P1 | #374 | KV/Table 快照读取与锁范围收缩：在短锁内取得不可变可见视图或版本化 cursor，在锁外枚举、复制和解码；保持同一 statement snapshot、事务内 read-your-writes、删除/更新 overlay、checkpoint/compaction/WAL replay 和异常释放语义。与 M40 #342~#346 共享 cursor/codec 地基，不重复实现。 | ✅ |
| P2 | #375 | 轻量统计信息：持久化表/索引行数与页数、平均行宽、NULL fraction、distinct、MCV 和等深直方图；支持显式 `ANALYZE` 与有预算的自动刷新，采样不得长时间阻塞业务，不保存原始敏感值，并记录 freshness/sample rate。 | 🚧 统计结构、持久化、显式 `ANALYZE` 与分配优化已完成；自动刷新仍可能在首个业务规划线程同步采样，后台合并刷新和无偏采样转入 M42 |
| P2 | #376 | 逻辑/物理计划与成本选择：统一 point/range/full/index-union access path，基于基数、选择率、行宽、解码、排序、内存和逻辑 I/O 估算选择计划；首版保持小而确定，不引入无界搜索，统计缺失或估算不可信时使用稳定启发式回退。 | 🟡 本地完成；发布证据后置 |
| P2 | #377 | 可解释计划与实际执行证据：默认 `EXPLAIN` 只读目录/统计元数据，不为估算候选数实际扫描业务数据；为 M36 #313 提供计划树、估算/实际行数、耗时、loops、rows removed、锁/队列等待、峰值内存、spill 和 fallback reason。M36 负责用户侧错误/取消/超时合同，本项只建设共享规划与算子证据源。 | 🟡 本地完成；发布证据后置 |
| P3 | #378 | JOIN 优化：按估算行数和行宽选择 Hash build side，支持 semijoin/antijoin、index nested-loop，并在有序输入和收益证据成立时准入 merge join；建立有限 join-order 枚举与大连接图回退，外连接和 NULL 语义不得被重写破坏。 | 🟡 本地完成；发布证据后置 |
| P3 | #379 | 阻塞算子内存预算与 spill：为 hash join、sort、Top-N、group、distinct 和索引候选集合设置按查询/全局预算、取消和临时文件生命周期；落盘结果必须与内存路径对拍，崩溃后可清理，禁止因预算不足静默截断结果。 | 🟡 本地完成；发布证据后置 |
| P3 | #380 | 受控并行与运行时反馈：仅对估算收益成立的 scan/JOIN/aggregate 启用有界并行，服从 SQL permit、查询内存和取消；记录估算偏差供统计刷新或下一次规划使用，不在执行中改变可观察结果顺序。必须在 #369~#379 已减少扫描并使内存有界后准入。 | 🟡 本地完成；发布证据后置 |
| 发布门禁 | #381 | 本地收口：运行语义差分、回退/资源边界、并发/事务合同和报告管线已接入；固定 ARM64/x64 硬件、木垒同语料、真进程 crash/replay、backup/restore、部署 Native AOT 与 7 天 mixed workload 属现场观察项，统一登记为 `DEFERRED`，后续部署或现场分析时补齐 P50/P95/P99、amplification、RSS/分配/GC、锁/队列等待和逻辑/物理 I/O。任一优化回归正确性或恢复保证时不得默认开启。 | 🟡 本地收口已完成；现场验证后置 |

### 参考边界、顺序与验收

参考 PostgreSQL 的统计信息、扩展统计、Bitmap Scan、有限 join-order 搜索、计划树和 `EXPLAIN ANALYZE`，参考 MySQL 的持久统计/直方图、range optimizer、Index Merge、semijoin/antijoin、Hash Join 内存界限与 spill；学习其机制和验证方法，不复制 wire protocol、完整 SQL 方言、系统目录或分布式能力。计划缓存只有在参数敏感选择和数据倾斜证据完成后另行准入，不能把单一计划盲目复用于所有参数。

原始切片按 `#368 -> #369/#370/#371 -> #372/#374 -> #373 -> #375/#376/#377 -> #378/#379 -> #380 -> #381` 实施。2026-09-01 复审确认 #375 的同步首读刷新仍需整改，因此 M41 不再按普通 ✅ 收口；后续由 M42 先关闭该 P0，再进入固定硬件和木垒同语料验证。P1 必须证明长扫描不在表级锁内完成全行解码；P2 必须报告 estimated/actual rows 偏差；P3 不以线程数或单条最佳数字验收，而以混合负载尾延迟、吞吐和内存上界验收。

#369~#374、#376~#379 当前完成了本地自动化门禁：固定随机种子差分覆盖主键/二级索引 semijoin、索引 OR、有符号倒序窗口、成本选择和 EXPLAIN 不扫描业务行；事务写集验证安全回退；#372 覆盖双侧索引谓词、跨输入残余、LEFT JOIN NULL 语义、聚合、相关子查询、逻辑视图、事务 overlay、有状态 UDF 回退与无排序纯 LEFT JOIN 的安全输入窗口；#373 覆盖 probe 侧 LIMIT 早停、完整等值 covering/index-only 零基表解码，以及 EXPLAIN 的 streaming、右侧 build/replay、aggregate、full sort 与 bounded Top-N 内存合同；#374 覆盖表读快照在索引/范围读取期间的并发写、稳定结果和异常租约释放；#378 覆盖 Hash build side、NULL-aware semijoin/antijoin、主键/二级索引 nested-loop、兼容有序输入 merge join、重复/NULL/空集/有符号/跨类型边界、3～6 表有限枚举、自连接别名与列序恢复、外连接和超过 6 表回退；#379 使用同一查询/数据库实例全局预算约束 Hash Join、稳定外部排序/Top-N、分组、DISTINCT 和索引候选去重，强制 96-byte 预算与内存路径逐行对拍，并覆盖取消释放、标记目录启动清理、全局额度竞争及 EXPLAIN 峰值/spill 指标；#380 覆盖 measurement scan、legacy aggregate 和物化 probe Hash JOIN 的有界 worker 上限、查询/全局预算竞争、取消释放、事务门控、稳定输出、LEFT/NULL 语义、串并行逐行对拍和 estimated/actual 反馈。#375 的统计持久化、显式分析和成本消费已有测试，但同步首读刷新及外层主键前 N 行采样仍是实现残余。#381 新增 `--m41-production-closeout` 收口报告：本地报告管线为 `PASS`，固定硬件、木垒同语料、真进程 crash/replay、backup/restore、部署 Native AOT 与 7 天 mixed workload 均显式为 `DEFERRED`；`DEFERRED` 不等于发布 `PASS`。

所有快速路径必须满足以下不变量：索引 union/MultiGet 按主键去重；残余谓词不得丢失；NULL/三值逻辑、排序稳定性、LIMIT/OFFSET、相关子查询和事务可见性不变；WAL/checkpoint/compaction/backup/recovery 合同不变；公开 API 与 EXPLAIN schema 采用 extend-only 演进；Core 保持零第三方运行时依赖、Safe-only 和 Native AOT。每个新计划先与当前执行器做随机化及木垒固定语料差分测试，再按 feature gate/canary 放量；无法证明等价、统计过期或资源预算不足时必须回退到已验证路径并暴露原因。

## Milestone 42 — 九域与规划器系统性能深化

目标是把九种数据模型放进同一套可复现性能合同，并继续深化关系规划器公共核心；原生属性图当前仍按 Graph Beta 取证。详细数据、竞品版本和复现入口以 [2026-09-01 系统性能报告](docs/benchmarks/system-performance-20260901.md) 为准；关系 SQL 仍是公共核心，不另算第十域，M40 发布门禁完成前不得宣称 Graph Production。

| 工作面 | 当前状态 | 已有证据与剩余门禁 |
|---|---|---|
| 九域矩阵、统一指标与竞品源码入口 | ✅ | 已覆盖吞吐、P50/P95/P99、放大、分配/GC/RSS、等待、I/O/WAL、spill、恢复、文件数和正确性；14 个竞品入口固定到版本/commit 与许可证边界。 |
| 关系统计刷新与向量 CRC32 热路径 | 🟡 | 本机最终短跑分别证明统计均值 `54.81 -> 51.25 ms`、分配 `34.72 -> 22.67 MB/op`，以及 IEEE CRC32 三档方向性收益；固定硬件与 ARM64 未运行。 |
| SQL 物理读指标与 Sparkplug Rebirth | 🟡 | 物理读冻结具备 64 次/5 ms 上界和 degraded 计数；Rebirth 有界合并队列及 readiness 合同 `12/12 PASS`，真实 broker 在 readiness/publish 间停机的集成竞态仍缺。 |
| 模型级 benchmark | 🚧 | KV/Document 本机热读 smoke 已完成；Object 仅 exploratory 且有最小迭代告警；时序、全文、MQ 吞吐、向量 Recall/容量和 Graph 正式门禁本轮均未运行。 |
| Native AOT 与硬件路径 | 🟡 | win-x64 CLI/Server publish 和 CLI 实际执行通过；Server 启动未测。ARM64 CI matrix 已配置，但真实 CI、publish/start/first-query 与指令差分均为 `NOT_RUN`。 |

后续按收益/风险推进：P0 将自动统计刷新移出首个业务读并完成 ARM64 可执行/AOT 门禁；P1 处理无偏采样、页感知索引成本、参数敏感计划、Embedded I/O 预算和向量 query norm 复用；P2 扩大 covering/index-only、增加 snapshot cold-miss single-flight、减少大值复制、统一 cold-start/file-count 合同并补真实 Sparkplug broker 竞态；P3 仅在独立 feature gate 与跨架构差分收益成立时评估 direct intrinsics 和 .NET 11 preview。固定 x64/ARM64、木垒同语料、168 小时 mixed workload 与生产发布全部保持 ⏳ `NOT_RUN`。

## 性能观察项

以下不是已完成里程碑的遗留验收，只有触发条件成立并取得独立基准后才排期：

| 编号 | 方向 | 进入条件 |
|---|---|---|
| PF1 | 级联删除按选择率切换二级索引或单次哈希扫描 | 在 1/10/50/100 父键矩阵中证明当前固定路径稳定劣于替代路径，并保持语义与事务回滚一致。 |
| PF2 | 高活跃词基数 fuzzy 词典结构 | 100k/500k 活跃 term 场景线性枚举成为主要 CPU 成本，且新结构至少有稳定 2 倍查询收益。 |
| PF3 | ANN tombstone gate/区间索引 | 高墓碑基数下区间扫描成为主要成本，且优化不降低 ANN/精确扫描召回对拍。 |

## 已完成里程碑摘要

| Milestone | 已交付结果 |
|---|---|
| 0~13 | Safe-only 存储引擎、WAL/Segment/Compaction、SQL/ADO.NET、Server/Web、函数、向量与知识库底座。 |
| 15~17 | GEOPOINT/轨迹、Copilot 产品 UX、OTel/结构化日志/诊断/health/慢查询/服务端会话。 |
| 18 | VS Code 扩展 `0.4.1` 发布、smoke、隔离 VSIX 安装和 Marketplace 产物校验。 |
| 21 | Document CRUD、查询/分页、局部更新第一批、索引、aggregation 子集、validator 和单机恢复。 |
| 23 | 全文与向量引擎收编、持久索引与 Hybrid Search。 |
| 24 | Document Explorer、Validator、导入导出、rebuild 与共享审批。 |
| 26 | C ABI 与 Go/Rust/Java/Python 等连接器入口和发布流程。 |
| 28 | 数据可靠性、并发正确性、写/查热路径、索引/向量、SonnetMQ 与全模型高吞吐接入加固。 |
| 30 | Sparkplug B、CoAP、Line Protocol UDP 的接入、生命周期、安全、parity 和基准。 |
| 31 | 字符串/布尔等 selector 与 categorical aggregate 类型语义。 |
| 32 | Document SDK、查询/更新、multikey/wildcard 索引、aggregation、mixed Bulk、迁移、Workbench 与 Quickstart。 |
| 33 | 聚合正确性、多聚合复用、残差流式化、count(*) 专路和 LIMIT/latest-N 下推。 |
| 34 | Modbus DDL/catalog、地址/codec、TCP master/slave、受限 Source 写、Endpoint 外部写治理、管理面和审计。 |
| 37~38 | 持久化逻辑/物化视图，以及 SQL 存储过程、关系表 AFTER ROW 触发器与治理收口。 |
| MM9 | 多模型备份、检查、校验和恢复 CLI 第一批。 |

详细历史只用于追溯，不覆盖本文件的当前完成判定；若历史文档与当前实现冲突，以代码、可执行测试和本文件的审计结论为准。
