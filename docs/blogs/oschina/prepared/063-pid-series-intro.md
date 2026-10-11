---
title: PID 控制算法入门：pid_series() 流式计算
categories: SonnetDB,PID,工业分析
draft: false
---

PID 以比例、积分和微分三项计算控制量。SonnetDB 将该控制律用于历史时序分析，便于回测参数和绘制控制量曲线。`pid_series` 的“流式”指按查询中的时间序列逐行推进内部状态，不表示数据库已经成为具备确定周期和安全联锁的 PLC。

正确调用是 **`pid_series(field, setpoint, kp, ki, kd)`**，共五参。时间取采样行的时间戳，不是另一个显式参数；旧稿把 setpoint 放在第一位并传入 time 的六参示例应修正。该签名同时可在 [4.0.0 标签源码](https://github.com/IoTSharp/SonnetDB/blob/v4.0.0/src/SonnetDB.Core/Query/Functions/Control/PidSeriesFunction.cs)核对。

## 一个完整回测样本

```sql
CREATE MEASUREMENT furnace_data (
    device_id TAG,
    temperature FIELD FLOAT
);

INSERT INTO furnace_data (time, device_id, temperature) VALUES
    (1700000000000, 'furnace-03', 90.0),
    (1700000001000, 'furnace-03', 92.0),
    (1700000002000, 'furnace-03', 94.0);

SELECT
    time,
    temperature AS pv,
    pid_series(temperature, 100.0, 2.0, 0.5, 0.1) AS control_output
FROM furnace_data
WHERE device_id = 'furnace-03'
ORDER BY time;
```

误差是 `setpoint - pv`。Kp 响应当前误差，Ki 积累随时间变化的误差，Kd 响应误差变化率。系数的单位和控制量量纲取决于自己的过程模型；误差 5、Kp 2 得到比例项 10，不能未经标定直接说成“10% 加热功率”。

## 时间与状态

相邻时间差由毫秒换算为秒。首行没有前一个样本，只输出比例项。空值行输出 NULL 并保持控制器状态；非正时间间隔不推进正常 I/D 更新。输入应按正确的 series 和时间范围组织，避免把不同设备过程混成一条控制曲线。

PID 系数使用数值常量。不同设备需要不同参数时，分别查询对应设备，或由客户端按设备配置发起查询，不把未核实的动态 CASE 系数写成已支持的合同。

```sql
SELECT time,
       pid_series(temperature, 100.0, 1.5, 0.3, 0.05) AS output
FROM furnace_data
WHERE device_id = 'furnace-03'
ORDER BY time;
```

## 从分析结果到工业应用

查询结果可以用于对比参数、识别积分积累和检查响应趋势。若应用要把建议控制量交给设备，仍需要独立的调度、输出限幅、设备确认、安全联锁和故障处理。一次 SELECT 完成不证明端到端闭环、硬实时周期或现场可靠性已经验收。

本文核对了文档与签名，没有运行 SQL 或驱动真实执行器。当前开发文档的新增行为，应与实际使用的 4.0.0 或其它发行版本分别确认。

参考：[PID 函数合同](https://github.com/IoTSharp/SonnetDB/blob/main/docs/pid-control.md)、[行级窗口函数](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Query/Functions/Control/PidSeriesFunction.cs)、[控制器实现](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Query/Functions/Control/PidController.cs)。
