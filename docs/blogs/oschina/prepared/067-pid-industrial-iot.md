---
title: 工业 IoT 场景：SonnetDB PID 与 PLC 的对比优势
categories: SonnetDB,工业IoT,PID
draft: false
---

PLC 与数据库承担不同职责。PLC 负责现场确定周期的控制、I/O 与安全逻辑，SonnetDB 适合保存历史 PV、设定值和执行器输出，进行跨设备分析、参数辨识与控制律回放。数据库中的 PID 分析可以补充现场控制系统的长期观测能力。

旧稿对 PLC 历史容量、通用成本和学习难度的绝对判断缺少特定产品与部署依据，本篇不据此宣称数据库替代 PLC。正式 [4.0.0](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0) 与当前主线文档也应分别核对。

## 将控制历史集中保存

```sql
CREATE MEASUREMENT production_lines (
    line_id TAG,
    device_id TAG,
    temperature FIELD FLOAT,
    setpoint FIELD FLOAT,
    actuator_output FIELD FLOAT
);

INSERT INTO production_lines
    (time, line_id, device_id, temperature, setpoint, actuator_output) VALUES
    (1700000000000, 'line-a', 'furnace-01', 90.0, 100.0, 30.0),
    (1700000001000, 'line-a', 'furnace-01', 92.0, 100.0, 32.0);

SELECT avg(temperature) AS avg_pv, avg(actuator_output) AS avg_output
FROM production_lines
WHERE line_id = 'line-a' AND device_id = 'furnace-01'
GROUP BY time(1h);
```

示例记录的是 PLC 实际输出，不能与数据库计算出的候选输出混为一列。生产系统还应记录设备时钟、质量标志、参数版本和试验身份。

## 数据库侧控制律回放

```sql
SELECT time, temperature, actuator_output,
       pid_series(temperature, 100.0, 2.0, 0.5, 0.1) AS candidate_output
FROM production_lines
WHERE line_id = 'line-a' AND device_id = 'furnace-01'
ORDER BY time;
```

`pid_series` 是五参窗口函数。它依据已有 PV 计算候选控制量，适合检查误差与积分趋势；它没有重新驱动被控对象，因此不能凭一条回放曲线断言参数改善了现场超调。

## PLC 与 SonnetDB 的协作

可由现有采集网关取得设备数据，再通过明确支持的 SonnetDB 摄取入口保存。OPC UA、MQTT 或其它现场协议的适配、设备授权和时钟同步，需要按实际网关与连接器验证，不能将架构图视为已完成的现场集成。

参数辨识可在维护窗口或离线分析阶段执行；候选参数由工程师复核，再通过设备正式配置流程下发。网络断连、数据库繁忙或建议参数不可用时，现场控制应按设备设计继续安全运行。

SonnetDB 的价值是让历史查询与分析更容易组合。实际成本、吞吐和可靠性要用目标设备、采集规模与故障恢复测试衡量。本文提供示例设计，没有现场客户验证、硬实时周期或安全认证结论。

参考：[PID 合同](https://github.com/IoTSharp/SonnetDB/blob/main/docs/pid-control.md)、[协议摄取](https://github.com/IoTSharp/SonnetDB/blob/main/docs/protocol-ingest.md)、[能力成熟度](https://github.com/IoTSharp/SonnetDB/blob/main/docs/capability-maturity.md)。
