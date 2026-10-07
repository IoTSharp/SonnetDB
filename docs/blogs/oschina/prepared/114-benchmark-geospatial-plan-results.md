---
title: 地理空间基准通稿：轨迹、围栏与空间过滤对比方案
categories: SonnetDB,地理空间,基准测试
draft: false
---

# 地理空间基准通稿：轨迹、围栏与空间过滤对比方案

轨迹查询通常同时包含时间、设备和空间条件。本文说明仓库现有地理基准的场景与结果验证方法；本次未取得旧稿性能数字的完整原始报告，没有重新执行测试，也不据此宣称优于 PostGIS 或其他服务端实现。

## 数据集和查询形状

`GeoQueryBenchmark` 默认生成十万条合成轨迹，另有百万条规模选项。轨迹属于上海范围内的 16 个模拟设备，写入后执行 flush。它是合成负载，不能当作真实车辆或客户运行数据。

源码覆盖圆形围栏过滤、边界框计数、单设备轨迹长度及点范围读取。下面是独立的小样本示例：

```sql
CREATE MEASUREMENT vehicle (
  device TAG, region TAG,
  position FIELD GEOPOINT, speed FIELD FLOAT
);
INSERT INTO vehicle (time, device, region, position, speed) VALUES
  (1700000000000, 'car-01', 'shanghai', POINT(31.2304, 121.4737), 35.0),
  (1700000001000, 'car-01', 'shanghai', POINT(31.2310, 121.4740), 36.0),
  (1700000002000, 'car-01', 'shanghai', POINT(31.2320, 121.4750), 37.0);

SELECT time, position FROM vehicle
WHERE geo_within(position, 31.2304, 121.4737, 1500)
ORDER BY time;

SELECT count(position) FROM vehicle
WHERE geo_bbox(position, 31.21, 121.45, 31.25, 121.50);

SELECT trajectory_length(position) FROM vehicle
WHERE device = 'car-01'
  AND time >= 1700000000000 AND time < 1700000003000;
```

这些 SQL 本次未执行。SonnetDB 的 `POINT` 示例按纬度、经度书写；GeoJSON 坐标数组采用经度、纬度。交换顺序会改变位置，必须在数据导入和对照报告中核查。距离/轨迹计算使用地理距离语义，圆形半径以米表示。

## 比速度之前先对齐结果

围栏需要核查边界内外和缺失位置值；轨迹长度需要相同设备、时间范围和顺序，避免把不同设备的相邻点连接成一条路线。还应说明坐标参考系、距离算法、边界包含规则，以及是否执行投影或坐标转换。

如果对照 PostGIS，索引、空间类型、距离算法和查询计划都应保存。如果测 HTTP 服务端，编码、网络和完整响应消费也应计入对应路径。当前本文没有完成这些跨实现对照，不能用缺失结果给出排名。

## 复现入口

```bash
dotnet run -c Release --project tests/SonnetDB.Benchmarks -- --filter '*Geo*'
```

实际运行应保存版本、硬件、数据生成参数和原始日志，设置总超时与取消方式，并使用独立数据目录。结束后只回收本次创建的进程和临时目录。报告可同时展示匹配数量、路径长度误差、延迟与资源采样，使读者能够区分正确性与速度。

参考：[地理查询基准](https://github.com/IoTSharp/SonnetDB/blob/main/tests/SonnetDB.Benchmarks/Benchmarks/GeoQueryBenchmark.cs)、[SQL 地理功能](https://github.com/IoTSharp/SonnetDB/blob/main/docs/sql-reference.md)。
