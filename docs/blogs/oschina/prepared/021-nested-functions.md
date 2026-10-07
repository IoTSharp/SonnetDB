---
title: 函数嵌套调用：构建复杂的计算表达式
categories: SonnetDB,SQL,函数
draft: false
---

函数可以组合使用：先替换缺值，再取绝对值，最后按显示精度舍入。SonnetDB 的表达式支持这种嵌套写法，适合把一个计算过程清楚地放在投影列中。

## 从内向外组织计算

```sql
CREATE MEASUREMENT readings (
    device TAG,
    deviation FIELD FLOAT,
    value FIELD FLOAT
);

INSERT INTO readings (time, device, deviation, value) VALUES
    (1700000000000, 'sensor-01', -1.256, 9),
    (1700000001000, 'sensor-01', 2.754, 16);

SELECT time,
       round(abs(coalesce(deviation, 0)), 2) AS magnitude,
       round(sqrt(value), 3) AS root
FROM readings
ORDER BY time;
```

第一个表达式先执行 `coalesce(deviation, 0)`，再取绝对值，最后保留两位小数。第二个表达式先开方，再整理显示精度。别名把整个表达式映射为一个可供客户端使用的结果列。

`round` 的默认中点规则来自 .NET `Math.Round`，中点取偶数。嵌套不会改变内部函数的类型、参数数量或舍入合同。

## 聚合结果的展示

受支持的聚合结果也可以交给标量函数处理：

```sql
SELECT round(avg(value), 2) AS average_value
FROM readings
WHERE device = 'sensor-01';
```

这表达的是“先求平均，再舍入”。`avg(round(value, 2))` 则是“先逐行舍入，再求平均”，两者在有小数输入时可能产生不同结果。选择顺序必须来自业务规则。

标量包裹聚合不意味着任意聚合之间都能嵌套，也不意味着所有窗口函数组合都受支持。新增查询形态时，应核对对应版本的执行器范围。

## 缺值与数值域放在输入端

```sql
SELECT abs(coalesce(NULL, 0)) AS safe_magnitude,
       round(log(100, 10), 2) AS logarithm;
```

数学函数的参数转换不自动传播 `NULL`，所以缺值替换应放在函数输入处。`coalesce(abs(NULL), 0)` 不能捕获 `abs` 的参数错误。`coalesce` 也不能保证平方根、对数等非法数值域变成空值。

表达式越长，越应关注可读性和重复计算。优先使用清晰别名、括号与分步骤验证；不要从简短 SQL 推导出固定性能收益。

本文按当前仓库实现校对：[函数实现](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Query/Functions/FunctionRegistry.cs)、[SQL 参考](https://github.com/IoTSharp/SonnetDB/blob/main/docs/sql-reference.md)、[4.0.0 正式发行](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。
