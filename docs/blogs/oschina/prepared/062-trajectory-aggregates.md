---
title: 轨迹聚合分析：全面洞察移动数据
categories: SonnetDB,轨迹,数据分析
draft: false
---

单个位置点描述“在哪里”，轨迹聚合回答“一段时间走了多远、活动范围多大、速度怎样”。SonnetDB 提供总路程、中心点、包围盒以及最大、平均和 P95 速度聚合，可用于车队运营、运动记录和移动资产分析。

本文按当前地理空间文档修订示例。正式发行入口是 [4.0.0 Release](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)；运行时以相应版本的函数合同为准，不把持续更新的 `main` 自动视为旧发行物保证。

## 建模与样本

```sql
CREATE MEASUREMENT trajectory_data (
    device_id TAG,
    trip TAG,
    position FIELD GEOPOINT
);

INSERT INTO trajectory_data (time, device_id, trip, position) VALUES
    (1700000000000, 'vehicle-01', 'trip-a', POINT(31.2304, 121.4737)),
    (1700000005000, 'vehicle-01', 'trip-a', POINT(31.2310, 121.4750)),
    (1700000010000, 'vehicle-01', 'trip-a', POINT(31.2315, 121.4760));
```

SQL `POINT` 使用纬度、经度顺序。设备与行程用 TAG 区分，位置是 `FIELD GEOPOINT`，时间列使用 Unix 毫秒。

## 一段行程的统计

```sql
SELECT
    trajectory_length(position) AS total_distance_m,
    trajectory_centroid(position) AS center_point,
    trajectory_bbox(position) AS bounding_box,
    trajectory_speed_max(position, time) AS max_speed_ms,
    trajectory_speed_avg(position, time) AS avg_speed_ms,
    trajectory_speed_p95(position, time) AS p95_speed_ms
FROM trajectory_data
WHERE device_id = 'vehicle-01' AND trip = 'trip-a';
```

`trajectory_length` 按时间顺序累计相邻点距离，单位米；中心点返回 GEOPOINT；包围盒返回包含 `min_lat/min_lon/max_lat/max_lon` 的 JSON 字符串。速度统计基于相邻点时间差，单位米/秒，乘以 3.6 可转换为公里/小时。

平均速度、P95 速度与“总距离除以整段耗时”是不同口径。报表必须标明使用哪一个统计量，也要说明停车和断采样怎样处理。

## 按时间桶观察变化

```sql
SELECT
    trajectory_length(position) AS distance_m,
    trajectory_speed_max(position, time) * 3.6 AS max_kmh
FROM trajectory_data
WHERE device_id = 'vehicle-01' AND trip = 'trip-a'
GROUP BY time(1m);
```

这里使用 SonnetDB 文档明确给出的 `GROUP BY time(...)` 桶。桶内轨迹独立聚合，跨桶边界的连续路段不宜当作已自动纳入每个桶的里程；需要精确整段里程时，另做整段行程查询。

## 运营应用与解释

车队看板可以把里程、最大速度和活动范围与车辆、行程关联；生态追踪可以按动物和观察期分析范围。包围盒描述经纬度极值，不能直接当成经过投影与地形校正的栖息地面积。中心点也不等于动物实际出现频率最高的地点。

GPS 漂移、稀疏采样和不同设备混入同一组都会影响统计。本文提供建模和查询路径，没有宣称现场车队已验证，也没有运行示例或给出性能成绩。

参考：[轨迹函数与坐标合同](https://github.com/IoTSharp/SonnetDB/blob/main/docs/geo-spatial.md)、[轨迹聚合实现](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Query/Functions/Aggregates/ExtendedAggregateFunctions.cs)、[SQL 参考](https://github.com/IoTSharp/SonnetDB/blob/main/docs/sql-reference.md)。
