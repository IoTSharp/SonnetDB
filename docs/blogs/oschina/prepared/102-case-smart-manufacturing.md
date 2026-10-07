---
title: 案例：智能制造——汽车总装线传感器数据采集与 PID 控制
categories: SonnetDB,工业,参考架构
draft: false
---

本文用合成焊机数据展示智能制造参考架构，没有真实工厂上线与收益证据。SonnetDB 负责历史数据和控制律回测，现场采集与实际执行由网关、PLC 和应用承担。

## 数据模型与输入质量

```sql
CREATE MEASUREMENT welding_machine (
    machine_id TAG,
    line_id TAG,
    temperature FIELD FLOAT,
    pressure FIELD FLOAT,
    quality_score FIELD FLOAT
);

INSERT INTO welding_machine (time, machine_id, line_id, temperature, pressure, quality_score) VALUES
    (1700000000000, 'WM-042', 'LINE-01', 820, 4.2, 91),
    (1700000001000, 'WM-042', 'LINE-01', 830, 4.3, 93),
    (1700000002000, 'WM-042', 'LINE-01', 840, 4.3, 95);
```

时间使用 Unix 毫秒，设备和产线是 TAG，过程变量是 FIELD。网关需要记录工程单位、采样周期和质量标识；采集失败不能无条件写成零。

## 用正确的 PID 参数顺序回测

```sql
SELECT time, temperature,
       pid_series(temperature, 850.0, 1.8, 0.3, 0.05) AS control_output
FROM welding_machine
WHERE machine_id = 'WM-042'
ORDER BY time;
```

真实签名是 `pid_series(field, setpoint, kp, ki, kd)`，不是把目标温度与 `time` 作为前面三个参数。时间间隔来自相邻记录，按秒归一化；首行只输出比例项。

这里的增益只用于演示语法，不是推荐工艺参数。函数按 series 维护独立状态；重复或逆序时间的 I/D 行为必须按函数合同核对。

## 整定与品质分析

`pid_estimate` 接收过程 FIELD、整定方法、阶跃幅度及样本比例等参数，需要真实且足够的阶跃响应数据。三条合成记录不能证明参数辨识有效，也不应伪造返回的“最优增益”。

```sql
SELECT avg(temperature) AS average_temp,
       avg(pressure) AS average_pressure,
       avg(quality_score) AS average_quality
FROM welding_machine
WHERE machine_id = 'WM-042'
GROUP BY time(1m);
```

这可以观察工艺与品质共同变化，不能直接证明因果。班次、材料、设备状态与检测延迟还需要应用层对齐。

## 从回测到现场

PID 查询不是硬实时控制器。执行机构需要自己的限幅、联锁、故障处理和批准路径；数据库输出不能直接当作已验证安全的动作指令。先离线回测，再现场受控试验，并核对实际采样、延迟和恢复行为。

原底稿中的合格率提升、停机减少和人力节约没有可核验客户报告，本文不作为事实发布。

参考：[PID 指南](https://github.com/IoTSharp/SonnetDB/blob/main/docs/pid-control.md)、[PID 实现](https://github.com/IoTSharp/SonnetDB/tree/main/src/SonnetDB.Core/Query/Functions/Control)、[SQL 参考](https://github.com/IoTSharp/SonnetDB/blob/main/docs/sql-reference.md)。
