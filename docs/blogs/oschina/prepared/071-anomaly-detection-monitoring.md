---
title: 实时异常检测监控：窗口函数与告警流水线
categories: SonnetDB,异常检测,监控
draft: false
---

把异常检测接入监控，除了计算标记，还需要调度、去重、通知和质量回看。SonnetDB 的 `anomaly` 窗口函数可作为检测环节，但一次查询不等于已经交付完整实时告警系统。

## 查询一个明确的检测窗口

```sql
CREATE MEASUREMENT sensor_data (device_id TAG, temperature FIELD FLOAT);
INSERT INTO sensor_data (time, device_id, temperature) VALUES
    (1700000000000, 'pump-03', 20.0),
    (1700000001000, 'pump-03', 20.2),
    (1700000002000, 'pump-03', 20.1),
    (1700000003000, 'pump-03', 35.0),
    (1700000004000, 'pump-03', 20.0);

SELECT time, temperature,
       anomaly(temperature, 'mad', 3.0) AS is_outlier
FROM sensor_data
WHERE device_id = 'pump-03'
  AND time BETWEEN 1700000000000 AND 1700000060000
ORDER BY time;
```

监控服务周期性移动这个时间区间，并在客户端读取布尔标记。函数使用当前查询窗口的整体统计基线，不是旧稿的 `OVER RANGE ... QUALIFY` 逐行因果滑窗；后者不能作为此接口的替代写法。

## 保存告警候选

```sql
CREATE MEASUREMENT alert_events (
    device_id TAG,
    alert_type TAG,
    severity FIELD INT,
    value FIELD FLOAT,
    threshold FIELD FLOAT,
    message FIELD STRING
);

INSERT INTO alert_events
    (time, device_id, alert_type, severity, value, threshold, message) VALUES
    (1700000003000, 'pump-03', 'temperature_anomaly', 2, 35.0, 3.0,
     'MAD anomaly candidate');
```

第二条 INSERT 只是展示候选事件写入结构，客户端应先取得检测结果再写入。不要把旧稿的 `INSERT ... SELECT`、命名 WINDOW 和 FORMAT 组合当成已经核实支持的自动流水线。

告警去重键可由设备、观测时间和检测规则版本组成。重叠查询窗口可能重复发现同一异常，通知客户端必须处理去重；写入超时的未知结果也不能靠无条件重试消除。

## 降噪与效果评价

可以在应用侧按严重度分级、要求连续若干窗口确认，或对已知维护时段抑制通知。持续异常规则会引入延迟，应与漏报风险一起评估。

人工确认或设备故障记录用于评价 Precision、Recall 和检测时延。Precision 是真实异常占已发告警的比例，不能写成误报率；Recall 则需要全部真实异常作为分母。

正式 [4.0.0](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0) 与当前开发文档分别核对。本文未运行 SQL、调度、通知或现场告警，实时性取决于采集、查询和应用链路。

参考：[异常检测合同](https://github.com/IoTSharp/SonnetDB/blob/main/docs/forecast.md)、[窗口函数实现](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Query/Functions/Window/AnomalyFunctions.cs)、[流订阅合同](https://github.com/IoTSharp/SonnetDB/blob/main/docs/streaming-subscription-contract.md)。
