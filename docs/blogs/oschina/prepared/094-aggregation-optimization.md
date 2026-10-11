---
title: SonnetDB 聚合查询优化：跨桶融合与 MemTable 增量聚合
categories: SonnetDB,聚合,性能
draft: false
---

聚合优化的关键是减少重复读取，同时保持时间范围、删除可见性和分桶结果正确。SonnetDB 当前查询引擎支持多聚合共享扫描、符合条件的 block 元数据利用，以及 MemTable 数值聚合快路径。

## 一次查询计算多个统计量

```sql
CREATE MEASUREMENT aggregation_demo (host TAG, usage FIELD FLOAT);
INSERT INTO aggregation_demo (time, host, usage) VALUES
    (1700000000000, 'server-01', 42.5),
    (1700000001000, 'server-01', 61.2),
    (1700000002000, 'server-01', 85.0);

SELECT avg(usage) AS average_usage,
       min(usage) AS minimum_usage,
       max(usage) AS maximum_usage,
       count(usage) AS sample_count
FROM aggregation_demo
WHERE host = 'server-01'
GROUP BY time(1m);
```

`ExecuteMultiAggregate` 可以一趟累加 count、sum、min、max、avg，避免为同一输入分别扫描。`first/last` 依赖有序首末值，不能直接套用这条共享数值统计路径。

## 元数据利用有明确门控

block 必须完整落入查询范围，并持有所需 sum/count 与 min/max 元数据。对分桶查询，还必须确保 block 的起止时间处于同一个桶，才能把整块摘要直接并入桶。

跨桶的 block、只覆盖部分时间的范围或缺少所需元数据，都不能无条件使用整块统计。当前实现有解码回退路径，而不是所有聚合都“无需读取原始点”。

存在相关 Tombstone 时，多聚合逐点路径复用点查询的合并与删除语义。删除可见性不能为了快路径被忽略。

## MemTable 的增量统计

MemTable 数值数据可提供运行期统计快照。只有统计切片整体位于查询范围内等条件满足时，查询才直接利用它；其它范围仍需按切片读取。

因此，全范围、边界范围、乱序输入与删除后的查询，应分别验证结果与开销。标题中的“跨桶融合”应理解为聚合状态的组织与合并主题，不能扩展成任意表达式都能预计算的保证。

## 验证优化的方法

```bash
dotnet run -c Release --project tests/SonnetDB.Benchmarks -- --filter '*Aggregate*'
```

将结果与逐点参考计算对拍，再记录冷热缓存、段布局、桶宽、过滤和删除状态。平均值应从总和与计数形成，不能简单平均各段平均值。

旧底稿中的百倍提升、固定延迟和分位数成绩未取得本次可核验的完整报告，本文不继续引用。近似分位数也应单独标注算法和误差，不能当作基础精确聚合的同一合同。

参考：[QueryEngine](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Query/QueryEngine.cs)、[MemTableSeries](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Memory/MemTableSeries.cs)、[基准说明](https://github.com/IoTSharp/SonnetDB/blob/main/tests/SonnetDB.Benchmarks/README.md)。
