---
title: '基础聚合函数：count/sum/min/max/avg/first/last'
categories: SonnetDB,数据库
draft: false
---

正式发行见 [4.0.0 Release](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。本文按 2026-10-07 当前 main 文档与源码修订旧稿；主线改动不自动代表全部 v4 发行物与客户端的已验收能力。以下 SQL/API 为复现示例，本次没有实际执行或性能测量。

基础聚合让应用把原始采样变成报表，但函数名相同，并不代表输入类型、空值和多序列行为完全相同。

## 从一个已声明的 measurement 开始

```sql
CREATE MEASUREMENT cpu (host TAG, usage FIELD FLOAT, status FIELD STRING);
INSERT INTO cpu (time, host, usage, status) VALUES
  (1700000000000, 'server-01', 20, 'idle'),
  (1700000001000, 'server-01', 40, 'busy');
SELECT count(*), count(usage), sum(usage), avg(usage),
       min(usage), max(usage), first(status), last(status)
FROM cpu
WHERE host = 'server-01'
  AND time >= 1700000000000 AND time < 1700000060000;
```

`count(*)` 统计行。一个 series 的多个 FIELD 在同一时间戳写入，计为一行；稀疏字段按所有 FIELD 时间戳并集确定行存在性。不同 series 在同一时刻仍是不同的行。`count(usage)` 只统计 usage 有值的时刻，因此两者可能不同。

## 类型与选择器

`sum/avg` 面向数值 FIELD；Boolean 沿用 false=0、true=1 的数值转换。`min/max` 也接受字符串，按 Ordinal 比较，而不是中文拼音排序。`first/last` 按时间选择首末值，保留字段类型，可用于状态、向量和地理点。

FIRST/LAST 仍有多 series 拒绝边界。本文用 host 等值过滤限定单一序列，实际 schema 有多个 TAG 时应同时限定它们；不要把任意多设备查询的首末状态解释为每台设备的结果。

## 时间桶与精确去重

```sql
SELECT time, count(usage), avg(usage), max(usage)
FROM cpu WHERE host = 'server-01'
GROUP BY time(1m);
```

显式投影 time 才返回桶起始时间。标准聚合的单字段 DISTINCT 可先去重，例如 `count(DISTINCT status)`；`count(DISTINCT *)` 不受支持。NULL、空输入和整数精度应按实际执行路径核对，不能把某个路径的行为推广到所有模型。

依据：[sql-reference.md](https://github.com/IoTSharp/SonnetDB/blob/main/docs/sql-reference.md)、[m45-measurement-tag-grouping.md](https://github.com/IoTSharp/SonnetDB/blob/main/docs/benchmarks/m45-measurement-tag-grouping.md)。
