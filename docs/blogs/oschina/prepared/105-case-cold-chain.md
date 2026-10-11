---
title: 案例：冷链物流——全程温湿度监控与超标告警系统
categories: SonnetDB,冷链,参考架构
draft: false
---

本文是冷链追溯系统参考架构，使用合成记录，没有真实物流客户或监管验收证据。数据库提供温湿度查询与位置记录，货品允许范围、通知升级和处置依据应来自具体产品及业务规定。

## 保存批次与传感器身份

```sql
CREATE MEASUREMENT cold_chain_temp (
    vehicle_id TAG,
    sensor_id TAG,
    batch_id TAG,
    temperature FIELD FLOAT,
    humidity FIELD FLOAT,
    door_open FIELD BOOL,
    location FIELD GEOPOINT
);

INSERT INTO cold_chain_temp (time, vehicle_id, sensor_id, batch_id, temperature, humidity, door_open, location) VALUES
    (1700000000000, 'VEH-01', 'S1', 'BATCH-A', 4.5, 60, false, POINT(39.9042, 116.4074)),
    (1700000030000, 'VEH-01', 'S1', 'BATCH-A', 9.2, 61, true, POINT(39.9050, 116.4085));
```

时间使用 Unix 毫秒，坐标是纬度在前。设备校准、采样质量和批次归属需要由采集端与业务系统维护。

## 查找超标候选

下面以某货品的示例范围 2–8°C 说明查询，不将它泛化成所有货物的法规要求：

```sql
SELECT time, vehicle_id, sensor_id, temperature, door_open
FROM cold_chain_temp
WHERE batch_id = 'BATCH-A'
  AND (temperature < 2 OR temperature > 8)
ORDER BY time;
```

告警服务还需处理连续超标、恢复、抑制与通知确认。超标样本数不能直接乘固定周期得到可靠持续时间；缺点、乱序和变采样间隔必须按实际时间处理。

## 统计与轨迹报告

```sql
SELECT min(temperature) AS minimum_temp,
       max(temperature) AS maximum_temp,
       avg(temperature) AS average_temp,
       count(temperature) AS samples
FROM cold_chain_temp
WHERE batch_id = 'BATCH-A';

SELECT time, location, temperature
FROM cold_chain_temp
WHERE batch_id = 'BATCH-A' AND sensor_id = 'S1'
ORDER BY time;
```

这些结果可供应用生成报告和地图，但数据库不会因为一次查询就自动生成监管合格 PDF。平均值也不能掩盖缺失区间和短时超标。

## 断网补传与可信记录

边缘本地存储、持久上传队列、确认点和重复处理共同构成补传合同。旧稿查询了未声明的 `synced` 字段并调用不存在的示例 API，不能当作已经实现的恰好一次同步。

可修改的数据库记录也不自动满足不可篡改证据要求。实际追溯需要权限、审计、校准记录、备份和符合业务要求的签名或归档流程。

原稿的销毁减少、检查合格率与告警时延没有可核验原证据，本文不引用。

参考：[SQL 参考](https://github.com/IoTSharp/SonnetDB/blob/main/docs/sql-reference.md)、[空间指南](https://github.com/IoTSharp/SonnetDB/blob/main/docs/geo-spatial.md)、[数据模型](https://github.com/IoTSharp/SonnetDB/blob/main/docs/data-model.md)。
