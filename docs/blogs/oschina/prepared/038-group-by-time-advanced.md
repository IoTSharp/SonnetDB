---
title: '高级时间桶聚合：同一时间窗口内的多指标组合分析'
categories: SonnetDB,数据库
draft: false
---

正式发行见 [4.0.0 Release](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。本文按 2026-10-07 当前 main 文档与源码修订旧稿；主线改动不自动代表全部 v4 发行物与客户端的已验收能力。以下 SQL/API 为复现示例，本次没有实际执行或性能测量。

同一个时间桶中组合样本数、平均值、峰值和分位数，能把吞吐与尾部波动放到同一时间轴。关键是先保证指标来自同一输入范围。

## 多指标投影

假设 service_metrics 包含 service TAG、latency FIELD FLOAT、requests FIELD INT：

```sql
SELECT time, count(latency), avg(latency), max(latency),
       p95(latency), sum(requests)
FROM service_metrics
WHERE service = 'checkout'
  AND time >= 1700000000000 AND time < 1700003600000
GROUP BY time(5m)
ORDER BY time;
```

requests 应表示每次采样的业务增量。如果它是累计计数器，直接 SUM 会重复累加历史数量，应先明确采集模型并在应用或独立处理阶段计算增量。latency 与 requests 是稀疏 FIELD 时，两项指标也可能具有不同的有效样本数。

## FIRST/LAST 不是任意跨序列选择

对一个确定 series，可以取时间桶内首末状态；当前 FIRST/LAST 保留多 series 拒绝边界。service 如果不是唯一 TAG，还应约束其它 TAG，避免把查询示例误当作任意服务维度分组合同。

## 组合后的处理顺序

先获得分桶结果，再在应用中计算指标比率、告警和可视化。本文不使用 measurement HAVING、嵌套窗口聚合或 PostgreSQL 的 FILTER/time_bucket 语法；这些入口不能因为相似数据库支持就推断为 SonnetDB 支持。

空样本、NULL、分母为零和低样本桶都要保留可解释状态。p95 是摘要估计值，分钟 p95 的再平均不会成为整段 p95。

依据：[sql-reference.md](https://github.com/IoTSharp/SonnetDB/blob/main/docs/sql-reference.md)、[m45-measurement-tag-grouping.md](https://github.com/IoTSharp/SonnetDB/blob/main/docs/benchmarks/m45-measurement-tag-grouping.md)。
