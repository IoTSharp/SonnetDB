---
title: CREATE MEASUREMENT：定义您的时序数据结构
categories: SonnetDB,SQL,数据模型
draft: false
---

`CREATE MEASUREMENT` 定义 SonnetDB 的时序 schema。它与关系模型的 `CREATE TABLE` 有相似的列声明外观，但 TAG、保留时间列和稀疏 FIELD 使用自己的语义，不能直接套用关系表约束。

正式版本见 [4.0.0](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)；以下依据当前文档整理，具体支持与运行版本核对。

## 从 CPU 监控开始

```sql
CREATE MEASUREMENT IF NOT EXISTS CpuMetrics (
    Host TAG,
    usage FIELD FLOAT,
    cores FIELD INT,
    healthy FIELD BOOL
);

INSERT INTO CpuMetrics (time, Host, usage, cores, healthy)
VALUES (1713676800000, 'server-01', 0.71, 8, TRUE);

DESCRIBE CpuMetrics;
```

TAG 默认为字符串，至少声明一个 FIELD。`time` 是保留时间列，不在 CREATE 中声明。IF NOT EXISTS 在同名对象已存在时保留原 schema，不会用新列定义覆盖它。

## FIELD 类型和稀疏值

当前 FIELD 类型为 `FLOAT`、`INT`、`BOOL`、`STRING`、`VECTOR(N)` 和 `GEOPOINT`。FLOAT 使用浮点值，不能沿用旧稿“FLOAT 32 位、DOUBLE 64 位”的二分描述；当前字段命名和返回类型以类型合同为准。

不同时间点可以携带不同 FIELD 集合。缺少某个 FIELD 时，查询该列得到 NULL；写入缺值应省略相应列，而不是写入 `VALUES(NULL)` 或 `DEFAULT`。

DDL 中的 NULL/NOT NULL 当前属于兼容修饰符，不形成持久化并强制执行的 measurement 约束；DEFAULT 声明也不能据 parser 接受就宣称运行时支持。

## 向量和地理字段

```sql
CREATE MEASUREMENT fleet_tracking (
    vehicle_id TAG,
    speed FIELD FLOAT,
    location FIELD GEOPOINT,
    vibration FIELD VECTOR(3)
        WITH INDEX hnsw(m=16, ef=64, ef_construction=200, metric='cosine')
);
```

索引声明附着在向量 FIELD 上，VECTOR 与 GEOPOINT 不能照搬成关系表列。3 维仅为示例，实际向量长度必须等于声明维度，分量必须有限；向量模型和检索质量另行评估。

## 名称、演进和设计选择

当前 SQL 名称合同保留创建拼写。普通引用 `cpumetrics` 可匹配 `CpuMetrics`，双引号则要求精确拼写；同作用域不能新增仅大小写不同的对象。这个规则不改变 TAG 字符串值。

摄取路径可以按受控 schema-on-write 补列，但仍检查类型、角色与增长额度。显式 CREATE 能让团队在写入前固定设备身份和字段类型。高 TAG 基数、过宽 schema 与长期恢复都要按目标负载验证，不应给出无证据的通用阈值。

参考：[CREATE 与 INSERT 合同](https://github.com/IoTSharp/SonnetDB/blob/main/docs/sql-reference.md)、[数据模型](https://github.com/IoTSharp/SonnetDB/blob/main/docs/data-model.md)、[向量检索](https://github.com/IoTSharp/SonnetDB/blob/main/docs/vector-search.md)和[类型边界](https://github.com/IoTSharp/SonnetDB/blob/main/docs/relation-type-boundary.md)。本文未重新执行示例。
