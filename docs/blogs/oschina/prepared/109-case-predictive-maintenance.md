---
title: 案例：设备预测性维护——振动信号分析与故障预警
categories: SonnetDB,工业,参考架构
draft: false
---

本文是一份预测性维护参考架构，使用合成特征，不对应真实工厂停机减少或故障提前预警成绩。SonnetDB 适合保存与分析时序特征，信号处理、故障标签和维修判定需要独立验证。

## 先区分原始波形与特征

RMS 是原始样本平方均值的平方根，不能把 `avg(vibration)` 直接命名为原始波形 RMS。峭度涉及四阶中心矩，也不是极差除标准差。若设备已经上报窗口 RMS，应明确窗口、单位与算法版本。

```sql
CREATE MEASUREMENT vibration_metrics (
    machine_id TAG,
    axis TAG,
    rms_mm_s FIELD FLOAT,
    temperature FIELD FLOAT,
    rpm FIELD FLOAT
);

INSERT INTO vibration_metrics (time, machine_id, axis, rms_mm_s, temperature, rpm) VALUES
    (1700000000000, 'PUMP-01', 'X', 1.8, 55, 1500),
    (1700000060000, 'PUMP-01', 'X', 2.0, 56, 1500),
    (1700000120000, 'PUMP-01', 'X', 2.5, 58, 1500),
    (1700000180000, 'PUMP-01', 'X', 3.0, 61, 1500);
```

这里的 RMS 由上游信号处理计算。高频原始波形可以另行归档，时序记录保存对应采集身份和特征版本。

## 查询趋势与异常信号

```sql
SELECT time, rms_mm_s, temperature, rpm
FROM vibration_metrics
WHERE machine_id = 'PUMP-01' AND axis = 'X'
ORDER BY time;

SELECT time, rms_mm_s,
       anomaly(rms_mm_s, 'mad', 3.0) AS is_outlier
FROM vibration_metrics
WHERE machine_id = 'PUMP-01' AND axis = 'X'
ORDER BY time;
```

异常函数输出统计信号，四条样本不足以证明真实故障检测效果。不同设备、转速、负载与启停阶段应分别建立基线；不能因为振动和温度同时变化就直接诊断轴承磨损。

## 统计与预测的限制

窗口 RMS 的平均值是“RMS 特征的平均”，不是将所有原始样本重新计算后的整体 RMS。RMS 最大值也不是原始波形峰值，不能据此计算真实峰值因子。

线性或季节预测可用于趋势试验，但不能直接转成可靠的剩余寿命。标准阈值需要核对设备类型、安装方式和适用标准版本，不能给所有泵机一个通用停机数值。

## 维修闭环与评价

故障预警需要工单、现场检查和维修标签。评测保留正常样本、故障样本、误报、漏报与提前量，严格分离训练、阈值选择与测试时间段。

Copilot 可以帮助生成已授权查询，但解释不是故障诊断证据。原底稿中的停机次数、可用率、维修成本和库存降低没有原客户报告，本文不引用。

参考：[异常与预测](https://github.com/IoTSharp/SonnetDB/blob/main/docs/forecast.md)、[SQL 参考](https://github.com/IoTSharp/SonnetDB/blob/main/docs/sql-reference.md)、[数据模型](https://github.com/IoTSharp/SonnetDB/blob/main/docs/data-model.md)。
