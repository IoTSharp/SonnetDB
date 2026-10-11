---
title: SQL Cookbook：常用查询模式速查
categories: SonnetDB,SQL,教程
draft: false
---

这份 Cookbook 把常用时序查询整理成十个可复用模式。先在独立练习数据库建立同一组数据，再按需要选择查询，避免复制到缺少字段或错误数据库的环境。

## 准备练习数据

客户端连接或 HTTP URL 应先选定练习数据库。若尚未创建数据库，由有权限的管理员在控制面执行 `CREATE DATABASE cookbook_demo`，然后把数据操作目标切换到 `cookbook_demo`。

```sql
CREATE MEASUREMENT cpu (
    host TAG,
    region TAG,
    usage FIELD FLOAT,
    bytes FIELD INT
);

INSERT INTO cpu (time, host, region, usage, bytes) VALUES
    (1700000000000, 'server-01', 'north', 42.5, 1024),
    (1700000001000, 'server-01', 'north', 61.2, 2048),
    (1700000002000, 'server-01', 'north', 85.0, 4096),
    (1700000003000, 'server-02', 'south', 30.0, 8192);
```

`time` 使用 Unix 毫秒。以下查询共享这份 schema 与种子数据。

## 1. 确认对象与字段

```sql
SHOW MEASUREMENTS;
DESCRIBE MEASUREMENT cpu;
```

`SHOW TABLES` 列出关系表，不能代替 measurement 列表。

## 2. 投影与列别名

```sql
SELECT time, host, usage AS usage_pct FROM cpu ORDER BY time;
```

固定投影适合稳定的客户端结果合同。

## 3. 标签与半开时间范围

```sql
SELECT time, usage FROM cpu
WHERE host = 'server-01'
  AND time >= 1700000000000
  AND time < 1700000003000
ORDER BY time;
```

左闭右开范围方便相邻窗口衔接。

## 4. 组合 FIELD 过滤

```sql
SELECT time, host, usage FROM cpu
WHERE region = 'north' AND usage >= 60
ORDER BY time;
```

当前 main 包含 FIELD 残余过滤路径；旧版本的查询范围需单独核对。条件书写顺序不保证执行顺序。

## 5. 偏移分页

```sql
SELECT time, usage FROM cpu
WHERE host = 'server-01'
ORDER BY time
LIMIT 2 OFFSET 1;
```

本示例单 series 的时间不并列。生产分页还需确定完整排序与跨请求一致性，`LIMIT` 不是全部资源开销的上限。

## 6. 范围聚合

```sql
SELECT count(usage) AS samples,
       avg(usage) AS average_usage,
       max(usage) AS peak_usage
FROM cpu
WHERE host = 'server-01';
```

聚合输入应由业务时间和对象范围明确限定。

## 7. 时间桶聚合

```sql
SELECT avg(usage) AS average_usage, max(usage) AS peak_usage
FROM cpu
WHERE host = 'server-01'
GROUP BY time(1s);
```

时间桶用于按固定间隔汇总，不会自动补齐所有缺失数据。

## 8. 单位转换与函数组合

```sql
SELECT time,
       bytes / 1024 AS kib,
       round(log(bytes, 2), 3) AS log2_bytes,
       round(abs(coalesce(usage, 0)), 2) AS displayed_usage
FROM cpu
ORDER BY time;
```

`log(x, base)` 的值在前、底在后。缺值处理放在数学函数输入处，默认值须有业务依据。

## 9. 有界删除练习

```sql
SELECT time, host, usage FROM cpu
WHERE host = 'server-01'
  AND time >= 1700000000000
  AND time < 1700000001000;

-- 仅在独立练习库确认目标后执行
DELETE FROM cpu
WHERE host = 'server-01'
  AND time >= 1700000000000
  AND time < 1700000001000;
```

预览不是事务快照。measurement 删除使用 Tombstone，后续 compaction 回收空间，不能当作可撤销操作。

## 10. 独立的向量检索样例

向量查询需要自己的字段与完整维度，不使用前面的 `cpu` schema：

```sql
CREATE MEASUREMENT documents (
    source TAG,
    title FIELD STRING,
    embedding FIELD VECTOR(4)
);

INSERT INTO documents (time, source, title, embedding) VALUES
    (1700000000000, 'demo', '样本A', [1, 0, 0, 0]),
    (1700000001000, 'demo', '样本B', [0.8, 0.6, 0, 0]);

SELECT * FROM knn(documents, embedding, [1, 0, 0, 0], 2, 'cosine');
```

这组向量是演示坐标；真实语义质量需要实际嵌入模型与检索集验证。索引声明、召回和延迟也应按具体版本测量。

本文按当前仓库文档与实现校对：[SQL 参考](https://github.com/IoTSharp/SonnetDB/blob/main/docs/sql-reference.md)、[向量搜索指南](https://github.com/IoTSharp/SonnetDB/blob/main/docs/vector-search.md)、[4.0.0 正式发行](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。
