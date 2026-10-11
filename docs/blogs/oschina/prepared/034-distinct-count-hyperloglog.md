---
title: 使用 HyperLogLog 进行基数估计：distinct_count() 函数详解
categories: SonnetDB,数据库,聚合
draft: false
---

唯一值数量常用于访问统计、设备状态分析和会话报表。SonnetDB 的 `distinct_count(field)` 使用 HyperLogLog 估算基数；`count(DISTINCT field)` 则在支持的聚合路径中做精确去重。选择哪一种，取决于业务允许的误差和实际资源成本。

正式发行见 [4.0.0 Release](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。本文依据当前 `main` 的文档和源码修订旧稿，不能把主线改动自动算作所有旧客户端与 Server 的能力；以下 SQL 是复现示例，本次未实际执行。

## HyperLogLog 如何工作

HLL 把输入值哈希到寄存器，记录比特串的统计特征，再估算唯一值数量。当前实现精度参数为 p=14，共 16,384 个寄存器；源码使用 `byte[]`，寄存器数组本身约 16 KiB，不是旧稿所称的 12 KiB，也不代表整条 SQL 的总内存。

公式 `1.04 / sqrt(16384)` 对应约 0.81% 的理论标准误差。它不是每一次查询的误差上限，更不是固定硬件上的性能测量。小基数还使用线性计数修正。

## 用 measurement FIELD 做去重统计

measurement 的时间列是 `time`，TAG 和 FIELD 应先声明。以下示例保留会话去重主题，采用当前支持的语法：

```sql
CREATE MEASUREMENT web_sessions (
    host TAG,
    session_id FIELD STRING
);

INSERT INTO web_sessions (time, host, session_id)
VALUES (1713676800000, 'web-01', 'session-a'),
       (1713676810000, 'web-01', 'session-b'),
       (1713676820000, 'web-01', 'session-a');

SELECT count(DISTINCT session_id) AS exact_count,
       distinct_count(session_id) AS estimated_count
FROM web_sessions
WHERE host = 'web-01'
  AND time >= 1713676800000
  AND time < 1713763200000;
```

已有 measurement 时不重复执行 CREATE。当前 `distinct_count` 接受 Float64、Int64、Boolean 和 String FIELD，不接受 Vector 或 GeoPoint。它返回基数估算数值，不返回可供应用再次聚合的 HLL 草图。

## 按小时观察基数

```sql
SELECT time,
       count(DISTINCT session_id) AS exact_count,
       distinct_count(session_id) AS estimated_count
FROM web_sessions
WHERE host = 'web-01'
  AND time >= 1713676800000
  AND time < 1713763200000
GROUP BY time(1h);
```

显式投影 `time` 才会返回桶起始时间。计算误差百分比时，应用可以对两个结果做除法，并单独处理精确计数为 0 的情况；本例不借用 PostgreSQL 的 `::float`、`INTERVAL`、`time_bucket` 或未核对的派生表语法。

## 草图合并的边界

内部 HLL 可以逐寄存器合并，但这不意味着 `distinct_count(distinct_count(...))` 可以合并 SQL 返回值。不同小时的估算数相加也不能得到一天的去重人数，因为同一会话可能跨小时出现。需要日粒度结果时，应直接对原始 FIELD 的日范围聚合。

高基数状态、不同值类型与数据分布都应使用目标机器验证；不能从寄存器数组大小推断执行扫描、结果、缓存或整个进程均为固定内存。

依据：[SQL 聚合合同](https://github.com/IoTSharp/SonnetDB/blob/main/docs/sql-reference.md)、[HLL 实现](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Query/Functions/Aggregates/HyperLogLog.cs)、[扩展聚合](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Query/Functions/Aggregates/ExtendedAggregateFunctions.cs)。
