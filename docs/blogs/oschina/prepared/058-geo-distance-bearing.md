---
title: '地理距离与方位角计算：geo_distance 与 geo_bearing'
categories: SonnetDB,数据库
draft: false
---

正式发行见 [4.0.0 Release](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。本文按 2026-10-07 当前 main 文档与源码修订旧稿；主线改动不自动代表全部 v4 发行物与客户端的已验收能力。以下 SQL/API 为复现示例，本次没有实际执行或性能测量。

geo_distance 计算两个地理点的大圆距离，geo_bearing 计算从第一个点指向第二个点的方位角。一个回答多远，一个回答向哪里。

```sql
SELECT time,
       geo_distance(position, POINT(39.9042, 116.4074)) AS distance_m,
       geo_bearing(position, POINT(39.9042, 116.4074)) AS bearing_deg
FROM vehicle
WHERE device = 'truck-01'
  AND time >= 1700000000000 AND time < 1700003600000
ORDER BY time;
```

## 米与角度

当前距离使用 Haversine 球面公式，地球半径常量为 6,371,008.8 米，结果单位为米。它是两点间球面距离，不是道路导航距离，也不是椭球测地线算法；不能宣称与 PostGIS geography 的所有配置逐值相同。

geo_bearing 结果按 0–360° 归一化。交换两点会改变方向，不能将 distance 的对称性质套到 bearing 上。近重合点和特殊地理位置的业务解释应由应用验证。

## 速度不是单纯距离

两点平均速度需要正的时间差；项目另有 geo_speed(p1,p2,elapsed_ms) 与轨迹速度聚合。距离除以未知采样间隔无法得到可靠速度，GPS 漂移也可能放大短时间差下的估计。

地图展示、测距和控制决策有不同的精度需求。本文没有执行定位基准或误差测量，不以公式与示例证明厘米精度、道路可达性或安全导航能力。

依据：[geo-spatial.md](https://github.com/IoTSharp/SonnetDB/blob/main/docs/geo-spatial.md)、[FunctionRegistry.cs](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Query/Functions/FunctionRegistry.cs)。
