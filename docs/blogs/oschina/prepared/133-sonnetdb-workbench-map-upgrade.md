## SonnetDB Workbench 新版发布：轨迹地图、国内瓦片切换与坐标系转换

查到一列经纬度，离看懂一条轨迹还有几步：识别坐标字段，按时间连线，区分车辆，再把数据坐标与底图对齐。SonnetDB Workbench 当前的地图组件把这些操作放进了查询结果和轨迹页面。

本文依据当前仓库的 Web 地图组件、坐标转换实现和地理空间文档整理。正式 [4.0.0 release](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0) 与 main 是两个版本边界；本文描述当前实现，不能据此推定全部功能已经进入某个历史安装包或扩展市场版本。示例供读者在自己的环境验证，本文没有重新运行各宿主的真实地图旅程。

### 从一份可查询的轨迹开始

在演示数据库中创建 measurement，写入三条位置记录：

```sql
CREATE MEASUREMENT vehicle (
  device TAG,
  position FIELD GEOPOINT,
  speed FIELD FLOAT
);

INSERT INTO vehicle (time, device, position, speed) VALUES
  (1700000000000, 'car-1', POINT(39.9042, 116.4074), 12.5),
  (1700000001000, 'car-1', POINT(39.9050, 116.4085), 13.2),
  (1700000002000, 'car-1', POINT(39.9057, 116.4094), 11.8);

SELECT time, device, position, speed
FROM vehicle
WHERE device = 'car-1'
  AND time >= 1700000000000
  AND time <= 1700000002000
ORDER BY time ASC
LIMIT 100;
```

时间范围和 `LIMIT` 控制这次展示的数据量，排序让轨迹按时间前进。查看多个设备时，还需要按 `device` 分组，避免把不同车辆的点连接成同一条线。

`POINT(lat, lon)` 的参数顺序是纬度在前、经度在后；GeoJSON 和 MapLibre 使用 `[lon, lat]`。例如北京这个点在 SQL 中写作 `POINT(39.9042, 116.4074)`，在 GeoJSON 中则写作 `[116.4074, 39.9042]`。交换顺序产生的是错误位置，坐标系转换无法修复它。

SQL Console 的地图结果组件提供坐标列、时间列和分组列选择。选择时间列可以连线；清除时间列可以查看散点。这比依赖某个固定列名更适合不同查询的结果。

### 底图和数据各自带有坐标系

当前瓦片配置列出四个服务商：

| 底图 | 配置中的数据坐标约定 |
| --- | --- |
| OpenStreetMap | WGS84 |
| 高德 | GCJ-02 |
| 腾讯 | GCJ-02 |
| 百度 | BD-09 |

地图工具栏另外提供“数据坐标”：WGS84、GCJ-02、BD-09。这里应填写查询结果本身的坐标系。例如设备记录的是 GPS/WGS84，就选择 WGS84；查询返回的是已经转换后的高德坐标，就选择 GCJ-02。

组件会从所选数据坐标系转换到当前服务商的坐标约定，然后交给地图渲染。更换底图改变显示位置，不改写数据库中的原始 `GEOPOINT`。数据来源不明确时，应先确认采集设备或上游接口的约定，不能靠轮流切换底图来猜。

瓦片 URL 和坐标配置存在于代码，并不代表每个供应商在所有网络与宿主中都已验证可用。腾讯、百度等服务还涉及瓦片索引规则，实际显示需要在目标环境核对；服务商访问政策、网络与 URL 变化也会影响出图。交付前应使用已知位置检查点位与底图对齐，并确认瓦片使用条件。

### SQL 可以显式转换坐标

坐标转换也提供 SQL 入口：

```sql
geo_transform(point, from, to)
geo_wgs84_to_gcj02(point)
geo_gcj02_to_wgs84(point)
geo_gcj02_to_bd09(point)
geo_bd09_to_gcj02(point)
geo_wgs84_to_bd09(point)
geo_bd09_to_wgs84(point)
```

`geo_transform` 支持 WGS84、GCJ02、BD09，以及 GPS、AMap、Tencent、Baidu 别名。下面在查询中生成 GCJ-02 和 BD-09 位置，同时保留原始时间和设备：

```sql
SELECT time, device,
  geo_wgs84_to_gcj02(position) AS gcj02_position,
  geo_wgs84_to_bd09(position) AS bd09_position,
  geo_transform(position, 'gps', 'baidu') AS bd09_alias
FROM vehicle
WHERE device = 'car-1'
  AND time >= 1700000000000
  AND time <= 1700000002000
ORDER BY time ASC
LIMIT 100;
```

若在地图中选择 `gcj02_position` 列，“数据坐标”就应选择 GCJ-02；选择 `bd09_position` 时则选择 BD-09。SQL 已经做过一次转换，再把结果误标成 WGS84，会产生二次转换。

逆转换使用近似数值方法，不应拿往返结果的逐位相等作为业务精度保证。对于测绘或其他严格定位场景，需要按实际数据和适用范围评估误差；地图显示功能也不替代专业坐标基准服务。

### 轨迹页面补充时间上的变化

轨迹页的实现包含轨迹线、起终点、当前回放位置和速度图表。查询结果地图适合检查一次 SQL 输出，轨迹页适合观察同一段行程如何随时间变化。

回放依赖正确的时间顺序。设备时钟错误、重复时间、缺测或 GPS 异常仍需要在数据层处理；画出一条连续的线，并不能证明采样完整或车辆真的经过了线上的每一点。

不同宿主可以复用 Web 组件，但 Web、Studio 与 VS Code 扩展的源码、打包版本和真实界面验收需要分别核对。扩展清单里的版本号也不能替代扩展市场的发布回执。

### 使用时先核对三个选择

实际操作时，先选正确的坐标列，再确认结果数据的坐标系，最后按时间与设备连线。保持原始数据的坐标约定明确，地图就可以承担展示工作；需要对外返回另一套坐标时，则在 SQL 中显式转换。

这种安排让同一份位置数据同时服务查询、地图和外部接口。相关源码与文档可以在 [SonnetDB 仓库](https://github.com/IoTSharp/SonnetDB) 的 `docs/geo-spatial.md`、`web/src/components/ResultMapPreview.vue`、`web/src/views/TrajectoryMap.vue` 与 `web/src/utils/geoTransforms.ts` 中查看。
