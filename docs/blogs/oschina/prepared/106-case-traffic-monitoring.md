---
title: 案例：城市交通监控——路口车流量时序分析与拥堵预测
categories: SonnetDB,交通,参考架构
draft: false
---

本文以合成路口记录展示交通分析参考架构，不代表真实交管项目或拥堵改善成果。车辆检测设备负责识别与计数，SonnetDB 保存观测，应用负责地图、预测评价和人工决策。

## 记录指标的实际含义

```sql
CREATE MEASUREMENT traffic_flow (
    intersection_id TAG,
    direction TAG,
    vehicle_count FIELD INT,
    avg_speed_kmh FIELD FLOAT,
    queue_length_m FIELD FLOAT,
    location FIELD GEOPOINT
);

INSERT INTO traffic_flow (time, intersection_id, direction, vehicle_count, avg_speed_kmh, queue_length_m, location) VALUES
    (1700000000000, 'INT-01', 'N', 20, 35, 10, POINT(31.2304, 121.4737)),
    (1700000060000, 'INT-01', 'N', 25, 28, 20, POINT(31.2304, 121.4737)),
    (1700000120000, 'INT-01', 'N', 30, 20, 35, POINT(31.2304, 121.4737)),
    (1700000180000, 'INT-01', 'N', 35, 15, 50, POINT(31.2304, 121.4737));
```

示例车流量表示一分钟区间计数。重复上传、采集缺失与区间边界需要网关核对，不能把累计计数与区间计数相加。

## 趋势和候选事件

```sql
SELECT time, vehicle_count, avg_speed_kmh, queue_length_m
FROM traffic_flow
WHERE intersection_id = 'INT-01' AND direction = 'N'
ORDER BY time;

SELECT time, avg_speed_kmh, queue_length_m
FROM traffic_flow
WHERE intersection_id = 'INT-01'
  AND avg_speed_kmh < 20 AND queue_length_m > 30
ORDER BY time;
```

阈值只用于演示。低速和长队可产生检查候选，但施工、信号相位和检测误差也会影响结果，不能直接判定事故。

## 按真实 TVF 形式预测

```sql
SELECT time, value, lower, upper
FROM forecast(traffic_flow, vehicle_count, 3, 'linear')
WHERE intersection_id = 'INT-01' AND direction = 'N';
```

预测入口接收 measurement 与 FIELD 名，不是把任意聚合子查询放进第一个参数。`horizon` 表示未来采样步数，步长来自历史间隔；不等同于未经核对的三分钟或三个小时。

四条样本只演示语法，不能证明节假日或长期预测准确率。真实评价需要留出历史区间，按正常日、节假日、天气和缺失情况分别比较误差。

## 应用与控制边界

地图使用 GeoJSON 时应转换为经度在前。邻近路口的相关变化不自动证明拥堵传播因果；拓扑和时延分析需要明确的数据模型。

信号配时还包括冲突相位、行人、最小绿灯和现场控制规则。数据库建议不能直接替代控制系统。原稿的城市规模、响应秒数和交通改善百分比没有原证据，本文不引用。

参考：[预测函数](https://github.com/IoTSharp/SonnetDB/blob/main/docs/forecast.md)、[空间指南](https://github.com/IoTSharp/SonnetDB/blob/main/docs/geo-spatial.md)、[SQL 参考](https://github.com/IoTSharp/SonnetDB/blob/main/docs/sql-reference.md)。
