---
title: 'SonnetDB 累计与积分函数：cumulative_sum() 运行总和与 integral() 梯形面积'
categories: SonnetDB,数据库
draft: false
---

正式发行见 [4.0.0 Release](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。本文按 2026-10-07 当前 main 文档与源码修订旧稿；主线改动不自动代表全部 v4 发行物与客户端的已验收能力。以下 SQL/API 为复现示例，本次没有实际执行或性能测量。

cumulative_sum 与 integral 都产生累计结果，含义却不同：前者累加采样值，后者按时间间隔计算梯形面积。

```sql
SELECT time, power,
       cumulative_sum(power) AS sample_sum,
       integral(power, 1h) AS energy_wh
FROM meter
WHERE device = 'meter-01'
  AND time >= 1700000000000 AND time < 1700003600000
ORDER BY time;
```

假设 meter 的 power 为以瓦计量的 FLOAT FIELD，按小时归一化的梯形积分单位就是瓦时。直接 cumulative_sum(power) 只累加数值，没有乘以采样间隔，不能替代电能计算。

## 初值与缺失

cumulative_sum 在首个有效值前返回 NULL；之后缺失输入保留前一累计值。integral 在首个有效点输出累计面积 0，对后续有效点使用 `(previous+current)/2 * delta_ms/unit_ms`；默认 unit 为 1 秒。

integral 的缺失输入不增加面积，但下一有效值会与前一有效值跨缺口形成梯形。这个数学假设未必符合断网期间真实功率，应由应用标出缺口，或者在明确策略下拆分积分区间。

积分需要时间顺序与明确物理单位。范围过滤会重新起算累计状态，不能把不同查询范围内的运行总和直接拼接为一个连续账本。存储原始采样、估计缺口和计费结算需要分别治理。

依据：[RunningFunctions.cs](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Query/Functions/Window/RunningFunctions.cs)。
