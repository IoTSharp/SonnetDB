# SonnetDB 九域与规划器系统性能闭环（2026-09-01）

> **证据边界：** 本轮有本机 x64 smoke、自动化正确性、win-x64 Native AOT CLI/Server 发布及 CLI 实际执行证据；Native AOT Server 启动、固定硬件 x64、ARM64、木垒同语料、七天 mixed workload 与生产发布门禁均为 ⏳ `NOT_RUN`。本文不把开发机短跑换算为容量或生产 SLO，也没有连接、修改或重启木垒生产。

机器可读结果见 [`../../artifacts/system-performance-20260901/system-performance-report.json`](../../artifacts/system-performance-20260901/system-performance-report.json)，紧凑摘要见 [`../../artifacts/system-performance-20260901/system-performance-report.md`](../../artifacts/system-performance-20260901/system-performance-report.md)。

状态图例：✅ 已完成并在声明范围内验证；🟡 本机或配置级完成，外部门禁待验证；🚧 只完成部分切片或仍有实现残余；⏳ 尚未执行；❌ 已执行但未通过或未产出有效样本；➖ 不适用或有意不采用。表情不替代 `PASS`、`NOT_RUN` 等机器状态码。

> **总体状态：🟡 `LOCAL_SMOKE_ONLY`。** 本机切片的实现、正确性与证据归档已闭环；九域容量闭环仍为 🚧，固定硬件、ARM64 实机、木垒同语料、168 小时和生产门禁均为 ⏳ `NOT_RUN`。

## 执行摘要

本轮从指定 dirty 基线完成了现状审计、竞品源码机制研究、可量化本机切片基线、分层设计、两个公共热路径实现切片、三个独立模型 benchmark、并发合同加固和证据归档。九域容量闭环仍为 🚧，没有用局部 smoke 替代其余模型。也没有重复实现此前已经完成的 KV 稳定快照、RandomAccess 并发点读、复合索引前缀与 `IN`、倒序 `ORDER BY/LIMIT` 早停、统计成本门控、Server 资源接线、动态 spill fan-in、Top-N 取消/清理及 TOLNSD 热点索引/CAS。

正式产品口径仍是 README 的八种数据模型：时序、关系表、KV/缓存、JSON 文档、全文检索、向量检索、对象存储、消息队列。M40 原生属性图只作为第九性能域纳入矩阵；在 #367 固定硬件、外部对拍、Native AOT 和 168 小时门禁通过前，不能据此把产品改称九模型。关系 SQL parser/binder/planner/optimizer/executor 是跨域公共核心，不另算第十域。

本轮有前后量化对照的两个实现切片为：

- 关系统计刷新复用真实索引编码长度逻辑，移除采样主键复制和每索引 key/prefix 临时分配。最终 dirty 本机短跑均值 `54.81 -> 51.25 ms`（`-6.495%`），分配 `34.72 -> 22.67 MB/op`（`-34.706%`）；较早的 `50.89 ms` 结果仅保留为中间重跑记录。
- 向量文件 IEEE CRC32 从私有逐字节表实现切到 `System.IO.Hashing.Crc32.HashToUInt32`。64 B、4 KiB、1 MiB 的本机生产路径均值分别为 `130.8 -> 8.596 ns`、`8.5847 us -> 213.571 ns`、`2.374972 ms -> 61.436644 us`。这没有把持久格式改成 CRC32C。

并发可靠性方面，✅ SQL 物理读快照最多尝试 64 次且总等待不超过 5 ms，降级零值不会进入物理读 histogram/累计；🟡 Sparkplug Rebirth 已使用容量可配、按 group/node 合并的有界单消费者队列，并暴露入队、合并、拒绝、丢弃和深度指标。MQTT 内部发布以有界 `MqttServer.IsStarted` 检查驱动 readiness，预期未就绪/期限耗尽使用专用 `SparkplugPublisherUnavailableException`，readiness helper 不会把未知 `InvalidOperationException` 重分类为可恢复未就绪；真实 broker 在 readiness 检查与 publish 之间停机的集成竞态仍为 ⏳。这两项是资源上界与观测正确性改进，不声明吞吐提升。

🟡 ARM64 Native AOT 已加入 CI matrix（`ubuntu-24.04-arm` / `linux-arm64`，NuGet cache 按 RID 隔离）。YamlDotNet 16.3.0 语法解析为 ✅ `PASS`（1 个 document、5 个 root entries），并已手工核对 matrix 与 RID 路径；actionlint、GitHub schema、真实 CI job 和 ARM64 执行均为 ⏳。本轮只能声明门禁配置存在，不能声明 ARM64 AOT 可运行，更不能推断木垒 Kunpeng 920 会选择某条 SIMD/CRC 指令。

## 基线身份

| 项目 | 请求/旧值 | 当前值 | 核验结论 |
| --- | --- | --- | --- |
| 外层 TOLNSD 请求基线 | `988ed78df18b46a399d5b544fd78904452d2405f` | `770a139f6f00512d7725458cb7ca43bb1ad75620` | 当前 HEAD 的直接父提交正是请求基线；新增提交为本轮等价延续，不回退。 |
| SonnetDB 必含祖先 | `3f362d1c387149524c8d08f536a687c135aa45eb` | `424f61ad16e883d6b9050a9eb29a352105d28cef` | `git merge-base --is-ancestor` 返回 0。 |
| 旧 gitlink | `0be6898b1f4ef8b646872ece749dd803cf990e24` | `424f61ad16e883d6b9050a9eb29a352105d28cef-dirty` | 旧 gitlink 未恢复；全部未提交改动保留。 |

报告中的“当前”均指 `424f61a + dirty`，不是干净提交的历史状态。基准 artifact 的 `commitSha` 若写为 `424f61a` 或 `424f61a-dirty`，均须结合其 process metadata 和本节解释，不可单独当作可复现的干净发布提交。

## 统一性能合同

每个域至少记录以下字段；没有数据时写 ⏳ `NOT_RUN`，不能用 0 代替未知。

| 指标 | 统一定义 | 判读要求 |
| --- | --- | --- |
| 吞吐 | ops/s、rows/s、points/s、messages/s 或 bytes/s | 同时记录操作语义、批大小、并发、durability 和数据规模。 |
| 延迟 | P50/P95/P99 | 冷启动与稳态分开；P99 不能由 3 个样本支撑生产结论。 |
| 扫描放大 | candidate、examined、returned 和 `examined/returned` | `returned=0` 单列；不能只看耗时。 |
| 分配与 GC | bytes/op、Gen0/1/2、GC pause | 与结果物化方式和批大小一起记录。 |
| RSS | steady/peak working set | 同时记录数据规模、cache 状态和进程边界。 |
| 等待 | 请求 permit、内部 worker、table/KV lock、队列等待 | 分开计量，避免把排队归因给执行器。 |
| I/O | 逻辑/物理读写次数与字节、随机/顺序、cache 状态 | 不完整快照必须标记 degraded，不能进入“真实 0”累计。 |
| WAL | record/bytes/fsync count、fsync P50/P95/P99 | 必须记录同步策略，不能跨 durability 横比。 |
| Spill | count/bytes/files、peak memory、fan-in | 成功、异常、取消都验证工作目录清理。 |
| 恢复与文件 | checkpoint、replay、cold open、kill/reopen、backup/restore、文件数 | 固定磁盘和数据规模后再设门。 |
| 正确性 | 精确 oracle、差分、Recall@K 或协议不变量 | 所有快路径覆盖取消、异常、资源释放、并发上界和回退原因。 |

证据分层固定为：

| 层级 | 当前状态 | 可以说明什么 |
| --- | --- | --- |
| 本机 smoke | 🟡 `PARTIAL_PASS` | 本轮已运行切片可运行且其自动化测试通过，性能方向值得继续验证。 |
| 固定硬件 x64 | ⏳ `NOT_RUN` | 尚无稳定容量和尾延迟结论。 |
| 固定硬件 ARM64 | ⏳ `NOT_RUN` | 尚未证明 Native AOT、SIMD/CRC 路径或 Kunpeng 920 指令选择。 |
| 木垒同语料 | ⏳ `NOT_RUN` | 尚未复测实际查询分布和资源竞争。 |
| 生产发布 | ⏳ `NOT_RUN` | 尚未执行部署、七天 mixed workload、恢复和回滚门禁。 |

## 本轮可量化结果

### 统计刷新 🟡

`TableStatisticsRefreshBenchmark` 固定 10,000 行、4 个二级索引、全量采样、2 次预热和 5 次正式迭代。初始化不计入测量。

| 版本 | Mean | Median | P90 | Allocated |
| --- | ---: | ---: | ---: | ---: |
| before | 54.81 ms | 54.77 ms | 55.24 ms | 34.72 MB/op |
| 较早中间 after | 50.89 ms | 50.80 ms | 51.43 ms | 22.67 MB/op |
| 最终 dirty after | 51.25 ms | 50.68 ms | 52.29 ms | 22.67 MB/op |
| before -> 最终 dirty | -6.495% | -7.468% | -5.340% | -34.706% |

证据：

- `artifacts/system-performance-20260901/before/statistics-refresh-run3/bdn/results/SonnetDB.Benchmarks.Benchmarks.TableStatisticsRefreshBenchmark-report.csv`
- `artifacts/system-performance-20260901/after/statistics-refresh/bdn/results/SonnetDB.Benchmarks.Benchmarks.TableStatisticsRefreshBenchmark-report.csv`
- `artifacts/system-performance-20260901/statistics-current-final/bdn/results/SonnetDB.Benchmarks.Benchmarks.TableStatisticsRefreshBenchmark-report.csv`

三组为独立短跑，最终 dirty 重跑是当前主证据，较早 after 仅保留过程溯源。该结果仍只是方向性证据；固定硬件门禁前必须在冻结环境重复测量，不能据此声明容量或 SLO。

### 向量文件 IEEE CRC32 🟡

`VectorCrc32Benchmark` 使用固定随机 payload、3 字节偏移、2 次预热和 5 次正式迭代；before/after 的生产路径均为 0 B/op 分配。

| 输入 | before production | after production | 比值 | 均值降低 |
| ---: | ---: | ---: | ---: | ---: |
| 64 B | 130.8 ns | 8.596 ns | 15.216x | 93.428% |
| 4 KiB | 8,584.7 ns | 213.571 ns | 40.196x | 97.512% |
| 1 MiB | 2,374,971.8 ns | 61,436.644 ns | 38.657x | 97.413% |

正确性覆盖 golden、确定性随机、offset `0..15`、边界长度和 1 MiB；算法仍为 IEEE 802.3 CRC32。两个 BenchmarkDotNet 进程的 legacy reference 波动明显，因此只比较各版本记录的 production 行并保留“本机短跑”标签，不把 speedup 外推到 ARM64。

### 独立模型 smoke 🚧

这三类结果没有 before 对照，只用于建立模型自身的可重复测量入口。BenchmarkDotNet 已按 `OperationsPerInvoke` 归一化。

| 域/操作 | 归一化 | Mean | Median | P95 | Allocated | 状态 |
| --- | --- | ---: | ---: | ---: | ---: | --- |
| KV 点读，10k keys/256 B value | 每 key | 2.921 us | 2.935 us | 2.996 us | 736 B | 🟡 `LOCAL_SMOKE_PASS` |
| KV GetMany，256 个离散 key | 每 key | 4.019 us | 4.043 us | 4.141 us | 823 B | 🟡 `LOCAL_SMOKE_PASS` |
| Document ID 读取，10k documents | 每 document | 3.099 us | 3.047 us | 3.287 us | 1.24 KB | 🟡 `LOCAL_SMOKE_PASS` |
| Document JSON-path 索引查询，32 个 site predicate | 每 query | 1,375.524 us | 1,368.046 us | 1,447.969 us | 894.94 KB | 🟡 `LOCAL_SMOKE_PASS` |
| Object 64 KiB 完整读取 | 每 object | 257.9 us | 266.1 us | 294.1 us | 8.84 KB | 🚧 `EXPLORATORY_LOCAL_SMOKE_PASS` |
| Object 4 KiB Range 读取 | 每 object | 230.2 us | 234.9 us | 259.6 us | 8.83 KB | 🚧 `EXPLORATORY_LOCAL_SMOKE_PASS` |

对象两项存在 BenchmarkDotNet “最小迭代短于 100 ms”警告，必须增加 invocation/iteration 后再用于回归阈值；当前仅证明真实对象 API、审计语义和 artifact 管线可运行。

表中的 P95 是 `OperationsPerInvoke` 归一化后 **BenchmarkDotNet iteration mean 的分位数**，不是请求级或单操作尾延迟；批内归一化会掩盖个别操作抖动。这批 BDN artifact 没有请求级 P99，BenchmarkDotNet 配置也不把统计列伪装成请求尾延迟。真实尾延迟由 `ModelReadLatencyEvidenceRunner` 逐请求采样：quick 合同为 32 次预热、256 个正式样本，Program 外层超时为 2 分钟。

### 逐请求尾延迟 🟡

`ModelReadLatencyEvidenceRunner` 是 KV、Document 与 Object Storage 的请求级 P50/P95/P99 权威入口。它与上表的 BenchmarkDotNet 吞吐/分配 smoke 分开归档，以下每项均为 256 个单请求样本，采用 nearest-rank 从原始样本计算并已逐项复算一致：

| 域/请求 | P50 ms | P95 ms | P99 ms | Max ms | req/s | Alloc P95 | 未知分配样本 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| KV point read | 0.0041 | 0.0050 | 0.0060 | 0.0182 | 234,303.50 | 760 B | 0 |
| KV GetMany 256 | 1.2828 | 1.4061 | 1.9250 | 6.1029 | 762.52 | 216,904 B | 0 |
| Document ID read | 0.0032 | 0.0039 | 0.0042 | 0.0044 | 307,803.29 | 1,376 B | 0 |
| Document indexed JSON-path | 2.6186 | 3.0759 | 4.0001 | 4.1119 | 374.03 | 921,992 B | 0 |
| Object full read 64 KiB | 0.1790 | 0.2715 | 0.3523 | 0.6137 | 5,275.21 | 9,112 B | 26 |
| Object range read 4 KiB | 0.1500 | 0.2216 | 0.3198 | 0.3317 | 6,171.54 | 9,144 B | 21 |

证据：`artifacts/system-performance-20260901/model-read-latency-current-final/model-read-latency.json`，SHA256 `6e189c38...116`。机器状态为 🟡 `PASS_LOCAL_HOT_EMBEDDED`，仅代表单线程、本机 x64、热本地嵌入式读取 smoke；不覆盖 Server 排队、并发上限、冷缓存、物理 I/O、固定硬件或 ARM64。Object async 请求发生线程切换时无法用当前线程分配计数器归因，因此分别有 26/21 个分配样本为 unknown；延迟样本仍完整，不得把已知样本的 Alloc P95 外推到全部请求，更不能把 unknown 写成零分配。

### M41 固定五查询 quick 🟡

before 与 after 都是 64 tasks、64 audits、8 devices、每查询 3 次的嵌入式 quick。两次运行均为 🟡 本机 `localCorrectness=PASS`，且 access path、examined rows、returned rows 和 P50 分配完全一致。

| 查询 | Access path | Examined/returned | P50 before | P50 after | P50 allocation |
| --- | --- | ---: | ---: | ---: | ---: |
| `indexed_exists` | `secondary_index` | 1/1 | 0.3320 ms | 0.2925 ms | 12,832 B |
| `scalar_in` | `mixed` | 40/20 | 1.2302 ms | 1.2043 ms | 71,352 B |
| `nullable_or` | `index_union` | 20/20 | 0.8853 ms | 0.7756 ms | 45,456 B |
| `multi_table_join` | `table_scan` | 72/20 | 1.6045 ms | 1.1760 ms | 88,816 B |
| `descending_pagination` | `secondary_index_range` | 64/20 | 1.2814 ms | 1.6916 ms | 85,544 B |

P50 有升有降，3 次样本不足以解释性能差异；此处只能认定行为证据未回归。runner 不经过 Server SQL permit，因此请求排队指标为 `NOT_APPLICABLE_EMBEDDED`。

## 九域性能矩阵

| 域 | 本轮状态 | 性能合同 | 当前热点 | 容量状态 | 竞品/机制对照 | 正确性回退 | Benchmark 与本轮证据 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 时序 | 🟡 harness 已有；本轮 ⏳ | 固定 durability 下写入、范围查询、窗口聚合 P50/P95/P99；flush/retention/compaction/reopen 无 missing/duplicate/value mismatch | WAL/fsync、memtable/frozen、segment pruning/解码、压缩、compaction、SIMD 聚合 | ⏳ `NOT_RUN`（容量尚未建立） | ClickHouse、DataFusion、QuestDB | SIMD 条件不满足回退 scalar；不能证明剪枝时走完整范围路径 | `Insert/Query/Aggregate/Compaction/NumericAggregateSimd`；本轮 ⏳ `NOT_RUN` |
| 关系表 | 🟡 本机 partial | SQL 三值逻辑、事务 overlay、稳定排序和结果不变；计划报告成本、放大、等待、I/O、spill | parser/binder、统计、复合/覆盖索引、下推、join、sort/group/distinct、反馈 | quick 仅 64/64/8；固定硬件 ⏳ `NOT_RUN` | PostgreSQL、SQLite、DuckDB、DataFusion | 统计过期、事务写集、资源不足或不能证明等价时启发式索引、串行或 scan，并暴露原因 | M41 系列、statistics benchmark；🟡 `PARTIAL_PASS` |
| KV/缓存 | 🟡 本机 smoke | 点读/GetMany/前缀/TTL/CAS 在稳定快照下保持版本与原子语义 | snapshot overlay、RandomAccess、value copy、keyspace lock、WAL/state、cold miss | 10k keys smoke；10M parity ⏳ 未跑 | RocksDB | cache miss 读取已验证 state/WAL；CAS 冲突不覆盖 | `KvModelBenchmark` 🟡 `LOCAL_SMOKE_PASS`；容量 ⏳ `NOT_RUN` |
| JSON 文档 | 🟡 本机 smoke | ID CRUD、path 查询/索引、update/TTL 一致；派生索引失败不能伪装业务未提交 | JSON parse/encode、path、索引、KV state、派生维护 | 10k documents smoke；million/ten-million ⏳ `NOT_RUN` | MongoDB parity、RocksDB 存储机制 | 索引不适用时 collection scan；异常为稳定错误或可修复派生状态 | `DocumentModelBenchmark` 🟡 `LOCAL_SMOKE_PASS`；容量 ⏳ `NOT_RUN` |
| 全文 | 🟡 harness 已有；本轮 ⏳ | tokenizer、BM25、facet/fuzzy、增量/tombstone 对拍；稳定 Top-K 和 posting 放大 | dictionary/tokenizer、posting intersection、BM25、active terms、Top-K、compaction | ⏳ `NOT_RUN`（容量尚未建立） | Lucene | 索引不可验证时显式失败、重建或精确路径；不得返回部分排序 | `FullTextActiveTerm`、1M parity；本轮 ⏳ `NOT_RUN` |
| 向量 | 🚧 仅 CRC | 维度/metric 一致；ANN 同报 Recall@K 与延迟；精确回退对拍；持久 CRC 兼容 | query norm 重算、SIMD distance、HNSW、filtered ANN、文件 codec/CRC | 本轮只测 CRC；10k/100k recall 与 ARM64 ⏳ `NOT_RUN` | Qdrant；Milvus/HNSWlib 本轮未锁源码版本 | ANN 条件不满足回退 brute-force；运行时 SIMD 不可用回退 scalar；CRC 不改算法 | CRC before/after ✅ `PASS`；召回与容量 ⏳ `NOT_RUN` |
| 对象存储 | 🚧 exploratory | Put/Get/Range/multipart/delete/audit 字节与元数据一致；流和临时资源可释放 | metadata KV、full/range stream、大值复制、audit、multipart cleanup | 256 x 64 KiB exploratory smoke | MinIO、RocksDB 存储机制 | 严格 Range；not-found 稳定；异常关闭流并只清理任务资源 | `ObjectStorageModelBenchmark` 🚧 `EXPLORATORY_LOCAL_SMOKE_PASS`，需延长迭代 |
| 消息队列 | 🚧 仅 Rebirth 合同 | publish/pull/ack/replay/group 顺序与至少一次语义；吞吐连同 fsync、积压和重投 | append、topic/group lock、group commit、cold payload、ack/retention、frame、Rebirth storm | MQ 容量 ⏳ `NOT_RUN`；Rebirth default outstanding=1024 | NATS、Kafka、Redpanda | 有界等待/拒绝；重启 replay；重复 Rebirth 合并；broker 未就绪专用异常 | `MqThroughput/FrameEncoding` 本轮 ⏳；queue/readiness/竞态合同 ✅ `12/12 PASS` |
| 原生属性图 | 🟡 runner 已有；正式门禁 ⏳ | 顶点/边/属性、遍历、路径、SQL/PGQ/GQL、snapshot 分页与恢复 oracle 全通过 | adjacency decode、frontier/visited、generation lease、weighted heap、spill、checkpoint | 1m vertices/10m edges、168h 8+1 均 ⏳ `NOT_RUN` | Neo4j、PostgreSQL oracle | 预算不足只允许有界 relation scan fallback 或显式失败，不能越预算读邻接 | M40 runner/weighted path 已有；正式门禁 ⏳ `NOT_RUN`，不是第九正式模型 |

## 关系规划器公共核心

### 当前已具备

- parser/AST、绑定和多模型 SQL 分派；规范化 SQL fingerprint 不保存参数值或行内容。
- 持久表统计包含 row/page/width、列 distinct/NULL/MCV/直方图与索引宽度/页数。
- 主键、二级索引、复合索引前缀、`IN` semijoin、索引并集、反向范围 Top-N 和完整等值 covering/index-only。
- 3～6 表有限 join ordering，Hash、index nested-loop、兼容有序输入 merge join；超过 6 表有明确回退。
- 谓词/投影输入下推、probe 侧 LIMIT 早停、稳定 Top-N；阻塞 Hash/Sort/Group/Distinct/index-union 受查询/数据库内存预算约束并可 spill。
- per-series scan、legacy aggregate 和物化 probe Hash JOIN 的有界内部并行；资源不足时串行回退。
- `EXPLAIN ANALYZE` 报告访问路径、候选/检查/返回、分配、锁/WAL、spill、峰值内存和并行决策。

### 残余风险

1. `TableCostPlanner.Estimate` 对 `RowCount >= 1024` 的首个业务读可同步调用 `TryAutomaticStatisticsRefresh()`。当前方法在调用线程扫描快照并写统计状态，可能把全表采样和 WAL/状态写入首查尾延迟。P0 应改为 coalesced background refresh；前台继续使用旧统计或启发式计划，并暴露 refresh queued/running/failed。
2. 统计采样读取主键顺序的前 N 行。直方图内部 reservoir 不能消除“外层只取前 N 主键”的分布偏差。P1 需要确定性 block/systematic/reservoir 采样，并用 skew corpus 报告基数误差和计划选择。
3. 当前窄索引查找成本包含 `0.5 * full index logical pages`，会系统性高估小范围 seek。P1 应使用树高、触达叶页、候选范围与 cache 状态估算，仍保留 scan 回退。
4. runtime feedback 只按规范化 SQL fingerprint 滚动平均 estimated/actual，最多 1,024 项；不同参数选择率会混在一起。它目前只适合并行准入修正，不应直接成为物理计划选择依据。参数敏感计划需要 plan identity、参数/选择率 bucket、aging 和有限 cache。
5. covering/index-only 仍以完整等值为主；range/prefix/IN 的投影覆盖属于 P2。扩大前必须保留 residual、NULL、visibility 和事务 overlay 检查。

## 公共存储路径

公共热路径按 `WAL -> mutable/frozen overlay -> state/segment -> checkpoint/compaction -> cold open/recovery` 审计：

- 已有 WAL 批量/同步策略、mutable/frozen/state、checkpoint/compaction 和恢复预算；性能报告必须把 durability 固定为输入参数。
- KV state、MQ cold payload 与 Graph spill 使用 `RandomAccess`，并发点读已避免共享 seek，但尚无独立 embedded I/O 许可。请求 permit 和 SQL worker 上界都不能替代物理 I/O 上界。
- 稳定快照缓存已落地；并发 cold miss 仍可能重复构建 overlay，适合 P2 single-flight，但必须定义取消所有权、失败淘汰和 waiter 上界。
- 大值点读仍存在二次复制可能，优化前先冻结 ownership/lifetime；不能把池化 buffer 暴露给超出 lease 的调用方。
- 动态 spill fan-in、最多 32 路归并、所有权标记目录和取消/异常清理已有合同；下一步需要把 file count、spill files 和 cold open 纳入九域统一指标。
- 统计刷新本轮减少了索引 key 临时分配；真实编码与长度估算共用逻辑，避免为了 benchmark 建立与生产分叉的算法。

## 并发、背压与取消

| 层级 | 当前预算 | 背压/回退 | 取消与释放 | 下一门禁 |
| --- | --- | --- | --- | --- |
| 每数据库 SQL 请求 | permit 4、queue 8；binder 范围 1..256 / 0..4096 | REST 503；frame `sql_overloaded` | 请求令牌覆盖等待与执行 | 固定负载测 queue P95/P99、拒绝率，不以盲目加 permit 调优 |
| 每数据库 SQL worker | `min(processorCount, 8)`；输入至少 2048 行 | 拿不到至少 2 个 worker 或内存时串行 | worker/预算在 `finally` 释放，异常解包 | x64/ARM64 找收益/饱和拐点 |
| 阻塞算子内存 | 64 MiB/query、256 MiB/database、64 KiB/worker | Hash/Sort/Top-N/Group/Distinct/index union spill | 成功/异常/取消删除带 owner marker 的目录 | mixed workload 记录 spill bytes/files 和 RSS |
| Embedded RandomAccess | ⏳ `NOT_DEFINED` | 当前无独立 I/O 队列 | 调用链可取消，但不能限制磁盘饱和 | P1 增加 per-db I/O semaphore/queue/metrics |
| 同实体写 | KV keyspace、Table、Document、MQ topic/group 各自串行锁 | lock wait；不扩散 worker | 🚧 待证明维护/等待有界并释放锁 | CAS/幂等/同 key-row-doc-topic 竞争与恢复 |
| 关系表冷开 | 默认 4，binder 限制 1..16 | 有限并发 warmup | host stopping token | 按表数/WAL/files 测 cold-open 和 RSS |
| Sparkplug Rebirth | 默认 1024，范围 1..65,536，单 publisher | 同 group/node 合并；满/停拒绝并恢复 retry 标记；readiness 以有界 `MqttServer.IsStarted` 检查为准 | stop complete channel，最多 capacity 次 drain，恢复 lifecycle 标记；停止竞态只收口专用 unavailable | 🟡 合同测试 ✅ `12/12 PASS`；真实 MQTT server 检查后停机集成竞态 ⏳ |

SQL 物理读指标冻结另有独立 64 次/5 ms 上限。若 writer 未稳定，snapshot 标记 `complete=false`；SlowQuery DTO/Top aggregate 暴露 valid/degraded 计数，降级零值不进入物理读累计和 histogram。

Sparkplug readiness helper 对未启动 broker 使用专用 `SparkplugPublisherUnavailableException`，对 deadline 和初始 publish/stop 竞态有有界测试；未知 `InvalidOperationException` 会立即从 helper 穿透，不会被伪装为未就绪。Rebirth 层仍按既有“单节点失败不终止后续节点”的边界记录失败。剩余低风险缺口是真实 MQTT server 的 `IsStarted` 在 readiness 检查与 publish 之间翻转，当前 12 个合同测试使用可控 publisher/readiness 注入，尚未覆盖该真实集成竞态。

## .NET、AOT 与硬件路径

| 能力 | 仓库事实 | 当前判定 | 后续门禁 |
| --- | --- | --- | --- |
| .NET 10 | `global.json` 10.0.100 + latestMinor；默认 `net10.0`；本机 SDK 10.0.400/runtime 10.0.11 | ✅ 默认生产基线 | 固定 SDK/runtime 重跑 |
| Span/Memory | codec、存储、协议、查询广泛使用 | ✅ 已使用 | lifetime/ownership 回归 |
| `SearchValues` | lexer、Point validation | ✅ 已使用 | 长短输入与 Unicode 差分 |
| Frozen collections | catalog/schema/index snapshots | ✅ 已使用 | rebuild/cold-start 分配 |
| `ArrayPool` | WAL、segment、ingest、MQ、object、endpoint | ✅ 已使用 | 异常/取消归还、敏感内容清理 |
| `MemoryPool` | 本轮源码搜索未发现 | ➖ 未使用 | 只有能减少跨 async ownership 成本时再引入 |
| `RandomAccess` | KV state、MQ cold payload、Graph spill | 🚧 已使用，缺独立 I/O 预算 | 固定磁盘饱和测试 |
| Pipelines | frame endpoints/codecs | ✅ 已使用 | cancellation、partial frame、backpressure |
| Channels | 当前四处均为 bounded | ✅ 已使用有界队列 | 每处明确 full-mode、丢弃/拒绝指标 |
| SIMD | `TensorPrimitives`、`Vector<T>`、`BitOperations` | 🟡 运行时分派；跨架构门禁待补 | scalar differential + fixed x64/ARM64 |
| Direct intrinsics | 未发现直接 `Avx2/Avx512/BMI/AdvSimd` 调用 | ➖ 未实现，不假设 | 只能在显式 feature gate/独立 benchmark 后加入 |
| Native AOT | analyzer 默认开启；win-x64 CLI/Server publish ✅ `PASS`，CLI `--version` 实际执行 ✅ `PASS`，Server 未启动；CI matrix 已含 `ubuntu-24.04-arm/linux-arm64` 且 cache 按 RID 隔离 | 🟡 win-x64 本机证据成立；ARM64 配置已接入但真实 run ⏳ `NOT_RUN` | 执行 `linux-arm64` publish/start/first-query 和回归 |
| .NET 11 preview | 没有默认依赖 | ➖ `NOT_USED` | 只允许条件编译或独立 experiment project |

硬件路径必须遵守以下原则：

- x64 浮点距离由 `TensorPrimitives/System.Numerics.Vector` 运行时选择 SSE/AVX2/AVX-512；仓库没有直接 intrinsic probe，报告不能宣称具体指令一定执行。
- Hamming 使用 `BitOperations.PopCount`；BMI 没有直接实现。运行时能否采用硬件指令由实际 target 决定。
- CRC 委托 `System.IO.Hashing.Crc32`；当前测试证明算法和字节边界，不证明目标 CPU 采用哪条指令。
- 数值聚合先检查 little-endian 和 `Vector.IsHardwareAccelerated`，小输入或不支持类型回退 scalar；CRC 另覆盖 offset `0..15` 和边界长度。
- ARM64 AdvSimd/CRC 与 Native AOT 均为 ⏳ `NOT_RUN`。木垒 Kunpeng 920 只能在目标机读取真实运行时能力并执行差分后启用，不能按处理器名称推断扩展。

## 竞品源码研究

以下均为本轮 pinned research 的上游源码入口，仅作机制级导航；没有逐行移植。许可证不兼容项严格 clean-room/behavior-only。

本轮用官方仓库 tag/commit ref 复核了 14 项版本绑定，并逐一验证表中至少一个 pinned 源码入口可访问；短 SHA 均可还原到对应 tag 的完整 commit。GitHub API 达到匿名限流后，未继续重试 API，而改用官方 `git ls-remote` tag ref 与固定 commit 的仓库源码 URL；没有 clone 或下载竞品仓库。

| 项目 | 版本/commit | 上游源码入口（pinned research） | 许可证 | 可借鉴机制与边界 |
| --- | --- | --- | --- | --- |
| PostgreSQL | 17.6 / `7885b94d` | `src/backend/optimizer/path/{costsize,allpaths,joinrels}.c` | PostgreSQL License | 基数/成本、path、join 枚举；按 SonnetDB 语义实现 |
| SQLite | 3.50.4 / `8ed5e736` | `src/where.c`, `wherecode.c`, `analyze.c`, `vdbe.c` | Public Domain | 轻量统计、where loop、字节码执行 |
| DuckDB | 1.3.2 / `0b83e5d2` | `src/optimizer/join_order`, `physical_hash_join.cpp`, `radix_partitioned_hashtable.cpp` | MIT | join ordering、向量化、radix hash |
| ClickHouse | 25.8.1 / `4f2b50b8` | `src/Processors/QueryPlan`, `src/Interpreters/Cache` | Apache-2.0 | pipeline/query plan、cache |
| RocksDB | 10.4.2 / `410c5623` | `db/db_impl`, `db/version_set`, `table/block_based` | Apache-2.0/GPL-2.0 dual | LSM/version/block read；只用兼容许可或 clean-room |
| DataFusion | 49.0.2 / `f43df3f2` | `datafusion/optimizer`, `physical-optimizer`, `core/src/execution` | Apache-2.0 | IOx 相关 planner substrate、rule/physical optimizer |
| QuestDB | 9.0.0 / `3609c551` | `griffin/SqlOptimiser.java`, `cairo/TableWriter.java` | Apache-2.0 | 时序 SQL 和 writer 热路径 |
| Lucene | 10.2.2 / `279eb7aa` | `IndexSearcher.java`, `BM25Similarity.java`, `SegmentReader.java` | Apache-2.0 | BM25、segment reader、Top-K |
| Qdrant | 1.15.1 / `af7ab5b1` | `lib/segment/src/index/hnsw_index`, `lib/collection/src/shards/local_shard` | Apache-2.0 | HNSW、filtered search、shard admission |
| MinIO | commit `0d7408fc` | `cmd/object-api-interface.go`, `cmd/erasure-server-pool.go` | AGPL-3.0 | 对象 API/erasure 调度；**clean-room only** |
| NATS | 2.11.7 / `df44964e` | `server/consumer.go`, `store.go`, `filestore.go` | Apache-2.0 | consumer backpressure、file store |
| Kafka | 4.0.0 / `985bc995` | `storage/.../log`, `core/src/main/scala/kafka/log` | Apache-2.0 | append log、segment/index、retention |
| Redpanda | 25.2.4 / `e04206cd` | `src/v/storage`, `src/v/kafka/server/handlers` | BSL/RCL | 调度和存储行为；**behavior-only** |
| Neo4j | 2025.08.0 / `04c8ed8d` | `community/cypher/cypher-planner`, `community/kernel` | GPL | Cypher planner/graph kernel；**clean-room only** |

MongoDB 在仓库中已有 Document parity，但本轮没有锁定其源码 commit；Milvus 与 HNSWlib 同样没有 pinned source audit；InfluxDB IOx 也未单独锁定源码，本轮只研究其 DataFusion planner substrate。因此矩阵只把这些项目标作后续对照，不虚构版本或源码结论。

## 有界源码扫描

性能启发式扫描严格限制在 `src/SonnetDB.Core` 与 `src/SonnetDB`，排除 `bin/obj`。精确计数如下：

| 规则 | 计数 | 解释 |
| --- | ---: | --- |
| literal `IndexOf` 未指定 comparison | 0 | 无候选 |
| `Substring` | 6 | 仅候选，需按调用上下文审查 |
| literal `StartsWith/EndsWith` 未指定 comparison | 0 | 无候选 |
| potential string `Contains` | 4 | 仅候选 |
| `async void` / `ToLower/Upper()` | 0 / 0 | 无候选 |
| triple `Replace` / `params` / char `All/Any` | 2 / 19 / 3 | 仅候选 |
| static readonly `Dictionary` / Frozen candidate | 2 / 0 | 不因计数机械替换 |
| `new List` / `new Dictionary` | 664 / 280 | 需要 profile 定位，计数本身不是缺陷 |
| LINQ `Select/Where/Cast/Take/Aggregate` | 615 | 需要热路径证据 |
| `new HttpClient` / `new JsonSerializerOptions` | 0 / 0 | 无候选 |
| `RegexOptions.Compiled` / `GeneratedRegex` / `new Regex` | 5 / 5 / 1 | 动态 regex 使用有界 cache 与 250 ms timeout |
| potential `Task.Result` / `Wait` | 53 / 18 | GraphTable 的 `record.Result` 是误报；Wait 主要为显式锁/有界等待 |
| unbounded / bounded Channel | 0 / 4 | 当前源码通道全部有界 |
| unsealed / sealed class | 1 / 524 | 仅候选，不以 sealed 数量作为性能结论 |

SqlLexer Frozen 候选已尝试运行，但在 BenchmarkDotNet 内部 120 秒 build timeout 前没有形成样本，`before/sql-lexer-frozen` 登记为 ❌ `NO_SAMPLE_BUILD_TIMEOUT`；这不是性能回归结论，因此没有基于该候选修改生产代码。该扫描只生成检查清单，不能把启发式命中直接标成 bug 或优化收益。

## P0-P3 残余队列

| 优先级 | 状态 | 项目 | 当前证据 | 完成门禁 |
| --- | --- | --- | --- | --- |
| P0 | 🚧 | 把自动统计刷新移出首个业务读 | `TableCostPlanner` 可同步触发全表采样 | coalesced background job；前台使用旧统计/启发式；取消、失败、reopen、同计划差分 |
| P0 | 🟡 配置完成；执行 ⏳ | ARM64 可执行/AOT 门禁 | `linux-arm64` CI 已配置但真实 run/artifact 尚无 | 执行 publish/start/first query；SIMD/CRC/scalar 差分；真实指令记录 |
| P1 | 🚧 | 无偏统计采样 | 外层取 PK 顺序前 N 行 | skew corpus 上基数误差和 plan-choice 门禁 |
| P1 | 🚧 | 页感知索引成本 | 窄查承担半个完整索引 logical pages | tree height/leaf range/cache-aware reads；计划回归 |
| P1 | 🚧 | 参数敏感计划与反馈 | fingerprint 滚动平均混合参数选择率 | 参数/选择率 bucket、plan identity、aging、bounded cache、fallback |
| P1 | 🚧 | Embedded I/O 预算 | RandomAccess 无独立 semaphore | per-db queue/permit/cancel/metrics，在 NVMe 与 ARM64 标定饱和点 |
| P1 | 🚧 | 向量 query norm 复用 | batch cosine 可逐 row 重算 query norm | 一次预计算；zero/NaN/dimension 差分；brute/HNSW benchmark |
| P2 | 🚧 | 扩大 covering/index-only | 主要限完整等值 | range/prefix/IN projection + residual/NULL/overlay 测试 |
| P2 | 🚧 | snapshot cold miss single-flight | 可重复构建 overlay | bounded waiter、取消 ownership、失败 eviction |
| P2 | 🚧 | 大值点读减 copy | disk point read 可能二次复制 | owned/pool-backed lifetime、取消释放、allocation benchmark |
| P2 | 🚧 | cold start/file count 合同 | 九域尚未统一 | files/count/bytes/replay/checkpoint P50/P95/P99 |
| P2 | 🟡 前置完成；集成门禁 🚧 | Sparkplug 真实 broker readiness/publish 竞态 | 合同测试覆盖初始 publish/stop 与专用异常，但没有真实 `MqttServer.IsStarted` 翻转 | 启动真实 broker，在 readiness 检查后停止并断言专用失败、继续处理与资源释放 |
| P3 | ⏳ 实验未启动 | direct intrinsics 实验 | 当前由运行时分派 | 显式 gate、`IsSupported`、scalar fallback、AOT、跨架构差分，收益不足即不合入 |
| P3 | ⏳ 实验未启动 | .NET 11 preview 实验 | 默认未使用 | 独立项目/条件编译；不得成为默认生产依赖 |

## 正确性与发布门禁

本轮最终验证结果如下；“测试通过”不改变九域固定硬件、ARM64、木垒同语料与生产门禁的 ⏳ `NOT_RUN` 状态。

| 门禁 | 最终结果 | 边界 |
| --- | --- | --- |
| Core 全量 | ✅ `3990/3990 PASS` | 本机 Release；不替代固定硬件性能门禁 |
| Core targeted | ✅ `33/33 PASS` | TableStatistics、TableIndexEntryKeyLength、CRC32、SqlExecutionMetrics；是 Core 全量的子集，不可相加 |
| Benchmark tests | ✅ `25/25 PASS` | runner/schema/nearest-rank 等自动化合同 |
| Server 全量 | ✅ `722/722 PASS` | 含 Sparkplug 最终代码形状 |
| Sparkplug targeted | ✅ `12/12 PASS` | 是 Server 全量的子集，不可相加；真实 broker 状态翻转仍缺 |
| Release solution | ✅ `PASS` | `/warnaserror`，`0 warnings / 0 errors` |
| win-x64 Native AOT CLI | ✅ publish/run `PASS` | `--version` exit 0，输出 `SonnetDB CLI 0.0.0-dev+424f61ad16e883d6b9050a9eb29a352105d28cef` |
| win-x64 Native AOT Server | 🟡 publish ✅ `PASS`；启动 ⏳ `NOT_RUN` | 只证明产物可发布，未证明启动、首查或恢复 |
| 固定硬件 x64 性能 | ⏳ `NOT_RUN` | 容量、并发拐点和尾延迟未建立 |
| ARM64 Native AOT | ⏳ `NOT_RUN` | 只配置了 CI matrix，不宣称执行成功 |
| 固定硬件 ARM64 性能 | ⏳ `NOT_RUN` | SIMD/CRC/scalar、RSS、I/O 与并发饱和点未验证 |
| CI YAML | 🟡 syntax ✅ `PASS`；其余 ⏳ | YamlDotNet 16.3.0：1 个 document、5 个 root entries；actionlint、GitHub schema 和真实 CI 未运行 |
| solution-wide format | ❌ `NOT_PASS`（exit 2） | dirty `SqlExecutionMetrics.cs` 的 scoped whitespace verify 已通过；剩余仅为未在本轮修改且不在 dirty 列表的 `TableManager.cs`、`M27LocalOnnxEvidenceTests.cs` 既有 whitespace 差异，因此不得写全解 format PASS |
| 木垒同语料 | ⏳ `NOT_RUN` | 未连接或修改生产；实际查询分布与资源竞争未复测 |
| 168 小时 mixed workload | ⏳ `NOT_RUN` | 长稳、恢复、文件增长和回滚门禁未执行 |
| 生产发布 | ⏳ `NOT_RUN` | 未部署、未执行 DDL/DML、未重启现场服务 |

统计测试包含所有标量类型、复合/唯一索引、NULL/empty、Unicode/BLOB、JSON path、确定性随机行、复合主键、取消和取消后复用；CRC 覆盖 golden/random/unaligned/boundary/1 MiB；物理读快照覆盖稳定、并发、降级与溢出合同。

每项后续优化必须同时满足：

1. 与已验证路径逐行/逐字节差分；ANN 另报 Recall@K。
2. 取消、异常、超时和资源不足不返回部分结果，不泄漏 permit、buffer、spill 目录或文件句柄。
3. 请求、worker、I/O 和同实体写竞争各有独立上界；任何队列都声明 full mode 和拒绝/丢弃指标。
4. x64 与 ARM64 都有 scalar 对照；不以 ISA 名称或 CPU 型号推断 `IsSupported`。
5. 固定硬件、木垒同语料和生产 gate 缺一项都保持 ⏳ `NOT_RUN`，不能人工改为 ✅ PASS。

## Evidence -> Finding -> Path

| Evidence | 不可变观察 | 来源/哈希 |
| --- | --- | --- |
| E-001 | 外层 HEAD/父提交、SonnetDB HEAD/祖先、dirty 与 gitlink 均已核对 | `git rev-parse/show/merge-base/status` |
| E-002 | README 是八模型；M40 发布 gate 未完成 | `README.md` SHA256 `943ff2...0a3`；`ROADMAP.md` SHA256 `84b0df...276` |
| E-003 | statistics before/最终 dirty 为 54.81/51.25 ms、34.72/22.67 MB | CSV SHA256 `bbd6d3...5cda` / `503c5d...a4a8d`；`50.89 ms` 为较早中间重跑 |
| E-004 | CRC production 三个尺寸 before/after 数值 | CSV SHA256 `ec6352...6614` / `bd9d45...7ce` |
| E-005 | KV/Document/Object 独立模型 smoke | CSV SHA256 `554dd4...cc1` / `f9358d...244` / `bfb95e...04f` |
| E-006 | M41 before/after 正确性、路径、放大和分配一致 | before SHA256 `894bab...2d4`；after SHA256 `28b195...326` |
| E-007 | 物理读冻结有 64 次/5 ms 上限并暴露 degraded | `SqlExecutionMetrics.cs`, `SqlQueryDiagnostics.cs`, DTO/aggregate tests |
| E-008 | Rebirth 有界、合并、单消费者、拒绝/回收；broker readiness 有界、专用异常且未知异常不被重分类 | `SparkplugRebirthQueue.cs`, `SparkplugHostApplicationService.cs`, `SonnetMqttBrokerBridge.cs` 及 12 个 targeted tests |
| E-009 | ARM64 Native AOT CI matrix 已配置；YAML syntax 与 matrix/path 手工核对通过，真实 CI/ARM64 未执行 | `.github/workflows/ci.yml`；YamlDotNet 16.3.0 |
| E-010 | 六类模型热读各 32 次预热、256 个逐请求样本的 nearest-rank P50/P95/P99 可由原始样本复算 | `model-read-latency-current-final/model-read-latency.json` SHA256 `6e189c...116` |
| E-011 | 最终本机测试、Release、win-x64 AOT 与 format 残留边界 | Core 3990/3990、targeted 33/33、Benchmark 25/25、Server 722/722、Sparkplug 12/12；Release 0/0；CLI/Server AOT publish PASS；format exit 2 仅剩两处既有文件 |

Findings：F-001（E-001/E-002，高置信）确认基线与八模型口径；F-002（E-003，高置信）确认最终 dirty statistics 本机方向性收益；F-003（E-004，高置信）确认 CRC32 本机方向性收益与格式不变；F-004（E-005/E-006/E-010，中置信 candidate）确认模型 benchmark 与真实请求尾延迟证据面已扩展但容量证据不足；F-005（E-007/E-008，高置信）确认两项并发路径具备有限等待/队列及显式失败分类合同，但 Sparkplug 仍缺真实 broker 状态翻转集成测试；F-006（E-009，中置信 candidate）确认 ARM64 门禁配置存在，但执行证据仍缺失；F-007（E-011，高置信）确认本机测试、Release 与 win-x64 AOT 通过，同时明确 solution-wide format gate 因两处既有 whitespace 差异未通过。

交付路径 P-001：先冻结 dirty 身份与产品边界（E-001/E-002），再测公共热路径（E-003/E-004），然后增加模型级 BDN/逐请求 smoke 与有界并发（E-005～E-008/E-010），配置 ARM64 AOT CI 入口（E-009），并执行本机测试/Release/x64 AOT/格式门禁（E-011），最终进入固定 x64/ARM64、木垒同语料和生产 gate。当前目标环境终点仍是 ⏳ `NOT_RUN`，这是残余风险而不是完成声明。

## 复现入口

以下命令应在 PowerShell 7、SonnetDB 仓库根目录执行；每个 benchmark 自带固定迭代，外部调度仍应设置进程超时并记录 PID/父 PID/开始时间/命令行。

```powershell
# 统计刷新
dotnet run -c Release --project tests/SonnetDB.Benchmarks/SonnetDB.Benchmarks.csproj -- `
  --filter '*TableStatisticsRefresh*' `
  --artifacts artifacts/system-performance-20260901/repro/statistics/bdn

# IEEE CRC32
dotnet run -c Release --project tests/SonnetDB.Benchmarks/SonnetDB.Benchmarks.csproj -- `
  --filter '*VectorCrc32*' `
  --artifacts artifacts/system-performance-20260901/repro/crc32/bdn

# KV / Document / Object 独立模型
dotnet run -c Release --project tests/SonnetDB.Benchmarks/SonnetDB.Benchmarks.csproj -- `
  --filter '*KvModel*' '*DocumentModel*' '*ObjectStorageModel*' `
  --artifacts artifacts/system-performance-20260901/repro/model-smoke/bdn

# KV / Document / Object 逐请求尾延迟
dotnet run -c Release --project tests/SonnetDB.Benchmarks/SonnetDB.Benchmarks.csproj -- `
  --model-read-latency-evidence --quick `
  --output artifacts/system-performance-20260901/repro/model-read-latency

# M41 quick，只验证本地查询/报告合同
dotnet run -c Release --project tests/SonnetDB.Benchmarks/SonnetDB.Benchmarks.csproj -- `
  --m41-baseline-evidence --quick `
  --output artifacts/system-performance-20260901/repro/m41/evidence

# 机器可读报告语法
Get-Content -LiteralPath artifacts/system-performance-20260901/system-performance-report.json -Raw | Test-Json
```

任何复跑都应写入新目录，不能覆盖本报告引用的 before/after 原始 artifact。
