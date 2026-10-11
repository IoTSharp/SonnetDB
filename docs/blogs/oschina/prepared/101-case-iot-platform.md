---
title: 案例：IoT 平台如何用 SonnetDB 管理百万设备实时数据
categories: SonnetDB,IoT,参考架构
draft: false
---

本文是一份面向大规模 IoT 平台的参考架构，示例数据均为合成数据。标题里的“百万设备”表示需要规划的目标规模，没有可核验的生产客户与容量报告支持，不能读作 SonnetDB 已通过该规模门禁。

## 把设备规模转换为工作负载

容量设计需要同时记录设备数、上报周期、字段数、并发连接、series 基数与保留时长。“一百万设备”本身不能推导写入吞吐；每条记录包含多个 FIELD 时，还要区分记录数与字段值数量。

参考分工是：设备网关负责协议转换、时间校验和断网队列；SonnetDB 保存与查询数据；应用负责租户授权、仪表盘和告警派单。MQTT、HTTP 与边缘嵌入式路径按实际环境选择。

## 从一个租户的小样本开始

由有权限的管理员创建练习数据库，然后把数据操作连接切到该库：

```sql
CREATE DATABASE factory_demo;
```

在 `factory_demo` 中执行：

```sql
CREATE MEASUREMENT device_metrics (
    device_id TAG,
    device_type TAG,
    temperature FIELD FLOAT,
    humidity FIELD FLOAT,
    location FIELD GEOPOINT
);

INSERT INTO device_metrics (time, device_id, device_type, temperature, humidity, location) VALUES
    (1700000000000, 'DEV001', 'sensor', 23.5, 65.2, POINT(39.9042, 116.4074)),
    (1700000001000, 'DEV001', 'sensor', 24.1, 63.8, POINT(39.9050, 116.4085));

SELECT time, device_id, temperature, humidity
FROM device_metrics
WHERE device_id = 'DEV001'
  AND time >= 1700000000000
  AND time < 1700000002000
ORDER BY time;
```

`POINT` 先纬度后经度。空间字段必须声明为 `FIELD GEOPOINT`。

## 批量接入与分析

远程 Line Protocol 入口是 `/v1/db/factory_demo/measurements/device_metrics/lp`。批大小、超时、flush 与重试策略应由实测确定，不使用固定“最佳一千条”作为普遍结论。

```sql
SELECT avg(temperature) AS average_temp, max(temperature) AS peak_temp
FROM device_metrics
WHERE device_id = 'DEV001'
GROUP BY time(1m);
```

告警通知和预测效果属于应用及评测工作，不是一次 SQL 查询自动完成的业务闭环。断网恢复还需要持久队列、明确确认点和重复记录策略；本地数据库可用不等于上传恰好一次。

## 租户与容量验证

数据库目录与 grant 可以作为租户治理入口，但实例 CPU、内存和部分模型资源仍有共享边界。Server MQ 持久化在实例 `.system/mq`，单库备份不能覆盖它。

上线前分别验证持续写入、峰值、长时间保留、重启恢复、撤权和备份，并取得实际目标硬件报告。原稿中的成本下降、P99 与上线吞吐没有原证据，本文不引用。

参考：[数据模型](https://github.com/IoTSharp/SonnetDB/blob/main/docs/data-model.md)、[批量写入](https://github.com/IoTSharp/SonnetDB/blob/main/docs/bulk-ingest.md)、[README 与模型边界](https://github.com/IoTSharp/SonnetDB/blob/main/README.md)。
