# SonnetDB 路线与里程碑总索引

2026-11-11（Asia/Shanghai）核查，基线`main@92d76007`。**[根ROADMAP当前推进顺序](../ROADMAP.md#当前推进顺序)是唯一全仓排序，本页负责全量归属和覆盖，不再保留第二份“当前顺序”。** 审查证据见[总报告](audits/roadmap-audit-20261111/README.md)、[核心](audits/roadmap-audit-20261111/core.md)、[生态](audits/roadmap-audit-20261111/ecosystem.md)与[独立反驳](audits/roadmap-audit-20261111/adversarial.md)。

M0～M51共52个编号全部保留，另列KV-LSM、V45-X、MM9及子库路线。下表的“指定研发完成”只指原合同的代码和历史适用验证；本轮全量盘点不等于重跑所有历史测试或逐行证明每段源码。按域深查和只做历史/入口对账的范围在报告中分开记录。原始历史见[roadmap-history](roadmap-history.md)与[CHANGELOG归档](../CHANGELOG.md#roadmap-completed-archive-2026-09-21)。

## 全部里程碑

| ID | 主题 | 实现状态 | 已完成/已有切片 | 仍须推进或证据边界 |
| --- | --- | --- | --- | --- |
| M0 | 项目脚手架 | ✅ 指定研发 | 既定代码/本地回归已归档；本轮做历史与入口对账 | 性能/容量/恢复的新范围归M19/M41/M42/KV-LSM；不作本轮重跑声明 |
| M1 | 内存与二进制基础设施（Safe-only） | ✅ 指定研发 | 既定代码/本地回归已归档；本轮做历史与入口对账 | 性能/容量/恢复的新范围归M19/M41/M42/KV-LSM；不作本轮重跑声明 |
| M2 | 逻辑模型与目录 | ✅ 指定研发 | 既定代码/本地回归已归档；本轮做历史与入口对账 | 性能/容量/恢复的新范围归M19/M41/M42/KV-LSM；不作本轮重跑声明 |
| M3 | 写入路径 | ✅ 指定研发 | 既定代码/本地回归已归档；本轮做历史与入口对账 | 性能/容量/恢复的新范围归M19/M41/M42/KV-LSM；不作本轮重跑声明 |
| M4 | 查询路径 | ✅ 指定研发 | 既定代码/本地回归已归档；本轮做历史与入口对账 | 性能/容量/恢复的新范围归M19/M41/M42/KV-LSM；不作本轮重跑声明 |
| M5 | 稳定性与性能（写入侧） | ✅ 指定研发 | 既定代码/本地回归已归档；本轮做历史与入口对账 | 性能/容量/恢复的新范围归M19/M41/M42/KV-LSM；不作本轮重跑声明 |
| M6 | SQL 前端 + Tag 倒排索引 | ✅ 指定研发 | 既定代码/本地回归已归档；本轮做历史与入口对账 | 性能/容量/恢复的新范围归M19/M41/M42/KV-LSM；不作本轮重跑声明 |
| M7 | 压缩编码 | ✅ 指定研发 | 既定代码/本地回归已归档；本轮做历史与入口对账 | 性能/容量/恢复的新范围归M19/M41/M42/KV-LSM；不作本轮重跑声明 |
| M8 | 服务器模式 | ✅ 指定研发 | 既定代码/本地回归已归档；本轮做历史与入口对账 | 性能/容量/恢复的新范围归M19/M41/M42/KV-LSM；不作本轮重跑声明 |
| M9 | 性能与发布 | ✅ 指定研发 | 既定代码/本地回归已归档；本轮做历史与入口对账 | 性能/容量/恢复的新范围归M19/M41/M42/KV-LSM；不作本轮重跑声明 |
| M10 | 批量入库快路径（原扩展和第三方占位已拆分） | ✅ 指定研发 | 既定代码/本地回归已归档；本轮做历史与入口对账 | 性能/容量/恢复的新范围归M19/M41/M42/KV-LSM；不作本轮重跑声明 |
| M11 | 写入快路径（PR #45 瓶颈收收尾） | ✅ 指定研发 | 既定代码/本地回归已归档；本轮做历史与入口对账 | 性能/容量/恢复的新范围归M19/M41/M42/KV-LSM；不作本轮重跑声明 |
| M12 | 函数与算子扩展（PID / Forecast / UDF） | ✅ 指定研发 | 既定代码/本地回归已归档；本轮做历史与入口对账 | 性能/容量/恢复的新范围归M19/M41/M42/KV-LSM；不作本轮重跑声明 |
| M13 | 向量类型与嵌入式向量索引（Copilot 知识库底座） | ✅ 指定研发 | 既定代码/本地回归已归档；本轮做历史与入口对账 | 性能/容量/恢复的新范围归M19/M41/M42/KV-LSM；不作本轮重跑声明 |
| M14 | SonnetDB Copilot：MCP 工具 + 知识库 + 智能体 | 🟡 | MCP/Copilot/知识库与真实入口已有 | 真实provider/质量/双网/宿主；接M27/M44 |
| M15 | 地理空间类型与轨迹分析 | ✅ 指定研发 | 空间/轨迹基础实现已归档 | 规模、权限、真实设备旅程接M36/M42 |
| M16 | Copilot 产品化升级（嵌入式 AI 助手 UX） | ✅ 指定研发 | Copilot产品化切片已归档 | 新治理/宿主/质量接M27/M44/M47 |
| M17 | 可观测性与运行时可见性 | ✅ 指定研发 | 日志/指标/观测入口已归档 | 全链路资源归属与SLO接M42 |
| M18 | VS Code数据库扩展 | ✅ 指定研发 | 旧VS Code #99～#107实现 | #108证据与M47新候选分别验收 |
| M19 | 生态适配底座（关系/KV/对象/大量measurement） | 🟡 | 关系/KV/对象与容量runner已有 | 代码/runner/verifier按能力路线；固定四档容量、Demo/现场恢复和硬件身份见[环境路线](roadmap-environment-validation.md) |
| M20 | 多模能力对齐与Parity | 🟡 | harness/适配器/CI已有 | 同候选原始证据及七天scheduled；skip不计pass |
| M21 | Document Store单机升级 | ✅ 指定研发 | 已声明Document单机子集 | 不含MongoDB wire；容量/TTL恢复接M25 |
| M22 | 上层应用候选（沿用原归属） | 📋 条件 | 应用候选保留，未据此扩展引擎 | 按业务价值单独冻结，不作为引擎隐含必选 |
| M23 | 搜索与向量引擎合并 | ✅ 工程合并 | FullText/Vector并入Core，VectorData入Data | 质量/Recall/容量/预算接M35/M36/M42 |
| M24 | Studio管理体验/Document管理面 | ✅ 指定研发 | Document等管理实现已归档 | 真实三面宿主与安装接M29/M47 |
| M25 | Document验收、文档与发布治理 | 🟡 | 容量/soak工具与文档已有 | million/ten-million、attestation与完整恢复见[环境路线](roadmap-environment-validation.md) |
| M26 | 连接器路线独立化 | ✅ 旧合同研发 | 旧C ABI与语言绑定基线 | M49新纯Frame/embedded矩阵另验 |
| M27 | Copilot/Provider治理与多端 | 🟡 | Provider/profile/双网/Studio本地切片 | 真实模型质量/成本、IdP、现场宿主 |
| M28 | 可靠性、并发正确性与热路径加固 | ✅ 指定研发 | 既定可靠性/并发/热路径修复归档 | 后续LSM/新故障交错不能由旧测试免验 |
| M29 | 多模型统一管理工作台 | 🟡 | 管理工作台与Studio本地切片 | 干净OS/WebView2、安装生命周期、现物料 |
| M30 | 多协议设备接入扩展 | ✅ 指定研发 | MQTT/Sparkplug/CoAP/LP UDP入口 | 实际设备、复杂负载及子库AOT/性能独立 |
| M31 | 时序聚合类型语义（Selector/Categorical） | ✅ 指定研发 | selector/categorical与类型语义 | M45扩展矩阵、state/增量不由此完成 |
| M32 | Document MongoDB-like易用性 | ✅ 指定研发 | Document查询/更新/索引子集 | M25规模与M36真实跨端；非MongoDB协议兼容 |
| M33 | 时序聚合执行与下推 | ✅ 指定研发 | sketch、聚合执行/下推 | 统一语料、尾延迟与M45/M46残余 |
| M34 | Modbus TCP内建映射表 | ✅ 指定研发 | Modbus TCP映射/设备接入 | 真实设备/复杂并发及错误恢复独立验收 |
| M35 | 语义内容与多模态检索 | 🟡 | 持久RAG/模型profile/混合检索 | 真实相关性/Recall、模型换代/删除/容量 |
| M36 | 九模型易用性与真实旅程 | 🟡 | 九模型SDK/CLI/管理入口及局部旅程 | 全文typed缺口、完整权限/取消/恢复与规模 |
| M37 | 视图与物化视图 | ✅ 指定研发 | 逻辑视图/全量REFRESH/代际恢复 | 有限增量物化与更新删除修正归M45 |
| M38 | SQL存储过程与触发器 | ✅ 指定研发 | 存储过程与触发器首版 | 触发器v2归M39；完整外部方言未承诺 |
| M39 | SQL触发器第二版 | 🟡 | 触发器v2及本地成本/恢复切片 | deferred/outbox混合DML长期尾延迟 |
| M40 | 原生属性图 | 🟡 Beta | 原生图/SQL/PGQ/本地evaluator | 原规模/外部对拍/AOT/Couplet/168小时见[环境路线](roadmap-environment-validation.md) |
| M41 | 查询规划器与成本 | 🟡 | planner/下推/spill本地优化 | 统一语料/I/O成本按能力路线；固定架构/生产尾延迟见[环境路线](roadmap-environment-validation.md) |
| M42 | 九模型有界执行与性能 | 🟡 | 局部预算、Top-K、结果准入 | 端到端pull、进程预算、冷读/资源按能力路线；固定硬件/长稳见[环境路线](roadmap-environment-validation.md) |
| M43 | 十四能力与生态总收口 | 🟡 | 本地CDC/窗口/订阅/DLQ/outbox/receipt | 远程/schema/冲突、组合恢复、完整旅程与发布证据 |
| M44 | AI应用与可治理推理 | 🟡 / 🚧 | A01～A05底座/切片，A06/A07在验 | 真实应用质量/成本/治理；A08～A10条件/后续 |
| M45 | 聚合与持续计算深化 | 🟡 | C01/C06/C08局部；既有Merge/窗口 | 版本化state、retract、增量物化与批流等价 |
| M46 | 存储编码与执行成本 | 🟡 | S02/S06/S09局部：Int64Rle/guard/v7 | 自适应/范围解码/资源与统一成本证据 |
| M47 | 统一管理工作台与三面发布 | 🟡 | P01～P08限定candidate12 R1 Web完成 | Studio/VSCode/三面正式版与完整模型动作 |
| M48 | 单机日志与事件分析 | 📋 | 日志R1设计，已有基础可复用 | L01～L20A代码候选未交付；L20B环境资格转[环境路线](roadmap-environment-validation.md)；L21～L24条件项 |
| M49 | 连接器/embedded与九模型访问 | 🟡 | C01完成；Protocol/.NET/SQL/raw多帧/typed KV及codec | 其余14包部分/未实施；真实新语言传输与按批消费者 |
| M50 | 时序ms/us/ns无损精度 | 📋 | T01～T27/T30A能力候选规划，现measurement仍ms | 库级持久单位、无损链路、迁移与恢复；T28/T29/T30B/T32环境资格见[环境路线](roadmap-environment-validation.md) |
| M51 | WorkBuddy生态与生命周期 | 🟡 | 0.2.0包文案与主线43工具；历史资料/脚本 | 平台/HTTPS/真实宿主、独立运维及各资产审核 |

历史M31～M39名称与原合同已通过Git历史和CHANGELOG对账，见[核心补审](audits/roadmap-audit-20261111/core.md)。M22保留上层应用候选的已知归属，不为凑齐清单杜撰已交付能力。

## 独立路线与工作包

| 路线/ID | 当前判定 | 权威入口与残余 |
| --- | --- | --- |
| KV-LSM K1～K6、K8 | ✅ 源码已接线 | [设计](design/kv-lsm-storage-engine.md)；默认LSM/打开迁移已存在，旧阶段叙述不再代表当前实现。 |
| KV-LSM K7与G1～G12 | 🟡 | 共享批次/计数/维护/回滚/Version备份已有；计量负载、原规模、旧二进制、随机故障按能力路线验，固定硬件/掉电/长稳转[环境路线](roadmap-environment-validation.md)。 |
| 时序P0-3～P0-5 / T1～T3 | 待决策 | [写入/分区/重复决策](design/time-series-write-partition-duplicates-decisions.md)；先冻结合同，不自动变更默认或KEEP_ALL。 |
| V45-X01/X02 | 🟡 / 🚧 | M41/M42/M49的端到端读取、总预算、公平准入；已有opt-in/单请求限制不等于全进程硬上限。 |
| V45-X03 | 📋 新远程增量 | 本地CDC/桥接已实现；远程/schema/冲突/离线合同待交付，不称HA。 |
| V45-X04 | 🟡 | MQ实例快照/CLI已有；数据库、MQ、身份配置、外部对象/派生索引组合恢复待完成。 |
| V45-X05/X06 | 🟡 / 🚧 | 全文/向量/对象/各端真实正确性、质量、容量及原始证据；先修当前接口缺口。 |
| V45-X07 | 📋 条件 | fencing/单写者/日志缺口/RPO/RTO先定义；不承诺复制/自动failover。 |
| V45-X08 | 📋 | 设备/业务实体模板与已有关系/measurement合同复用，不增加原生模型。 |
| M42-H07A/H07B | H07A代码候选 / H07B环境资格 | [热点并发专题](design/m42-relational-hot-write-concurrency.md)；H07A保留代码/过载/崩溃合同，H07B固定硬件、Demo/现场和168小时转[环境路线](roadmap-environment-validation.md)。 |
| MM9 | 历史已验证研发 | 治理/恢复底座归档；后续证据统一回M36/M42/M43，不新开同名功能。 |
| SonnetMQ四阶段 | MVP已实现，其余部分 | [MQ路线](sonnetmq-roadmap.md)；默认OS flush、至少一次与实例范围公开，ring buffer等仅按实测收益选择。 |
| Search/Vector Phase0～5 | ✅ 工程合并 | [合并路线](search-vector-engine-consolidation-roadmap.md)；质量/规模不由目录合并完成。 |
| VS Code #99～#108 | 旧插件实现＋剩余证据 | [子路线](../extensions/sonnetdb-vscode/ROADMAP.md)；M47新候选独立，旧Marketplace版本不代验。 |
| CoAP C0～C13 | C0～C11/C13工程完成，C12部分 | [子路线](../extensions/IoTSharp.CoAP.NET/ROADMAP.md)；benchmark/smoke存在与NativeAOT/性能实物分开。 |
| MQTT routing R0～R7 | R0～R5/R7工程完成，R6部分 | [子路线](../extensions/MQTTnet.AspNetCore.Routing/ROADMAP.md)；保留通用协议/宿主边界，性能和AOT独立。 |
| CAP / 缓存 / Testcontainers / ORM | 指定切片成立，能力矩阵不等齐全 | [生态审计](audits/roadmap-audit-20261111/ecosystem.md)；CAP同组单活动实例、EF应用AOT、纯Frame与旧Data各自有边界。 |
| 环境与现场资格 ENV-00～ENV-06 | 独立环境路线 | [环境路线](roadmap-environment-validation.md)；集中管理 Demo、固定硬件、物理掉电/介质、168小时/7天长稳、现场日志和环境发布回读，不阻断普通代码切片。 |

完整工作包行与原始文档位置见[inventory.json](audits/roadmap-audit-20261111/inventory.json)。M44～M51、V45-X的详细必选/条件划分保留根路线及各专题，不因本轮重排减少原合同。

## 十四能力与证据层

九原生模型：时序、关系、KV、Document、全文、向量、对象、MQ、Graph；五跨模型能力：空间/轨迹、流处理/订阅、CDC/本地同步、AI/RAG、治理/恢复/观测。实现入口均存在；[机器索引](audits/fourteen-capability-evidence-index.json)整体仍为13项partial和Graph beta，表示完整能力/验收仍有残余，不表示13项未实现。

本轮修正机器索引中的共享LSM多表/计数/维护“未实现”、全文候选窗口、预算/恢复域与最新证据入口。旧九模型报告保留历史；不能继续用其“无实例MQ快照/对象全桶分页”等旧事实派新任务。

## 外部事项与独立验收

[实时issue快照](github-issues-roadmap.md)仅有GH-Issue #396/#397/#398/#399/#401/#402开放，0个开放PR。#230/#392/#394/#395/#403/#404已实时核为CLOSED/COMPLETED。内部路线#N不是GitHub #N；不得按数字碰撞关联。

代码实现、候选消费者、公开发布、干净系统安装与环境资格分层验收。固定硬件、Demo/现场、长稳及物理掉电统一进入[环境路线](roadmap-environment-validation.md)；M20七天scheduled为独立观察，不是所有普通代码修复的统一前置。Graph不因上层AI退出Beta，MQ不因单库备份宣称实例一致恢复，M49不因codec存在宣称全部原生HTTP/2可用。

“4.5”与V45保留为历史规划命名空间和已批准范围，不是当前最新公开版本的声明；2026-11-11读取GitHub latest为v5.0.0，仅核元信息/52资产数，未重新下载或消费包。后续版本冻结须逐批明确旧必选/条件项的归属，不自行将所有新计划塞入同一次发行。
