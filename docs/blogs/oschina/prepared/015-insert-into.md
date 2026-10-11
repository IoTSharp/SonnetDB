---
title: INSERT INTO：向时序表写入数据
categories: SonnetDB,SQL,时序写入
draft: false
---

SonnetDB measurement 使用 `INSERT INTO ... VALUES` 写入 TAG、FIELD 和保留时间列。本文修订旧稿中的 `ts TIMESTAMP PRIMARY KEY`、任意字符串转数字和重复点自动覆盖描述，保留完整的批量写入示例。

正式版本见 [4.0.0](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)；以下按当前 SQL 文档整理，实际发行物与 `main` 演进分别核对。

## 创建 schema 后写入

```sql
CREATE MEASUREMENT sensor_data (
    sensor_id TAG,
    temperature FIELD FLOAT,
    humidity FIELD FLOAT
);

INSERT INTO sensor_data (time, sensor_id, temperature, humidity)
VALUES (1713676800000, 'sensor-001', 23.5, 65.2);
```

`time` 表示 Unix 毫秒，不在 CREATE 中定义为关系主键。每行至少提供一个 FIELD，TAG 必须是字符串。已有列按 schema 解释，而不是每次任意推断角色。

## 多行 VALUES

```sql
INSERT INTO sensor_data (time, sensor_id, temperature, humidity)
VALUES
    (1713676801000, 'sensor-001', 23.6, 65.0),
    (1713676802000, 'sensor-001', 23.4, 65.3),
    (1713676803000, 'sensor-002', 23.7, 64.8);

SELECT time, sensor_id, temperature, humidity
FROM sensor_data
ORDER BY time;
```

一批可以包含不同时间和 TAG。选择批大小时考虑请求体、SQL 文本、处理延迟和 Server 预算，不预设一个适用于所有机器的最佳行数。字段值数量也可能多于 SQL 行数，统计吞吐时要说明单位。

## 缺值和类型

measurement FIELD 是稀疏的。某时刻只有温度时，可以省略 humidity：

```sql
INSERT INTO sensor_data (time, sensor_id, temperature)
VALUES (1713676804000, 'sensor-001', 23.9);
```

查询缺失 humidity 时得到 NULL。当前 INSERT 不接受显式 NULL 或 measurement DEFAULT 作为缺值写法。FLOAT 可以接受有限整数/浮点字面量，INT→FLOAT 的受控提升不表示字符串、布尔与其它类型都可任意转换。

未给 time 时按合同使用当前 UTC 毫秒；采样时刻由设备确定的应用应显式传入。乱序数据、重复时间点、删除和向量替换都需要遵循相应语义，不能把一次相同 TAG/time 的 INSERT 宣称为关系表 UPSERT 或自动最后写胜出。

## schema-on-write 与名称

未知列默认推断为 FIELD，包括字符串。自动建 schema 时需要明确新 TAG，可以使用列角色提示：

```sql
INSERT INTO new_readings (time, device TAG, value FIELD)
VALUES (1713676800000, 'device-01', 21.4);
```

受控 schema 策略仍可能因额度或类型不兼容拒绝写入。当前名称绑定保留创建拼写，普通引用忽略大小写、双引号精确匹配；它不改变 TAG 字符串或 JSON 键。

REST/ADO 写入使用其数据面合同，原生 SQL Frame 查询端点保持只读。收到 HTTP 响应或部分结果后仍应检查实际 SQL 终态；未知写结果不直接重试。

参考：[INSERT 合同](https://github.com/IoTSharp/SonnetDB/blob/main/docs/sql-reference.md)、[数据模型](https://github.com/IoTSharp/SonnetDB/blob/main/docs/data-model.md)和[批量摄取](https://github.com/IoTSharp/SonnetDB/blob/main/docs/bulk-ingest.md)。本文未执行实际写入。
