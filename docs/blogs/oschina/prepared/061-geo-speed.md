---
title: 地理空间速度计算：geo_speed() 函数详解
categories: SonnetDB,地理空间,时序
draft: false
---

车辆、配送设备和运动轨迹都需要从位置与时间估算速度。SonnetDB 的 `geo_speed` 计算的是两个已知位置之间的平均速度，结果单位为米/秒。它不会自动读取上一行：旧稿中的 `geo_speed(position, time)` 两参写法应改成 **`geo_speed(p1, p2, elapsed_ms)`**。

下面按当前文档和函数注册表说明接口。正式版本入口为 [SonnetDB 4.0.0](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)；`main` 文档持续更新，部署时仍应核对自己的版本。

## 两点速度与单位

函数以 Haversine 公式计算两点距离，再除以毫秒时间差换算出的秒数。SQL 的 `POINT(lat, lon)` 纬度在前，经度在后；GeoJSON 输出则使用 `[lon, lat]`，两种顺序不要混用。

```sql
SELECT geo_speed(
    POINT(31.2304, 121.4737),
    POINT(31.2310, 121.4750),
    5000
) AS speed_ms;

SELECT geo_speed(
    POINT(31.2304, 121.4737),
    POINT(31.2310, 121.4750),
    5000
) * 3.6 AS speed_kmh;
```

`elapsed_ms` 必须大于零。重复时间戳、逆序采样和不合理的 GPS 跳跃，应在组成位置对时处理，不能用零时间差解释为静止。

## 连续轨迹采用轨迹聚合

如果已经将每次采样保存为 measurement，通常更需要一段轨迹的速度统计：

```sql
CREATE MEASUREMENT trajectory_data (
    device_id TAG,
    position FIELD GEOPOINT
);

INSERT INTO trajectory_data (time, device_id, position) VALUES
    (1700000000000, 'vehicle-01', POINT(31.2304, 121.4737)),
    (1700000005000, 'vehicle-01', POINT(31.2310, 121.4750)),
    (1700000010000, 'vehicle-01', POINT(31.2315, 121.4760));

SELECT
    trajectory_speed_avg(position, time) * 3.6 AS avg_kmh,
    trajectory_speed_max(position, time) * 3.6 AS max_kmh,
    trajectory_speed_p95(position, time) * 3.6 AS p95_kmh
FROM trajectory_data
WHERE device_id = 'vehicle-01';
```

轨迹函数按照相邻采样点计算统计量。单辆车、单次行程的过滤十分重要，避免把不同行程之间的位置跳跃当作行驶路段。若要展示逐段速度曲线，可先查询 `time, position`，由客户端按时间排序组成相邻位置对，再调用相同的距离/时间逻辑。

## 采样质量与分析边界

离散采样得到的是区间平均速度，不能直接等同于车辆仪表盘上的瞬时速度。长采样间隔会掩盖停车、转弯与短暂加速；短间隔下 GPS 噪声又可能放大速度波动。超速分析应结合采样质量、道路规则和设备误差设置阈值。

旧稿“百万点毫秒完成”的说法没有随文提供固定硬件和原始日志，这里不保留该性能保证。实际延迟取决于过滤范围、落盘状态与查询路径；本文只核对接口，没有执行这些 SQL 或基准。

参考：[地理空间与轨迹分析](https://github.com/IoTSharp/SonnetDB/blob/main/docs/geo-spatial.md)、[函数注册表](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Query/Functions/FunctionRegistry.cs)、[SQL 参考](https://github.com/IoTSharp/SonnetDB/blob/main/docs/sql-reference.md)。
