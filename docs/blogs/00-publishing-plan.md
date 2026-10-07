# SonnetDB 博客文章发布计划

> 维护说明（2026-10-07）：仓库有 001–134 共 134 篇历史底稿，其中 75 篇已由博客园公开页面标题或历史正文对账确认发布，59 篇待核对；135–142 共 8 篇新稿中 6 篇已发布、2 篇排队。权威状态见 [publishing-state.json](publishing-state.json)，发布证据见 [publishing-reconciliation-2026-10-05.json](publishing-reconciliation-2026-10-05.json)；075 的链接指向历史发布稿，不代表当前本地 Provider-neutral 改写已发布。

## 当前发布规则

- 每天 Asia/Shanghai 11:00 启动一次任务，串行发布两篇；两次请求之间保留短暂间隔，避免并发请求。
- 每天最多两篇，即时发布也计入当天额度；队列不足时从 [143–202 后续系列选题](series-backlog.md) 中核实并写作最多两篇，完成事实复核、去重和 dry-run 才进入 `queued`，未写成正文的 `planned` 不发布。
- 自动任务每次只选择状态为 `queued` 的文章；`needs-reconciliation`、`needs-update`、`failed` 不会自动重发。
- 状态含义固定为：`published` 必须有博客园 URL 或 postId，`queued` 表示已复核且确认未发布，`needs-reconciliation` 表示账号侧状态未知并暂停，`needs-update` 表示事实已过时，`unknown` 表示发布响应不确定且禁止重试。
- 发布前必须对照文章列出的源码/文档事实源，确认版本、入口、支持边界和性能证据；Graph、真实模型质量、固定硬件和长期稳定性只能按 `beta`/`partial`/`not verified` 表述。
- 发布成功后记录博客园 URL、postId、发布时间、源提交和发布响应；失败保留错误与重试次数，不改变为已发布。

## 当前文章发布状态

标题在博客园 URL 已确认后直接链接到文章；“是否已发布”使用表情标记，状态清单与 [`publishing-state.json`](publishing-state.json) 保持同步。

| # | 标题（博客园链接） | 是否已发布 | 状态 | 计划发布时间 |
|---|---|---|---|---|
| 135 | [SonnetDB 当前能力全景：九种原生模型与一套数据库目录](https://www.cnblogs.com/IoTSharp/p/23202291) | ✅ 已发布 | 已发布 | 2026-10-05 12:51 |
| 136 | [SonnetDB SQL 名称大小写合同：原名、双引号与安全迁移](https://www.cnblogs.com/IoTSharp/p/23202334) | ✅ 已发布 | 已发布 | 2026-10-05 13:00 |
| 137 | [SonnetDB KV 与 JSON 文档：从 TTL 到有界查询](https://www.cnblogs.com/IoTSharp/p/23207325) | ✅ 已发布 | published | 2026-10-06 11:00 |
| 138 | [SonnetMQ 与流处理：Topic、ACK、DLQ 以及恢复边界](https://www.cnblogs.com/IoTSharp/p/23207331) | ✅ 已发布 | published | 2026-10-06 11:00 |
| 139 | [SonnetDB typed MCP、Copilot 与 RAG：只读工具如何接入 AI](https://www.cnblogs.com/IoTSharp/p/23213677) | ✅ 已发布 | published | 2026-10-07 11:00 |
| 140 | [SonnetDB 语义图片检索续篇：从能运行到可发布的证据边界](https://www.cnblogs.com/IoTSharp/p/23213681) | ✅ 已发布 | published | 2026-10-07 11:00 |
| 141 | SonnetDB 统一管理工作台：Web Admin、Studio 与 VS Code 的共同边界 | 🕒 待发布 | queued | 2026-10-08 11:00 |
| 142 | SonnetDB 性能与可靠性文章怎么写：把数字和证据放在一起 | 🕒 待发布 | queued | 2026-10-08 11:00 |

001–075 已确认历史发布，逐篇链接并标记为 ✅ 已发布；076–134 共 59 篇保留 ❓ 待核对，不会被自动任务重发。075 的公开旧稿与历史 Git 原稿正文对账一致，当前本地 Provider-neutral 改写尚未发布。证据与匹配边界见 [公开页面对账记录](publishing-reconciliation-2026-10-05.json)。

## 2026-10-05 核查结论

- 001–074 共 74 篇历史稿已通过博客园公开页面精确规范化标题匹配确认发布；075 通过公开正文与 Git 历史原稿内容对账确认，历史已发布合计 75 篇。076–134 共 59 篇未确认，继续待核对，不能据此认定未发布。075 的公开文章为历史稿，当前本地 Provider-neutral 改写未上线。公开页面还确认了 10 篇未匹配本地编号的多模型专题，见下表与 [对账证据](publishing-reconciliation-2026-10-05.json)。
- 001、007、008、049、050、051、055、071、074、076、079、085、086、089、091–100、101–110、133 等文章需要先修订或重新核验；具体原因见 [`publishing-review-2026-10-05.md`](publishing-review-2026-10-05.md)。
- 135–142 是本轮根据当前九模型、名称大小写合同、MQ/流处理、typed MCP、语义图片检索、Workbench 与可靠性边界新增的首批 8 篇文章，已经过源码/文档事实核对；135–140 已确认发布，141–142 共 2 篇保持 queued。

## 新系列总规划（以当前实现为准）

1. **产品与部署**：九模型全景、Docker/安装向导、认证与权限、备份恢复、观测与发布边界。
2. **九模型使用**：Measurement、关系表、KV、JSON 文档、全文、向量、对象、SonnetMQ、Graph Beta；每个模型分别写入门、查询、运维/边界。
3. **SQL 与执行器**：名称大小写、Schema-on-Write、JOIN/CTE、聚合与 TAG+time 分组、窗口、分页/取消/预算、RETURNING/UPSERT 合同。
4. **数据流平台**：Line Protocol/JSON/Bulk、flush/WAL/TTL、订阅窗口、DLQ、任务恢复、CDC 本地拓扑与桥接。
5. **AI 与 RAG**：Provider-neutral Copilot、知识库与技能库、持久摄取/融合检索、typed MCP、语义图片检索；真实模型质量与成本单列评测系列。
6. **连接器与三面工作台**：ADO.NET/EF Core/CLI、多语言 NativeAOT 连接器、REST/Frame、Web Admin、Studio、VS Code。
7. **性能与可靠性**：基准方法、Segment v6、崩溃恢复、查询热路径、MemTable 快照、typed window、codec 专用化；所有数字绑定版本、机器和原始报告。
8. **行业案例**：101–110 作为参考架构和示例数据；只有取得真实模型/现场证据后才写成实测案例。

首批新文章编号从 135 开始，后续每篇在状态清单中记录 `sourceDocs`、`sourceCommit`、`featureStatus` 和 `lastReviewedAt`，不再靠文件名推断发布状态。

[后续系列选题](series-backlog.md) 已细分为 14 个系列、143–202 共 60 个选题，每篇列明读者任务、事实来源、支持边界与历史稿关系；它们仍是 `planned`，不代表正文已完成或已发布。

---

## 第一阶段：产品认知（第 1-12 篇）

| # | 标题 | 内容要点 | 建议日期 | 是否已发布 |
|---|------|---------|----------| --- |
| 1 | [SonnetDB 简介：开源时序数据库的新星](https://www.cnblogs.com/IoTSharp/p/20023315) | 产品定位、核心特性、适用场景、与同类对比 | D1 | ✅ 已发布 |
| 2 | [为什么选择 SonnetDB：五大核心优势解析](https://www.cnblogs.com/IoTSharp/p/20023341) | 嵌入式优先、50+内建函数、向量检索、PID控制、AI Copilot | D3 | ✅ 已发布 |
| 3 | [Docker 快速上手：5 分钟运行 SonnetDB](https://www.cnblogs.com/IoTSharp/p/20023342) | Docker 拉取、启动、首次安装向导全流程 | D5 | ✅ 已发布 |
| 4 | [从源码编译 SonnetDB：开发环境搭建指南](https://www.cnblogs.com/IoTSharp/p/20023343) | git clone、dotnet build、VS Code/Rider 配置 | D7 | ✅ 已发布 |
| 5 | [架构深度解析：SonnetDB 的写入与查询路径](https://www.cnblogs.com/IoTSharp/p/20023344) | 写入路径、查询路径、WAL/MemTable/Segment 分层 | D9 | ✅ 已发布 |
| 6 | [性能对比：SonnetDB vs InfluxDB vs TDengine vs SQLite](https://www.cnblogs.com/IoTSharp/p/20023345) | 性能对比、功能对比、场景推荐；补充 SonnetDB Server vs IoTDB Server 同口径写入对比（1.98x，22867 vs 11541 val/s） | D11 | ✅ 已发布 |
| 7 | [CLI 工具安装与使用：sndb 命令行指南](https://www.cnblogs.com/IoTSharp/p/20023346) | `dotnet tool install`、配置文件管理、REPL 模式 | D13 | ✅ 已发布 |
| 8 | [首次设置向导：从零开始配置 SonnetDB](https://www.cnblogs.com/IoTSharp/p/20023348) | 管理员创建、Token 生成、数据库创建 | D15 | ✅ 已发布 |
| 9 | [深入理解 SonnetDB 数据模型：Measurement、Tag、Field 与 Time](https://www.cnblogs.com/IoTSharp/p/20023349) | Measurement、Tag、Field、Time、Series 概念解析 | D17 | ✅ 已发布 |
| 10 | [深入探讨：SonnetDB 的文件格式与存储布局](https://www.cnblogs.com/IoTSharp/p/20023350) | catalog/tombstones/WAL/Segment 的目录布局 | D19 | ✅ 已发布 |
| 11 | [安全机制详解：用户、角色与权限管理](https://www.cnblogs.com/IoTSharp/p/20023351) | readonly/readwrite/admin、GRANT/REVOKE | D21 | ✅ 已发布 |
| 12 | [Token 认证机制：使用 ISSUE TOKEN 保障 API 安全](https://www.cnblogs.com/IoTSharp/p/20023352) | ISSUE TOKEN、BEARER 认证、Token 吊销 | D23 | ✅ 已发布 |

## 第二阶段：SQL 基础（第 13-30 篇）

| # | 标题 | 内容要点 | 建议日期 | 是否已发布 |
|---|------|---------|----------| --- |
| 13 | [CREATE MEASUREMENT：定义您的时序数据结构](https://www.cnblogs.com/IoTSharp/p/20023354) | TAG/FIELD 类型、VECTOR/GEOPOINT 高级类型 | D25 | ✅ 已发布 |
| 14 | [HNSW 向量索引：加速向量搜索的强力引擎](https://www.cnblogs.com/IoTSharp/p/20023355) | WITH INDEX hnsw(m, ef) 语法、参数选择 | D27 | ✅ 已发布 |
| 15 | [INSERT INTO：向时序表写入数据](https://www.cnblogs.com/IoTSharp/p/20023356) | 单行/多行、time 省略、不同类型字段写入 | D29 | ✅ 已发布 |
| 16 | [GEOPOINT 地理空间数据：使用 POINT 语法写入经纬度](https://www.cnblogs.com/IoTSharp/p/20023357) | GEOPOINT 类型的数据写入方式 | D31 | ✅ 已发布 |
| 17 | [VECTOR 字面量：使用 `[v0, v1, ...]` 语法操作嵌入向量](https://www.cnblogs.com/IoTSharp/p/20023358) | VECTOR 类型的 `[v0, v1, ...]` 语法 | D33 | ✅ 已发布 |
| 18 | [SELECT 查询基础：投影、标签过滤与时间范围](https://www.cnblogs.com/IoTSharp/p/20023359) | `SELECT *`、投影、tag 过滤、时间范围 | D35 | ✅ 已发布 |
| 19 | [算术表达式：在投影列中灵活计算数据](https://www.cnblogs.com/IoTSharp/p/20023360) | 投影中直接加减乘除、一元负号 | D37 | ✅ 已发布 |
| 20 | [标量函数：abs、round、sqrt、log 与 coalesce](https://www.cnblogs.com/IoTSharp/p/20023362) | 数学函数与空值处理函数详解 | D39 | ✅ 已发布 |
| 21 | [函数嵌套调用：构建复杂的计算表达式](https://www.cnblogs.com/IoTSharp/p/20023364) | `round(abs(usage - 0.5), 2)` 模式 | D41 | ✅ 已发布 |
| 22 | [SQL 分页查询：LIMIT/OFFSET 与 FETCH 语法](https://www.cnblogs.com/IoTSharp/p/20023366) | 两种分页风格详解 | D43 | ✅ 已发布 |
| 23 | [多条件过滤：AND 连接多个 WHERE 约束](https://www.cnblogs.com/IoTSharp/p/20023379) | tag+时间范围联合过滤 | D45 | ✅ 已发布 |
| 24 | [查询元数据：SHOW 与 DESCRIBE 的使用](https://www.cnblogs.com/IoTSharp/p/20023399) | SHOW MEASUREMENTS/TABLES/USERS/GRANTS/TOKENS/DATABASES | D47 | ✅ 已发布 |
| 25 | [删除数据：SonnetDB 的 DELETE 与 Tombstone 机制](https://www.cnblogs.com/IoTSharp/p/20023400) | 时间范围删除、tag 删除、Tombstone 生命周期 | D49 | ✅ 已发布 |
| 26 | [注释语法：SonnetDB SQL 支持的四种注释方式](https://www.cnblogs.com/IoTSharp/p/20023401) | `--`、`//`、`/* */`、`REM` | D51 | ✅ 已发布 |
| 27 | [标识符引用：双引号的使用场景](https://www.cnblogs.com/IoTSharp/p/20023402) | `"column_name"` 语法 | D53 | ✅ 已发布 |
| 28 | [标量向量函数：cosine_distance / l2_distance / inner_product / vector_norm](https://www.cnblogs.com/IoTSharp/p/20023406) | 四种向量距离函数详解 | D55 | ✅ 已发布 |
| 29 | [pgvector 兼容运算符：<=> <-> <#>](https://www.cnblogs.com/IoTSharp/p/20023407) | 运算符语法与函数语法的等价性 | D57 | ✅ 已发布 |
| 30 | [SQL Cookbook：常用查询模式速查](https://www.cnblogs.com/IoTSharp/p/20023409) | 时序查询的 10 个典型场景 | D59 | ✅ 已发布 |

## 第三阶段：聚合与分析（第 31-48 篇）

| # | 标题 | 内容要点 | 建议日期 | 是否已发布 |
|---|------|---------|----------| --- |
| 31 | [基础聚合函数：count/sum/min/max/avg/first/last](https://www.cnblogs.com/IoTSharp/p/20023411) | 七种基础聚合详解与使用场景 | D61 | ✅ 已发布 |
| 32 | [统计聚合函数：stddev/variance/spread/median/mode](https://www.cnblogs.com/IoTSharp/p/20023413) | Welford 在线算法、应用场景 | D63 | ✅ 已发布 |
| 33 | [深入 T-Digest：分位数聚合与 percentile](https://www.cnblogs.com/IoTSharp/p/20023414) | p50/p90/p95/p99、tdigest_agg 内部状态 | D65 | ✅ 已发布 |
| 34 | [使用 HyperLogLog 进行基数估计：distinct_count() 函数详解](https://www.cnblogs.com/IoTSharp/p/20023418) | 基数估计原理与使用 | D67 | ✅ 已发布 |
| 35 | [使用 histogram() 进行等宽分桶分布分析](https://www.cnblogs.com/IoTSharp/p/20023419) | 等宽分桶、分布洞察 | D69 | ✅ 已发布 |
| 36 | [向量维度平均：centroid(embedding) 聚合函数详解](https://www.cnblogs.com/IoTSharp/p/20053081) | 向量均值、聚类中心计算 | D71 | ✅ 已发布 |
| 37 | [按时间段分组：GROUP BY time() 时间桶聚合](https://www.cnblogs.com/IoTSharp/p/20053102) | 桶时长单位、聚合函数配合 | D73 | ✅ 已发布 |
| 38 | [高级时间桶聚合：同一时间窗口内的多指标组合分析](https://www.cnblogs.com/IoTSharp/p/20053103) | 组合多个聚合函数的实战案例 | D75 | ✅ 已发布 |
| 39 | [行间变化检测：difference() 与 delta() 窗口函数](https://www.cnblogs.com/IoTSharp/p/20053105) | 行间差分、变化量计算 | D77 | ✅ 已发布 |
| 40 | [计数器重置感知的增长计算：increase() 函数](https://www.cnblogs.com/IoTSharp/p/20053107) | 抑制计数器回零毛刺 | D79 | ✅ 已发布 |
| 41 | [深入理解 SonnetDB 的差分类窗口函数：derivative / non_negative_derivative / rate / irate](https://www.cnblogs.com/IoTSharp/p/20053109) | 四种变化率函数详解与差异 | D81 | ✅ 已发布 |
| 42 | [SonnetDB 累计与积分函数：cumulative_sum() 运行总和与 integral() 梯形面积](https://www.cnblogs.com/IoTSharp/p/20053119) | 累积求和与梯形积分 | D83 | ✅ 已发布 |
| 43 | [SonnetDB 平滑函数解析：moving_average() 与 ewma() 降噪技术](https://www.cnblogs.com/IoTSharp/p/20053127) | 移动平均与指数加权移动平均 | D85 | ✅ 已发布 |
| 44 | [SonnetDB 的 Holt-Winters 平滑：holt_winters() 双指数平滑与趋势提取](https://www.cnblogs.com/IoTSharp/p/20053129) | Holt 加法模型、趋势平滑 | D87 | ✅ 已发布 |
| 45 | [SonnetDB 缺失值处理三剑客：fill() / locf() / interpolate()](https://www.cnblogs.com/IoTSharp/p/20053131) | 常量填充、前向填充、线性插值 | D89 | ✅ 已发布 |
| 46 | [SonnetDB 状态分析函数：state_changes() 变化检测与 state_duration() 持续时间](https://www.cnblogs.com/IoTSharp/p/20053136) | 状态变化检测、持续时长计算 | D91 | ✅ 已发布 |
| 47 | [SonnetDB 窗口函数流水线：从差值计算到异常检测的完整监控链路](https://www.cnblogs.com/IoTSharp/p/20053137) | 差分→异常检测→告警的工作流 | D93 | ✅ 已发布 |
| 48 | [SonnetDB INSERT 批处理性能优化：批量写入与 flush 策略](https://www.cnblogs.com/IoTSharp/p/20053140) | 多行 VALUES、flush 模式选择 | D95 | ✅ 已发布 |

## 第四阶段：高级功能（第 49-73 篇）

| # | 标题 | 内容要点 | 建议日期 | 是否已发布 |
|---|------|---------|----------| --- |
| 49 | [SonnetDB KNN 向量搜索入门：knn() TVF 语法与距离度量选择](https://www.cnblogs.com/IoTSharp/p/20053145) | knn() 语法、距离度量、结果解读 | D97 | ✅ 已发布 |
| 50 | [SonnetDB 语义搜索实战：knn() 与标签过滤和时间范围的联合查询](https://www.cnblogs.com/IoTSharp/p/20053147) | 带 tag 过滤、时间范围过滤 | D99 | ✅ 已发布 |
| 51 | [SonnetDB HNSW 索引架构：m/ef 参数调优与 .SDBVIDX 文件格式](https://www.cnblogs.com/IoTSharp/p/20053148) | ANN 原理、m/ef 参数调优、Recall 基准 | D101 | ✅ 已发布 |
| 52 | [暴力搜索 vs HNSW 索引：精度与延迟的权衡](https://www.cnblogs.com/IoTSharp/p/20053150) | 精确率 vs 延迟的权衡 | D103 | ✅ 已发布 |
| 53 | [向量距离度量深度对比：Cosine vs L2 vs Inner Product](https://www.cnblogs.com/IoTSharp/p/20053151) | 不同度量的数学含义与场景 | D105 | ✅ 已发布 |
| 54 | [向量召回率基准测试：Recall@10 评估与 HNSW 参数影响](https://www.cnblogs.com/IoTSharp/p/20053152) | 精度衡量、HNSW 参数影响 | D107 | ✅ 已发布 |
| 55 | [从 pgvector 迁移到 SonnetDB：运算符兼容性与 SQL 差异](https://www.cnblogs.com/IoTSharp/p/20053154) | 运算符兼容性、SQL 语法差异 | D109 | ✅ 已发布 |
| 56 | [GEOPOINT 类型入门：地理空间数据存储与查询](https://www.cnblogs.com/IoTSharp/p/20053155) | 创建含 GEOPOINT 的表、写入数据 | D111 | ✅ 已发布 |
| 57 | [坐标分量提取：lat() 和 lon() 函数详解](https://www.cnblogs.com/IoTSharp/p/20053157) | 从 GEOPOINT 中分离经纬度 | D113 | ✅ 已发布 |
| 58 | [地理距离与方位角计算：geo_distance 与 geo_bearing](https://www.cnblogs.com/IoTSharp/p/20053160) | Haversine 算法、距离/方向计算 | D115 | ✅ 已发布 |
| 59 | [地理空间过滤：geo_within 圆形查询与 geo_bbox 矩形查询](https://www.cnblogs.com/IoTSharp/p/20053161) | 圆形半径过滤与矩形过滤 | D117 | ✅ 已发布 |
| 60 | [PostGIS 兼容函数：ST_Distance / ST_Within / ST_DWithin](https://www.cnblogs.com/IoTSharp/p/20053165) | 从 PostGIS 迁移到 SonnetDB | D119 | ✅ 已发布 |
| 61 | [地理空间速度计算：geo_speed() 函数详解](https://www.cnblogs.com/IoTSharp/p/20053173) | 利用相邻点经纬度+时间差算速度 | D121 | ✅ 已发布 |
| 62 | [轨迹聚合分析：全面洞察移动数据](https://www.cnblogs.com/IoTSharp/p/20053174) | trajectory_length/centroid/bbox/speed | D123 | ✅ 已发布 |
| 63 | [PID 控制算法入门：pid_series() 流式计算](https://www.cnblogs.com/IoTSharp/p/20053178) | PID 控制原理、在 SQL 中使用 | D125 | ✅ 已发布 |
| 64 | [PID 聚合函数：pid() 与 GROUP BY 时间窗口](https://www.cnblogs.com/IoTSharp/p/20053179) | GROUP BY time + PID 的工业场景 | D127 | ✅ 已发布 |
| 65 | [PID 参数整定估计：三种经典方法](https://www.cnblogs.com/IoTSharp/p/20053182) | IMC/Ziegler-Nichols/Cohen-Coon 对比 | D129 | ✅ 已发布 |
| 66 | [PID 调参实战：阶跃响应分析与参数解读](https://www.cnblogs.com/IoTSharp/p/20053185) | 阶跃信号分析、整定结果解读 | D131 | ✅ 已发布 |
| 67 | [工业 IoT 场景：SonnetDB PID 与 PLC 的对比优势](https://www.cnblogs.com/IoTSharp/p/20053186) | 传统 PLC vs 时序数据库 PID | D133 | ✅ 已发布 |
| 68 | [PID 参数调节完全指南：Kp/Ki/Kd 的作用与整定技巧](https://www.cnblogs.com/IoTSharp/p/20053189) | 各参数作用、调参口诀、常见问题 | D135 | ✅ 已发布 |
| 69 | [时序预测函数：forecast() 线性与 Holt-Winters 方法](https://www.cnblogs.com/IoTSharp/p/20053191) | 线性预测与 Holt-Winters 算法 | D137 | ✅ 已发布 |
| 70 | [异常检测方法对比：Z-Score、MAD 与 IQR](https://www.cnblogs.com/IoTSharp/p/20053193) | Z-Score/MAD/IQR 对比 | D139 | ✅ 已发布 |
| 71 | [实时异常检测监控：窗口函数与告警流水线](https://www.cnblogs.com/IoTSharp/p/20053195) | 实时异常检测管道、阈值选择 | D141 | ✅ 已发布 |
| 72 | [结构变化检测：CUSUM 与 Changepoint 分析](https://www.cnblogs.com/IoTSharp/p/20053197) | 变点检测原理、参数调优 | D143 | ✅ 已发布 |
| 73 | [预测、异常检测与变化点分析：组合工作流](https://www.cnblogs.com/IoTSharp/p/20053200) | 三者联合使用的实战案例 | D145 | ✅ 已发布 |

## 第五阶段：AI 与生态（第 74-100 篇）

| # | 标题 | 内容要点 | 建议日期 | 是否已发布 |
|---|------|---------|----------| --- |
| 74 | [SonnetDB Copilot AI 架构解析：Agent Orchestrator + Knowledge Base + Skills + MCP Tools](https://www.cnblogs.com/IoTSharp/p/20053202) | Copilot 能做什么、架构概述 | D147 | ✅ 已发布 |
| 75 | [SonnetDB Copilot AI 供应商配置：OpenAI / DashScope / ZhiPu / Moonshot / DeepSeek](https://www.cnblogs.com/IoTSharp/p/20053203) | OpenAI/DashScope/ZhiPu/Moonshot/DeepSeek 配置 | D149 | ✅ 已发布（历史稿） |
| 76 | Copilot 知识库：自动文档索引与检索 | 知识库构建、文档摄入管道 | D151 | ❓ 待核对 |
| 77 | Copilot 技能库：6 个内置技能详解 | 查询聚合/PID调优/预测/慢查询排查/Schema设计/批量写入 | D153 | ❓ 待核对 |
| 78 | 多轮对话：与 Copilot 协作编写 SQL | 对话上下文、SQL 自纠正机制 | D155 | ❓ 待核对 |
| 79 | CopilotDock：Web 界面中的浮动 AI 面板 | 页面感知、读写模式、模型选择、会话历史 | D157 | ❓ 待核对 |
| 80 | 嵌入式集成：在 C# 应用中使用 SonnetDB | Tsdb.Open、SqlExecutor、进程内数据库 | D159 | ❓ 待核对 |
| 81 | ADO.NET 入门：使用 SndbConnection 连接 SonnetDB | 连接字符串、执行查询、DataReader | D161 | ❓ 待核对 |
| 82 | ADO.NET 远程连接：通过 HTTP 访问远程 SonnetDB | sonnetdb+http:// 协议、Token 认证 | D163 | ❓ 待核对 |
| 83 | ADO.NET 批量写入：使用 TableDirect 命令类型 | 高性能批量导入 | D165 | ❓ 待核对 |
| 84 | 用户自定义函数：扩展 SonnetDB 的功能 | RegisterScalar/RegisterAggregate/RegisterWindow | D167 | ❓ 待核对 |
| 85 | CLI 进阶：使用 sndb 命令行管理数据库 | REPL 交互模式、配置文件管理、远程执行 | D169 | ❓ 待核对 |
| 86 | HTTP API 完整参考：所有 REST 端点详解 | 数据面、控制面、MCP 端点全集 | D171 | ❓ 待核对 |
| 87 | 三种批量写入格式对比：LP vs JSON vs Bulk | 格式差异、性能对比、场景选择 | D173 | ❓ 待核对 |
| 88 | Flush 模式选择：同步、异步与不 flush | 写入性能 vs 数据持久化的权衡 | D175 | ❓ 待核对 |
| 89 | 使用 Line Protocol 高效写入 | InfluxDB 兼容格式、批量导入 | D177 | ❓ 待核对 |
| 90 | 生产部署：Docker Compose 与安装器使用 | docker-compose.yml、MSI/DEB/RPM | D179 | ❓ 待核对 |
| 91 | 性能揭秘：SonnetDB 如何实现 180 万点/秒写入 | WriteMany 批处理、零拷贝路径 | D181 | ❓ 待核对 |
| 92 | 5 款时序数据库性能大对决 | SonnetDB vs SQLite vs InfluxDB vs TDengine 全面基准 | D183 | ❓ 待核对 |
| 93 | 范围查询为何如此之快？ | Segment 跳过、Block 元数据、向量化 | D185 | ❓ 待核对 |
| 94 | 聚合查询性能优化指南 | 跨桶融合、MemTable 增量聚合 | D187 | ❓ 待核对 |
| 95 | 基准测试方法论：如何正确地比较时序数据库 | BenchmarkDotNet、统一数据、多次运行；强调嵌入式与服务端链路必须同口径比较，实测 1.98x 案例 | D189 | ❓ 待核对 |
| 96 | Segment 压缩：Size-Tiered 策略详解 | 压缩触发条件、合并策略、IO 优化 | D191 | ❓ 待核对 |
| 97 | 数据保留策略：TTL 自动过期删除 | Retention Worker、自动注入 Tombstone | D193 | ❓ 待核对 |
| 98 | MCP 协议集成：将 SonnetDB 接入任意 AI 应用 | MCP 工具列表、权限模型 | D195 | ❓ 待核对 |
| 99 | SonnetDB for VS Code：数据库管理的未来（预览） | 扩展功能预览、开发路线图 | D197 | ❓ 待核对 |
| 100 | SonnetDB 路线图：下一个版本的新功能预览 | M17 Observability、M18 VS Code 扩展、社区贡献指南 | D199 | ❓ 待核对 |

## 第六阶段：行业应用案例（第 101-110 篇）

> 以真实场景为背景，讲述 SonnetDB 在各行业的落地实践，每篇包含：背景、挑战、解决方案、关键 SQL 示例、实施效果。

| # | 标题 | 行业/场景 | 建议日期 | 是否已发布 |
| --- | ------ | --------- | ---------- | --- |
| 101 | 案例：IoT 平台如何用 SonnetDB 管理百万设备实时数据 | IoT 平台/边缘计算 | D201 | ❓ 待核对 |
| 102 | 案例：智能制造——汽车总装线传感器数据采集与 PID 控制 | 工业制造 | D203 | ❓ 待核对 |
| 103 | 案例：光伏电站运维——基于 SonnetDB 的发电量异常检测 | 新能源/能源监控 | D205 | ❓ 待核对 |
| 104 | 案例：楼宇自动化——SonnetDB 助力智慧园区能耗管理 | 楼宇/智慧城市 | D207 | ❓ 待核对 |
| 105 | 案例：冷链物流——全程温湿度监控与超标告警系统 | 物流/冷链 | D209 | ❓ 待核对 |
| 106 | 案例：城市交通监控——路口车流量时序分析与拥堵预测 | 智慧交通 | D211 | ❓ 待核对 |
| 107 | 案例：数据中心——服务器集群的指标采集与容量预测 | 运维监控/可观测性 | D213 | ❓ 待核对 |
| 108 | 案例：农业 IoT——温室大棚环境数据采集与智能灌溉 | 智慧农业 | D215 | ❓ 待核对 |
| 109 | 案例：设备预测性维护——振动信号分析与故障预警 | 工业维护 | D217 | ❓ 待核对 |
| 110 | 案例：用 SonnetDB + AI Copilot 构建无代码数据分析平台 | AI 应用/SaaS | D219 | ❓ 待核对 |

## 第七阶段：基准测试与新特性历史选题（第 111-117 篇，发布前需重核）

| # | 标题 | 内容要点 | 建议日期 | 是否已发布 |
|---|------|---------|----------| --- |
| 111 | 基准测试：SonnetDB 插入性能深度报告 | 单点/批量插入吞吐量、不同数据类型对比；包含 SonnetDB Server vs IoTDB Server 同口径对比（AB BA ×4，平均 1.98x，CLI: --comparison-server） | D221 | ❓ 待核对 |
| 112 | 基准测试：SonnetDB 查询性能深度报告 | 范围查询、过滤查询延迟对比 | D223 | ❓ 待核对 |
| 113 | 基准测试：SonnetDB 聚合性能深度报告 | 常见聚合函数性能、跨桶聚合效率 | D225 | ❓ 待核对 |
| 114 | 基准测试：SonnetDB 地理空间性能深度报告 | GEOPOINT 类型读写性能、空间查询延迟 | D227 | ❓ 待核对 |
| 115 | 基准测试：SonnetDB PID 控制性能深度报告 | PID 计算吞吐量、实时控制场景模拟 | D229 | ❓ 待核对 |
| 116 | 基准测试：SonnetDB 向量搜索性能深度报告 | HNSW 索引构建、knn() 查询延迟、召回率 | D231 | ❓ 待核对 |
| 117 | Schema-on-Write：可控的字段自动创建 | 自动 Tag/Field 创建、宽松 vs 严格模式、生产环境最佳实践 | D233 | ❓ 待核对 |

## 第八阶段：SQL 兼容性增强（第 118-120 篇）

| # | 标题 | 内容要点 | 建议日期 | 是否已发布 |
|---|------|---------|----------| --- |
| 118 | SQL 兼容性基础：SELECT 1 与 count(1) 支持 | 字面量投影、count(1) 与 count(*) 等价、ORM 兼容 | D235 | ❓ 待核对 |
| 119 | ORDER BY 排序支持：让时序查询井然有序 | ORDER BY time ASC/DESC 语法、解析器与执行器实现 | D237 | ❓ 待核对 |
| 120 | 单表别名与 DDL 修饰符：写出更地道的 SQL | alias.column 限定列名、AS/无 AS 语法、NOT NULL/DEFAULT 框架 | D239 | ❓ 待核对 |

## 第九阶段：多语言连接器（第 121-122 篇）

| # | 标题 | 内容要点 | 建议日期 | 是否已发布 |
|---|------|---------|----------| --- |
| 121 | SonnetDB C 连接器：嵌入式时序数据库的原生接入 | C ABI 设计、17 个导出函数、构建与平台支持 | D241 | ❓ 待核对 |
| 122 | SonnetDB Java 连接器：一套 API 双后端驱动 | JNI vs FFM 架构对比、多版本 JAR、Java 8/21 自适应 | D243 | ❓ 待核对 |

## 第十阶段：性能与可靠性工程化（第 123-128 篇）

| # | 标题 | 内容要点 | 建议日期 | 是否已发布 |
|---|------|---------|----------| --- |
| 123 | Segment v6 与崩溃恢复：把可靠性做到文件尾部 | v6 extension section、mini-footer、v4/v5 兼容读取 | D245 | ❓ 待核对 |
| 124 | 查询热路径优化：索引、缓存与少一点 LINQ | SegmentReader series/time index、reader map 缓存、tombstone 手写过滤、block 解码缓存 | D247 | ❓ 待核对 |
| 125 | MemTable 优化：从热路径统计到快照并发 | 增量 EstimatedBytes、字符串 byte count、snapshot 缓存、range 二分裁剪 | D249 | ❓ 待核对 |
| 126 | 窗口函数执行器：从 object 数组走向 typed streaming | typed evaluator、IWindowState、Span 批量路径、窗口函数降分配 | D251 | ❓ 待核对 |
| 127 | 读多写少结构治理：FrozenDictionary、Lexer 快路径与 Analyzer | catalog/tag index 冻结快照、options record、SearchValues lexer、性能 analyzer | D253 | ❓ 待核对 |
| 128 | Codec 专用化：BlockDecoder 为什么选择手写 fast path | source generator/泛型/手写方案评估、DecodeInto、BenchmarkDotNet 对比 | D255 | ❓ 待核对 |

## 第十一阶段：连接器与管理检索历史稿（第 129-134 篇，发布前需重核）

| # | 标题 | 内容要点 | 建议日期 | 是否已发布 |
|---|---|---|---|---|
| 129 | SonnetDB Go 连接器：cgo 与 database/sql 的双入口 | 原生 cgo 入口、database/sql 适配、构建与平台边界 | 待对账后重排 | ❓ 待核对 |
| 130 | SonnetDB Rust 连接器：手写 FFI 与安全封装 | Native FFI、安全封装、资源释放与构建边界 | 待对账后重排 | ❓ 待核对 |
| 131 | SonnetDB Visual Basic 6 连接器：让经典 Windows 应用接入时序数据 | 经典 Windows 应用、Native ABI、部署边界 | 待对账后重排 | ❓ 待核对 |
| 132 | SonnetDB PureBasic 连接器：用 Include 文件动态加载 Native 引擎 | 动态加载、Include 文件、平台与打包边界 | 待对账后重排 | ❓ 待核对 |
| 133 | SonnetDB Workbench 新版发布：轨迹地图、国内瓦片切换与坐标系转换 | 轨迹工作台、地图瓦片与坐标转换；按当前实现重核 | 待对账后重排 | ❓ 待核对 |
| 134 | SonnetDB 新增语义图片检索：用 SigLIP2 + USearch 实现文搜图与图搜图 | 显式 Profile、文搜图/图搜图、真实模型质量证据 | 待对账后重排 | ❓ 待核对 |

## 已发布的额外多模型专题（未匹配本地编号）

以下 10 篇已由博客园公开页面确认存在，未与仓库编号稿精确匹配；证据 JSON 未记录其发布时间，因此本表不推定日期，也不自动复用本地编号。

| 博客园标题 | 是否已发布 | 本地编号 |
|---|---|---|
| [SonnetDB 多模型能力更新：向量、全文搜索、S3 对象桶与消息队列场景总览](https://www.cnblogs.com/IoTSharp/p/20524618) | ✅ 已发布 | 未匹配 |
| [用 C# 在 SonnetDB 中写入 VECTOR 并执行 KNN 检索](https://www.cnblogs.com/IoTSharp/p/20524619) | ✅ 已发布 | 未匹配 |
| [SonnetDB 向量索引声明：HNSW、IVF、IVF-PQ 与 Vamana 的 C# 用法](https://www.cnblogs.com/IoTSharp/p/20524621) | ✅ 已发布 | 未匹配 |
| [SonnetDB 全文搜索入门：CREATE FULLTEXT INDEX、match 与 BM25](https://www.cnblogs.com/IoTSharp/p/20524622) | ✅ 已发布 | 未匹配 |
| [SonnetDB Hybrid Search：把全文 BM25 与向量相似度融合起来](https://www.cnblogs.com/IoTSharp/p/20524623) | ✅ 已发布 | 未匹配 |
| [从设备向量到知识文档：SonnetDB Measurement KNN 与文档关联检索](https://www.cnblogs.com/IoTSharp/p/20524624) | ✅ 已发布 | 未匹配 |
| [SonnetDB S3 对象桶：用 C# 上传、下载、Range Read 与删除对象](https://www.cnblogs.com/IoTSharp/p/20524626) | ✅ 已发布 | 未匹配 |
| [SonnetDB S3 进阶：Multipart、预签名 URL、版本、生命周期与审计](https://www.cnblogs.com/IoTSharp/p/20524627) | ✅ 已发布 | 未匹配 |
| [用 SonnetDB 观测消息队列：积压、延迟、死信与消费审计](https://www.cnblogs.com/IoTSharp/p/20524629) | ✅ 已发布 | 未匹配 |
| [IoTSharp + SonnetDB 多模型 Profile：关系、时序、缓存、对象桶与搜索怎么组合](https://www.cnblogs.com/IoTSharp/p/20524630) | ✅ 已发布 | 未匹配 |
