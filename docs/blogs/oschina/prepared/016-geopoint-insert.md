---
title: GEOPOINT 地理空间数据：使用 POINT 语法写入经纬度
categories: SonnetDB,SQL,地理空间
draft: false
---

定位数据除了时间和设备标识，还需要一个明确的坐标合同。SonnetDB 的 measurement 可以用 `GEOPOINT` FIELD 保存 WGS84 经纬度，再把位置与速度、海拔等观测值放在同一条时序记录中。

## 建立一个轨迹 measurement

先连接到用于练习的数据库，再执行下面的定义和写入。`time` 是 measurement 的内置时间列，单位为 Unix 毫秒，不需要再声明一个 `TIMESTAMP` 主键。

```sql
CREATE MEASUREMENT vehicle (
    device TAG,
    trip TAG,
    position FIELD GEOPOINT,
    speed FIELD FLOAT,
    altitude FIELD FLOAT
);

INSERT INTO vehicle (time, device, trip, position, speed, altitude) VALUES
    (1700000000000, 'truck-01', 'trip-a', POINT(39.9042, 116.4074), 12.5, 43),
    (1700000001000, 'truck-01', 'trip-a', POINT(39.9050, 116.4085), 13.2, 45);

SELECT time, device, position, speed
FROM vehicle
WHERE device = 'truck-01'
ORDER BY time;
```

这里的 `POINT(lat, lon)` 先纬度、后经度。北京的示例是 `POINT(39.9042, 116.4074)`。GeoJSON 的 `coordinates` 则采用 `[lon, lat]`；转换接口时必须交换顺序，不能把两种表示直接混用。

纬度范围是 `[-90, 90]`，经度范围是 `[-180, 180]`。构造路径会校验坐标，不会把越界输入默默修正为合法位置。摄取端应在写入前检查单位、坐标系和缺失值。

## 距离、半径与矩形范围

`geo_distance` 返回基于 Haversine 公式计算的球面距离，单位为米：

```sql
SELECT time, geo_distance(position, POINT(39.9042, 116.4074)) AS distance_m
FROM vehicle
WHERE device = 'truck-01'
ORDER BY time;
```

半径过滤使用四个参数：位置、中心纬度、中心经度、半径米数。`ST_DWithin` 是对应别名，参数合同相同。

```sql
SELECT time, device, position
FROM vehicle
WHERE geo_within(position, 39.9042, 116.4074, 1000);

SELECT time, device, position
FROM vehicle
WHERE geo_bbox(position, 39.90, 116.40, 39.91, 116.42);
```

`geo_bbox` 的参数依次为位置、最小纬度、最小经度、最大纬度、最大经度。它适合地图视窗筛选；涉及跨越日期变更线等特殊范围时，应先核对具体版本的边界处理。

## 从正确坐标到有效查询

具备条件的字面量空间谓词可以参与 geohash 块裁剪，减少需要读取的数据块。它不是任意 `CREATE INDEX ... geohash` 语法，也不意味着所有空间表达式都会得到相同的执行计划。

实际轨迹查询还应加上设备和时间范围，让空间条件与业务边界共同缩小输入。距离计算采用球面近似，不应直接当作测绘级椭球精度结果。

本文按当前仓库文档与实现校对，使用安装版本时请核对其对应说明：[地理空间指南](https://github.com/IoTSharp/SonnetDB/blob/main/docs/geo-spatial.md)、[SQL 参考](https://github.com/IoTSharp/SonnetDB/blob/main/docs/sql-reference.md)、[4.0.0 正式发行](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。
