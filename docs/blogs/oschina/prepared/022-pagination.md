---
title: SQL 分页查询：LIMIT/OFFSET 与 FETCH 语法
categories: SonnetDB,SQL,分页
draft: false
---

分页把较大的结果分成便于显示的批次。SonnetDB 支持 `LIMIT/OFFSET` 和 `OFFSET ... FETCH` 两种常用写法。分页规则之外，还必须考虑排序并列和请求之间的数据变化。

## 建立一个可复现的示例

```sql
CREATE MEASUREMENT events (host TAG, value FIELD INT);

INSERT INTO events (time, host, value) VALUES
    (1700000000000, 'server-01', 10),
    (1700000001000, 'server-01', 20),
    (1700000002000, 'server-01', 30),
    (1700000003000, 'server-01', 40),
    (1700000004000, 'server-01', 50);
```

示例只有一个 series，时间戳各不相同，便于观察分页。第一页返回前两条，第二页跳过两条再返回两条：

```sql
SELECT time, value FROM events
WHERE host = 'server-01'
ORDER BY time
LIMIT 2 OFFSET 0;

SELECT time, value FROM events
WHERE host = 'server-01'
ORDER BY time
LIMIT 2 OFFSET 2;
```

`OFFSET` 从零开始表示跳过的结果行数。页码从一开始时，常用计算是 `(page - 1) * pageSize`，应用还应验证页码、页大小和溢出。

## FETCH 形式

```sql
SELECT time, value FROM events
WHERE host = 'server-01'
ORDER BY time
OFFSET 2 ROWS FETCH NEXT 2 ROWS ONLY;
```

对应语法也接受 `FIRST`，以及 `ROW/ROWS`。当前合同还支持只写 `OFFSET`。分页应用在投影、聚合等结果形成之后，聚合查询的页数不能简单用原始点数推导。

## 排序和一致性

没有明确排序的分页不适合作为稳定列表合同。即使写了 `ORDER BY time`，多个 series 的同时间记录仍可能并列，需要足够的排序条件来确定顺序。

不同分页请求也不是天然共享一个数据库快照。期间新增或删除记录可能让偏移页重复或遗漏，业务应决定是否需要快照、固定时间边界或其它一致性策略。

深分页可能需要跳过大量输入。`LIMIT` 限制的是输出，不是所有扫描、排序和内存开销。对单 series、时间严格唯一的场景，可以考虑用上次时间戳作为下一页边界；若时间存在并列，单独使用 `time > lastTime` 会跳过未返回的同时间记录。

本文按当前仓库合同校对：[SQL 参考](https://github.com/IoTSharp/SonnetDB/blob/main/docs/sql-reference.md)、[4.0.0 正式发行](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。
