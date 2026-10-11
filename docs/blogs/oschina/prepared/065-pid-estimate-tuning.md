---
title: PID 参数整定估计：三种经典方法
categories: SonnetDB,PID,参数整定
draft: false
---

PID 参数可以从受控阶跃试验中估计。SonnetDB 的 `pid_estimate` 先根据时序样本识别一阶加纯滞后过程，再按 Ziegler–Nichols、Cohen–Coon 或 IMC 规则计算候选 Kp、Ki、Kd。结果是整定起点，不是已经在真实设备上验证的最优参数。

当前 SQL 合同是六参聚合：`pid_estimate(field, method, step_magnitude, initial_fraction, final_fraction, imc_lambda)`。旧稿把模型参数塞进 `json_object` 的调用不符合该接口。正式版本入口为 [4.0.0](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)；本文依据当前文档与实现复核，运行时仍应核对自己的发行版本。

## 准备阶跃响应数据

```sql
CREATE MEASUREMENT reactor (
    device TAG,
    temperature FIELD FLOAT
);

INSERT INTO reactor (time, device, temperature) VALUES
    (1700000000000, 'r1', 20.0),
    (1700000001000, 'r1', 20.0),
    (1700000002000, 'r1', 20.4),
    (1700000003000, 'r1', 21.0),
    (1700000004000, 'r1', 21.8),
    (1700000005000, 'r1', 22.8),
    (1700000006000, 'r1', 24.0),
    (1700000007000, 'r1', 25.4),
    (1700000008000, 'r1', 26.6),
    (1700000009000, 'r1', 27.6),
    (1700000010000, 'r1', 28.4),
    (1700000011000, 'r1', 29.0),
    (1700000012000, 'r1', 29.5),
    (1700000013000, 'r1', 29.8),
    (1700000014000, 'r1', 30.0),
    (1700000015000, 'r1', 30.0);
```

这是说明采样组织方式的合成数据，不是现场设备证据。真实试验必须记录施加的阶跃幅度、采样间隔和足够的初末稳态。

## 三种规则

```sql
SELECT pid_estimate(temperature, 'imc', 1.0, 0.1, 0.1, NULL) AS tuning
FROM reactor WHERE device = 'r1';

SELECT pid_estimate(temperature, 'zn', 1.0, 0.1, 0.1, NULL) AS tuning
FROM reactor WHERE device = 'r1';

SELECT pid_estimate(temperature, 'cc', 1.0, 0.1, 0.1, NULL) AS tuning
FROM reactor WHERE device = 'r1';
```

`step_magnitude` 是输入控制量的变化 Δu，不能把响应温差当作它。初末比例分别取首尾样本估计稳态，要求处于 `(0,0.5)`。IMC 的 lambda 以秒表示，NULL 使用辨识滞后参数；增大 lambda 通常使整定更保守，具体效果仍取决于过程。

结果是一行 JSON 字符串，键为小写 `kp/ki/kd`。客户端解析后，可把数值常量带回 `pid_series(field, setpoint, kp, ki, kd)` 做控制律回放。

## 辨识与上线边界

实现采用 35%/85% 两点法识别 FOPDT 模型。少于 10 个样本、首尾变化太小或未覆盖响应阈值时，辨识可能失败，应补充有效数据，而不是把错误转换为默认控制参数。

三种方法并不存在对所有工业过程成立的优胜顺序。整定、模型仿真、受控闭环验证和设备上线是不同阶段；数据库分析不能替代 PLC 的确定周期、安全联锁与执行器保护。本文只核对代码和文档，没有运行 SQL、辨识试验或现场闭环。

参考：[PID 合同](https://github.com/IoTSharp/SonnetDB/blob/main/docs/pid-control.md)、[SQL 自动整定实现](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Query/Functions/Control/PidEstimateFunction.cs)、[参数辨识实现](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Query/Functions/Control/PidParameterEstimator.cs)。
