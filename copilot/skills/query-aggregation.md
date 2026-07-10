---
name: query-aggregation
description: 编写时间窗口聚合查询（GROUP BY time(...)）的最佳实践与常见反模式。
triggers:
  - aggregation
  - group by
  - 聚合
  - downsample
  - sum / avg / max / count / first / last / mode
requires_tools:
  - query_sql
  - describe_measurement
---

# 编写聚合查询

当用户要求“按 1 分钟统计平均温度”、“按小时聚合”、“downsample”、“group by time” 时，使用本技能。

## 核心模式

```sql
SELECT
  time AS bucket,
  avg(temperature) AS avg_temp,
  max(temperature) AS max_temp,
  count(*) AS samples
FROM cpu
WHERE host = 'server-01'
  AND time >= now() - 1h
GROUP BY time(1m);
```

## 步骤

1. 调用 `describe_measurement` 确认列名与数据类型。
2. 用 `query_sql` 先做一次小窗口的 sample 查询（例如 `LIMIT 5`）确认结果结构。
3. 在 SELECT 中显式给聚合列起别名（`AS avg_temp`），让客户端字段名稳定。
4. 始终带 `WHERE time >= ...` 做时间裁剪；对裸 `SELECT *` 务必在 `query_sql` 上加 `maxRows`。
5. `GROUP BY time(...)` 的桶大小要参考查询区间：>30 天用 1h；>1 天用 5m；最近 1 小时用 5s/10s。
6. 需要桶起始时间时显式投影 `time AS bucket`；系统不会自动添加该列。

## 按字段类型选择聚合

- Float64 / Int64：可使用全部数值聚合，以及 `first/last/mode/distinct_count`。
- Boolean：`first/last/min/max/mode/distinct_count` 保留布尔语义；数学聚合延续 `false=0`、`true=1` 的兼容行为。
- String：使用 `count/first/last/min/max/mode/distinct_count`；不要生成 `sum/avg/stddev/percentile`。字符串 `min/max/mode` 固定按 Ordinal 比较。
- Vector / GeoPoint：通用聚合只使用 `count/first/last`；Vector 中心使用 `centroid`，GeoPoint 轨迹使用 `trajectory_*`。

字符串遥测分桶示例：

```sql
SELECT time, first(status) AS first_status, last(status) AS last_status
FROM device_state
WHERE device_id = 'device-01' AND time >= now() - 1h
GROUP BY time(60s);
```

## 反模式

- 不带时间过滤的全表聚合（容易扫描整个 segment）。
- 期望 `GROUP BY host`、`GROUP BY device_id` 这类按 tag 聚合：当前版本只支持 `GROUP BY time(...)`。
- 在客户端用 `SUM` 替代数据库聚合：会跨网络传送原始点。
- 对字符串状态生成 `sum/avg/stddev/percentile`：这些函数需要数值字段。
