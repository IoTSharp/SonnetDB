---
title: 深入理解 SonnetDB 数据模型：Measurement、Tag、Field 与 Time
categories: SonnetDB,时序,数据模型
draft: false
---

SonnetDB 当前有九种原生模型，measurement 是其中的时序模型。本篇保留时序数据建模主题，解释 measurement、TAG、FIELD、time 与 series；关系表、KV、JSON 文档、全文、向量、对象、SonnetMQ 和 Graph 仍使用各自的模型语义。

正式版本见 [4.0.0 Release](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。本文同时链接当前开发文档，名称绑定和 schema 策略等合同应与实际包/Server 版本核对，不能由最新 `main` 推断所有发行版本相同。

## Measurement：同类观测的 schema

```sql
CREATE MEASUREMENT cpu (
    host TAG,
    region TAG,
    usage FIELD FLOAT,
    cores FIELD INT,
    healthy FIELD BOOL
);
```

measurement 可以显式创建，也可以在受支持的摄取路径中通过受控 schema-on-write 建立。schema 至少需要一个 FIELD；TAG 只能是字符串。保留列 `time` 不在 CREATE 中声明。

## TAG：序列身份与过滤维度

TAG 可以表达主机、设备、区域等维度。同一 measurement 与相同 TAG 集合构成同一逻辑 series，TAG 顺序不同不应生成不同 series。

```text
cpu{host=server-01, region=cn-hz}
cpu{host=server-02, region=cn-hz}
```

这两条 series 共享 measurement schema，但 `host` 不同。TAG 值与字段值都是数据，SQL 标识符名称的大小写合同不会改变它们的比较语义。高频变化的大文本和采样数值通常应放在 FIELD，TAG 基数及索引成本仍需按实际负载评估。

## FIELD：有类型的观测值

| FIELD 类型 | 使用范围 |
| --- | --- |
| `FLOAT` | 浮点观测值 |
| `INT` | 有符号整数值 |
| `BOOL` | 布尔状态 |
| `STRING` | 字符串观测值 |
| `VECTOR(N)` | 固定维度浮点向量 |
| `GEOPOINT` | WGS84 纬度/经度 |

VECTOR 和 GEOPOINT 是 FIELD 类型，不能当作独立的“第四类列”。它们也不能直接外推成关系表支持的列类型。不同函数接受的 FIELD 类型不同，字符串与布尔字段并不因此适用于所有数学聚合。

## time：Unix 毫秒时间戳

```sql
INSERT INTO cpu (time, host, region, usage, cores, healthy)
VALUES (1713676800000, 'server-01', 'cn-hz', 0.71, 8, TRUE);

INSERT INTO cpu (time, host, region, usage, cores, healthy)
VALUES (1713676801000, 'server-01', 'cn-hz', 0.85, 8, TRUE);

SELECT time, host, usage
FROM cpu
WHERE host = 'server-01'
  AND time >= 1713676800000
  AND time < 1713676810000
ORDER BY time;
```

`time` 可以投影和过滤。INSERT 未提供 time 时按当前合同使用 UTC 毫秒时间；需要采样时间准确时应由数据源显式提供。稳定返回顺序用 ORDER BY 表达，不应将 measurement 的时间维度描述成关系表主键或任意 UPDATE/UPSERT 合同。

## 行与底层点并非一一等价

SQL 中一行可以携带多个 FIELD，底层按 series 与 FIELD 存储相应时序点。因此基准和容量估算应说明计数是行、series，还是字段值总数。

schema-on-write 可以补充列，但仍受策略和增长额度限制。当前文档说明 INT→FLOAT 可提升、FLOAT 接收整数时转换保存，其它不兼容类型漂移会拒绝；这不是无限制 schema-less。

## 名称与模型边界

当前名称合同保留创建拼写，普通标识符按 `OrdinalIgnoreCase` 引用，双引号名称按 `Ordinal` 精确匹配，并禁止新增仅大小写不同的同作用域对象。该规则不改变 JSON 属性键、KV key 或字符串数据。

Graph 仍为 Beta；Server 的 MQ 权限属于数据库，物理状态位于实例级 `.system/mq`。模型共享引擎，不表示共享全部 SQL 语法、跨模型事务或九模型原子恢复。

参考：[数据模型](https://github.com/IoTSharp/SonnetDB/blob/main/docs/data-model.md)、[SQL 参考](https://github.com/IoTSharp/SonnetDB/blob/main/docs/sql-reference.md)、[类型边界](https://github.com/IoTSharp/SonnetDB/blob/main/docs/relation-type-boundary.md)和[名称决策](https://github.com/IoTSharp/SonnetDB/blob/main/docs/design/sql-identifier-case.md)。本文示例依据文档核对，未在本次整理中重新执行 SQL。
