---
title: SELECT 查询基础：投影、标签过滤与时间范围
categories: SonnetDB,SQL,时序
draft: false
---

查询时序数据通常从三个选择开始：读哪些列、读哪些设备、读哪个时间段。SonnetDB 的 `SELECT` 用投影表达返回列，用 `WHERE` 表达输入范围，适合把这三个边界直接写清楚。

## 准备一个温湿度示例

在练习数据库中建立 measurement 并写入三条数据。内置 `time` 列使用 Unix 毫秒。

```sql
CREATE MEASUREMENT sensors (
    sensor_id TAG,
    region TAG,
    temperature FIELD FLOAT,
    humidity FIELD FLOAT
);

INSERT INTO sensors (time, sensor_id, region, temperature, humidity) VALUES
    (1700000000000, 'sensor-01', 'north', 22.5, 60),
    (1700000001000, 'sensor-01', 'north', 23.1, 58),
    (1700000002000, 'sensor-02', 'south', 26.8, 65);
```

先查询需要的列：

```sql
SELECT time, sensor_id, temperature FROM sensors ORDER BY time;

SELECT time, temperature AS temperature_c, humidity AS humidity_pct
FROM sensors
WHERE sensor_id = 'sensor-01'
ORDER BY time;
```

显式列出字段便于下游确定结果结构。`SELECT *` 适合临时检查；稳定的应用接口更适合使用固定投影和清晰别名。

## 标签与时间范围

标签等值过滤可以选择一个设备或业务分组。时间范围建议写成左闭右开，方便连续窗口衔接。

```sql
SELECT time, temperature, humidity
FROM sensors
WHERE sensor_id = 'sensor-01'
  AND time >= 1700000000000
  AND time < 1700000002000
ORDER BY time;
```

该范围包含起点，排除终点。下一段窗口可从 `1700000002000` 开始，减少端点重复统计的机会。

TAG 与 FIELD 具有不同用途：TAG 标识 series 的维度，FIELD 保存观测值。当前执行器也具备 FIELD 残余过滤路径，但不同查询形态的支持与预算仍需核对对应版本。不要把每一个 `WHERE` 条件都理解为索引查找。

## 顺序与返回数量

```sql
SELECT time, sensor_id, temperature
FROM sensors
WHERE region = 'north'
ORDER BY time DESC
LIMIT 10;
```

`ORDER BY` 明确结果顺序，`LIMIT` 控制返回数量。它不自动构成所有扫描、排序和内存开销的上限。

多个设备可能在同一毫秒产生记录，仅按 `time` 排序可能出现并列。需要稳定分页时，应提供足够的排序条件，并考虑写入并发对不同请求结果的影响。

本文按当前仓库文档与实现校对：[SQL 参考](https://github.com/IoTSharp/SonnetDB/blob/main/docs/sql-reference.md)、[数据模型](https://github.com/IoTSharp/SonnetDB/blob/main/docs/data-model.md)、[4.0.0 正式发行](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。
