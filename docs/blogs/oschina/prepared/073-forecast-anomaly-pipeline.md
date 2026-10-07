---
title: 预测、异常检测与变化点分析：组合工作流
categories: SonnetDB,预测,异常检测
draft: false
---

预测给出未来预期，异常检测标记离群样本，变点检测寻找持续漂移。这三类结果可以共同用于服务器和设备监控，但它们的输入、输出与时间口径不同。本文用实际 SonnetDB 接口组合工作流，修正旧稿里未定义 pred/stddev 和 PostgreSQL JSON 操作符的单条 SQL。

## 统一采样对象

```sql
CREATE MEASUREMENT server_metrics (host TAG, response_time FIELD FLOAT);
INSERT INTO server_metrics (time, host, response_time) VALUES
    (1700000000000, 'api-server-01', 10.0),
    (1700000060000, 'api-server-01', 10.2),
    (1700000120000, 'api-server-01', 9.8),
    (1700000180000, 'api-server-01', 10.1),
    (1700000240000, 'api-server-01', 10.0),
    (1700000300000, 'api-server-01', 20.0),
    (1700000360000, 'api-server-01', 20.2),
    (1700000420000, 'api-server-01', 20.1);
```

样本是演示数据，响应时间单位应由应用明确，例如毫秒。真实质量评估需要更多正常历史和人工确认的异常。

## 分别读取三类结果

```sql
SELECT time, value, lower, upper
FROM forecast(server_metrics, response_time, 5, 'linear')
WHERE host = 'api-server-01';

SELECT time, response_time,
       anomaly(response_time, 'mad', 3.0) AS outlier,
       changepoint(response_time, 'cusum', 4.0, 0.5) AS regime_shift
FROM server_metrics
WHERE host = 'api-server-01'
ORDER BY time;
```

预测是 FROM 中的 TVF，返回未来 `time/value/lower/upper` 行；异常与变点函数返回与历史输入对应的布尔列。预测训练时应只读取评估时刻之前的历史；不能把包含未来故障的整个窗口当作在线基线。

## 客户端组合

监控应用保存预测值及对应的设备、时间和模型版本。新观测到达后，按采样时间匹配预期区间，再将偏差、离群标记和 CUSUM 触发记录为不同证据。缺失或错位采样不能直接认作预测误差。

“离群且变点触发”可能代表持续状态变化，也可能是基线选择、传感器校准或业务负载改变。诊断需要结合部署日志、维护记录和设备质量标志，不能凭两个布尔值自动认定故障原因。

## 告警与验证

应用负责通知去重、严重度、冷却期和人工确认。预测区间是算法残差估计，真实 Coverage、Precision、Recall 和延迟需在留出的真实数据上评估；SQL 返回成功不代表这些指标达标。

当前内置预测仅为线性与 Holt/Holt-Winters，变点仅为 CUSUM。正式 [4.0.0](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0) 与 `main` 能力分开核对。本文没有运行 SQL、模型质量测试或现场监控流程。

参考：[三类分析合同](https://github.com/IoTSharp/SonnetDB/blob/main/docs/forecast.md)、[预测器](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Query/Functions/Forecasting/TimeSeriesForecaster.cs)、[异常与变点实现](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Query/Functions/Window/AnomalyFunctions.cs)。
