# SonnetDB 全面能力博客系列选题库

核查日期：2026-10-05（Asia/Shanghai）。本文件规划 **143–202 共 60 篇**后续文章，全部为 `planned`：尚未写成完整正文，尚未逐篇执行示例，尚未进入自动发布队列。它补全 001–134 历史底稿及 135–142 首批现状文章，没有把源码存在、文档存在或选题规划视为博客园已发布。

总体事实以 [README](../../README.md)、[十四能力证据索引](../audits/fourteen-capability-evidence-index.json)、[能力成熟度](../capability-maturity.md)、[当前路线图](../../ROADMAP.md)及各行专题为准。当前九模型中 Graph 为 `beta`，其余模型和五项平台能力总体为 `partial`；某个切片已实现不代表该模型的所有容量、恢复、远程或发布门禁已通过。专题含历史描述时，写作前须继续核对最新源码、测试与审计，采用范围更明确的合同。

表中的“能力状态与限制”描述技术事实；“稿件状态”描述编辑流程，二者分开。历史底稿关联用于复用示例、修订和交叉链接：先对账博客园已发布内容，再决定写续篇还是更新原文，避免把同一能力重复包装为新功能。所有性能、质量与恢复数字须附版本、提交、机器、配置和原始报告；没有证据时只介绍方法或明确边界。

## 系列一：时序建模与写查闭环（143–146）

| 编号 | 标题 | 读者可完成的任务 | 事实来源 | 能力状态与限制 | 历史底稿关联与新增角度 | 稿件状态 |
|---|---|---|---|---|---|---|
| 143 | Measurement 实用建模：TAG、FIELD、稀疏值与原始名称 | 为设备遥测建立 schema，区分 series 身份与数据值，用正确拼写查询列 | [数据模型](../data-model.md)、[SQL 参考](../sql-reference.md) | `partial`；名称遵循原名与双引号合同；不把 TAG 大小写绑定规则用于数据值 | 复用 009、013、015、136 的基础，新增稀疏 FIELD 与跨入口名称一致性检查 | planned |
| 144 | 受控 Schema-on-Write：从自动建列到拒绝坏批次 | 配置自动创建/演进策略与列额度，观察类型冲突及批次预检结果 | [写入准入](../timeseries-write-admission.md)、[Schema-on-Write 底稿](117-schema-on-write.md) | `partial`；WriteMany 内部块与普通 SQL 多行 INSERT 原子性/额度边界不同，不承诺整句全有或全无 | 117 为技术基础；续写面向调用方的“预检—失败—修正”旅程，避免重述实现过程 | planned |
| 145 | TAG 加时间桶分组：COUNT、稀疏字段与精确整数 SUM | 按设备 TAG 和 time 桶统计，验证 COUNT(*)/COUNT(field)、空值和溢出 | [M45 TAG 分组合同](../benchmarks/m45-measurement-tag-grouping.md)、[验证审计](../audits/m45-measurement-grouping-20261004.md) | `partial`；裸聚合投影；拒绝 measurement HAVING、残差/Geo WHERE、TAG/别名/多键排序及多 series FIRST/LAST；无分组 spill/总 heap 上限 | 031、037、038 只作 time-only 基础；新增 TAG 组合键与 M45 首批边界，不称整包完成 | planned |
| 146 | 有界时序查询：范围、结果准入、取消与首行成本 | 选择支持的原始查询形状，设置可用物化额度，并区分扫描与返回预算 | [查询预检](../timeseries-query-preflight.md)、[Measurement 结果边界](../benchmarks/m42-measurement-result-bounds.md) | `partial`；显式预算只覆盖支持子集；LIMIT/Frame 分块不代表执行器到客户端全链路流式 | 018、022、093 为背景；新增 M42 预算与取消，不复用旧延迟数字 | planned |

## 系列二：关系表与业务 SQL（147–150）

| 编号 | 标题 | 读者可完成的任务 | 事实来源 | 能力状态与限制 | 历史底稿关联与新增角度 | 稿件状态 |
|---|---|---|---|---|---|---|
| 147 | 业务表从建表到演进：主键、默认值、约束与索引 | 建关系表并执行受支持 ALTER，核验精确 DECIMAL、TIME 与约束行为 | [SQL 参考](../sql-reference.md)、[关系类型边界](../relation-type-boundary.md) | `partial`；SQL 为子集；DECIMAL 受 System.Decimal 范围约束，TIME 不含日期/时区且不接受 24:00 | 120 的早期 DDL 说明须先勘误；本篇聚焦当前关系表合同而非 measurement 修饰符 | planned |
| 148 | JOIN 使用合同：关系表与时序维表怎样关联 | 写 INNER/OUTER JOIN，处理 NULL、同名列和 measurement 到关系维表的关联 | [标准 JOIN 合同](../standard-join-contract.md)、[时序 JOIN 审计](../audits/measurement-join-196-closure-20260923.md) | `partial`；按支持矩阵选择连接源、条件与投影；不外推完整标准 SQL 或跨模型原子性 | 030 和 135 为概览；新增 JOIN 端到端示例与列冲突说明 | planned |
| 149 | UPSERT 与 RETURNING：取得真实写入结果 | 使用 ON CONFLICT 和 RETURNING 完成条件更新，核验受影响行及返回值 | [SQL 参考](../sql-reference.md)、[UPSERT 审计](../audits/github-issue-184-conditional-upsert-20260925.md) | `partial`；冲突目标须匹配主键/唯一索引；不把预览当执行结果，不盲重放未知提交 | 015、081、082 仅提供旧写入/连接基础；新增当前关系 DML 与远程事务结果合同 | planned |
| 150 | 轻事务与 ROWVERSION：安全处理并发修改 | 在同库关系表事务内更新，用旧版本条件识别冲突并回滚 | [ADO.NET](../ado-net.md)、[SQL 事务与锁定读](../sql-reference.md) | `partial`；同库关系表轻事务；SELECT FOR UPDATE/FOR SHARE 拒绝，非跨九模型事务 | 修正 081 “完整支持事务”的泛化说法；新增乐观并发和冲突恢复旅程 | planned |

## 系列三：KV 与缓存（151–153）

| 编号 | 标题 | 读者可完成的任务 | 事实来源 | 能力状态与限制 | 历史底稿关联与新增角度 | 稿件状态 |
|---|---|---|---|---|---|---|
| 151 | 持久 KV 入门：字节值、Namespace、TTL 与前缀扫描 | 保存设备状态，设置到期时间，按 namespace 和 prefix 读取 | [KV Keyspace](../kv-keyspace.md)、[KV 样例](../../samples/SonnetDB.KvQuickstart/README.md) | `partial`；raw bytes 为权威；严格 UTF-8；无 Redis wire protocol、KV SQL 或跨 keyspace 事务 | 137 是模型概览；本篇新增可运行 KV 单任务，不重复比较产品 | planned |
| 152 | KV 原子操作：NX/XX、CAS 与交换值 | 用条件写、版本比较和原子 GetAndSet/GetAndDelete 消除读后写竞态 | [KV 原子合同](../kv-atomic-contract.md)、[远程闭环审计](../audits/kv-remote-closure-20260905.md) | `partial`；单 key/单 keyspace 合同；WAL 提交未知须 fail closed，异常不代表确定未写 | 137 只作入口；新增并发例子、TTL 后条件判断及失败处理 | planned |
| 153 | 从 KV 到 .NET 缓存：IDistributedCache 与 EasyCaching | 接入缓存 Provider，验证 namespace、TTL 与可重建值的生命周期 | [生态接入](../ecosystem-integration.md)、[生态样例](../../samples/SonnetDB.EcosystemSample/README.md) | `partial`；复用 KV 语义；缓存数据可重建，不等同关系主数据迁移或分布式一致性 | 080–082 和 137 为基础；新增缓存 Provider 应用任务 | planned |

## 系列四：JSON 文档与迁移（154–157）

| 编号 | 标题 | 读者可完成的任务 | 事实来源 | 能力状态与限制 | 历史底稿关联与新增角度 | 稿件状态 |
|---|---|---|---|---|---|---|
| 154 | 文档查询工作流：JSON Path、Builder、投影与 Cursor | 用 .NET builder 查询 JSON 集合，逐页读取并处理 continuation 错误 | [Document Store](../document-store.md)、[Document Quickstart](../../samples/SonnetDB.DocumentQuickstart/README.md) | `partial`；不是 Mongo cursor/wire；token 过期、错配或快照变化明确拒绝，不自动从头读 | 137 为概览；新增 builder 与分页恢复代码 | planned |
| 155 | 文档局部更新与 Mixed Bulk：版本、幂等和逐项结果 | 执行 $set/$inc、findOneAndUpdate 和混合批次，检验 requestId 重放 | [Document Store](../document-store.md)、[Document Quickstart](../../samples/SonnetDB.DocumentQuickstart/README.md) | `partial`；单 collection batch；不支持 positional/arrayFilters；requestId 有有限保留期 | 137 为入口；新增更新前后镜像、ordered/unordered 与重放边界 | planned |
| 156 | 文档索引与 Validator：复合、Multikey、TTL 和在线重建 | 声明索引/validator，查看 planner，安排 TTL 与重建维护 | [Document Store](../document-store.md)、[在线索引构建](../online-index-build.md) | `partial`；parallel arrays、wildcard unique/TTL 等组合明确拒绝；百万/千万容量待目标机验证 | 137 仅提模型；新增 schema governance 和维护任务 | planned |
| 157 | MongoDB-like 迁移实操：Dry-run、Checkpoint 与 Change Feed | 导入 JSON/NDJSON/BSON 子集并续跑，读取 SonnetDB change feed 位置 | [MongoDB 迁移](../mongodb-migration.md)、[Document Store](../document-store.md) | `partial`；不兼容官方 Mongo Driver、完整 BSON/MQL、replica set；Change Feed 不等于 change stream | 137 为概览；新增可验证迁移步骤与客户端改造清单 | planned |

## 系列五：全文检索（158–160）

| 编号 | 标题 | 读者可完成的任务 | 事实来源 | 能力状态与限制 | 历史底稿关联与新增角度 | 稿件状态 |
|---|---|---|---|---|---|---|
| 158 | BM25 搜索入门：评分、短语、布尔与模糊查询 | 为文档文本建索引，运行支持的查询并解释评分与高亮结果 | [SQL 全文合同](../sql-reference.md)、[管理工具](../management-tools.md) | `partial`；支持的查询子集；固定语料质量与容量另验，不宣称 Elasticsearch/OpenSearch 等价 | 076、135 只提检索底座；新增全文独立使用旅程 | planned |
| 159 | 中文 Analyzer 与领域词库：编译、版本与重建 | 添加业务词库，生成 .dat，比较 token 并重建既有索引 | [全文词库](../fulltext-dictionaries.md)、[第三方声明](../../THIRD_PARTY_NOTICES.md) | `partial`；分词变化要求重建；固定词库来源/许可证，不自动形成相关性提升结论 | 127 涉 analyzer 治理背景；新增用户词库可操作教程 | planned |
| 160 | 全文维护工作台：Analyze、Relevance Explain 与 Rebuild 状态 | 在工作台查看 analyzer/评分解释，提交受审批的重建并跟踪任务 | [管理工具](../management-tools.md)、[SQL 参考](../sql-reference.md) | `partial`；状态来自实际服务；fixture 截图不代表真实重建/长稳验收 | 141 为三面概览；新增全文专用任务与失败/只读状态 | planned |

## 系列六：向量与混合检索（161–164）

| 编号 | 标题 | 读者可完成的任务 | 事实来源 | 能力状态与限制 | 历史底稿关联与新增角度 | 稿件状态 |
|---|---|---|---|---|---|---|
| 161 | 向量放在哪：Measurement、Document 与 VectorData 的选择 | 为带时序标签及通用文档分别选模型，用相同维度/metric 查询 | [向量检索](../vector-search.md)、[VectorData 生命周期](../vector-lifecycle-preflight.md) | `partial`；同维度不等于同 embedding profile；SQL/服务层支持边界分别核对 | 017、028、049–055 为基础；新增三种入口决策，不重讲距离数学 | planned |
| 162 | 段内 ANN 四种选择：HNSW、IVF、IVF-PQ 与 Vamana | 声明支持的索引参数，对比精确扫描结果并检查索引命中/回退 | [向量检索](../vector-search.md)、[SQL 参考](../sql-reference.md) | `partial`；ANN 派生索引可精确补偿/回退；真实 Recall、固定硬件收益待证 | 014、051、052、116 的 HNSW 基础可修订复用；新增其它索引与适用边界 | planned |
| 163 | Filtered Search 不混用：通用文档 SQL 与图片语义路径 | 分别验证 WHERE 精确回退、图片 metadata/tag prefilter 和查询预算 | [Filtered ANN 预算](../m35-filtered-search-budgets.md)、[语义图片检索](../semantic-image-search.md) | `partial`；通用 Document WHERE 不自动复用图片服务 filtered ANN；超扫描/时间预算显式失败 | 050、134、140 可交叉链接；新增两条路径的语义对照 | planned |
| 164 | Hybrid Search 与 RRF：把文本召回和向量召回合并 | 使用 SQL hybrid_search 或 RAG fusion，保留来源并解释排名 | [SQL Hybrid Search](../sql-reference.md)、[RAG 融合](../rag-search-fusion.md) | `partial`；SQL 加权分数与 RAG RRF 是不同入口；真实质量/ACL/删除同步需单独验证 | 050、076、139 为背景；新增融合选型与证据回答评估 | planned |

## 系列七：对象桶与文件传输（165–168）

| 编号 | 标题 | 读者可完成的任务 | 事实来源 | 能力状态与限制 | 历史底稿关联与新增角度 | 稿件状态 |
|---|---|---|---|---|---|---|
| 165 | Object Bucket 日常 API：版本、Metadata、Range 与签名 URL | 上传对象，查看版本/元数据，读区间内容并创建受限访问 URL | [对象客户端合同](../object-client-contract.md)、[生态接入](../ecosystem-integration.md) | `partial`；S3 风格子集，不承诺完整 S3/SigV4；鉴权与 URL 权限须单独核对 | 134/140 只把 bucket 用作图片源；新增通用文件存储任务 | planned |
| 166 | 对象列表分页：Prefix、Continuation 与高变更率 | 分页浏览对象，处理 token 变化和重复/缺失检查 | [分页审计](../audits/object-pagination-20260906.md)、[对象客户端合同](../object-client-contract.md) | `partial`；按 token 合同处理变化；高变更率/目标规模现场证据待证 | 022 是 SQL 分页，不移植其 offset 语义；新增对象 continuation 教程 | planned |
| 167 | 可恢复 Multipart 上传与校验下载 | 配置分片/并发/resume manifest，校验 SHA-256，失败后保留已有目标文件 | [Object Transfer Manager](../object-transfer-manager.md)、[传输审计](../audits/object-transfer-validation-20260920.md) | `partial`；仅安全分片可有限重试；PUT/Complete 发送后未知结果不自动重放；大文件跨进程/断电待证 | 134 的图片上传只作背景；新增通用大文件恢复任务 | planned |
| 168 | Bucket 治理：Quota、Retention、Legal Hold 与生命周期 | 配置保留规则，预览删除影响，核对真正过期对象和审计 | [对象客户端合同](../object-client-contract.md)、[管理工具](../management-tools.md) | `partial`；保留/hold 会阻止删除；数据与派生索引清理异步，不等于九模型原子删除 | 134/140 的清理链可复用；新增治理而非语义搜索主流程 | planned |

## 系列八：SonnetMQ（169–171）

| 编号 | 标题 | 读者可完成的任务 | 事实来源 | 能力状态与限制 | 历史底稿关联与新增角度 | 稿件状态 |
|---|---|---|---|---|---|---|
| 169 | Producer 与 Consumer Builder：Prefetch、Manual ACK 与 Drain | 有界发布/消费消息，显式 ACK，并停止轮询后排空已预取消息 | [MQ 高层客户端](../mq-high-level-client.md)、[MQ 路线与合同](../sonnetmq-roadmap.md) | `partial`；AutoAck 为交给枚举器前确认，可接受 at-most-once 才使用；无 exactly-once | 138 为恢复概览；新增 producer/consumer 使用代码 | planned |
| 170 | MQ 重投运维：NACK、DLQ、Offset Reset 与 Message ID | 将毒消息转 DLQ，选择重放起点，核验有界去重窗口与 retention | [MQ 高层客户端](../mq-high-level-client.md)、[管理工具](../management-tools.md) | `partial`；去重窗口有限，retention 后旧 ID 不命中；无跨节点 rebalance 承诺 | 138 为背景；新增消息运维操作和重放风险 | planned |
| 171 | MQ 的两个作用域：数据库权限与实例 Snapshot | 按 database+topic 定位资源，准备实例快照/恢复与数据库恢复的组合清单 | [十四能力索引](../audits/fourteen-capability-evidence-index.json)、[管理工具](../management-tools.md)、[路线图](../../ROADMAP.md) | `partial`；Server `.system/mq` 实例共享；单库备份不覆盖 MQ，不承诺组合恢复原子性 | 135、138、141 已解释边界；续写核对 offset/消费者组的恢复操作清单 | planned |

## 系列九：原生属性图 Graph Beta（172–175）

| 编号 | 标题 | 读者可完成的任务 | 事实来源 | 能力状态与限制 | 历史底稿关联与新增角度 | 稿件状态 |
|---|---|---|---|---|---|---|
| 172 | 原生图建模：顶点、边、标签、属性与 Statement Snapshot | 建一个有限资产拓扑，读单一 sequence 快照并验证并发变更隔离 | [原生图合同](../m40-native-graph-341-contract.md)、[Statement Snapshot](../m40-graph-360-statement-snapshot.md) | `beta`；原生邻接已实现；固定硬件、容量、外部语义及长稳门禁未闭环 | 135 是九模型概览；新增原生图首个可操作任务 | planned |
| 173 | SQL/PGQ 与受限 GQL 风格：一跳、路径与 EXPLAIN | 用 GRAPH_TABLE 查询拓扑，比较嵌入式 ExecuteGql 的等价只读查询 | [SQL 图规划](../m40-graph-354-358-sql-planner.md)、[GQL 风格入口](../m40-graph-364-gql-entry.md) | `beta`；ExecuteGql 显式 opt-in、嵌入式受限；远程继续 SQL/PGQ；非完整 GQL/Cypher | 135/141 只提图入口；新增查询形状、预算与错误对照 | planned |
| 174 | 加权路径与离线图算法：预算、恢复和结果版本 | 选择 Dijkstra/A*/双向算法，运行 degree/PageRank 等有界离线任务 | [加权路径](../m40-graph-362-weighted-path.md)、[离线算法](../m40-graph-363-offline-algorithms.md) | `beta`；非负有限权重；A* heuristic 由调用方保证；算法发布按批次，不是全图原子提交 | 无对应旧函数底稿；新增路径约束、checkpoint、spill 与完成版本读取 | planned |
| 175 | Graph Workbench 运维：版本写、维护审批与 GraphRAG 引用 | 浏览有界画布，提交版本化编辑/维护，并保留知识图证据出处 | [图运维](../m40-graph-366-operations.md)、[GraphRAG 合同](../m40-graph-365-knowledge-contract.md)、[生产 Gate](../m40-graph-367-production-gate.md) | `beta`；有界合同可用；GraphRAG 质量、Couplet 联合语料、1m/10m 与 168h 门禁待证 | 141 为三面定位；新增 Graph 专用任务，不把截图/quick 当生产可用 | planned |

## 系列十：SQL、分析与可解释执行（176–180）

| 编号 | 标题 | 读者可完成的任务 | 事实来源 | 能力状态与限制 | 历史底稿关联与新增角度 | 稿件状态 |
|---|---|---|---|---|---|---|
| 176 | CTE 与集合运算：有界递归、UNION、INTERSECT 和 EXCEPT | 拆解查询并执行支持的 CTE/集合运算，测试类型、优先级及递归上限 | [SQL 参考](../sql-reference.md)、[递归审计](../audits/github-issue-189-recursive-cte-20260925.md) | `partial`；只用文档列出的有界子集；不外推无界递归或全部方言优先级 | 118/120 只覆盖基础兼容；新增复杂查询组织任务 | planned |
| 177 | OVER 窗口不混用：关系分区与时序 Series | 分别运行 row_number/累计聚合和 measurement 时间窗口，核验 NULL 与 peer | [SQL OVER 合同](../sql-reference.md)、[窗口审计](../audits/issue-172-window-20260925.md) | `partial`；关系与 measurement 矩阵不同；显式 frame 与嵌套等拒绝；关系有序累计最坏 O(n²) | 039–047 为时序旧语法基础；新增 ANSI OVER 与模型差异 | planned |
| 178 | 视图、物化视图与 SQL 例程：保存查询还是保存结果 | 创建受支持视图/物化视图，使用有限 procedure/trigger 并查看诊断 | [SQL 参考](../sql-reference.md)、[触发器基线](../benchmarks/m39-trigger-v2-baseline.md) | `partial`；全量物化不等于 M45 增量 rollup；routine/trigger 预算与 outbox 边界明确 | 100 旧路线不可当事实；新增已有 M37–M39 的实际入口 | planned |
| 179 | EXPLAIN 到成本诊断：索引、Spill、并行与结果预算 | 查看访问路径与阻塞算子，按支持形状设置预算并识别串行回退 | [SQL 参考](../sql-reference.md)、[Spill 合同](../m41-blocking-operator-spill.md)、[受控并行](../m41-controlled-parallelism-feedback.md) | `partial`；局部额度非总 CLR heap；并行只覆盖指定算子；全链路首行/流式仍待证 | 080、094、124 提供背景；新增完整诊断任务，不声称普遍更快 | planned |
| 180 | 工业分析示例：轨迹、PID、预测与异常的使用边界 | 对同一示例数据运行 geo/PID/forecast/anomaly，并保存输入与结果 | [地理空间](../geo-spatial.md)、[PID](../pid-control.md)、[预测](../forecast.md)、[工业 AI](../industrial-ai-applications.md) | `partial`；SQL 分析不替代 PLC 硬实时控制；示例不等于现场质量/闭环控制验收 | 056–073、101–110 的示例可修订复用；新增统一复现与边界，不冒充客户案例 | planned |

## 系列十一：部署、治理、恢复与观测（181–186）

| 编号 | 标题 | 读者可完成的任务 | 事实来源 | 能力状态与限制 | 历史底稿关联与新增角度 | 稿件状态 |
|---|---|---|---|---|---|---|
| 181 | 安全部署第一天：Docker、首次初始化与远程端口 | 从 loopback 启动、完成管理员引导，核验 needsSetup/认证后开放指定接口 | [开始使用](../getting-started.md)、[Docker 镜像](../releases/docker-image.md) | `partial`；使用实际发行物标签；单节点，无内置 HA/自动 failover；不可只配置静态 token 跳过初始化 | 修订复用 003、008、090；新增当前初始化与远程验证闭环 | planned |
| 182 | 用户、GRANT、Token 与写审批：最小权限实操 | 创建读写用户/授权、签发和吊销 token，验证只读拒写与审批目标绑定 | [SQL 权限](../sql-reference.md)、[管理工具](../management-tools.md) | `partial`；控制面与数据库权限分别验；token 明文一次显示；前端确认不代替 Server 鉴权 | 011、012 为基础；新增跨模型权限旅程与未知写结果处理 | planned |
| 183 | 目录备份与迁移：Manifest、SHA-256、Dry-run 与新目录恢复 | 创建/校验备份，用 MigrationService 预检并恢复到新目录 | [备份恢复](../backup-restore.md)、[生态迁移边界](../ecosystem-integration.md) | `partial`；离线/维护窗口、兼容格式；非跨产品转换；不覆盖实例 MQ/九模型原子恢复 | 010、090 为背景；新增可核对 manifest 的恢复任务 | planned |
| 184 | WAL、Segment v6 与恢复演练：写成功到底保证什么 | 对照 durability 配置、WAL 回放与段发布，记录 kill/reopen 验证范围 | [架构](../architecture.md)、[可靠性变更](../performance-reliability-updates.md)、[文件格式](../file-format.md) | `partial`；fsync 受 OS/介质保证影响；kill 测试不是任意断电零丢失；默认编码不冒称默认压缩/zero-copy | 005、088、096、123–128 可复用；本篇聚焦恢复演练与默认配置 | planned |
| 185 | Metrics、SSE、Slow Query 与 OTLP：建立可观测闭环 | 启用受支持采集/导出，关联 slow-query fingerprint 与执行指标 | [可观测性](../observability.md)、[故障排查](../troubleshooting.md) | `partial`；Prometheus 完整端点默认关闭；未配置目标不外发；标签不含 SQL/参数等敏感高基数字段 | 086/100 旧说明须重核；新增当前配置、隐私与诊断任务 | planned |
| 186 | 发布验证清单：Server、SDK、安装包与性能报告身份 | 核对同候选版本/提交的发行物、校验和、安装合同和原始 Gate 状态 | [发布打包](../releases/README.md)、[M43 发布证据](../m43-release-evidence.md)、[路线图](../../ROADMAP.md) | `partial`；工具/模板非 Gate PASS；干净目标机安装、真实质量、固定硬件和长稳分别验；不假定新包已发布 | 090、095、111–116、142 为背景；新增发行物与证据身份对账 | planned |

## 系列十二：设备接入、CDC 与持续数据流（187–191）

| 编号 | 标题 | 读者可完成的任务 | 事实来源 | 能力状态与限制 | 历史底稿关联与新增角度 | 稿件状态 |
|---|---|---|---|---|---|---|
| 187 | 设备接入选型：MQTT、Sparkplug B、CoAP 与 Line Protocol | 用同一 telemetry 样例选择路由、权限、QoS/确认方式并查询落库结果 | [协议接入](../protocol-ingest.md)、[Sparkplug B](../sparkplug-b.md)、[批量摄取](../bulk-ingest.md) | `partial`；MQTT QoS 2 不支持；UDP 无 ack/身份且默认关闭；Sparkplug 生命周期另验 | 087、089、101–110 为背景；新增多协议语义对照，不声称端口监听等于可靠持久化 | planned |
| 188 | Modbus TCP：本地映射、Quality 与 Staged 写审批 | 配置 source/endpoint，读 LATEST/HISTORY，检查质量位并审批受限写 | [Modbus TCP](../modbus-tcp.md)、[SQL Modbus](../sql-reference.md) | `partial`；runtime 与对象须显式启用；普通 SELECT 不同步访问 PLC；现场设备/写回证据独立 | 067/102 的工业示例不作已验证 PLC 证据；新增当前内建 runtime 任务 | planned |
| 189 | 持久订阅与窗口：Watermark、Tumbling、Sliding 和 Session | 创建有界文件订阅与窗口，分页结果，取消/重开后核对 checkpoint | [订阅合同](../streaming-subscription-contract.md)、[数值窗口](../m43-numeric-windows.md)、[滑动窗口](../m43-sliding-window-contract.md)、[任务目录](../m43-streaming-task-catalog.md) | `partial`；本地有界子集；数值/NULL、分组和重叠矩阵各有合同，不等价通用 SQL 聚合 | 047/071/138 为背景；新增持久 state 的使用旅程 | planned |
| 190 | 流任务运维：Pause、Retry、DLQ Replay 与 Dispatcher | 查看持久任务目录，隔离失败批次，恢复投递并核验 replay 身份 | [订阅运维](../m43-subscription-operations.md)、[DLQ 运维](../m43-streaming-dlq-operations.md)、[Dispatcher](../m43-streaming-dispatcher.md) | `partial`；业务副作用非事务 exactly-once；超时 handler 不并行重入；未知外部结果需交接 | 138、170 只作 MQ/流背景；新增流任务生命周期，不混用 MQ offset 与窗口 checkpoint | planned |
| 191 | 本地 CDC 到持久流：Snapshot、Topology、Outbox 与 Receipt | 捕获文档变化，推进有限分区拓扑，恢复桥接意图后确认源位点 | [CDC 合同](../cdc-contract.md)、[本地拓扑](../m43-local-cdc-topology.md)、[CDC 桥接](../m43-cdc-streaming-bridge.md)、[组合样例](../../samples/SonnetDB.CdcStreamingJourney/README.md) | `partial`；本地显式拓扑/专属单分区 bridge；远程/冲突/schema/离线同步未闭环；未知目标发布 fail closed | 138 为概览；新增位点/receipt 操作，不称复制底座为 HA | planned |

## 系列十三：AI、MCP、RAG 与多模态（192–197）

| 编号 | 标题 | 读者可完成的任务 | 事实来源 | 能力状态与限制 | 历史底稿关联与新增角度 | 稿件状态 |
|---|---|---|---|---|---|---|
| 192 | Provider-neutral Copilot：Cloud、IChatProvider 与模型分组 | 配置 OpenAI-compatible/Ollama/vLLM 入口，区分 cloud 优先与本地 Chat readiness | [Copilot Provider](../copilot-providers.md)、[双网/Relay 合同](../copilot-relay-multi-instance.md) | `partial`；自研 CopilotAgent，不是 Microsoft Agent Framework；当前本地 HTTP Chat 拒绝 read-write；真实双网/质量另验 | 074、075、078、139 的架构可修订引用；新增当前接线任务 | planned |
| 193 | ONNX Embedding Profile：Tokenizer、Pooling、Batch 与 Fallback | 配置明确模型/profile，区分真实 session 推理、配置 readiness 与 hash fallback | [Provider 配置](../copilot-providers.md)、[ONNX Profile 证据](../benchmarks/m27-provider-model-profile.md) | `partial`；显式 profile 的 ONNX 已可执行；缺资源/profile 可能 hash fallback；tiny fixture/Native AOT 不等于真实语义质量 | 075/076 的旧描述须按当前事实修订；新增 profile 校验，不能再称 ONNX 仅占位 | planned |
| 194 | Typed HTTP MCP：只读工具、Schema、预算与错误码 | 接入 /mcp/{database}，读取 tools/resources，执行 query_sql/explain_sql 并处理稳定错误 | [MCP Typed Contract](../mcp-contract.md)、[Agent 指南](../ai-agent-guide.md) | `partial`；Bearer+Read grant，MCP 无写工具；不把 Copilot draft/execute 工具当 MCP；stdio bridge/配置向导仍按 M47 规划 | 098 旧命令/工具名须勘误；139 是概览，本篇新增实操协议与拒绝测试 | planned |
| 195 | 持久 RAG 摄取：稳定 Chunk、Generation 与取消续跑 | 用 Core writer/CLI 生成分块，发布完整 generation，暂停/恢复/回滚任务 | [RAG Core](../rag-ingestion-core.md)、[RAG CLI](../rag-cli.md)、[RAG 治理](../rag-governance.md) | `partial`；Text/Document 首批；embedding 由明确 provider/回调提供；无完整 generation 不发布；远程/现场恢复另验 | 076/139 是私有知识管线背景；新增公开持久摄取 API，不称其空壳 | planned |
| 196 | 多模态内容治理：图片摄取、媒体片段与数据外发 | 开启 Bucket 图片派生任务，查询状态/清理；将外部媒体提取结果导入受支持片段 | [语义图片检索](../semantic-image-search.md)、[媒体片段](../media-segments.md)、[Embedding 治理](../semantic-embedding-providers.md)、[派生内容](../visual-derived-content.md) | `partial`；图片 SigLIP2/USearch 本地路径与外部媒体提取分开；不自动下载模型，不承诺内置全套音视频抽取/理解 | 134/140 可复用图片主体；新增跨媒体来源、egress 与删除同步，避免旧不存在端点 | planned |
| 197 | AI 发布评测：引用支持率、Recall、成本与三条应用旅程 | 准备真实模型/语料评测，记录 usage、费用、无证据回答与失败样本 | [Copilot Eval 成本](../benchmarks/m27-copilot-eval-cost.md)、[M44 应用设计](../design/sonnetdb-45-ai-applications.md)、[工业样例](../../samples/SonnetDB.IndustrialDiagnostics/README.md) | `partial`；方法/runner 与真实效果证据分开；hash/合成 fixture 不计模型质量；工业/业务/资产旅程未闭环不写实测案例 | 054、101–110、142 为背景；新增真实质量/成本验收方案，不捏造生产收益 | planned |

## 系列十四：SDK、连接器与三面工作台（198–202）

| 编号 | 标题 | 读者可完成的任务 | 事实来源 | 能力状态与限制 | 历史底稿关联与新增角度 | 稿件状态 |
|---|---|---|---|---|---|---|
| 198 | ADO.NET 本地到远程：参数、Batch 与 REST/Frame 选择 | 复用连接字符串/参数查询，用 Protocol 选择数据面并核验返回列类型 | [ADO.NET](../ado-net.md)、[Frame 协议](../frame-protocol.md) | `partial`；七基础服务及 Graph service=8 有限 Expand；控制面 SQL 走 REST；列式切帧非执行器流式 | 080–083、086 为基础；新增当前 Protocol/参数/返回类型对照，不复写最短连接示例 | planned |
| 199 | EF Core 应用接入：Migrations、LINQ 翻译与事务矩阵 | 用 UseSonnetDB 建应用，执行 migrations/CRUD，验证支持 LINQ 及失败形状 | [EF Core](../efcore.md)、[生态样例](../../samples/SonnetDB.EcosystemSample/README.md)、[类型边界](../relation-type-boundary.md) | `partial`；EnsureCreated 仅样例/一次性使用；支持矩阵为子集；上层租户/灰度/业务回滚由应用负责 | 081、118 提到 ORM；新增 Provider 实际旅程与 migrations，不承诺全部 EF 能力 | planned |
| 200 | sndb 工具箱：Profile、REPL、Document Import 与对象传输 | 在本地/远程 profile 间切换，执行有界导入、备份和对象传输操作 | [CLI](../cli-reference.md)、[MongoDB 迁移](../mongodb-migration.md)、[对象传输](../object-transfer-manager.md) | `partial`；命令以实际包/帮助为准；不假定新版本已发布，不将本地 checkpoint 当远程任务接管 | 007/085 的旧命令先重核；新增跨模型 CLI 任务串联 | planned |
| 201 | 多语言连接器实用矩阵：C、Go、Rust、Java、Python 与 BASIC | 按 RID/ABI 选择运行时，用 SQL/Bulk/KV/Document wrapper 完成同一 smoke 并释放句柄 | [连接器总览](../../connectors/README.md)、[C](../../connectors/c/README.md)、[Java](../../connectors/java/README.md)、[Python](../../connectors/python/README.md)、[Go](../../connectors/go/README.md)、[Rust](../../connectors/rust/README.md)、[VB6](../../connectors/vb6/README.md)、[PureBasic](../../connectors/purebasic/README.md) | `partial`；C ABI 分组与各语言包装范围分别核对；BASIC 为源码/授权本地构建；存在打包工作流非已发布包证明 | 121/122/129–132 分语言稿作专题链接；新增 Python、当前能力矩阵/RID 与统一验证，不再重复七份入门 | planned |
| 202 | 一项任务三个宿主：Web Admin、Studio 与 VS Code 的真实边界 | 浏览同一数据库资源，转交 SQL/结果任务，比较凭据、文件、托管 Server 与写审批 | [管理工具](../management-tools.md)、[Web 工作台](../web-workbench.md)、[VS Code](../../extensions/sonnetdb-vscode/README.md)、[M47 设计](../design/m47-unified-management-workbench.md) | `partial`；VS Code Remote-first 子集；现有实现与 M47 新迁移分别记录；静态状态合同不等于三面/安装/发布验收 | 079/099/133/141 为背景；新增具体任务与宿主差异，不宣布 166 页签全部交付 | planned |

## 写作与发布前的有界推进

1. 先对账 [发布登记](publishing-state.json) 与博客园文章 URL/ID。143–202 仅在本选题库保留 `planned`，不由编号或文档存在自动转为 `queued`。
2. 按读者路径分批写作：先 143、147、151、154、158、161、165、169、172 的九模型入门，再处理对应查询/维护篇；随后 SQL、部署、数据流、AI、SDK/宿主系列。一次写作建议最多两篇，保存检查点，不用无界循环。
3. 每篇独立读取事实源与对应源码，记录源提交、核查日期、示例输入、适用版本/包、模型与权限前提、实际验证和未验证边界。若源码或专题变化，返回复核阶段。
4. 复用历史文章前核对账号侧是否已发布及内容差异；明显过时原文优先更正，同一读者任务不重复发布。新篇只增加当前未覆盖的工作流、入口或边界。
5. 完整正文与示例核查通过、确认未发布后，由负责发布登记的任务显式加入发布队列。失败/未知结果留存记录，不根据计划状态重发。

本轮验证仅覆盖选题数量、连续编号、状态、引用路径和 Markdown 差异；没有执行上述 60 篇中的 API/CLI 示例、构建、测试、网络请求、发布或目标机 Gate。
