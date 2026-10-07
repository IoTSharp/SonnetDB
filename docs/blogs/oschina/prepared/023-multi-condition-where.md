---
title: 多条件过滤：AND 连接多个 WHERE 约束
categories: SonnetDB,SQL,过滤
draft: false
---

真实查询往往同时要求设备、区域、时间和观测值满足条件。SonnetDB 可以用 `AND` 连接这些约束，并用括号清楚表达 `OR` 分组。写清逻辑关系有助于避免误查，也便于检查输入范围。

## 准备完整数据

```sql
CREATE MEASUREMENT cpumetrics (
    host TAG,
    region TAG,
    usage FIELD FLOAT,
    enabled FIELD BOOL
);

INSERT INTO cpumetrics (time, host, region, usage, enabled) VALUES
    (1700000000000, 'server-01', 'north', 42.5, true),
    (1700000001000, 'server-01', 'north', 85.0, true),
    (1700000002000, 'server-02', 'south', 60.0, false);

INSERT INTO cpumetrics (time, host, region, enabled) VALUES
    (1700000003000, 'server-03', 'north', true);
```

最后一条省略 `usage`，用于展示稀疏 FIELD 的缺值。

```sql
SELECT time, host, usage
FROM cpumetrics
WHERE host = 'server-01'
  AND time >= 1700000000000
  AND time < 1700000002000
  AND usage > 50
  AND enabled = true
ORDER BY time;
```

TAG 和时间条件限定业务范围，FIELD 条件筛选值。当前 main 实现包含 FIELD 残余过滤路径；旧安装版本与仓库部分历史说明可能存在差异，应按具体版本核对。

## OR、IN 与括号

```sql
SELECT time, host, usage
FROM cpumetrics
WHERE (host = 'server-01' OR host = 'server-02')
  AND usage >= 50
ORDER BY time;

SELECT time, host, usage
FROM cpumetrics
WHERE host IN ('server-01', 'server-02')
  AND time < 1700000003000
ORDER BY time;
```

混用 `AND/OR` 时建议始终写括号。括号表达逻辑分组，不是对扫描顺序的指令。SQL 文本中哪个条件写在前面，也不保证执行器先计算哪个条件或一定使用索引。

## 空值使用 IS NULL

```sql
SELECT time, host FROM cpumetrics WHERE usage IS NULL;
SELECT time, host FROM cpumetrics WHERE usage IS NOT NULL;
```

不要写 `usage = NULL` 来查缺值。涉及 `NULL` 的普通比较可能得到 `UNKNOWN`，而 `WHERE` 只保留结果为真的行。

查询用作删除前检查时，检查结果只是那个请求的观察。并发写入可能在后续删除前改变目标集合；预览本身不构成事务性批准或快照。

本文按当前仓库实现与测试校对：[SELECT 测试](https://github.com/IoTSharp/SonnetDB/blob/main/tests/SonnetDB.Core.Tests/Sql/SqlExecutorSelectTests.cs)、[measurement 过滤预算测试](https://github.com/IoTSharp/SonnetDB/blob/main/tests/SonnetDB.Core.Tests/Sql/SqlMeasurementMaterializationBudgetTests.cs)、[4.0.0 正式发行](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。
