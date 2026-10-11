---
title: PID 聚合函数：pid() 与 GROUP BY 时间窗口
categories: SonnetDB,PID,时间窗口
draft: false
---

逐行 PID 曲线适合回测，时间桶末的控制量适合粗粒度观察。SonnetDB 的 `pid(field, setpoint, kp, ki, kd)` 是聚合函数：在桶内逐行推进控制器，最后返回该桶最后一个有效样本对应的控制量。

这与旧稿描述的 JSON 诊断报告不同。它不会直接返回 `p_term/i_term/d_term/error` 等字段，也不代表桶内平均输出。正式版本入口是 [4.0.0 Release](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)，部署时核对相应版本文档。

## 建模与每分钟观察

```sql
CREATE MEASUREMENT furnace_data (
    device_id TAG,
    temperature FIELD FLOAT
);

INSERT INTO furnace_data (time, device_id, temperature) VALUES
    (1700000000000, 'furnace-03', 90.0),
    (1700000001000, 'furnace-03', 92.0),
    (1700000002000, 'furnace-03', 94.0);

SELECT pid(temperature, 100.0, 2.0, 0.5, 0.1) AS final_output
FROM furnace_data
WHERE device_id = 'furnace-03'
GROUP BY time(1m);
```

第一个参数是 FIELD 列，后四个参数是数值常量。这里以 measurement 的时间列确定相邻样本的时间差，不需要将 time 作为第六个参数。

## 桶之间如何衔接

每个时间桶使用独立累加器，控制器会重新初始化。因而这条查询不是跨桶持续运行的闭环控制器。若要分析连续积分与微分状态，改用 `pid_series` 查看逐点曲线，再按需要在客户端下采样。

```sql
SELECT time,
       pid_series(temperature, 100.0, 2.0, 0.5, 0.1) AS output
FROM furnace_data
WHERE device_id = 'furnace-03'
ORDER BY time;
```

## 比较两组参数

对相同设备、相同历史区间分别执行查询，可以比较参数变化后的桶末输出；不要仅凭输出大小选“最优”参数。

```sql
SELECT pid(temperature, 100.0, 2.0, 0.5, 0.1) AS candidate_a
FROM furnace_data
WHERE device_id = 'furnace-03'
GROUP BY time(1m);

SELECT pid(temperature, 100.0, 1.5, 0.3, 0.2) AS candidate_b
FROM furnace_data
WHERE device_id = 'furnace-03'
GROUP BY time(1m);
```

实际整定还需要过程模型或闭环试验，检查超调、稳态偏差、执行器饱和和采样周期。开环历史 PV 不会因为更换查询中的参数而自动生成一条新的被控对象响应。

旧稿“数亿记录秒级完成”没有固定环境和原始数据支持，不作为性能保证。本文未运行 SQL，也未验证现场控制回路；生产应用中的调度、设备通信与安全联锁仍是独立边界。

参考：[PID 文档](https://github.com/IoTSharp/SonnetDB/blob/main/docs/pid-control.md)、[聚合实现](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Query/Functions/Control/PidAggregateFunction.cs)、[行级窗口实现](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Query/Functions/Control/PidSeriesFunction.cs)。
