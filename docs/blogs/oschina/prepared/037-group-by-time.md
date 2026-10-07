---
title: '按时间段分组：GROUP BY time() 时间桶聚合'
categories: SonnetDB,数据库
draft: false
---

正式发行见 [4.0.0 Release](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。本文按 2026-10-07 当前 main 文档与源码修订旧稿；主线改动不自动代表全部 v4 发行物与客户端的已验收能力。以下 SQL/API 为复现示例，本次没有实际执行或性能测量。

GROUP BY time() 将采样划分到固定宽度时间桶。它适合仪表盘降采样，但桶宽不是返回行数上限，也不是一个滑动窗口。

## 显式取回桶起始时间

假设 cpu 已声明 host TAG、usage FIELD FLOAT：

```sql
SELECT time, count(usage), avg(usage), max(usage)
FROM cpu
WHERE host = 'server-01'
  AND time >= 1700000000000 AND time < 1700003600000
GROUP BY time(1m)
ORDER BY time;
```

时间以 Unix 毫秒表达，桶宽使用 duration 字面量，例如 1000ms、30s、1m。投影中省略 time 时，查询不会自动添加桶时间列。采用左闭右开的查询范围，能避免相邻批次重复计入边界采样。

## 空桶与缺失值

分组不是生成完整时间轴的日历函数。不能假设没有数据的分钟会自动产生一行零值，也不能认为 `fill` 可以补出未存在的时间戳。需要完整分钟轴时，应用应先生成目标轴，再关联已有桶结果，并明确 NULL 与真实零值的区别。

## TAG 分组的当前边界

当前 main 已有 TAG 与 time 组合分组的部分实现，不应沿用旧稿“完全不支持 TAG 分组”的绝对说法；其 HAVING、复合投影、排序和预算限制见 M45-C01 合同。本文采用既有 time-only 路径，不能把主线切片等同于 v4 所有发行物完成验收。

LIMIT 在聚合后分页，减少返回行数不代表提前停止扫描或限制全部分组状态。窗口跨度、桶数量与每桶累加器状态仍需按实际工作负载测量。

依据：[sql-reference.md](https://github.com/IoTSharp/SonnetDB/blob/main/docs/sql-reference.md)、[m45-measurement-tag-grouping.md](https://github.com/IoTSharp/SonnetDB/blob/main/docs/benchmarks/m45-measurement-tag-grouping.md)。
