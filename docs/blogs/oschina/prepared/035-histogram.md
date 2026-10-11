---
title: 使用 histogram() 进行等宽分桶分布分析
categories: SonnetDB,数据库,聚合
draft: false
---

直方图可以帮助观察响应时间、温度或电压集中在哪些区间。SonnetDB 的 `histogram(field, bin_width)` 在 SQL 聚合中计算等宽区间计数。旧稿把第二个参数写成“桶数量”，并把返回值描述为嵌套表，这两点都需要纠正。

正式发行见 [4.0.0 Release](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。本文按当前 `main` 的文档和实现核对；部署时检查自己的版本。以下 SQL 与结果说明为操作示例，本次没有执行 SQL 或测量性能。

## 第二个参数是桶宽

`histogram(response_time_ms, 50)` 表示每个区间宽 50 毫秒，不是产生 50 个桶。当前实现要求桶宽为正的有限数，并按 `floor(value / bin_width)` 找桶，所以边界相对于零对齐；负数也按向下取整归入相应区间。

例如 35、85、120 三个值，在宽度 50 下分别落入 `[0,50)`、`[50,100)`、`[100,150)`。这只是便于理解的手工分桶示例，不是本次数据库运行回执。

## 写入响应时间并查看分布

```sql
CREATE MEASUREMENT api_requests (
    service TAG,
    response_time_ms FIELD FLOAT
);

INSERT INTO api_requests (time, service, response_time_ms)
VALUES (1713676800000, 'orders', 35),
       (1713676810000, 'orders', 85),
       (1713676820000, 'orders', 120);

SELECT histogram(response_time_ms, 50) AS distribution
FROM api_requests
WHERE service = 'orders'
  AND time >= 1713676800000
  AND time < 1713763200000;
```

已有 schema 时不重复 CREATE。当前数值聚合接受 Float64、Int64 和 Boolean 的既有数值转换，不接受字符串、向量或地理字段。

## 返回值是 JSON 字符串

实现生成一个 JSON 对象字符串，键是左闭右开的区间文字，值是该区间计数。例如：

```json
{"[0,50)":1,"[50,100)":1,"[100,150)":1}
```

只输出实际有值的桶，并按桶序排列。没有有效输入时返回 NULL；NaN 和 Infinity 不参与分桶。它不是可直接 `LATERAL UNNEST` 的表，也没有旧稿假定的 `bucket_start/bucket_end/count` 列。需要画图或计算百分比时，在应用中解析字符串、求计数之和，并处理 NULL 或空数据。

## 与时间桶组合

```sql
SELECT time, histogram(response_time_ms, 50) AS distribution
FROM api_requests
WHERE service = 'orders'
  AND time >= 1713676800000
  AND time < 1713763200000
GROUP BY time(1h);
```

这样可以分别观察每小时的分布。更窄的桶宽提供更细的区间，但也可能增加实际桶数量与状态开销；当前字典保存出现过的桶，不能宣称固定数量、固定内存或任意规模的查询性能。

直方图适合观察分布，精确阈值告警、分位数和直方图各自回答不同的问题。选桶宽时先依据业务单位，再用真实分布核对有效性。

依据：[SQL 参考](https://github.com/IoTSharp/SonnetDB/blob/main/docs/sql-reference.md)、[HistogramFunction/Accumulator](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Query/Functions/Aggregates/ExtendedAggregateFunctions.cs)。
