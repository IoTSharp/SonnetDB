---
title: '统计聚合函数：stddev/variance/spread/median/mode'
categories: SonnetDB,数据库
draft: false
---

正式发行见 [4.0.0 Release](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。本文按 2026-10-07 当前 main 文档与源码修订旧稿；主线改动不自动代表全部 v4 发行物与客户端的已验收能力。以下 SQL/API 为复现示例，本次没有实际执行或性能测量。

平均值回答整体水平，标准差、极差和分位数回答波动与分布。SonnetDB 提供这些聚合时，需要先明确“样本”语义。

## 一次观察多项统计

假设 sensors 已声明 device TAG、temperature FIELD FLOAT、status FIELD STRING：

```sql
SELECT count(temperature), avg(temperature),
       stddev(temperature), variance(temperature),
       spread(temperature), median(temperature), mode(status)
FROM sensors
WHERE device = 'sensor-01'
  AND time >= 1700000000000 AND time < 1700003600000;
```

stddev 和 variance 使用样本统计，分母为 n-1；有效样本不足两个返回 NULL。不要把它们直接当作总体方差，或者在单点输入下期望返回 0。底层使用可合并的统计状态，仍需注意数值范围与输入质量。

## 每个函数分别回答什么

| 函数 | 含义 | 需要注意 |
|---|---|---|
| stddev | 样本标准差 | 与原字段单位相同 |
| variance | 样本方差 | 单位为原单位的平方 |
| spread | 最大值减最小值 | 容易受离群点影响 |
| median | 中位数估计 | 使用 T-Digest，不保证精确排序中位数 |
| mode | 最常见值 | 并列时选择最小值；字符串按 Ordinal |

数值统计接受数值 FIELD，mode 另支持 Boolean 和 String。String 的最常见状态可以用于告警分类，不能拿字符串温度直接计算标准差。

## 让比较有意义

先确定同一设备、同一时间范围与相同采样策略，再比较波动。如果两台设备的采样频率不同，混合聚合会按样本数量加权，不是每台设备等权。稀疏 FIELD 的有效样本数量也应同时投影。

按分钟比较时可以加 `GROUP BY time(1m)` 并显式投影 time。生产阈值应由业务数据和误报成本确定，本文不把示例统计量换成未经测量的告警保证。

依据：[sql-reference.md](https://github.com/IoTSharp/SonnetDB/blob/main/docs/sql-reference.md)、[ExtendedAggregateFunctions.cs](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Query/Functions/Aggregates/ExtendedAggregateFunctions.cs)。
