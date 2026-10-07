---
title: 'SonnetDB 窗口函数流水线：从差值计算到异常检测的完整监控链路'
categories: SonnetDB,数据库
draft: false
---

正式发行见 [4.0.0 Release](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。本文按 2026-10-07 当前 main 文档与源码修订旧稿；主线改动不自动代表全部 v4 发行物与客户端的已验收能力。以下 SQL/API 为复现示例，本次没有实际执行或性能测量。

监控流水线常包含缺失处理、差分、平滑和告警。SonnetDB 提供这些单独函数，但当前 measurement 窗口参数主要接受 FIELD 标识符，不能把任意嵌套表达式当作一条可执行流水线。

## 先并列观察每一步

```sql
SELECT time, bytes_total,
       increase(bytes_total) AS growth,
       rate(bytes_total, 1s) AS bytes_per_second,
       moving_average(bytes_total, 5) AS smooth_counter
FROM network
WHERE host = 'gateway-01'
  AND time >= 1700000000000 AND time < 1700003600000
ORDER BY time;
```

这里的 moving_average 平滑原始计数器，并没有平滑 rate 的输出。列别名也不能自动成为同一 SELECT 中窗口函数的新 FIELD。

## 真实的多阶段处理

应用可以读取时间和值，先用明确策略计算增量或速率，再对这些中间结果平滑，最后结合阈值与最少有效样本判断告警。若保存中间 measurement，应显式定义 schema、原始时间、模型版本、缺失原因和幂等写入策略。

不要直接使用 `moving_average(rate(bytes_total),5)`，也不要借用关系 ANSI OVER、CTE 或派生表拼接后就宣称 measurement 窗口嵌套受支持。语法与模型执行边界必须分别核对。

## 让告警可解释

increase 的 reset 负差输出 NULL；rate 比较相邻有效点，跨缺口时间仍影响分母。fill(0) 可能把停报变成零速率，locf 可能掩盖离线，平滑也会推迟异常出现。保留原值、缺口、reset 和处理中间值，才能判断告警是真实业务变化还是采集问题。

本文只给出当前可表达的步骤与组合边界，没有运行完整监控链或证明异常检测质量。

依据：[WindowFunctionBinder.cs](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Query/Functions/Window/WindowFunctionBinder.cs)、[PointDifferenceFunctions.cs](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Query/Functions/Window/PointDifferenceFunctions.cs)。
