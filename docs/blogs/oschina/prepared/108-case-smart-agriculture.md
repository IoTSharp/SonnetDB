---
title: 案例：农业 IoT——温室大棚环境数据采集与智能灌溉
categories: SonnetDB,农业IoT,参考架构
draft: false
---

本文使用合成温室数据展示参考架构，没有真实农场增产、节水或病害改善报告。传感器采集、数据库分析和灌溉执行各有职责，不能因为查询可运行就认为自动种植策略已通过验证。

## 建立分区数据

```sql
CREATE MEASUREMENT greenhouse_env (
    greenhouse_id TAG,
    zone_id TAG,
    crop_type TAG,
    temperature FIELD FLOAT,
    humidity FIELD FLOAT,
    soil_moisture FIELD FLOAT
);

INSERT INTO greenhouse_env (time, greenhouse_id, zone_id, crop_type, temperature, humidity, soil_moisture) VALUES
    (1700000000000, 'GH-01', 'A', 'strawberry', 18, 70, 40),
    (1700000060000, 'GH-01', 'A', 'strawberry', 19, 72, 38),
    (1700000120000, 'GH-01', 'A', 'strawberry', 20, 73, 34);
```

土壤含水率需要说明传感器校准、基质与单位。不同探头输出不能仅因字段同名就直接比较。

## 产生检查候选

```sql
SELECT time, temperature, humidity, soil_moisture
FROM greenhouse_env
WHERE greenhouse_id = 'GH-01' AND zone_id = 'A'
ORDER BY time;

SELECT time, soil_moisture FROM greenhouse_env
WHERE greenhouse_id = 'GH-01' AND zone_id = 'A'
  AND soil_moisture < 35
ORDER BY time;
```

35 只是语法示例阈值，不是草莓或全部作物的灌溉标准。应用规则还需要生长阶段、蒸腾、土壤、天气、可供水量和设备状态。

## 用控制律做离线回测

```sql
SELECT time, temperature,
       pid_series(temperature, 20.0, 2.0, 0.3, 0.15) AS control_output
FROM greenhouse_env
WHERE greenhouse_id = 'GH-01' AND zone_id = 'A'
ORDER BY time;
```

真实参数顺序是 FIELD、目标值和三项增益。示例增益未经现场整定；输出正负取决于误差定义和执行机构映射，不能通用地把正数直接解释成开通风或开灌溉。

## 边缘与现场边界

本地嵌入式存储适合断网时继续采集，但持续控制、硬件故障处理和恢复补传需要独立应用合同。去掉旧稿中的无限采集循环与虚构执行器 API，实际采集任务应具备取消、资源上限和数据确认。

照度 lux 转换为 PAR/DLI 依赖光谱和传感器校准，不应使用一个固定系数作为所有环境的精确结论。高温高湿也只是病害风险输入，不能由数据库单独诊断病害。

真实试验应保留灌溉量、产量、环境与对照区证据，再评价策略效果。

参考：[PID 指南](https://github.com/IoTSharp/SonnetDB/blob/main/docs/pid-control.md)、[数据模型](https://github.com/IoTSharp/SonnetDB/blob/main/docs/data-model.md)、[SQL 参考](https://github.com/IoTSharp/SonnetDB/blob/main/docs/sql-reference.md)。
