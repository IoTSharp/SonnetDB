---
title: 架构深度解析：SonnetDB 的写入与查询路径
categories: SonnetDB,存储引擎,WAL
draft: false
---

理解写入、读取和段发布的顺序，有助于选择持久性配置和排查恢复问题。SonnetDB 的 measurement 存储采用 WAL、MemTable、不可变 Segment 与后台 Compaction 组成的 LSM 风格路径。本文讨论时序存储，关系表、文档、对象和 MQ 保留各自合同，不能把这条路径直接套到所有模型。

SonnetDB 已正式发布 [4.0.0](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。以下链接展示开发主线实现，后续改动以实际版本为准；性能数字和生产恢复保证需要对应版本的独立证据。

## 写入：先记录日志，再进入内存

measurement 写入由 SQL、Point/批量 API、ADO.NET 或 HTTP 入口进入。通过 schema 与类型检查后，先追加 WAL，再写入 MemTable，后台或显式 Flush 将内存数据写为不可变 Segment。

当前 MemTable 按 `SeriesId + FieldName` 建桶，桶中保存相应点与统计量，并提供查询快照。它不是旧文所猜测的“通常为跳表或有序树”；实际数据结构和并发合同可以直接从源码检查。

```text
写入 -> schema/类型检查 -> WAL -> MemTable
                                  |
                                 Flush
                                  |
                            immutable Segment
```

## “写入成功”的持久性分级

`TsdbOptions` 对 WAL 提供两个关键选项：

| 配置 | 写入确认后的 WAL 所在位置 | 关注点 |
| --- | --- | --- |
| `FlushWalToOsOnWrite=false`，且不启用每批同步 | 可能仍在进程缓冲 | 进程崩溃可能丢失最近的缓冲写入 |
| `FlushWalToOsOnWrite=true`（默认），不启用每批同步 | 交给 OS page cache | 与介质持久化不同，掉电风险仍须处理 |
| `SyncWalOnEveryWrite=true` | 按 group-commit 路径同步 WAL | 更强持久性，写延迟和吞吐需在目标介质实测 |

Flush、文件同步和目录发布共同参与恢复顺序。文件系统、操作系统和存储设备的行为也影响最终结果，不能把进程 kill 测试外推成任意断电零丢失承诺。

## 查询：合并内存与持久段

SQL 经过解析、名称绑定和计划后，查询根据 measurement schema、series 与过滤条件选取候选数据，读取 MemTable 快照和 Segment，再应用 tombstone 与查询表达式。

SegmentReader 可以利用 series/block 索引和时间范围跳过不相关数据。缓存与 reader 快照也有自己的更新和失效周期，因此不能把查询概括成“每次顺序扫描全部文件”。结果顺序、聚合和分组以具体 SQL 形状为准；需要稳定时间顺序时显式使用 `ORDER BY time`。

```sql
SELECT time, usage
FROM cpu
WHERE host = 'server-01'
  AND time >= 1713676800000
  AND time < 1713680400000
ORDER BY time;
```

## Compaction：合并段与消化 tombstone

当前默认策略为 Size-Tiered，由文件大小和 tier 条件形成计划，并不是旧稿所说的 Leveled 策略。Compaction 合并段、处理删除与保留策略，随后发布替换结果。

主线的段替换清单记录 pending/committed 状态，以区分未提交的新段和已被替换的旧段。查询读取的 segment 快照与替换发布需要保持一致，后台执行也会消耗 CPU、内存和 I/O，不能预先声称对前台影响极小。

## 架构之外的运维边界

备份应使用受支持的目录备份与校验流程。数据库目录不是单个数据文件；Server 的 SonnetMQ 物理日志位于实例级 `.system/mq`，单库备份不覆盖它。Graph 仍为 Beta，容量和长期运行验证也不能由 WAL/Segment 路径的测试推导。

参考：[架构文档](https://github.com/IoTSharp/SonnetDB/blob/main/docs/architecture.md)、[TsdbOptions](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Engine/TsdbOptions.cs)、[MemTable](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Memory/MemTable.cs)、[CompactionPlanner](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Engine/Compaction/CompactionPlanner.cs)和[可靠性变更](https://github.com/IoTSharp/SonnetDB/blob/main/docs/performance-reliability-updates.md)。
