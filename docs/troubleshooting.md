---
layout: default
title: "故障排查"
description: "SonnetDB 慢查询定位、Diagnostic Dump 解读与运行时故障排查指南。"
permalink: /troubleshooting/
---

# 故障排查

排查顺序建议保持一致：先看 `/healthz/ready` 判断依赖是否可用，再用 metrics 确认异常时间窗，用 trace 定位路径，最后采集 Diagnostic Dump 查看当时的进程和数据库 metadata。Diagnostic Dump 不替代持续监控，也不包含用户数据点、WAL 内容、measurement 名或绝对路径。

## SQL 请求过载保护

`POST /v1/db/{db}/sql`、`/sql/batch` 与 `Protocol=frame-http2` 的 SQL query 帧共享同一个数据库级并发限制。每个数据库默认允许 4 个执行中请求和 8 个异步排队请求；REST 队列满后立即返回 `503 Service Unavailable`、`Retry-After: 1`，且不会先读取或反序列化请求体。frame-http2 队列满后保持 HTTP 200 帧协议语义，返回原 `streamId` 的 `sql_overloaded` 错误帧；若 HTTP 响应尚未开始，也附带 `Retry-After: 1`。

容器现场可通过环境变量调整：

```text
SONNETDB_SonnetDBServer__SqlHttpAdmission__PermitLimit=4
SONNETDB_SonnetDBServer__SqlHttpAdmission__QueueLimit=8
```

许可数最小为 1、最大为 256；队列最小为 0、最大为 4096。客户端收到 `sql_overloaded` 时应遵守 `Retry-After` 并使用有界重试，不应无间隔立即重放。

## 关系表 `IN (SELECT ...)` 更新与删除

关系表 `UPDATE` / `DELETE` 支持以普通关系表或 measurement 为数据源的非相关、单列 `IN (SELECT ...)` 与 `NOT IN (SELECT ...)`。子查询在外层行遍历前只执行一次，参数、`ORDER BY`、`LIMIT`、空结果和 `NULL` 三值逻辑均按普通 `SELECT` 处理；多列结果与相关子查询会在执行重型扫描前明确拒绝。Document、information schema、TVF、vector/hybrid 等暂未建立静态绑定合同的数据源会在扫描前返回明确的“不支持”错误。正向单列主键 `IN` 会把物化结果转为主键点读，避免外层再次全表解码宽行。

该快路只消除外层重复扫描，不改变内层 `SELECT` 的访问路径。如果内层按无索引列过滤或排序，`TableStore` 仍需扫描并解码完整行；图片、视频或大 Base64 列所在宽表不应以秒级周期运行这类清理 SQL。应先降低调度频率、使用有界批次，并通过访问路径计数或实际内存指标确认内层扫描成本可接受。轻事务中目标表已有缓冲写时，带 `IN` 子查询的 `UPDATE` / `DELETE` 会被明确拒绝，避免内外层事务视图不一致。

## 慢查询入口

慢查询采集默认开启，基础、警告和严重阈值分别为 10、30、60 秒。可以通过配置调整：

```text
SONNETDB_SonnetDBServer__Observability__SlowQueryLog__Enabled=true
SONNETDB_SonnetDBServer__Observability__SlowQueryLog__ThresholdMs=10000
SONNETDB_SonnetDBServer__Observability__SlowQueryLog__WarningThresholdMs=30000
SONNETDB_SonnetDBServer__Observability__SlowQueryLog__CriticalThresholdMs=60000
```

有数据库读权限的用户只能看到自己可读数据库的记录；admin 可以查看全部数据库和控制面 SQL。

```http
GET /v1/diagnostics/slow-queries?database=plant&limit=100
GET /v1/diagnostics/top-queries?database=plant&limit=20
```

先按 `fingerprint` 聚合同形 SQL，比较 `p50Ms`、`p95Ms`、`maxMs` 和 `failedCount`。单次尖峰优先对照 trace；稳定高 P95 再检查查询形状、Segment 数量和并发资源。

## 常见慢查询模式

| 现象 | 常见原因 | 核查与处理 |
| --- | --- | --- |
| Raw SELECT 返回量持续增大 | 缺少时间范围或 `LIMIT`，跨多个 series/field 物化大量行 | 增加窄时间窗与分页；监控 `query.duration`、Segment read bytes |
| `OFFSET` 越大越慢 | 引擎仍需读取并跳过前面的结果 | 优先按 time/业务键续查，避免深分页 |
| 聚合读块突然增加 | 范围只覆盖 Block 的一部分，无法使用完整 Block metadata 快路径 | 对齐查询时间窗或接受部分块解码；对照 `segment.block.reads` |
| 带残差、跨字段或 Geo 条件的聚合较慢 | 为保证正确性需要逐点过滤，不能走全部聚合下推 | 缩小时间范围，减少无关字段，确认谓词落在正确列上 |
| `ORDER BY`、高基数 TAG 或宽投影占用高 | 排序、series 展开和结果编码成本上升 | 只投影需要的列，使用有限 `LIMIT`，拆分高基数查询 |
| 冷查询慢、第二次明显变快 | OS page cache 或 Block decode cache 首次未命中 | 区分冷/热基准；物理读取会出现 `sonnetdb.segment.read`，缓存命中时不会产生该 span |
| Flush 期间写入或查询抖动 | MemTable 达阈值后编码落盘，磁盘或 WAL fsync 延迟较高 | 对照 `flush.pending`、`flush.duration`、`wal.fsync.duration` 与磁盘延迟 |
| Segment 数长期增长 | Compaction 跟不上写入或存储吞吐不足 | 查看 `segments.count`、dump 的 `pendingCompactionTasks` 与 compaction span |
| Copilot 查询慢但 SQL 本身快 | provider、工具轮次或回答生成占主要耗时 | 在 trace 中拆分 `copilot.chat`、`run_tool`、Core query 与 provider HTTP span |

不要只根据一次 `maxMs` 扩容。相同 fingerprint 的 P95、物理读字节、Segment 数和 ThreadPool 积压同时上升，才更像持续资源瓶颈。

## 采集 Diagnostic Dump

端点默认不映射，需显式启用并重启 Server：

```text
SONNETDB_SonnetDBServer__Observability__DiagnosticDump__Enabled=true
```

该端点仅允许 admin 访问。可直接请求，也可用 CLI 保存到文件：

```powershell
$env:SONNETDB_DIAG_URL = "http://127.0.0.1:5080"
$env:SONNETDB_DIAG_TOKEN = "<admin-token>"
sndb diag dump --output sonnetdb-dump.json
```

在异常期间连续采集 2 至 3 份、间隔几十秒，比单个快照更容易判断计数是在增长还是保持稳定。Dump 可能包含数据库名与 WAL 文件名，分享前仍应按组织策略审查。

## Dump 字段解读

### Process 与 GC

| 字段 | 如何判断 |
| --- | --- |
| `workingSetBytes` | 进程实际驻留内存；明显高于托管 heap 时，同时考虑 mmap、文件缓存和 native 组件 |
| `totalMemoryBytes` / `heapSizeBytes` | 当前托管对象与 GC heap 规模；连续增长且 Gen2 同步增长时检查长期存活对象 |
| `fragmentedBytes` | 与 `heapSizeBytes` 比例持续升高表示碎片化压力，单次快照不能证明泄漏 |
| `memoryLoadBytes` / `highMemoryLoadThresholdBytes` | 接近阈值表示 GC 已感知系统内存压力 |
| `pinnedObjectsCount` | 持续上升可能限制 GC compaction，应与 workload 和 trace 对照 |
| `finalizationPendingCount` | 长期积压说明终结器处理跟不上或资源释放延迟 |
| `gen0/1/2Collections` | 两份 dump 的增量比绝对值更有意义；频繁 Gen2 通常表示长期对象或内存压力 |

### ThreadPool

`pendingWorkItemCount` 持续增长且 `availableWorkerThreads` 接近 0，说明线程池饥饿或同步阻塞。若 worker 充足而 `availableCompletionPortThreads` 很低，优先检查网络与异步 I/O。`completedWorkItemCount` 在两份 dump 间不增长则可能已经停滞。

### 数据库

| 字段组合 | 可能含义 |
| --- | --- |
| `memTablePointCount`、`memTableEstimatedBytes` 增长且 `pendingFlushTasks=0` | 尚未达到 Flush 阈值，通常正常 |
| MemTable 增长且 `pendingFlushTasks` 持续大于 0 | Flush 落后，检查磁盘、编码耗时和 `flush.duration` |
| `segmentCount` 与 `pendingCompactionTasks` 同时增长 | Compaction 吞吐跟不上或策略暂未收敛 |
| 活跃 WAL `fileLength` 增长 | 正常写入；需结合 `checkpointLsn` 是否推进判断回收是否正常 |
| 多个非活跃 WAL 长期保留且 checkpoint 不推进 | Flush/checkpoint 可能受阻，检查 Flush 错误日志和存储健康 |
| `copilot.inFlightSessions` 长期偏高 | provider 慢、工具轮次多或客户端并发高，继续查看 Copilot trace |

`pendingCompactionTasks` 是按当前稳定 Segment 快照可立即规划的任务数，不是后台队列的精确长度。WAL metadata 只报告文件名、长度、LSN 范围与 active 状态，不读取记录正文。

## 快速决策

1. readiness 存储项 `Unhealthy`：先保护数据并修复磁盘、权限或挂载，不继续压测。
2. readiness 仅 provider `Degraded`：数据库仍可服务，隔离 Copilot 故障并检查 endpoint/token。
3. 查询 P95 与 Segment read bytes 同涨：先缩小查询范围、检查分页与 Segment/Compaction 状态。
4. ThreadPool pending 增长但磁盘正常：查找同步阻塞和外部 provider 延迟。
5. 指标恢复但原因不明：保留同时间窗的 trace、结构化日志、Top-N 与 dump，避免只保留截图。

指标、span 名称和本地观测栈配置见[可观测性]({{ site.docs_baseurl | default: '/help' }}/observability/)。
