---
title: 'PostGIS 兼容函数：ST_Distance / ST_Within / ST_DWithin'
categories: SonnetDB,数据库
draft: false
---

正式发行见 [4.0.0 Release](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。本文按 2026-10-07 当前 main 文档与源码修订旧稿；主线改动不自动代表全部 v4 发行物与客户端的已验收能力。以下 SQL/API 为复现示例，本次没有实际执行或性能测量。

SonnetDB 提供 ST_Distance、ST_Within 和 ST_DWithin 别名，便于识别熟悉的用途。但参数和几何语义有明确边界，不能把它们称为完整 PostGIS 兼容层。

| SonnetDB 形式 | 对应实现 | 语义 |
|---|---|---|
| ST_Distance(p1,p2) | geo_distance(p1,p2) | 两点 Haversine 距离，米 |
| ST_Within(p,lat,lon,radius) | geo_within | 点落入圆形范围 |
| ST_DWithin(p,lat,lon,radius) | geo_within | 同一四参圆形判断 |

## 使用四参圆心

```sql
SELECT time, position
FROM vehicle
WHERE ST_DWithin(position, 39.9042, 116.4074, 1500);

SELECT time, ST_Distance(position, POINT(39.9042, 116.4074)) AS distance_m
FROM vehicle WHERE device = 'truck-01';
```

SonnetDB 的 ST_DWithin 不是 PostGIS 的 `(geometry,geometry,distance)` 三参形式；ST_Within 也不是任意多边形拓扑包含函数。旧稿直接把两点加半径或缓冲区 SQL 搬过来会误导使用者。

## 迁移时核对的差异

POINT 使用纬度在前、经度在后；常见 ST_MakePoint 使用经度在前。当前 GEOPOINT 是 measurement FIELD，不能在关系表中直接替换 geometry/geography 列。SRID、GIST、复杂几何、PostgreSQL cast 和 wire protocol 也需要单独替代设计。

当前距离采用球面半径公式，并非保证与 PostGIS geography 默认椭球距离一致。段内 geohash 剪枝也不是 GIST 索引 DDL 的同义替代。

保留原系统测试样本，逐项对拍坐标、单位、边界、NULL、距离误差、过滤与恢复，再决定迁移范围。本文未进行外部 PostGIS 服务对比或完整兼容验收。

依据：[geo-spatial.md](https://github.com/IoTSharp/SonnetDB/blob/main/docs/geo-spatial.md)、[FunctionRegistry.cs](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Query/Functions/FunctionRegistry.cs)。
