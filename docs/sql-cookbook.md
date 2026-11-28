---
layout: default
title: "SQL Cookbook"
description: "面向 SonnetDB 当前真实能力的常用 SQL 模板与场景化示例。"
permalink: /sql-cookbook/
---

# SQL Cookbook

这份 cookbook 面向“直接复制一段能跑的 SQL”的场景，补充 [SQL 参考]({{ '/sql-reference/' | relative_url }}) 中偏规则化的说明。

示例以当前仓库 `demo.sql` 和已有回归测试覆盖的语法为准，只使用 SonnetDB 当前版本**已经实现**的写法。

## 使用前先记住 5 条

- 时间列固定叫 `time`，表示 Unix 毫秒时间戳。
- 时序数据建模用 `CREATE MEASUREMENT`；关系表元数据、小对象等可用 `CREATE TABLE ... PRIMARY KEY (...)`。
- Measurement 聚合可按 TAG 和 `time(...)` 分组。
- 跨模型语法以 SQL 参考中的对应入口为准。例如 measurement 与关系表的混合 JOIN 支持内连接，连接键为 measurement 的 TAG。
- 想查“这个库里有什么”，时序数据先 `SHOW MEASUREMENTS` / `DESCRIBE MEASUREMENT <name>`，关系表先 `SHOW TABLES` / `DESCRIBE TABLE <name>`。

## 1. 建库、建用户、授权

适用于服务端模式的控制面 SQL。

```sql
CREATE DATABASE demo;

CREATE USER viewer WITH PASSWORD 'viewer123';
CREATE USER writer WITH PASSWORD 'writer456';
CREATE USER admin2 WITH PASSWORD 'admin789' SUPERUSER;

GRANT READ ON DATABASE demo TO viewer;
GRANT WRITE ON DATABASE demo TO writer;
GRANT ADMIN ON DATABASE demo TO admin2;
```

常用检查语句：

```sql
SHOW DATABASES;
SHOW USERS;
SHOW GRANTS;
SHOW GRANTS FOR viewer;
SHOW TOKENS;
SHOW TOKENS FOR writer;
```

## 2. 创建 Measurement

### 2.1 监控类时序表

```sql
CREATE MEASUREMENT cpu (
    host      TAG,
    region    TAG,
    usage     FIELD FLOAT NULL,
    cores     FIELD INT,
    throttled FIELD BOOL,
    label     FIELD STRING NOT NULL
);
```

### 2.2 向量检索表

```sql
CREATE MEASUREMENT documents (
    source    TAG,
    category  TAG,
    title     FIELD STRING,
    score     FIELD FLOAT,
    embedding FIELD VECTOR(4)
);
```

建表后常用自检：

```sql
SHOW MEASUREMENTS;
DESCRIBE MEASUREMENT cpu;
DESCRIBE MEASUREMENT documents;
```

### 2.3 稀疏字段与 DDL 修饰符

`NULL` / `NOT NULL` 可以写在列声明后，主要用于兼容常见 SQL 生成器；当前不会持久化为 catalog 约束，也不会改变写入行为。SonnetDB 的 field 是稀疏的：某个时间点没写入某个 field，查询时该列返回 `NULL`。

```sql
CREATE MEASUREMENT sensors (
    device      TAG NOT NULL,
    temperature FIELD FLOAT NULL,
    pressure    FIELD FLOAT NOT NULL
);
```

当前 `DEFAULT` 只作为保留语法被 parser 接受，执行 `CREATE MEASUREMENT` 会明确报不支持。需要默认值时，在应用侧或写入 SQL 中直接提供该值；需要缺值时，省略该 field。

## 3. 写入数据

### 3.1 写入单条数据

```sql
INSERT INTO cpu (time, host, region, usage, cores, throttled, label)
VALUES (1713657600000, 'server-01', 'cn-hz', 0.42, 8, FALSE, 'normal');
```

### 3.2 批量写入多条数据

```sql
INSERT INTO cpu (time, host, region, usage, cores, throttled, label) VALUES
    (1713657600000, 'server-01', 'cn-hz', 0.42, 8, FALSE, 'normal'),
    (1713657660000, 'server-01', 'cn-hz', 0.55, 8, FALSE, 'normal'),
    (1713657720000, 'server-01', 'cn-hz', 0.61, 8, FALSE, 'normal');
```

### 3.3 写入向量

```sql
INSERT INTO documents (time, source, category, title, score, embedding) VALUES
    (1713657600000, 'wiki', 'tech', '时序数据库简介', 0.92, [0.10, 0.20, 0.30, 0.40]),
    (1713657601000, 'blog', 'tech', 'SonnetDB 快速入门', 0.95, [0.11, 0.21, 0.29, 0.39]);
```

## 4. 原始查询

### 4.1 查某个序列的原始点

```sql
SELECT *
FROM cpu
WHERE host = 'server-01'
  AND region = 'cn-hz';
```

### 4.2 指定列投影 + 时间范围

```sql
SELECT time, host, usage
FROM cpu
WHERE host = 'server-01'
  AND time >= 1713657600000
  AND time < 1713658200000;
```

### 4.3 标量函数

```sql
SELECT
    abs(usage - 0.5)           AS deviation,
    round(usage * 100, 1)      AS usage_pct,
    sqrt(cores)                AS sqrt_cores,
    log(cores, 2)              AS log2_cores,
    coalesce(label, 'unknown') AS safe_label
FROM cpu
WHERE host = 'server-01';
```

### 4.4 单表别名

```sql
SELECT c.time, c.host, c."usage"
FROM cpu AS c
WHERE c.host = 'server-01'
ORDER BY c.time ASC
LIMIT 10;
```

measurement 单表查询支持别名限定列名；需要把设备、租户、站点等维度补到时序结果时，可使用 MM4 的 measurement JOIN 关系维表。

### 4.5 分页

```sql
SELECT time, host, usage
FROM cpu
WHERE host = 'server-01'
ORDER BY time ASC
LIMIT 5 OFFSET 0;
```

```sql
SELECT time, host, usage
FROM cpu
WHERE host = 'server-01'
ORDER BY time ASC
OFFSET 5 ROWS FETCH NEXT 5 ROWS ONLY;
```

### 4.6 时序 JOIN 关系维表

```sql
SELECT t.time, d.name, d.site, t.value
FROM temperature AS t
JOIN devices AS d ON t.device_id = d.id
WHERE d.tenant = 'tenant-1'
  AND t.time >= 1713657600000
ORDER BY t.time DESC
LIMIT 100;
```

当前 JOIN 第一版限定为一个 measurement 与一个关系表的 inner 等值 JOIN；measurement 侧连接键必须是 TAG 列。有歧义的列名请使用 `alias.column`。

## 5. 聚合与时间桶

### 5.1 基础聚合

```sql
SELECT
    count(usage) AS cnt,
    sum(usage)   AS total,
    min(usage)   AS min_usage,
    max(usage)   AS max_usage,
    avg(usage)   AS avg_usage,
    first(usage) AS first_usage,
    last(usage)  AS last_usage
FROM cpu
WHERE host = 'server-01';
```

### 5.2 Measurement 扩展聚合

```sql
SELECT
    stddev(usage)       AS std,
    variance(usage)     AS var,
    spread(usage)       AS spread,
    median(usage)       AS median,
    percentile(usage, 95) AS p95,
    histogram(usage, 5) AS hist
FROM cpu
WHERE host = 'server-01';
```

### 5.3 按时间桶聚合

```sql
SELECT
    avg(usage)   AS avg_usage,
    max(usage)   AS max_usage,
    count(usage) AS cnt
FROM cpu
WHERE host = 'server-01'
GROUP BY time(2m);
```

说明：

- Measurement 的时间桶语法为 `GROUP BY time(...)`，可以同时按 TAG 分组，例如 `GROUP BY host, time(2m)`。
- 需要桶起始时间时，在投影中显式加入 `time AS bucket_start`。

### 5.4 按关系表日期分组

对于含有 `occurred_at DATETIME2` 的关系表 `events`，可以用 `DATE_TRUNC` 截出每个月的起点，再对该时间值分组。

```sql
SELECT DATE_TRUNC('month', occurred_at) AS month_start, COUNT(*) AS event_count
FROM events
GROUP BY DATE_TRUNC('month', occurred_at)
ORDER BY month_start;
```

`DATE_TRUNC` 保留 `DATETIME2` 的结果类型。`DATETIMEOFFSET` 按 UTC 截断并返回零偏移。

### 5.5 关系表扩展聚合

```sql
CREATE TABLE request_samples (id INT, site STRING, latency_ms DECIMAL(8,2), PRIMARY KEY (id));
INSERT INTO request_samples (id, site, latency_ms) VALUES
    (1, 'A', 13.25), (2, 'A', 20.50), (3, 'B', 10.00), (4, 'B', NULL);
SELECT site, median(latency_ms) AS median_ms, p50(latency_ms) AS p50_ms,
       p90(latency_ms) AS p90_ms, percentile(latency_ms, 95) AS p95_ms,
       p95(latency_ms) AS fixed_p95_ms, p99(latency_ms) AS p99_ms,
       stddev(latency_ms) AS sample_sd, variance(latency_ms) AS sample_variance
FROM request_samples
GROUP BY site
HAVING COUNT(latency_ms) > 0
ORDER BY p99_ms DESC;
```

这些统计接受 INT、FLOAT 和 DECIMAL，返回可为 NULL 的 FLOAT。`percentile` 的第二个参数是 `(0, 100]` 内的数值字面量或绑定参数。TDigest 估算分位数，样本方差和标准差在少于两个有效值时返回 NULL。

```sql
SELECT site, spread(latency_ms) AS range_ms, mode(latency_ms) AS most_common_ms,
       histogram(latency_ms, 10) AS buckets, distinct_count(latency_ms) AS distinct_values,
       tdigest_agg(latency_ms) AS digest
FROM request_samples
GROUP BY site
ORDER BY distinct_values DESC;
```

`distinct_count` 返回精确计数，`histogram` 和 `tdigest_agg` 返回 JSON 文本。[SQL 聚合参考](sql-reference.md#关系表聚合)

## 6. 窗口分析

### 6.1 差分与变化率

```sql
SELECT time, difference(usage) AS diff_usage
FROM cpu
WHERE host = 'server-01';
```

```sql
SELECT time, delta(usage) AS delta_usage
FROM cpu
WHERE host = 'server-01';
```

```sql
SELECT time, derivative(usage) AS rate_per_sec
FROM cpu
WHERE host = 'server-01';
```

```sql
SELECT time, non_negative_derivative(usage) AS nn_rate
FROM cpu
WHERE host = 'server-01';
```

### 6.2 平滑与累计

```sql
SELECT time, cumulative_sum(usage) AS cumsum
FROM cpu
WHERE host = 'server-01';
```

```sql
SELECT time, moving_average(usage, 3) AS ma3
FROM cpu
WHERE host = 'server-01';
```

```sql
SELECT time, ewma(usage, 0.3) AS ewma_usage
FROM cpu
WHERE host = 'server-01';
```

### 6.3 状态分析

```sql
SELECT time, state_changes(throttled) AS changed
FROM cpu
WHERE host = 'server-01';
```

```sql
SELECT time, state_duration(throttled) AS duration_ms
FROM cpu
WHERE host = 'server-01';
```

## 7. PID / 预测 / 异常 / 变点

### 7.1 PID 行级控制量

```sql
SELECT
    time,
    temperature,
    pid_series(temperature, 75.0, 0.6, 0.1, 0.05) AS valve
FROM reactor
WHERE device = 'r1'
ORDER BY time ASC;
```

### 7.2 PID 自动整定

```sql
SELECT
    pid_estimate(temperature, 'imc', 1.0, 0.1, 0.1, NULL) AS tuning_json
FROM reactor
WHERE device = 'r1'
  AND time >= 1713657600000
  AND time < 1713657800000;
```

### 7.3 预测

```sql
SELECT *
FROM forecast(cpu, usage, 5, 'linear')
WHERE host = 'server-01';
```

### 7.4 异常检测

```sql
SELECT
    time,
    usage,
    anomaly(usage, 'mad', 2.5) AS is_outlier
FROM cpu
WHERE host = 'server-01';
```

### 7.5 变点检测

```sql
SELECT
    time,
    value,
    changepoint(value, 'cusum', 4.0) AS shift_detected
FROM signal
WHERE source = 's-1';
```

## 8. 向量检索

### 8.1 基础 KNN

```sql
SELECT *
FROM knn(documents, embedding, [0.10, 0.20, 0.30, 0.40], 3);
```

### 8.2 指定距离度量

```sql
SELECT *
FROM knn(documents, embedding, [0.10, 0.20, 0.30, 0.40], 3, 'l2');
```

### 8.3 混合检索

```sql
SELECT *
FROM knn(documents, embedding, [0.10, 0.20, 0.30, 0.40], 5, 'cosine')
WHERE source = 'wiki'
  AND time >= 1713657600000
  AND time < 1713657605000;
```

### 8.4 标量向量函数

```sql
SELECT
    cosine_distance(embedding, [0.10, 0.20, 0.30, 0.40]) AS cos_dist,
    l2_distance(embedding, [0.10, 0.20, 0.30, 0.40])     AS l2_dist,
    inner_product(embedding, [0.10, 0.20, 0.30, 0.40])   AS dot_prod,
    vector_norm(embedding)                                AS norm
FROM documents
WHERE source = 'wiki';
```

## 9. 元数据查询

```sql
SHOW MEASUREMENTS;
SHOW TABLES;
```

```sql
DESCRIBE MEASUREMENT cpu;
DESCRIBE MEASUREMENT documents;
DESC reactor;
```

## 10. 删除与清理

### 10.1 按时间范围删除

```sql
DELETE FROM cpu
WHERE host = 'server-01'
  AND time >= 1713658080000
  AND time <= 1713658140000;
```

### 10.2 按 tag 删除整条序列

```sql
DELETE FROM signal
WHERE source = 's-1';
```

建议先用对应的 `SELECT` 估算影响范围，再执行 `DELETE`。

## 11. 常见误区

不要这样写：

```sql
CREATE TABLE cpu (...);                         -- 时序数据应改为 CREATE MEASUREMENT
SELECT usage, avg(usage) FROM cpu GROUP BY usage; -- measurement 的 FIELD 不可用作分组键
SELECT time_bucket(time, '1m'), avg(usage) ...;   -- measurement 用 GROUP BY time(1m)
UPDATE cpu SET usage = 1.0 WHERE ...;           -- measurement 普通数值 FIELD 不支持 UPDATE；已有 VECTOR FIELD 可按 TAG/time 条件更新
```

如果你拿不准当前能力边界：

- 先看 [SQL 参考]({{ '/sql-reference/' | relative_url }})
- 再让 Copilot 先 `SHOW MEASUREMENTS` / `DESCRIBE MEASUREMENT` 或 `SHOW TABLES` / `DESCRIBE TABLE`
- 写入或删除前，优先走只读模式先起草 SQL
