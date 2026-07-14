# ROADMAP

本文件只保留当前仍需交付、补证或继续深入的工作。已经完成的里程碑压缩为结果摘要；历史 PR 拆分与设计记录见 [docs/roadmap-history.md](docs/roadmap-history.md)。

图例：✅ 已完成 / 🚧 进行中或待补证 / 📋 计划中 / ⏸️ 暂停 / ➡️ 移交

## 完成判定

2026-07-14 起，里程碑只有同时满足以下条件才标记为完成：

1. 代码存在，且真实产品入口已经接到该实现；占位类型、未调用服务或仅有 UI 原型不算完成。
2. 自动化测试覆盖主要合同，并至少完成一次与声明相符的运行验证。
3. 涉及 CI、nightly、容量、发布或 Marketplace 的声明，必须有对应 workflow、报告或已发布产物证据。
4. 文档描述与实际依赖、调用链和限制一致；“计划采用”不能写成“已经基于”。

本轮核查基于合并提交 `59ecd3a`，并复核 Core、Server、EF Provider、IoTSharp compatibility、Web/Studio、VS Code、Parity、容量报告和发布流程。

## 里程碑总览

| Milestone | 主题 | 状态 | 核查结论 |
|---|---|---|---|
| 0~13 | 引擎、SQL、服务端、函数、向量底座 | ✅ | 实现与测试已落地，详情归档。 |
| 14 | SonnetDB Copilot | 🚧 | MCP、知识库、skills 和自研 `CopilotAgent` 已落地；Microsoft Agent Framework、本地 ONNX 执行和在线 provider-neutral 接线未完成，转入 M27。 |
| 15~17 | GEO/轨迹、Copilot UX、可观测性 | ✅ | 功能与测试已落地；会话以服务端持久化为准，不回退 `localStorage`。 |
| 18 | SonnetDB for VS Code | 🚧 | `0.4.1` 已发布且哈希一致；仅最终 Electron 实机截图待补。 |
| 19 | 生态适配底座 | 🚧 | #109~#124、#126/#126.1 实现完成；#125 四个默认容量档缺固定目标硬件报告。 |
| 20 | 多模型 Parity | 🚧 | 套件、宿主 readiness 与失败路径结构化 summary 已实现；仍需完整 scheduled run 和 7 天 nightly 连续证据。 |
| 21 | Document Store 单机能力 | ✅ | 常用单机 Document 子集已落地。 |
| 22 | 上层应用/示例候选 | ⏸️ | 不作为 SonnetDB 内置里程碑；通用能力缺口再回收。 |
| 23 | 搜索与向量引擎合并 | ✅ | DotSearch / DotVector 能力已收编。 |
| 24 | Document 管理面 | ✅ | Explorer、Validator、导入导出和维护入口已接入共享工作台。 |
| 25 | Document 验收与发布治理 | 🚧 | parity 与文档完成；#174 仅有 1 万文档 quick 证据，百万/千万档未验证。 |
| 26 | 连接器路线 | ✅ | C ABI 与多语言入口已交付，连接器 release workflow 通过。 |
| 27 | AI / Agent 数据访问与治理 | 🚧 | 产品定位已校准；工具合同、运行时接线、工业 Demo 和 eval 仍有实际缺口。 |
| 28 | 可靠性、并发与热路径加固 | ✅ | P0~P5 与 SDK 补口已收官。 |
| 29 | 多模型统一管理工作台 | 🚧 | Web/Studio/VS Code 功能与合同已落地；Studio 安装包和宿主生命周期仍需实机验收。 |
| 30 | Sparkplug B / CoAP / UDP 接入 | ✅ | 协议入口、生命周期、安全、parity 和基准已落地。 |
| 31 | 时序聚合类型语义 | ✅ | selector / categorical aggregates 已落地。 |
| 32 | Document MongoDB-like 易用性 | 🚧 | SDK builder、分页 cursor 与稳定游标错误码已落地；批量结果、缺失查询/更新/索引语义、迁移工具和 gap report 继续推进。 |
| 33 | 时序聚合执行与下推 | ✅ | Geo 正确性、多聚合复用、残差流式化、count(*)、LIMIT/latest-N 下推已落地。 |
| 34 | Modbus TCP 内建映射表 | 📋 | 尚未开始。 |
| 35 | 语义内容与多模态检索 | 📋 | 尚未开始。 |
| MM9 | 多模型备份恢复第一批 | ✅ | `BackupService` 与 `sndb backup` 已落地。 |

## 当前推进顺序

1. 恢复 M20 Parity nightly 的有效报告，并补齐 M19/M25 目标硬件容量证据。
2. 完成 M27 的真实 provider/Agent 接线、工业 Demo 和 eval，消除历史虚标。
3. 收口 M18 Electron 截图和 M29 Studio 安装包/宿主生命周期实机验收。
4. 按真实差距推进 M32，不重复实现已有 update/index/change feed/UI 能力。
5. M34 先做合同、DDL 和安全边界；M35 在过滤 ANN 与内容生命周期地基完成后再做媒体场景。

## 待补验收证据

### M18 — VS Code 发布收口

- ✅ `0.4.1` 已发布；Node、Language Server、Extension Host smoke、隔离 VSIX 安装和本地/Marketplace SHA256 对拍通过。
- 🚧 在干净的 VS Code Electron 宿主中保留最终功能截图，覆盖激活、Explorer、SQL 结果、diagnostics、signature help 和 quick fix。

### M19 — 生态容量证据

#125 runner、workflow 和缩规模 quick 验证已经完成，但容量声明尚未闭环。固定规格目标硬件必须分别运行并归档：

- `high-cardinality`：默认 1,000,000 series。
- `small-segments`：默认 10,000 segment。
- `maintenance-chaos`：默认 20 次确定性 kill/reopen。
- `many-measurements`：默认 10,000 measurement。

每份报告必须记录 commit、机器/磁盘规格、持续时间、working set/托管内存峰值、查询与恢复 P50/P95/P99，以及 missing/duplicate/unexpected/value mismatch。缩规模 PASS 不能代替发布容量证据。

### M20 — Parity nightly 证据

Parity 场景、适配器和 compose 已存在，但“完成”还需要：

- ✅ workflow 已改为宿主 readiness 探测；restore、build、stack 或 test 失败仍生成带稳定 `gap_reason`、commit SHA 和门禁分类的 schema v2 summary，并保留容器诊断。
- 修复后的完整 compose 仍需在 CI 中健康启动并实际运行全部场景，而不是只通过 `docker compose config`。
- scheduled workflow 连续 7 天成功率不低于 95%，每次都生成非空 summary 和结构化 `gap_reason`。
- NATS、VictoriaMetrics 等第三方镜像的健康检查不得依赖镜像内不存在的 shell/wget；探活由宿主 workflow 或可用的原生命令完成。
- 失败 run 必须保留容器日志、测试报告和 commit SHA，不能发布 `No summary was produced for this run.` 作为完成证据。

### M25 — Document 容量证据

- ✅ MongoDB 参考 parity、Document 能力矩阵、迁移边界和 1 万文档 quick profile 已完成。
- 🚧 在固定目标硬件运行 `million` 与 `ten-million` profile，归档写入、查询、rebuild、TTL、热/冷启动、crash recovery、backup/restore 和内存曲线。
- 没有对应 PASS 报告前，对外只声明“profile 可执行，规模未在目标硬件验证”；当前发布证据仅支持 1 万文档级完整治理闭环。

### M29 — Studio 实机验收

Web/Bridge smoke、Server 管理合同、Web Admin、Studio Release build 和 VS Code consumer smoke 已覆盖实现。剩余验收只保留：

- 在干净 Windows 环境安装 Studio 安装包，验证首次启动、升级/卸载和数据目录保留策略。
- 验证托管 Server 的启动、停止、异常退出、宿主退出策略、端口冲突和日志/健康状态。
- 保存实机报告与截图；这项完成后 M29 才转为 ✅。

## Milestone 27 — AI / Agent 数据访问与治理

目标是在不改变 SonnetDB“八种数据模型，一套引擎”核心定位的前提下，为 Copilot、MCP 和外部 Agent 提供受权限、审计与人工确认约束的数据访问能力。工业数据诊断是验证该能力的示例之一，不是产品类别。当前实现不是 Microsoft Agent Framework：实际为 `Microsoft.Extensions.AI` 抽象加自研 `CopilotAgent`；`LocalOnnxEmbeddingProvider.EmbedAsync` 尚未执行模型；在线 `/v1/copilot/chat` 只走 `ICopilotCloudGatewayClient`。在接线完成前，文档必须如实描述这些边界。

| 项目 | 剩余交付 | 状态 |
|---|---|---|
| #182 产品定位校准 | README / README.en、文档首页、`llms.txt` 和产品欢迎页统一为“八种数据模型，一套引擎”；实现语言、部署方式、行业场景和 Agent 能力按层表达，不进入一级定位。 | ✅ |
| #183 MCP 合同 | 为现有 list/describe/sample/query/explain/docs 工具形成稳定 typed contract，写清参数、返回、权限、错误和版本兼容；不新增大工具面。 | 🚧 |
| #184 工业 Demo | 用 MQTT/HTTP 写入温度、电流、振动，演示异常设备查询、维修建议、引用和报告；数据模型、脚本、文档和视频口径一致。 | 📋 |
| #185 Provider 接线 | 配置样例和模型分组已完成；仍需让在线 Chat 按配置走 `IChatProvider` 或云 Gateway，并实现可运行的本地 embedding/provider 路径。 | 🚧 |
| M14 纠偏 | 接入最新 Microsoft Agent Framework 并以测试证明，或继续明确标注“自研 orchestrator”；实现本地 ONNX 前不得宣称 bge-small-zh 已可用。 | 🚧 |
| #186 写审批 | 已移交 M29，共享 staged preview/dry-run/confirm 已完成；M27 只消费。 | ➡️ |
| #187 Eval/成本 | 增加异常设备、慢查询、schema、维修建议和审批场景，记录 provider/model/tool/失败原因/token 成本，并给出可复现报告。 | 📋 |
| #188 上层边界 | IoTSharp 联合样例归 IoTSharp；SonnetDB 只提供授权 MCP、通用引擎和 Agent 素材。 | ✅ |

验收要求：AI / Agent 文案不得替代多模型引擎的核心产品定位；本地关闭云端外发时仍有一条可运行路径；高风险写入必须经权限和人工确认；外部 Agent 只通过授权 MCP/HTTP 合同访问，不直读目录或系统表。

## Milestone 32 — Document MongoDB-like 易用性

已实现基线：`$set/$unset/$inc/$min/$max/$rename/$push/$pull/$addToSet/$currentDate`、upsert、update preview、compound/unique/sparse/partial/TTL 索引、planner/validate、Document change feed，以及 Web/Studio 查询、更新、索引和 feed 界面。以下计划不得再次把这些能力列为待实现。

| 方向 | 真实剩余工作 | 状态 |
|---|---|---|
| SDK | ✅ 类型化 filter/projection/sort/update builder、AOT 类型值、分页 cursor 与 invalid/mismatch/expired/stale 标准错误码；🚧 统一批量结果模型仍与下方混合 Bulk 一并推进。 | 🚧 |
| 更新 | `$mul`、`$pop`、`findOneAndUpdate` 的 before/after 返回语义和完整单文档原子性。 | 📋 |
| 查询 | `$elemMatch`、`$regex`、`$type`、`$size`、`$all`、复杂 `$not`、嵌套 path 边界和基础 collation。 | 📋 |
| 索引 | multikey 与 wildcard 语义、恢复一致性、planner/EXPLAIN；现有 compound/unique/sparse/partial/TTL 不重做。 | 📋 |
| Aggregation | 深化 `$unwind/$project/$group` 表达式，评估 `$lookup/$facet/$bucket` 的 SonnetDB-native 子集，并保持流式/分页边界。 | 📋 |
| Bulk | 混合 insert/update/delete/upsert 的 ordered/unordered 结果、分项错误、重试安全和明确的批次事务边界。 | 📋 |
| 迁移 | 可执行的 MongoDB dump/NDJSON/Extended JSON 导入、索引建议、dry-run 和机器可读差异报告；现有说明文档不等于迁移工具。 | 📋 |
| 收口 | 用结构化 gap report 标记 supported/partial/planned/not_planned，并补示例应用；UI 只在引擎语义完成后开放对应控件。 | 📋 |

边界：不承诺 MongoDB wire protocol、BSON command、官方 Driver 直连、replica set、sharding 或分布式事务；对外只使用 “Document Store” 或 “MongoDB-like workloads”。

## Milestone 34 — Modbus TCP 内建映射表

SonnetDB 同时支持两个明确角色：主站/client 主动轮询外部 PLC/RTU 并写入表；从站/server 暴露受控寄存器映射供外部主站读取或 staged 写入。协议运行时默认关闭，普通 `SELECT` 只读已采集状态，不同步阻塞访问 PLC。

| PR | 交付 | 状态 |
|---|---|---|
| #288 | 定稿 `CREATE MODBUS SOURCE/ENDPOINT`、`FROM MODBUS`、`EXPOSE AS MODBUS` 的 DDL、方向、地址、类型、字节序、缩放、访问和错误策略。 | 📋 |
| #289 | Parser/AST/catalog、版本兼容和 `SHOW/DESCRIBE MODBUS` 元数据。 | 📋 |
| #290 | 地址冲突校验及 BIT、整数、浮点、BCD、STRING 的 Span/BinaryPrimitives 编解码。 | 📋 |
| #291 | 默认关闭的 TCP master runtime、批量读取、轮询、取消、退避、超时、重连和指标。 | 📋 |
| #292 | 受限 SQL 写寄存器、preview/dry-run、权限和审计；远端失败不得伪造本地成功。 | 📋 |
| #293 | 质量位、错误码、source health、latest/history 与 KEEP_LAST/NULL/SKIP/MARK_BAD 策略。 | 📋 |
| #294 | 默认关闭的 TCP slave endpoint、读请求、绑定/白名单/unit id/最大连接数。 | 📋 |
| #295 | 外部写入的 REJECT/STAGED/UPDATE_TABLE 策略、待确认队列和审计；默认 STAGED。 | 📋 |
| #296 | Web/Studio 管理面、模拟 PLC parity、文档，以及 IoTSharp Product/Collection Template/Gateway/EdgeNode 合同边界。 | 📋 |

验收要求：四类寄存器读写与类型转换可对拍；写入不绕过审批、权限和审计；IoTSharp 只通过稳定合同消费，不依赖 SonnetDB 内部 catalog。第一版只做 Modbus TCP，不扩张到 RTU/ASCII、OPC UA、S7 或完整 SCADA。

## Milestone 35 — 语义内容与多模态检索

复用 Object Bucket、Document、FullText、Vector 和 Hybrid Search，建立“原始内容 → 异步提取/embedding → 可重建派生索引 → 检索”的受治理链路。Core 只负责确定性存储和检索，不下载模型、解码媒体或同步调用外部推理。

| PR | 交付 | 状态 |
|---|---|---|
| #297 | Semantic Content 清单、object reference、chunk/segment、状态机和 Embedding Profile 合同。 | 📋 |
| #298 | metadata-filtered ANN、精确补偿/回退、similar-by-id 和可解释 EXPLAIN。 | 📋 |
| #299 | 异步摄取、幂等 hash、重试/取消/背压/重启恢复，以及对象覆盖删除后的对账。 | 📋 |
| #300 | provider-neutral text/image/object embedding 能力发现、外发策略和调用审计。 | 📋 |
| #301 | 图片搜图片、文字搜图片、缩略图/来源/profile/分数展示和工业图片样例。 | 📋 |
| #302 | 通用 RAG 摄取 SDK/CLI、稳定 chunk、增量更新、删除同步和 Copilot 可回滚迁移。 | 📋 |
| #303 | RRF/归一化/去重/rerank hook，以及 Recall@K、nDCG、P50/P95、体积和重建评测。 | 📋 |
| #304 | 音视频 transcript、关键帧和 timecode segment；媒体处理留在可选扩展或外部工具。 | 📋 |
| #305 | 管理面、安全、失败恢复、备份重建、模型换代和 10k/100k 容量基线。 | 📋 |
| #306 | 派生目标、区域、track 与 detector profile 模型，保持原对象为唯一主数据。 | 📋 |
| #307 | 默认关闭且受治理的人脸 1:1 验证/1:N 候选，独立权限、审计、删除和 FAR/FRR/TAR 评测。 | 📋 |
| #308 | ReID、步态、姿态/动作的独立 profile、查询和对应 mAP/CMC/precision/recall 评测。 | 📋 |
| #309 | 车辆外观向量与车牌 OCR；号码以标准化精确索引为主，向量不替代相等语义。 | 📋 |

顺序固定为 #297/#298 地基 → #299/#300 摄取/provider → #301/#302 首批场景 → #303 质量 → #304/#305 扩展收口 → #306~#309 专业视觉。完成 #301 前只宣称“具备多模态检索底座”。所有生物特征能力默认关闭，并要求用途、权限、访问/导出审计、保留期限和删除闭环。

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
| 21 | Document CRUD、查询/分页、局部更新第一批、索引、aggregation 子集、validator 和单机恢复。 |
| 23 | 全文与向量引擎收编、持久索引与 Hybrid Search。 |
| 24 | Document Explorer、Validator、导入导出、rebuild 与共享审批。 |
| 26 | C ABI 与 Go/Rust/Java/Python 等连接器入口和发布流程。 |
| 28 | 数据可靠性、并发正确性、写/查热路径、索引/向量、SonnetMQ 与全模型高吞吐接入加固。 |
| 30 | Sparkplug B、CoAP、Line Protocol UDP 的接入、生命周期、安全、parity 和基准。 |
| 31 | 字符串/布尔等 selector 与 categorical aggregate 类型语义。 |
| 33 | 聚合正确性、多聚合复用、残差流式化、count(*) 专路和 LIMIT/latest-N 下推。 |
| MM9 | 多模型备份、检查、校验和恢复 CLI 第一批。 |

详细历史只用于追溯，不覆盖本文件的当前完成判定；若历史文档与当前实现冲突，以代码、可执行测试和本文件的审计结论为准。
