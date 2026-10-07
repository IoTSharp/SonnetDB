---
title: '行间变化检测：difference() 与 delta() 窗口函数'
categories: SonnetDB,数据库
draft: false
---

正式发行见 [4.0.0 Release](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。本文按 2026-10-07 当前 main 文档与源码修订旧稿；主线改动不自动代表全部 v4 发行物与客户端的已验收能力。以下 SQL/API 为复现示例，本次没有实际执行或性能测量。

difference 和 delta 都计算当前有效值减去上一有效值。在 SonnetDB 的 measurement 行级窗口路径中，这两个名字使用同一个差分求值器。

## 观察相邻有效采样

假设 sensors 包含 device TAG、temperature FIELD FLOAT：

```sql
SELECT time, temperature,
       difference(temperature) AS change,
       delta(temperature) AS delta_value
FROM sensors
WHERE device = 'sensor-01'
  AND time >= 1700000000000 AND time < 1700003600000
ORDER BY time;
```

序列 20、23、21 对应首点 NULL、随后 3、-2。这是函数定义的手算说明。差值保留正负号，不除以时间间隔，所以同样的 3 度变化在 1 秒和 1 分钟内得到相同差值。

## 缺失值与过滤边界

缺失行输出 NULL，且不更新上一有效值。因此 20、NULL、23 的最后差分是 3；“相邻”是有效样本链，而不是保证相邻采样行都有值。WHERE 时间范围先决定输入，范围内首点缺少范围外前驱时会返回 NULL。

函数参数应为 FIELD 列名，不能将任意嵌套窗口表达式当作参数。本文使用直接投影，不附加 PostgreSQL OVER 子句；关系表 ANSI 窗口是另一条执行合同。

差值适合突变观察；需要单位时间变化率时使用 derivative。需要计数器 reset 行为时，应读取 increase/rate 的真实实现，不能把差分结果自动视为累计业务增长。

依据：[PointDifferenceFunctions.cs](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Query/Functions/Window/PointDifferenceFunctions.cs)。
