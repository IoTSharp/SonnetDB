---
title: 时序预测函数：forecast() 线性与 Holt-Winters 方法
categories: SonnetDB,时序预测,数据分析
draft: false
---

SonnetDB 的 `forecast` 是 **FROM 子句中的表值函数**，返回预测行，而不是 SELECT 中返回 JSON 的普通函数。正确形式为 `forecast(measurement, field, horizon, 'algo'[, season])`，不使用旧稿的命名参数、显式 time 参数或 PostgreSQL JSON 展开语法。

## 线性预测样本

```sql
CREATE MEASUREMENT sensor_data (device_id TAG, temperature FIELD FLOAT);
INSERT INTO sensor_data (time, device_id, temperature) VALUES
    (1700000000000, 'sensor-01', 20.0),
    (1700000060000, 'sensor-01', 21.0),
    (1700000120000, 'sensor-01', 22.0),
    (1700000180000, 'sensor-01', 23.0),
    (1700000240000, 'sensor-01', 24.0);

SELECT time, value, lower, upper
FROM forecast(sensor_data, temperature, 5, 'linear')
WHERE device_id = 'sensor-01';
```

`horizon=5` 表示外推五个采样间隔，不必然是五小时。步长按历史平均采样间隔计算；输出时间从最后一个历史样本之后开始。线性方法通过最小二乘拟合趋势，适合短期、近似线性的序列。

输出稳定列为 `time/value/lower/upper` 与维度 TAG。区间依据残差估计，不是对实际未来误差的保证。本文中的合成直线可能产生很窄的区间，不能据此证明模型质量。

## Holt-Winters 与季节周期

```sql
SELECT time, value, lower, upper
FROM forecast(sensor_data, temperature, 12, 'holt_winters', 24)
WHERE device_id = 'sensor-01';
```

周期 24 是采样点数。若采样间隔一小时，才对应一天。当前实现使用固定平滑系数 `alpha=0.4,beta=0.1,gamma=0.2`，不是旧稿中可用 SQL 任意传入的参数；也没有据此确认自动季节周期检测。

只有周期大于 1 且历史至少覆盖两个周期，才启用加性季节项。上面的五点样本不足两个 24 点周期，会走无季节的 Holt 路径。真实季节评估应准备足够的等间隔历史并留出未来验证集。

## 业务应用与验证

可以将预测行保存到应用侧，再按时间和设备与新观测值比较。预测区间外的值是异常候选，仍需要处理缺失、采样漂移、业务事件与模型失配；不能直接当作设备故障诊断。

当前内置算法是线性与 Holt/Holt-Winters，ARIMA、Prophet 或神经网络不是同一内置接口已经交付的能力。正式 [4.0.0](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0) 与当前 `main` 文档分别核对；本文未运行预测 SQL，也没有真实质量报告。

参考：[预测合同](https://github.com/IoTSharp/SonnetDB/blob/main/docs/forecast.md)、[预测器实现](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Query/Functions/Forecasting/TimeSeriesForecaster.cs)、[SQL 参考](https://github.com/IoTSharp/SonnetDB/blob/main/docs/sql-reference.md)。
