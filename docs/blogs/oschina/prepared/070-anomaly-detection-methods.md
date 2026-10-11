---
title: 异常检测方法对比：Z-Score、MAD 与 IQR
categories: SonnetDB,异常检测,统计
draft: false
---

Z-Score、MAD 和 IQR 使用不同统计基线标记离群点。SonnetDB 提供 `anomaly(field, 'method', threshold)` 窗口函数，逐行输出布尔值，缺失输入返回 NULL。本篇使用实际内置接口，修正旧稿中未核实的 OVER、QUALIFY 与嵌套 percentile 例子。

## 同一组数据比较三种方法

```sql
CREATE MEASUREMENT sensor_data (sensor_id TAG, temperature FIELD FLOAT);
INSERT INTO sensor_data (time, sensor_id, temperature) VALUES
    (1700000000000, 'temp-01', 20.0),
    (1700000001000, 'temp-01', 20.2),
    (1700000002000, 'temp-01', 19.9),
    (1700000003000, 'temp-01', 20.1),
    (1700000004000, 'temp-01', 40.0);

SELECT time, temperature,
       anomaly(temperature, 'zscore', 2.0) AS zscore_flag,
       anomaly(temperature, 'mad', 3.0) AS mad_flag,
       anomaly(temperature, 'iqr', 1.5) AS iqr_flag
FROM sensor_data
WHERE sensor_id = 'temp-01'
ORDER BY time;
```

这是合成样本，展示调用方式，没有运行或声称三种方法一定给出某个结果。

## 统计口径

Z-Score 使用与均值的偏差除以标准差，阈值是标准差倍数。离群点本身可能拉大标准差，因此小样本中用 3 并不一定识别一个非常大的尖峰。

MAD 使用中位数和中位数绝对偏差，以 `1.4826 × MAD` 缩放。它对极值更稳健，但大量重复值可能使 MAD 为零，需要核对该数据和实现的退化行为。

IQR 使用 Q1、Q3，阈值是 IQR 倍数，例如 1.5。它适用于箱线图式离群检测，但偏态、多峰和分组混杂仍会影响解释。

这些阈值不能直接横向比较为相同的敏感度。应针对同一设备、相同查询区间，结合标注的真实异常调整。

## 窗口与生产评估

当前函数基于所选查询窗口中有效样本的统计量，按 series 计算；它不是只利用每行之前数据的因果在线检测器。改变 WHERE 时间范围会改变基线与标记，离线回放时应避免把未来样本混入在线质量评估。

应用读取布尔列后，可以单独筛选异常候选并记录结果。Precision 是告警中真实异常的比例，Recall 是实际异常被检出的比例；二者都需要真实标签，而不是只看 SQL 是否成功。

正式 [4.0.0](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0) 中也可核对该窗口函数签名，主线文档仍按实际版本使用。本文未执行 SQL、实时告警或真实质量测试。

参考：[预测与异常检测](https://github.com/IoTSharp/SonnetDB/blob/main/docs/forecast.md)、[窗口函数实现](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Query/Functions/Window/AnomalyFunctions.cs)、[4.0.0 实现](https://github.com/IoTSharp/SonnetDB/blob/v4.0.0/src/SonnetDB.Core/Query/Functions/Window/AnomalyFunctions.cs)。
