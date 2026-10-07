---
title: '深入 T-Digest：分位数聚合与 percentile'
categories: SonnetDB,数据库
draft: false
---

正式发行见 [4.0.0 Release](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。本文按 2026-10-07 当前 main 文档与源码修订旧稿；主线改动不自动代表全部 v4 发行物与客户端的已验收能力。以下 SQL/API 为复现示例，本次没有实际执行或性能测量。

延迟分布通常有长尾，平均值会掩盖少数慢请求。SonnetDB 的 percentile、median 和 p50/p90/p95/p99 通过 T-Digest 估算分位数。

## 参数使用百分数

假设 requests 已声明 service TAG、latency FIELD FLOAT：

```sql
SELECT count(latency), avg(latency),
       percentile(latency, 95), p95(latency), p99(latency)
FROM requests
WHERE service = 'api'
  AND time >= 1700000000000 AND time < 1700003600000;
```

第二个参数 q 的范围为 `(0,100]`。95 表示第 95 百分位；0.95 表示第 0.95 百分位，二者不能混用。空输入返回 NULL。

## 摘要保存什么

T-Digest 把值分布压缩为带权重的质心，以减少保存全部样本的需求。它适合近似分位统计，准确度会随数据分布、压缩和样本数变化，不应把 p99 估计描述成精确次序统计量。

```sql
SELECT tdigest_agg(latency)
FROM requests WHERE service = 'api';
```

当前 tdigest_agg 返回 JSON 字符串，质心以 `[mean,weight]` 数组表示。它不是可任意嵌套进 percentile 的 SQL 中间类型；内部支持合并状态，不等于 SQL 已支持 `percentile(tdigest_agg(...),95)`。

## 分桶结果如何使用

```sql
SELECT time, count(latency), p95(latency), p99(latency)
FROM requests WHERE service = 'api'
GROUP BY time(1m);
```

分钟 p95 的平均值不等于整小时 p95，因为各分钟样本量和分布不同。需要小时指标时，应直接对小时范围内的原始 latency 聚合。评估误差时保留同一批数据的精确排序结果；本次没有执行精度或性能测量。

依据：[sql-reference.md](https://github.com/IoTSharp/SonnetDB/blob/main/docs/sql-reference.md)、[ExtendedAggregateFunctions.cs](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Query/Functions/Aggregates/ExtendedAggregateFunctions.cs)、[TDigest.cs](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Query/Functions/Aggregates/TDigest.cs)。
