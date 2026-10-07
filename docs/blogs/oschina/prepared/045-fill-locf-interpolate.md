---
title: 'SonnetDB 缺失值处理三剑客：fill() / locf() / interpolate()'
categories: SonnetDB,数据库
draft: false
---

正式发行见 [4.0.0 Release](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。本文按 2026-10-07 当前 main 文档与源码修订旧稿；主线改动不自动代表全部 v4 发行物与客户端的已验收能力。以下 SQL/API 为复现示例，本次没有实际执行或性能测量。

缺失值处理必须区分“这一行某字段缺失”和“这一时刻根本没有行”。SonnetDB 的 fill、locf、interpolate 处理既有输入行，不会自动补齐连续时间轴。

```sql
SELECT time, temperature,
       fill(temperature, 0) AS filled_zero,
       locf(temperature) AS carried_forward,
       interpolate(temperature) AS interpolated
FROM sensors
WHERE device = 'sensor-01'
  AND time >= 1700000000000 AND time < 1700003600000
ORDER BY time;
```

## 三种不同假设

fill 用给定数值替换缺失字段，原值存在时透传。0 是业务数据，不应因为绘图方便就当作普遍合理的替代值。

locf 沿用最近一次有效值；首段尚无有效值时输出 NULL。它表达“此前状态继续有效”的假设，而不是证明传感器仍正常。

interpolate 使用左右两个有效锚点，并按实际时间戳比例线性插值。首段或末段没有对应锚点时保留 NULL；不对边界做外推。它需要未来锚点，因此不能从函数名推断它是无等待的在线算法。

## 对采样缺口保留解释

稀疏 measurement 中，其他 FIELD 可以使这一时间戳的行存在，从而显示 temperature 的 NULL。如果设备整分钟都没写任何 FIELD，则这些函数不会创造那一分钟。

需要固定频率输出时，应由应用明确生成时间轴，并记录填充来源、最大允许缺口和过期策略。财务、计量或安全告警通常应保留原值与估计值，避免把修补数据当作真实观测。

依据：[NullHandlingFunctions.cs](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Query/Functions/Window/NullHandlingFunctions.cs)。
