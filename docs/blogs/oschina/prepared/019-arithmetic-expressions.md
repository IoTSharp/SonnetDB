---
title: 算术表达式：在投影列中灵活计算数据
categories: SonnetDB,SQL,表达式
draft: false
---

原始观测值经常需要转换后才便于展示。例如把电压、电流换算成功率，把摄氏温度转成华氏温度，或把字节数转成 MiB。SonnetDB 的 SQL 投影支持算术表达式，可以在返回结果时完成这些计算。

## 从电压和电流计算功率

在同一练习数据库执行以下完整示例：

```sql
CREATE MEASUREMENT electrical_meter (
    device TAG,
    voltage FIELD FLOAT,
    current FIELD FLOAT
);

INSERT INTO electrical_meter (time, device, voltage, current) VALUES
    (1700000000000, 'meter-01', 220, 2.5),
    (1700000001000, 'meter-01', 221, 2.8);

SELECT time,
       voltage * current AS power_w,
       (voltage * current) / 1000 AS power_kw
FROM electrical_meter
WHERE device = 'meter-01'
ORDER BY time;
```

括号使计算意图明确，别名给结果列一个稳定的业务名称。这些表达式产生查询结果，不会把 `power_w` 自动写回 schema。

## 运算符与类型

支持的基础运算包括 `+`、`-`、`*`、`/` 和 `%`。乘除优先于加减；复杂表达式建议显式加括号。

```sql
SELECT 5 / 2 AS quotient,
       5 % 2 AS remainder,
       (2 + 3) * 4 AS grouped_value;
```

整数除法 `5 / 2` 得到浮点结果 `2.5`，不按整数截断。整数加、减、乘和取模走 Int64 路径，溢出会失败。包含浮点操作数时使用 Float64 算术；Decimal 操作数存在时遵循对应 Decimal 运算路径，不能把所有除法结果统一描述成一种类型。

除零和对零取模会报错，不会自动返回 `NULL`。查询应先确保分母合法，或在应用中定义明确的业务处理。

## 缺值与单位

普通算术遇到 `NULL` 会传播缺值。若业务明确把缺失电流看作零，可以在操作数层使用 `coalesce`：

```sql
SELECT time,
       voltage * coalesce(current, 0) AS estimated_power_w
FROM electrical_meter
ORDER BY time;
```

这改变了业务含义，不能把“缺少测量”无条件等同于“测量值为零”。对于金额、精确计量等场景，还要明确精度和舍入规则。

字符串不能通过 `+` 自动拼接或任意转成数字；应使用已支持的字符串函数、显式转换，或者在摄取阶段整理类型。结果上的排序与过滤也可能增加计算成本，需要结合数据量验证。

本文按当前仓库实现校对：[算术实现](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Sql/Execution/SqlScalarOperations.cs)、[SQL 参考](https://github.com/IoTSharp/SonnetDB/blob/main/docs/sql-reference.md)、[4.0.0 正式发行](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。
