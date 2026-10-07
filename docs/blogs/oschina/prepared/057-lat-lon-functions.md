---
title: '坐标分量提取：lat() 和 lon() 函数详解'
categories: SonnetDB,数据库
draft: false
---

正式发行见 [4.0.0 Release](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。本文按 2026-10-07 当前 main 文档与源码修订旧稿；主线改动不自动代表全部 v4 发行物与客户端的已验收能力。以下 SQL/API 为复现示例，本次没有实际执行或性能测量。

lat 和 lon 从 GEOPOINT 中提取两个 double 坐标分量。它们适合导出表格、地图适配与数据核对，但不会自动转换坐标系。

```sql
SELECT time, device,
       lat(position) AS latitude,
       lon(position) AS longitude
FROM vehicle
WHERE device = 'truck-01'
  AND time >= 1700000000000 AND time < 1700003600000
ORDER BY time;
```

假设 vehicle 已声明 position FIELD GEOPOINT。lat 对应纬度，lon 对应经度；不要把函数结果列顺序和 POINT 的参数顺序与 GeoJSON 的坐标顺序混在一起。

## 输出到外部系统

SonnetDB SQL POINT 使用 lat/ lon 顺序，GeoJSON 和常见 Web 地图使用 [lon,lat]。例如北京点 POINT(39.9042,116.4074) 导出 GeoJSON 时应是 [116.4074,39.9042]。这只是坐标顺序交换，不是 WGS84 到 GCJ-02 的转换。

## 分量平均与地理中心

分别平均纬度和经度，并不普遍等同于球面中心；跨日期变更线时，经度的线性平均尤其容易失真。若需要轨迹中心，应核对 trajectory_centroid 的专门合同，不能把普通 avg(lat/lon) 当成所有地理场景的正确答案。

接口适配还需保留时间、设备和坐标系信息。只取两个数值会失去观测来源与轨迹顺序，不能据此证明定位精度或完成完整 GIS 迁移。

依据：[geo-spatial.md](https://github.com/IoTSharp/SonnetDB/blob/main/docs/geo-spatial.md)。
