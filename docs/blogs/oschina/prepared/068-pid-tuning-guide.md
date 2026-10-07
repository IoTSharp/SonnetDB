---
title: PID 参数调节完全指南：Kp/Ki/Kd 的作用与整定技巧
categories: SonnetDB,PID,参数整定
draft: false
---

理解 PID 三项的作用，才能解释控制律回放。SonnetDB 的 `pid_series(field, setpoint, kp, ki, kd)` 提供按采样时间逐点计算的入口。本文修正旧稿参数顺序，并明确调参分析与现场闭环之间的差别。

## Kp：响应当前误差

比例项等于 Kp 乘以 `setpoint - pv`。增大 Kp 通常提高误差响应强度，但是否振荡取决于被控过程、采样周期与反馈延迟，不能把“将现场调到持续振荡”当作所有设备适用的操作建议。

```sql
CREATE MEASUREMENT furnace_data (device TAG, temperature FIELD FLOAT);
INSERT INTO furnace_data (time, device, temperature) VALUES
    (1700000000000, 'f1', 90.0),
    (1700000001000, 'f1', 92.0),
    (1700000002000, 'f1', 94.0);

SELECT time, temperature,
       pid_series(temperature, 100.0, 1.0, 0.0, 0.0) AS kp_1,
       pid_series(temperature, 100.0, 2.0, 0.0, 0.0) AS kp_2
FROM furnace_data WHERE device = 'f1' ORDER BY time;
```

## Ki：累计随时间变化的误差

积分项按相邻样本时间差累计，时间差单位为秒。Ki 的量纲因此依赖过程变量与控制量单位。不能普遍规定 Ki 等于 Kp 的某个固定比例；应先明确采样、过程动态和执行器限制。

```sql
SELECT time, temperature,
       pid_series(temperature, 100.0, 2.0, 0.0, 0.0) AS p_only,
       pid_series(temperature, 100.0, 2.0, 0.3, 0.0) AS pi
FROM furnace_data WHERE device = 'f1' ORDER BY time;
```

## Kd：响应误差变化率

微分项使用相邻误差变化除以时间差。它可能提供阻尼，也可能放大测量噪声和设定值突变。下面比较控制量变化，而不是自动生成新的温度响应：

```sql
SELECT time, temperature,
       pid_series(temperature, 100.0, 2.0, 0.3, 0.0) AS pi,
       pid_series(temperature, 100.0, 2.0, 0.3, 0.5) AS pid
FROM furnace_data WHERE device = 'f1' ORDER BY time;
```

## 输出限幅与积分饱和

当前 `PidController` 更新中持续累计误差，没有输出上下限参数或自动暂停积分的 anti-windup 分支。旧稿“内置抗积分饱和”应删除。把查询结果限制在 0–100 只是限制最终显示或下发值，不会反向改变函数内部积分状态。

应用若需要 anti-windup，应在明确设计的控制层实现条件积分、反算或其它策略，并验证饱和后的恢复。数据库回放不替代设备保护、安全联锁和确定周期控制。

可以先用 `pid_estimate` 得到候选参数，再通过模型或受控试验比较超调、稳态误差与执行器负担。本文只核对文档与源码，没有执行 SQL 或设备试验。正式 [4.0.0](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0) 与主线新增行为仍须按使用版本确认。

参考：[PID 文档](https://github.com/IoTSharp/SonnetDB/blob/main/docs/pid-control.md)、[控制器实现](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Query/Functions/Control/PidController.cs)、[整定估计](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Query/Functions/Control/PidParameterEstimator.cs)。
