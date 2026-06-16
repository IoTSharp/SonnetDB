# SonnetDB 地理空间与轨迹分析

SonnetDB 在 Milestone 15 支持 `GEOPOINT` 字段、地理空间标量函数、轨迹聚合、GeoJSON 输出、Web Admin 地图页，以及 PR #76 引入的段内 geohash 剪枝。

## 功能矩阵

| 能力 | SQL / API | 说明 |
| --- | --- | --- |
| 地理点类型 | `FIELD GEOPOINT` | WGS84，经纬度以 `double` 存储。 |
| 写入字面量 | `POINT(lat, lon)` | SQL 中纬度在前、经度在后。 |
| 经纬度提取 | `lat(position)`, `lon(position)` | 返回 `FLOAT64`。 |
| 距离 / 方位 | `geo_distance`, `geo_bearing` | Haversine 距离（米）与 0–360° 方位角。 |
| 围栏过滤 | `geo_within`, `geo_bbox` | 圆形 / 矩形空间过滤，支持 PostGIS 风格别名。 |
| 坐标系转换 | `geo_transform`, `geo_wgs84_to_gcj02`, `geo_gcj02_to_wgs84`, `geo_gcj02_to_bd09`, `geo_bd09_to_gcj02`, `geo_wgs84_to_bd09`, `geo_bd09_to_wgs84` | WGS84 / GCJ-02 / BD-09 互转，支持 `GPS` / `AMap` / `Tencent` / `Baidu` 别名。 |
| 轨迹聚合 | `trajectory_length`, `trajectory_centroid`, `trajectory_speed_*` | 支持总路程、重心、速度统计与时间桶聚合。 |
| GeoJSON 输出 | HTTP ndjson / `/trajectory` | Point 输出遵循 GeoJSON `[lon, lat]` 顺序。 |
| Web Admin | 轨迹地图 / SQL Console 地图视图 | MapLibre GL + OSM / 高德 / 腾讯 / 百度瓦片，可展示轨迹、散点与回放，并支持按数据坐标系自动投影。 |
| 查询加速 | geohash block pruning | `geo_within` / `geo_bbox` 字面量谓词下跳过不相交 block。 |

## GEOPOINT 建模

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
```

注意：

- SQL `POINT(lat, lon)` 使用 **纬度在前、经度在后**。
- GeoJSON / MapLibre 使用标准坐标顺序 `[lon, lat]`。
- SonnetDB 内部 `GEOPOINT`、轨迹 REST 输出和 SQL Console 预览统一使用 WGS84；如果底图使用高德 / 腾讯 / 百度，可以在前端选择 `GCJ-02` 或 `BD-09` 作为数据坐标系。
- 设备、车辆、用户、行程等低基数维度建议放在 `TAG`。
- 速度、海拔、电量等随时间变化的采样值建议放在 `FIELD`。

## 坐标系转换

```sql
SELECT
  geo_wgs84_to_gcj02(position) AS gcj02_position,
  geo_gcj02_to_wgs84(geo_wgs84_to_gcj02(position)) AS roundtrip_wgs84,
  geo_wgs84_to_bd09(position) AS bd09_position,
  geo_transform(position, 'gps', 'baidu') AS bd09_alias
FROM vehicle
WHERE device = 'car-1';
```

- `geo_transform(point, from, to)` 是通用入口，`from` / `to` 支持 `WGS84`、`GCJ02`、`BD09` 以及 `GPS` / `AMap` / `Tencent` / `Baidu` 别名。
- `geo_wgs84_to_gcj02` / `geo_gcj02_to_wgs84` 适合国内地图底图与原始 WGS84 数据互转。
- `geo_gcj02_to_bd09` / `geo_bd09_to_gcj02` 适合高德 / 腾讯 / 百度之间的底图适配。
- `geo_wgs84_to_bd09` / `geo_bd09_to_wgs84` 提供 WGS84 与百度坐标的直接转换。

## 空间过滤

```sql
-- 圆形围栏：距离上海人民广场 1.5km 内的点
SELECT time, device, position, speed
FROM vehicle
WHERE geo_within(position, 31.2304, 121.4737, 1500);

-- 矩形范围：上海城区附近
SELECT count(position)
FROM vehicle
WHERE geo_bbox(position, 31.21, 121.45, 31.25, 121.50);

-- PostGIS 风格别名（注意：SonnetDB 的 ST_DWithin 采用 (point, lat, lon, radius_m) 四参形式，
-- 圆心以经纬度标量给出，而非 POINT 字面量）
SELECT time, position
FROM vehicle
WHERE ST_DWithin(position, 31.2304, 121.4737, 1500);
```

函数语义：

- `geo_distance(p1, p2)` / `ST_Distance(p1, p2)`：返回两点 Haversine 距离（米）。
- `geo_bearing(p1, p2)`：返回从 `p1` 指向 `p2` 的方位角（0–360°）。
- `geo_within(p, lat, lon, radius_m)` / `ST_DWithin(p, lat, lon, radius_m)`：判断点是否落在圆形围栏内。
- `geo_bbox(p, lat_min, lon_min, lat_max, lon_max)`：判断点是否落在矩形框内。
- `geo_speed(p1, p2, elapsed_ms)`：返回两点之间平均速度（m/s）。

## 轨迹聚合

```sql
-- 单设备总里程与重心
SELECT
  trajectory_length(position),
  trajectory_centroid(position),
  trajectory_speed_avg(position, time),
  trajectory_speed_p95(position, time)
FROM vehicle
WHERE device = 'truck-01';

-- 分钟级窗口内的轨迹长度和最大速度
SELECT
  trajectory_length(position),
  trajectory_speed_max(position, time)
FROM vehicle
WHERE trip = 'trip-a'
GROUP BY time(1m);
```

当前轨迹函数：

- `trajectory_length(position)`：按时间顺序累加相邻点距离，单位米。
- `trajectory_centroid(position)`：返回轨迹点重心 `GEOPOINT`。
- `trajectory_bbox(position)`：返回 JSON 字符串，包含 `min_lat/min_lon/max_lat/max_lon`。
- `trajectory_speed_max(position, time)` / `trajectory_speed_avg(position, time)` / `trajectory_speed_p95(position, time)`：基于相邻点时间差计算速度统计。

## GeoJSON 与 REST 端点

查询结果中的 `GEOPOINT` 会在 HTTP ndjson 中输出为 GeoJSON Point：

```json
{"type":"Point","coordinates":[121.4737,31.2304]}
```

轨迹端点：

```http
GET /v1/db/fleet/geo/vehicle/trajectory?device=truck-01&from=1700000000000&to=1700000600000
GET /v1/db/fleet/geo/vehicle/trajectory?device=truck-01&format=linestring
```

- 默认返回 `FeatureCollection`，每个采样点是一条 `Feature/Point`。
- `format=linestring` 返回 `FeatureCollection`，每个匹配 series 是一条 `LineString Feature`。
- 可通过 query string 传入任意 tag 过滤，例如 `device=truck-01&trip=trip-a`。
- `field=position` 可显式选择 `GEOPOINT` 字段；不传时使用第一个 `GEOPOINT` field。

## Web Admin 使用

### 轨迹地图标签页（PR #74）

进入 Web Admin 的“轨迹地图”：

1. 选择数据库与含 `GEOPOINT` 字段的 Measurement。
2. 选择 `GEOPOINT` 字段、时间范围和 TAG 过滤。
3. 在“瓦片服务商”下拉框里切换 OSM / 高德 / 腾讯 / 百度。
4. 点击“加载轨迹”。
5. 右侧地图展示轨迹线、起终点标记；底部时间轴可逐帧回放，并联动速度折线图。

### SQL Console 地图视图（PR #75）

SQL Console 查询结果中如果包含 GeoJSON Point / `GeoPoint` 列，会自动出现“地图”视图：

```sql
SELECT time, device, position, speed
FROM vehicle
WHERE trip = 'trip-a';
```

- 未选择时间列时以散点展示。
- 选择 `time` / `timestamp` 列后按时间排序连线。
- 可选择低基数字符串 / 数值列作为分组列，展示多设备轨迹对比。
- 可在“瓦片”下拉框切换 OSM / 高德 / 腾讯 / 百度；在“数据坐标”下拉框切换 WGS84 / GCJ-02 / BD-09。
- “图表”视图会自动识别 `time` 作为 x 轴，并将数值字段作为 y 轴 series。

## PR #76 Geohash 段内剪枝

Segment 格式 v5 在每个 `GEOPOINT` block 的 `BlockHeader` 中写入以下字段；当前 v6 继续保留该布局并兼容读取 v5 段：

- `GeoHashMin`：block 内最小 32-bit geohash 前缀。
- `GeoHashMax`：block 内最大 32-bit geohash 前缀。

当 `WHERE` 中出现字面量形式的 `geo_within` / `geo_bbox` 谓词时，查询执行器会先把过滤区域转换为 geohash 范围，并在解码前跳过明显不相交的落盘 block。最终结果仍逐点执行原始空间谓词校验，因此剪枝只影响性能，不改变语义。

## 基准

地理空间基准位于 `tests/SonnetDB.Benchmarks/Benchmarks/GeoQueryBenchmark.cs`：

```bash
# 默认 100k 轨迹点
dotnet run -c Release --project tests/SonnetDB.Benchmarks -- --filter *Geo*

# 显式启用 1M 轨迹点
SONNETDB_GEO_BENCH_INCLUDE_1M=1 dotnet run -c Release --project tests/SonnetDB.Benchmarks -- --filter *Geo*
```

覆盖场景：

- `geo_within` 圆形围栏过滤。
- `geo_bbox` + `count` 矩形过滤聚合。
- `trajectory_length` 单设备轨迹总路程。
- `GEOPOINT` range scan 基础查询路径。

当前基准以 SonnetDB 嵌入式真实引擎路径为主；PostGIS / 其他空间数据库对比可在后续具备稳定外部环境后单独补充。

## 端到端示例

### 车辆追踪

```sql
CREATE MEASUREMENT fleet_position (
  vehicle TAG,
  route TAG,
  position FIELD GEOPOINT,
  speed FIELD FLOAT,
  fuel FIELD FLOAT
);

INSERT INTO fleet_position (time, vehicle, route, position, speed, fuel) VALUES
  (1700000000000, 'v001', 'r-a', POINT(31.2304, 121.4737), 9.8, 82),
  (1700000005000, 'v001', 'r-a', POINT(31.2310, 121.4750), 10.5, 81.9);

SELECT time, vehicle, position, speed
FROM fleet_position
WHERE vehicle = 'v001'
  AND geo_within(position, 31.2304, 121.4737, 3000);
```

### 户外运动

```sql
CREATE MEASUREMENT workout_track (
  user_id TAG,
  workout_id TAG,
  position FIELD GEOPOINT,
  heart_rate FIELD INT,
  altitude FIELD FLOAT
);

SELECT
  trajectory_length(position),
  trajectory_speed_avg(position, time),
  trajectory_speed_p95(position, time)
FROM workout_track
WHERE workout_id = 'run-2026-04-25';
```

### IoT 地理围栏告警

```sql
CREATE MEASUREMENT asset_location (
  asset TAG,
  site TAG,
  position FIELD GEOPOINT,
  battery FIELD FLOAT
);

SELECT time, asset, position, battery
FROM asset_location
WHERE site = 'warehouse-a'
  AND NOT geo_bbox(position, 31.20, 121.40, 31.30, 121.55);
```
