# SonnetDB 开源中国 发布进度

更新时间：2026-10-08T04:12:08.7326315+00:00

权威状态为 [publishing-state.json](publishing-state.json)，逐次回执见 [publishing-events.jsonl](publishing-events.jsonl)。已有 ID 不重复创建，unknown/publishing 先只读对账。submitted 是接口接收，不自动证明公开审核通过。动弹与博客独立记录。
队列后续计划：每天 11:00，最多 2 篇；时区 Asia/Shanghai。

| 编号 | 标题 | 博客 | 动弹 |
| --- | --- | --- | --- |
| 001 | SonnetDB 简介：开源时序数据库的新星 | [submitted](https://my.oschina.net/u/7172/blog/19773758) | [submitted](https://www.oschina.net/osc-tweet/30401376) |
| 002 | 为什么选择 SonnetDB：五大核心优势解析 | [submitted](https://my.oschina.net/u/7172/blog/19773759) | [submitted](https://www.oschina.net/osc-tweet/30401377) |
| 003 | Docker 快速上手：5 分钟运行 SonnetDB | [submitted](https://my.oschina.net/u/7172/blog/19773766) | [submitted](https://www.oschina.net/osc-tweet/30401378) |
| 004 | 从源码编译 SonnetDB：开发环境搭建指南 | [submitted](https://my.oschina.net/u/7172/blog/19773773) | [submitted](https://www.oschina.net/osc-tweet/30401379) |
| 005 | 架构深度解析：SonnetDB 的写入与查询路径 | [submitted](https://my.oschina.net/u/7172/blog/19773777) | [submitted](https://www.oschina.net/osc-tweet/30401382) |
| 006 | 性能对比：SonnetDB vs InfluxDB vs TDengine vs SQLite | [submitted](https://my.oschina.net/u/7172/blog/19773778) | [submitted](https://www.oschina.net/osc-tweet/30401383) |
| 007 | CLI 工具安装与使用：sndb 命令行指南 | [submitted](https://my.oschina.net/u/7172/blog/19773779) | [submitted](https://www.oschina.net/osc-tweet/30401384) |
| 008 | 首次设置向导：从零开始配置 SonnetDB | [submitted](https://my.oschina.net/u/7172/blog/19773780) | [submitted](https://www.oschina.net/osc-tweet/30401385) |
| 009 | 深入理解 SonnetDB 数据模型：Measurement、Tag、Field 与 Time | [submitted](https://my.oschina.net/u/7172/blog/19773781) | [submitted](https://www.oschina.net/osc-tweet/30401386) |
| 010 | 深入探讨：SonnetDB 的文件格式与存储布局 | [submitted](https://my.oschina.net/u/7172/blog/19773782) | [submitted](https://www.oschina.net/osc-tweet/30401387) |
| 011 | 安全机制详解：用户、角色与权限管理 | [submitted](https://my.oschina.net/u/7172/blog/19773785) | [submitted](https://www.oschina.net/osc-tweet/30401388) |
| 012 | Token 认证机制：使用 ISSUE TOKEN 保障 API 安全 | [submitted](https://my.oschina.net/u/7172/blog/19773787) | [submitted](https://www.oschina.net/osc-tweet/30401389) |
| 013 | CREATE MEASUREMENT：定义您的时序数据结构 | [submitted](https://my.oschina.net/u/7172/blog/19773788) | [submitted](https://www.oschina.net/osc-tweet/30401390) |
| 014 | HNSW 向量索引：加速向量搜索的强力引擎 | [submitted](https://my.oschina.net/u/7172/blog/19773789) | [submitted](https://www.oschina.net/osc-tweet/30401391) |
| 015 | INSERT INTO：向时序表写入数据 | [submitted](https://my.oschina.net/u/7172/blog/19773790) | [submitted](https://www.oschina.net/osc-tweet/30401392) |
| 016 | GEOPOINT 地理空间数据：使用 POINT 语法写入经纬度 | [submitted](https://my.oschina.net/u/7172/blog/19773791) | [submitted](https://www.oschina.net/osc-tweet/30401393) |
| 017 | VECTOR 字面量：使用 `[v0, v1, ...]` 语法操作嵌入向量 | [submitted](https://my.oschina.net/u/7172/blog/19773792) | [submitted](https://www.oschina.net/osc-tweet/30401394) |
| 018 | SELECT 查询基础：投影、标签过滤与时间范围 | [submitted](https://my.oschina.net/u/7172/blog/19773793) | [submitted](https://www.oschina.net/osc-tweet/30401395) |
| 019 | 算术表达式：在投影列中灵活计算数据 | [submitted](https://my.oschina.net/u/7172/blog/19773794) | [submitted](https://www.oschina.net/osc-tweet/30401396) |
| 020 | 标量函数：abs、round、sqrt、log 与 coalesce | [submitted](https://my.oschina.net/u/7172/blog/19773795) | [submitted](https://www.oschina.net/osc-tweet/30401397) |
| 021 | 函数嵌套调用：构建复杂的计算表达式 | [submitted](https://my.oschina.net/u/7172/blog/19773796) | [submitted](https://www.oschina.net/osc-tweet/30401398) |
| 022 | SQL 分页查询：LIMIT/OFFSET 与 FETCH 语法 | [submitted](https://my.oschina.net/u/7172/blog/19773797) | [submitted](https://www.oschina.net/osc-tweet/30401399) |
| 023 | 多条件过滤：AND 连接多个 WHERE 约束 | [submitted](https://my.oschina.net/u/7172/blog/19773798) | [submitted](https://www.oschina.net/osc-tweet/30401400) |
| 024 | 查询元数据：SHOW 与 DESCRIBE 的使用 | [submitted](https://my.oschina.net/u/7172/blog/19773802) | [submitted](https://www.oschina.net/osc-tweet/30401401) |
| 025 | 删除数据：SonnetDB 的 DELETE 与 Tombstone 机制 | [submitted](https://my.oschina.net/u/7172/blog/19773803) | [submitted](https://www.oschina.net/osc-tweet/30401402) |
| 026 | 注释语法：SonnetDB SQL 支持的四种注释方式 | [submitted](https://my.oschina.net/u/7172/blog/19773804) | [submitted](https://www.oschina.net/osc-tweet/30401403) |
| 027 | 标识符引用：双引号的使用场景 | [submitted](https://my.oschina.net/u/7172/blog/19773805) | [submitted](https://www.oschina.net/osc-tweet/30401404) |
| 028 | 标量向量函数：cosine_distance / l2_distance / inner_product / vector_norm | [submitted](https://my.oschina.net/u/7172/blog/19773806) | [submitted](https://www.oschina.net/osc-tweet/30401405) |
| 029 | pgvector 兼容运算符：<=> <-> <#> | [submitted](https://my.oschina.net/u/7172/blog/19773807) | [submitted](https://www.oschina.net/osc-tweet/30401406) |
| 030 | SQL Cookbook：常用查询模式速查 | [submitted](https://my.oschina.net/u/7172/blog/19774317) | [submitted-verification-pending](https://www.oschina.net/osc-tweet/30401409) |
| 031 | 基础聚合函数：count/sum/min/max/avg/first/last | [submitted](https://my.oschina.net/u/7172/blog/19774318) | [submitted](https://www.oschina.net/osc-tweet/30401410) |
| 032 | 统计聚合函数：stddev/variance/spread/median/mode | queued | 未发送 |
| 033 | 深入 T-Digest：分位数聚合与 percentile | queued | 未发送 |
| 034 | 使用 HyperLogLog 进行基数估计：distinct_count() 函数详解 | queued | 未发送 |
| 035 | 使用 histogram() 进行等宽分桶分布分析 | queued | 未发送 |
| 036 | 向量维度平均：centroid(embedding) 聚合函数详解 | queued | 未发送 |
| 037 | 按时间段分组：GROUP BY time() 时间桶聚合 | queued | 未发送 |
| 038 | 高级时间桶聚合：同一时间窗口内的多指标组合分析 | queued | 未发送 |
| 039 | 行间变化检测：difference() 与 delta() 窗口函数 | queued | 未发送 |
| 040 | 计数器重置感知的增长计算：increase() 函数 | queued | 未发送 |
| 041 | 深入理解 SonnetDB 的差分类窗口函数：derivative / non_negative_derivative / rate / irate | queued | 未发送 |
| 042 | SonnetDB 累计与积分函数：cumulative_sum() 运行总和与 integral() 梯形面积 | queued | 未发送 |
| 043 | SonnetDB 平滑函数解析：moving_average() 与 ewma() 降噪技术 | queued | 未发送 |
| 044 | SonnetDB 的 Holt-Winters 平滑：holt_winters() 双指数平滑与趋势提取 | queued | 未发送 |
| 045 | SonnetDB 缺失值处理三剑客：fill() / locf() / interpolate() | queued | 未发送 |
| 046 | SonnetDB 状态分析函数：state_changes() 变化检测与 state_duration() 持续时间 | queued | 未发送 |
| 047 | SonnetDB 窗口函数流水线：从差值计算到异常检测的完整监控链路 | queued | 未发送 |
| 048 | SonnetDB INSERT 批处理性能优化：批量写入与 flush 策略 | queued | 未发送 |
| 049 | SonnetDB KNN 向量搜索入门：knn() TVF 语法与距离度量选择 | queued | 未发送 |
| 050 | SonnetDB 语义搜索实战：knn() 与标签过滤和时间范围的联合查询 | queued | 未发送 |
| 051 | SonnetDB HNSW 索引架构：m/ef 参数调优与 .SDBVIDX 文件格式 | queued | 未发送 |
| 052 | 暴力搜索 vs HNSW 索引：精度与延迟的权衡 | queued | 未发送 |
| 053 | 向量距离度量深度对比：Cosine vs L2 vs Inner Product | queued | 未发送 |
| 054 | 向量召回率基准测试：Recall@10 评估与 HNSW 参数影响 | queued | 未发送 |
| 055 | 从 pgvector 迁移到 SonnetDB：运算符兼容性与 SQL 差异 | queued | 未发送 |
| 056 | GEOPOINT 类型入门：地理空间数据存储与查询 | queued | 未发送 |
| 057 | 坐标分量提取：lat() 和 lon() 函数详解 | queued | 未发送 |
| 058 | 地理距离与方位角计算：geo_distance 与 geo_bearing | queued | 未发送 |
| 059 | 地理空间过滤：geo_within 圆形查询与 geo_bbox 矩形查询 | queued | 未发送 |
| 060 | PostGIS 兼容函数：ST_Distance / ST_Within / ST_DWithin | queued | 未发送 |
| 061 | 地理空间速度计算：geo_speed() 函数详解 | queued | 未发送 |
| 062 | 轨迹聚合分析：全面洞察移动数据 | queued | 未发送 |
| 063 | PID 控制算法入门：pid_series() 流式计算 | queued | 未发送 |
| 064 | PID 聚合函数：pid() 与 GROUP BY 时间窗口 | queued | 未发送 |
| 065 | PID 参数整定估计：三种经典方法 | queued | 未发送 |
| 066 | PID 调参实战：阶跃响应分析与参数解读 | queued | 未发送 |
| 067 | 工业 IoT 场景：SonnetDB PID 与 PLC 的对比优势 | queued | 未发送 |
| 068 | PID 参数调节完全指南：Kp/Ki/Kd 的作用与整定技巧 | queued | 未发送 |
| 069 | 时序预测函数：forecast() 线性与 Holt-Winters 方法 | queued | 未发送 |
| 070 | 异常检测方法对比：Z-Score、MAD 与 IQR | queued | 未发送 |
| 071 | 实时异常检测监控：窗口函数与告警流水线 | queued | 未发送 |
| 072 | 结构变化检测：CUSUM 与 Changepoint 分析 | queued | 未发送 |
| 073 | 预测、异常检测与变化点分析：组合工作流 | queued | 未发送 |
| 074 | SonnetDB Copilot AI 架构解析：Agent Orchestrator + Knowledge Base + Skills + MCP Tools | queued | 未发送 |
| 075 | SonnetDB Copilot Provider-neutral 配置 | queued | 未发送 |
| 076 | 自动文档摄入管道：Chunking、Embedding 与向量检索 | queued | 未发送 |
| 077 | 六大内置技能：从聚合查询到批量导入的专家知识 | queued | 未发送 |
| 078 | 多轮对话与 SQL 自修复：可达 3 次重试的智能 Agent | queued | 未发送 |
| 079 | CopilotDock 全局浮动面板：拖拽、全屏、页面感知与 50 条历史会话 | queued | 未发送 |
| 080 | 嵌入式 C# API：Tsdb.Open() 与 SqlExecutor.Execute() 进程内数据库编程 | queued | 未发送 |
| 081 | ADO.NET 基础：使用 SndbConnection、SndbCommand 和 SndbDataReader 连接本地 SonnetDB | queued | 未发送 |
| 082 | ADO.NET 远程连接：使用 sonnetdb+http:// 协议连接远程 SonnetDB 服务 | queued | 未发送 |
| 083 | ADO.NET 批量写入：使用 CommandType.TableDirect 实现高速数据摄入 | queued | 未发送 |
| 084 | 用户自定义函数：使用 RegisterScalar、RegisterAggregate 等扩展 SonnetDB 分析能力 | queued | 未发送 |
| 085 | CLI 高级技巧：配置文件管理、REPL 高效操作与跨平台使用 | queued | 未发送 |
| 086 | HTTP API 参考：SonnetDB 全部 REST 端点详解 | queued | 未发送 |
| 087 | 批量摄入格式对比：Line Protocol vs JSON vs SQL VALUES | queued | 未发送 |
| 088 | Flush 模式深入解析：?flush=false\|true\|async 的持久性与延迟权衡 | queued | 未发送 |
| 089 | SonnetDB 行协议写入：兼容 InfluxDB Line Protocol 的大批量数据接入 | queued | 未发送 |
| 090 | SonnetDB 生产部署指南：Docker Compose、安装包与环境变量 | queued | 未发送 |
| 091 | SonnetDB 写入性能实录：545 ms 写入 100 万点，吞吐 1.83 M pts/s | queued | 未发送 |
| 092 | 多数据库横向对比：SonnetDB vs SQLite vs InfluxDB vs TDengine | queued | 未发送 |
| 093 | SonnetDB 范围查询速度揭秘：100k 行仅需 6.71 ms | queued | 未发送 |
| 094 | SonnetDB 聚合查询优化：跨桶融合与 MemTable 增量聚合 | queued | 未发送 |
| 095 | SonnetDB 基准测试方法论：BenchmarkDotNet、统一数据与多 DB 对比 | queued | 未发送 |
| 096 | SonnetDB 段合并机制：基于大小分层的 Compaction 策略 | queued | 未发送 |
| 097 | SonnetDB 数据保留策略：RetentionWorker、自动 Tombstone 与过期段清理 | queued | 未发送 |
| 098 | SonnetDB MCP 协议支持：以 Model Context Protocol 赋能 AI 驱动数据库管理 | queued | 未发送 |
| 099 | SonnetDB VS Code 扩展预览：连接管理、SQL 编辑器与结果可视化 | queued | 未发送 |
| 100 | SonnetDB 路线图与未来展望：M17 可观测性、M18 VS Code 与社区共建 | queued | 未发送 |
| 101 | 案例：IoT 平台如何用 SonnetDB 管理百万设备实时数据 | queued | 未发送 |
| 102 | 案例：智能制造——汽车总装线传感器数据采集与 PID 控制 | queued | 未发送 |
| 103 | 案例：光伏电站运维——基于 SonnetDB 的发电量异常检测 | queued | 未发送 |
| 104 | 案例：楼宇自动化——SonnetDB 助力智慧园区能耗管理 | queued | 未发送 |
| 105 | 案例：冷链物流——全程温湿度监控与超标告警系统 | queued | 未发送 |
| 106 | 案例：城市交通监控——路口车流量时序分析与拥堵预测 | queued | 未发送 |
| 107 | 案例：数据中心——服务器集群的指标采集与容量预测 | queued | 未发送 |
| 108 | 案例：农业 IoT——温室大棚环境数据采集与智能灌溉 | queued | 未发送 |
| 109 | 案例：设备预测性维护——振动信号分析与故障预警 | queued | 未发送 |
| 110 | 案例：用 SonnetDB + AI Copilot 构建无代码数据分析平台 | queued | 未发送 |
| 111 | 写入基准通稿：SonnetDB 在嵌入式与服务端两条路径上的吞吐对比 | queued | 未发送 |
| 112 | 查询基准通稿：100 万点数据集上的时间范围检索对比 | queued | 未发送 |
| 113 | 聚合基准通稿：60,000 ms 时间桶上的 AVG/MIN/MAX/COUNT 对比 | queued | 未发送 |
| 114 | 地理空间基准通稿：轨迹、围栏与空间过滤对比方案 | queued | 未发送 |
| 115 | PID 基准通稿：控制律、时间桶控制与自动整定的 SQL 性能 | queued | 未发送 |
| 116 | 向量基准通稿：Brute-force 与 HNSW 在时序数据库内的召回对比 | queued | 未发送 |
| 117 | SonnetDB 受控 Schema-on-Write：写入时自动补齐 Tag 与 Field | queued | 未发送 |
| 118 | SQL 兼容性基础：SELECT 1 与 count(1) 支持 | queued | 未发送 |
| 119 | ORDER BY 排序：让时序查询结果井然有序 | queued | 未发送 |
| 120 | 单表别名与 DDL 修饰符：写出更地道的 SQL | queued | 未发送 |
| 121 | SonnetDB C 连接器：嵌入式时序数据库的原生接入 | queued | 未发送 |
| 122 | SonnetDB Java 连接器：一套 API，双后端驱动 | queued | 未发送 |
| 123 | Segment v6 与崩溃恢复：把可靠性做到文件尾部 | queued | 未发送 |
| 124 | 查询热路径优化：索引、缓存与少一点 LINQ | queued | 未发送 |
| 125 | MemTable 优化：从热路径统计到快照并发 | queued | 未发送 |
| 126 | 窗口函数执行器：从 object 数组走向 typed streaming | queued | 未发送 |
| 127 | 读多写少结构治理：FrozenDictionary、Lexer 快路径与 Analyzer | queued | 未发送 |
| 128 | Codec 专用化：BlockDecoder 为什么选择手写 fast path | queued | 未发送 |
| 129 | SonnetDB Go 连接器：cgo 与 database/sql 的双入口 | queued | 未发送 |
| 130 | SonnetDB Rust 连接器：手写 FFI 与安全封装 | queued | 未发送 |
| 131 | SonnetDB Visual Basic 6 连接器：让经典 Windows 应用接入时序数据 | queued | 未发送 |
| 132 | SonnetDB PureBasic 连接器：用 Include 文件动态加载 Native 引擎 | queued | 未发送 |
| 133 | SonnetDB Workbench 新版发布：轨迹地图、国内瓦片切换与坐标系转换 | queued | 未发送 |
| 134 | SonnetDB 新增语义图片检索：用 SigLIP2 + USearch 实现文搜图与图搜图 | queued | 未发送 |
| 135 | SonnetDB 当前能力全景：九种原生模型与一套数据库目录 | [submitted](https://my.oschina.net/u/7172/blog/19773742) | unknown |
| 136 | SonnetDB SQL 名称大小写合同：原名、双引号与安全迁移 | [submitted](https://my.oschina.net/u/7172/blog/19773749) | [submitted](https://www.oschina.net/osc-tweet/30401375) |
| 137 | SonnetDB KV 与 JSON 文档：从 TTL 到有界查询 | [submitted](https://my.oschina.net/u/7172/blog/19773750) | [submitted](https://www.oschina.net/osc-tweet/30401380) |
| 138 | SonnetMQ 与流处理：Topic、ACK、DLQ 以及恢复边界 | [submitted](https://my.oschina.net/u/7172/blog/19773751) | [submitted](https://www.oschina.net/osc-tweet/30401381) |
| 139 | SonnetDB typed MCP、Copilot 与 RAG：只读工具如何接入 AI | queued | 未发送 |
| 140 | SonnetDB 语义图片检索续篇：从能运行到可发布的证据边界 | queued | 未发送 |
| 141 | SonnetDB 统一管理工作台：Web Admin、Studio 与 VS Code 的共同边界 | queued | 未发送 |
| 142 | SonnetDB 性能与可靠性文章怎么写：把数字和证据放在一起 | queued | 未发送 |

新闻：4.0.0 ID 502847，账号状态1，公开接口正文已核验，网页可见性未核验。投稿接收与公开审核分别记录。
来源：https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0
