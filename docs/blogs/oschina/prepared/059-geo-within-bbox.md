---
title: '地理空间过滤：geo_within 圆形查询与 geo_bbox 矩形查询'
categories: SonnetDB,数据库
draft: false
---

正式发行见 [4.0.0 Release](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。本文按 2026-10-07 当前 main 文档与源码修订旧稿；主线改动不自动代表全部 v4 发行物与客户端的已验收能力。以下 SQL/API 为复现示例，本次没有实际执行或性能测量。

geo_within 用于圆形围栏，geo_bbox 用于经纬度矩形。当前参数以中心或边界的标量经纬度给出，不使用旧稿中的三参 POINT 圆心形式。

## 圆形过滤

```sql
SELECT time, device, position
FROM vehicle
WHERE device = 'truck-01'
  AND geo_within(position, 39.9042, 116.4074, 1500)
  AND time >= 1700000000000 AND time < 1700003600000;
```

参数是 point、center_lat、center_lon、radius_m。半径单位米；当前使用距离 <= radius 的边界判断，负半径拒绝。它计算球面圆形范围，不是道路沿线或多边形包含关系。

## 矩形过滤

```sql
SELECT time, position
FROM vehicle
WHERE geo_bbox(position, 39.90, 116.40, 39.92, 116.42);
```

五个参数依次为 point、lat_min、lon_min、lat_max、lon_max。发布应用前应核对边界、非法坐标和跨日期变更线的业务策略，不能把普通矩形函数当作任意复杂区域处理器。

## 剪枝和最终核验

段内 geohash 范围 metadata 可以跳过明显不相交的 block，最终仍执行空间谓词。因此剪枝是优化，不改变围栏语义；数据尚在 MemTable 或谓词不满足下推合同，执行成本也会不同。

实际业务需要分开评估采样漂移、边界抖动、进入离开去重和告警延迟。单次范围查询并不自动形成持续围栏事件系统，本文也没有报告实测加速倍数。

依据：[geo-spatial.md](https://github.com/IoTSharp/SonnetDB/blob/main/docs/geo-spatial.md)、[FunctionRegistry.cs](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Query/Functions/FunctionRegistry.cs)。
