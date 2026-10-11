---
title: 标量函数：abs、round、sqrt、log 与 coalesce
categories: SonnetDB,SQL,函数
draft: false
---

标量函数把一行中的输入转换为一个结果。SonnetDB 提供常用数学函数与 `coalesce`，适合在查询中完成数值展示、单位处理和明确的缺值替换。正确使用的前提是核对参数顺序、数值范围和空值行为。

## 准备有效输入

```sql
CREATE MEASUREMENT readings (
    device TAG,
    deviation FIELD FLOAT,
    value FIELD FLOAT,
    bytes FIELD INT
);

INSERT INTO readings (time, device, deviation, value, bytes) VALUES
    (1700000000000, 'sensor-01', -1.25, 9, 1024),
    (1700000001000, 'sensor-01', 2.75, 16, 2048);

SELECT time,
       abs(deviation) AS magnitude,
       round(deviation, 1) AS rounded,
       sqrt(value) AS root,
       log(bytes, 10) AS log10_bytes
FROM readings
ORDER BY time;
```

示例先建立 schema 并使用有效数值域。`abs(x)` 取绝对值，`sqrt(x)` 求平方根，`round(x)` 或 `round(x, digits)` 按指定小数位舍入。

`round` 使用 .NET `Math.Round` 的默认中点舍入规则，即中点取偶数；不能笼统描述为传统的“四舍五入、五总向上”。金额等业务应先确认需要的舍入规则。

## LOG 的参数顺序

`log(x)` 是自然对数；`log(x, base)` 的第一个参数是值，第二个参数是底。

```sql
SELECT log(100) AS natural_log,
       log(100, 10) AS base10_log;
```

十进制字节对数应写 `log(bytes, 10)`，不要反写成 `log(10, bytes)`。

平方根和对数要求调用者考虑数值域。当前实现使用 .NET 数学函数，非法数值域可能产生非有限结果，不能假设它们都会返回 `NULL` 或被自动修复。

## COALESCE 与缺值处理

`coalesce(a, b, ...)` 返回第一个非 `NULL` 输入；所有输入均为空时返回空值。

```sql
SELECT coalesce(NULL, 0) AS default_value;

SELECT time, abs(coalesce(deviation, 0)) AS magnitude
FROM readings
ORDER BY time;
```

数学函数的参数转换会拒绝 `NULL`，因此应在调用函数前处理缺值。`abs(coalesce(deviation, 0))` 表达了这个顺序；`coalesce(abs(NULL), 0)` 不能用来捕获前一个函数的错误。

`coalesce` 也不是通用异常处理器。它不能代替对零分母、负数开方、无效对数底等输入的校验。选择默认值时，应把业务含义写在应用规则中。

本文按当前仓库实现校对：[函数注册与实现](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Query/Functions/FunctionRegistry.cs)、[SQL 参考](https://github.com/IoTSharp/SonnetDB/blob/main/docs/sql-reference.md)、[4.0.0 正式发行](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。
