## SQL Cookbook：常用查询模式速查

在时序数据库的日常使用中，有一些查询模式会反复出现。本文将汇总 10 个最常用的时序查询场景，每个场景提供一个可直接运行的 SQL 模板，方便你在需要时快速参考。

### 1. 建库、建用户与授权

```sql
CREATE DATABASE demo;
CREATE USER writer WITH PASSWORD 'writer456';
GRANT WRITE ON DATABASE demo TO writer;
```

### 2. 创建 Measurement

```sql
CREATE MEASUREMENT cpu (
    host      TAG,
    region    TAG,
    usage     FIELD FLOAT,
    cores     FIELD INT,
    throttled FIELD BOOL,
    label     FIELD STRING
);
```

### 3. 写入数据

```sql
-- 单条写入
INSERT INTO cpu (time, host, region, usage, cores, throttled, label)
VALUES (1713657600000, 'server-01', 'cn-hz', 0.42, 8, FALSE, 'normal');

-- 批量写入
INSERT INTO cpu (time, host, region, usage, cores, throttled, label) VALUES
    (1713657600000, 'server-01', 'cn-hz', 0.42, 8, FALSE, 'normal'),
    (1713657660000, 'server-01', 'cn-hz', 0.55, 8, FALSE, 'normal');
```

### 4. 原始数据查询

```sql
-- 查询特定序列的原始数据
SELECT time, host, usage
FROM cpu
WHERE host = 'server-01'
  AND time >= 1713657600000
  AND time < 1713658200000;

-- 带标量函数
SELECT round(abs(usage - 0.5), 2) AS deviation
FROM cpu
WHERE host = 'server-01';
```

### 5. 单表别名

```sql
SELECT c.time, c.host, c."usage"
FROM cpu AS c
WHERE c.host = 'server-01'
ORDER BY c.time ASC
LIMIT 10;
```

SonnetDB 支持单个 measurement 的别名限定列名，例如 `c.time` 或 `c."usage"`；从 MM4 起，也支持一个 measurement 与一个关系维表的 inner 等值 JOIN。

### 6. 分页查询

```sql
-- 传统风格
SELECT time, host, usage
FROM cpu WHERE host = 'server-01'
ORDER BY time ASC
LIMIT 5 OFFSET 0;

-- SQL 标准风格
SELECT time, host, usage
FROM cpu WHERE host = 'server-01'
ORDER BY time ASC
OFFSET 5 ROWS FETCH NEXT 5 ROWS ONLY;
```

### 7. 基础聚合

```sql
SELECT
    count(usage)  AS cnt,
    sum(usage)    AS total,
    min(usage)    AS min_usage,
    max(usage)    AS max_usage,
    avg(usage)    AS avg_usage,
    first(usage)  AS first_usage,
    last(usage)   AS last_usage
FROM cpu
WHERE host = 'server-01';
```

### 7. 统计聚合与分位数

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

### 8. 时间桶聚合

```sql
SELECT
    avg(usage)   AS avg_usage,
    max(usage)   AS max_usage,
    count(usage) AS cnt
FROM cpu
WHERE host = 'server-01'
GROUP BY time(2m);
```

### 9. 窗口分析

```sql
-- 差分与变化率
SELECT time, difference(usage) AS diff FROM cpu WHERE host = 'server-01';
SELECT time, derivative(usage) AS rate FROM cpu WHERE host = 'server-01';

-- 移动平均与累积求和
SELECT time, moving_average(usage, 3) AS ma3 FROM cpu WHERE host = 'server-01';
SELECT time, cumulative_sum(usage) AS cumsum FROM cpu WHERE host = 'server-01';
```

### 10. 向量检索

```sql
-- KNN 检索（余弦距离默认）
SELECT * FROM knn(documents, embedding, [0.10, 0.20, 0.30, 0.40], 3);

-- 混合检索（带 Tag 和时间过滤）
SELECT * FROM knn(documents, embedding, [0.10, 0.20, 0.30, 0.40], 5, 'cosine')
WHERE source = 'wiki'
  AND time >= 1713657600000;

-- 向量距离函数
SELECT cosine_distance(embedding, [0.10, 0.20, 0.30, 0.40]) AS cos_dist
FROM documents;
```

这份 Cookbook 覆盖了 SonnetDB 最常用的查询场景。每个模板都可以直接复制运行，是日常开发中的实用速查手册。
