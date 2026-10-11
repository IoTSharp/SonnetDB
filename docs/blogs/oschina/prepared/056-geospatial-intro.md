---
title: 'GEOPOINT 类型入门：地理空间数据存储与查询'
categories: SonnetDB,数据库
draft: false
---

正式发行见 [4.0.0 Release](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。本文按 2026-10-07 当前 main 文档与源码修订旧稿；主线改动不自动代表全部 v4 发行物与客户端的已验收能力。以下 SQL/API 为复现示例，本次没有实际执行或性能测量。

GEOPOINT 为带时间的定位采样保存 WGS84 经纬度。它是 measurement FIELD 类型；不是支持任意几何对象的 PostGIS geometry 替代品。

## 经纬度顺序

```sql
CREATE MEASUREMENT vehicle (
  device TAG, position FIELD GEOPOINT, speed FIELD FLOAT
);
INSERT INTO vehicle (time, device, position, speed) VALUES
  (1700000000000, 'truck-01', POINT(39.9042, 116.4074), 12.5),
  (1700000001000, 'truck-01', POINT(39.9050, 116.4085), 13.2);
SELECT time, device, position, speed
FROM vehicle WHERE device = 'truck-01'
ORDER BY time;
```

POINT(lat,lon) 的纬度在前、经度在后。合法范围是纬度 [-90,90]、经度 [-180,180]；不要把 GIS 常见的 lon/lat 顺序原样复制。GeoJSON 则采用标准 coordinates:[lon,lat]，导出时需要转换顺序。

## 类型与坐标系

当前关系表不支持 GEOPOINT 列，关系实体可显式保存标量经纬度，或关联时序 measurement。WGS84 数据与高德/腾讯 GCJ-02、百度 BD-09 底图不能直接当作同一坐标系，应按项目转换函数与地图合同处理。

## 查询加速的真实方式

当前段内 geohash metadata 可对 geo_within/geo_bbox 字面量谓词剪枝，最终仍逐点核验。它不是旧稿所写的通用 CREATE INDEX WITH index_type='geohash' SQL；不要发布没有核实的索引 DDL。

设备、车辆与行程可用 TAG，速度与海拔用 FIELD。定位数据还应考虑采样间隔、漂移和隐私授权，字段存在不自动解决业务隔离或轨迹质量。

依据：[geo-spatial.md](https://github.com/IoTSharp/SonnetDB/blob/main/docs/geo-spatial.md)、[GeoPoint.cs](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Model/GeoPoint.cs)。
