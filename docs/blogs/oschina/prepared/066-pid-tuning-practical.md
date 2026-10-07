---
title: PID 调参实战：阶跃响应分析与参数解读
categories: SonnetDB,PID,工业分析
draft: false
---

参数整定的前提是一段可信的阶跃响应。工程师先确认过程处于稳态，记录控制量的变化幅度，再持续采集过程变量，直到覆盖新的稳态。SonnetDB 用历史时序组织和分析这些样本，帮助比较候选参数。

本篇依据当前 PID 文档修订旧稿。正式发行入口是 [4.0.0](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)，`main` 持续更新，实际部署应核对相应版本。

## 保存试验条件

```sql
CREATE MEASUREMENT reactor_data (
    reactor_id TAG,
    trial TAG,
    valve_position FIELD FLOAT,
    temperature FIELD FLOAT
);

SELECT time, valve_position, temperature
FROM reactor_data
WHERE reactor_id = 'R-101' AND trial = 'step-01'
  AND time BETWEEN 1700000000000 AND 1700001800000
ORDER BY time;
```

查询前由采集客户端将本次试验样本写入上述 schema。时间用 Unix 毫秒；至少需要 10 个有效样本，并覆盖初末稳态与中间响应。不能把启动过程、扰动过程和不同试验混成同一个辨识区间。

过程增益是稳态响应变化除以输入阶跃幅度。时间常数和滞后描述响应快慢，单位为秒。当前实现使用 35%/85% 两点法辨识 FOPDT 模型，SQL 接口直接接收样本 FIELD，而不是接收旧稿的 K/T/L JSON 对象。

## 比较三种整定规则

假设阀门开度以百分数保存，本次从 30 到 50，则输入阶跃幅度是 20。请按实际单位替换：

```sql
SELECT pid_estimate(temperature, 'zn', 20.0, 0.1, 0.1, NULL) AS zn
FROM reactor_data WHERE reactor_id = 'R-101' AND trial = 'step-01';

SELECT pid_estimate(temperature, 'cc', 20.0, 0.1, 0.1, NULL) AS cc
FROM reactor_data WHERE reactor_id = 'R-101' AND trial = 'step-01';

SELECT pid_estimate(temperature, 'imc', 20.0, 0.1, 0.1, 15.0) AS imc
FROM reactor_data WHERE reactor_id = 'R-101' AND trial = 'step-01';
```

输出为 JSON 字符串，包含小写 `kp/ki/kd`。旧稿给出的 method、K、T、L 不是该 SQL 输出的完整合同，不能当作实际回执字段。

## 解读参数与回放

Kp、Ki、Kd 的量纲与 PV、控制量和时间单位相关，不应直接将系数描述为普遍成立的百分比。客户端解析结果后，将候选数值常量代入：

```sql
SELECT time, temperature,
       pid_series(temperature, 100.0, 2.0, 0.3, 0.05) AS candidate_output
FROM reactor_data
WHERE reactor_id = 'R-101' AND trial = 'step-01'
ORDER BY time;
```

这里的系数仅演示调用方式。对固定历史 PV 重新计算控制律，不会自动模拟新的被控对象温度响应。真正比较超调、稳定时间和饱和情况，需要过程模型或受控闭环试验。

每次记录试验区间、输入幅度、参数、采样周期与实际效果，才有可重复的调参日志。本文未执行 SQL 或现场试验；上线时的设备调度、安全联锁和故障降级仍需独立验证。

参考：[PID 文档](https://github.com/IoTSharp/SonnetDB/blob/main/docs/pid-control.md)、[参数估计器](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Query/Functions/Control/PidParameterEstimator.cs)、[SQL 估计函数](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Query/Functions/Control/PidEstimateFunction.cs)。
