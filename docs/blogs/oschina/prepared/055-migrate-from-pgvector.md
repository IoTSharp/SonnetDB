---
title: '从 pgvector 迁移到 SonnetDB：运算符兼容性与 SQL 差异'
categories: SonnetDB,数据库
draft: false
---

正式发行见 [4.0.0 Release](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。本文按 2026-10-07 当前 main 文档与源码修订旧稿；主线改动不自动代表全部 v4 发行物与客户端的已验收能力。以下 SQL/API 为复现示例，本次没有实际执行或性能测量。

SonnetDB 支持部分 pgvector 风格距离运算符，但迁移仍需核对模型、DDL、查询和客户端协议。符号兼容不等于 PostgreSQL 全兼容。

## 运算符映射

| pgvector 风格符号 | SonnetDB 含义 |
|---|---|
| <=> | 余弦距离 |
| <-> | 欧氏距离 |
| <#> | 负点积 |

SonnetDB 向量字面量直接用 `[1,0,0]`，不要沿用 PostgreSQL 的字符串向量与 `::vector` 转换语法。KNN 可明确使用：

```sql
SELECT time, title, distance
FROM knn(documents, embedding, [1, 0, 0], 10, 'cosine');
```

## 类型不能直接照搬

当前 VECTOR(N) 是 measurement FIELD 类型，不支持关系表 CREATE TABLE、ALTER TABLE ADD COLUMN 或 ALTER COLUMN TYPE。可以用关系主数据配合 companion measurement，或用 Document Collection 保存通用文档向量；这需要显式建模，不能由 ORM 静默转换。

## 索引与查询重写

measurement 用 FIELD VECTOR(N) WITH INDEX hnsw(...) 声明段索引，Document 用 CREATE VECTOR INDEX 与向量 path。pgvector 的索引 operator class、参数名和 wire protocol 都需逐项替换。

迁移前保留精确查询结果，核对负点积排序、过滤后的 Top-K、维度、NULL、模型版本、更新删除和恢复。不要把旧系统的延迟数字移植到 SonnetDB，也不要用语法演示宣称既有应用可以零改动迁移。

依据：[vector-search.md](https://github.com/IoTSharp/SonnetDB/blob/main/docs/vector-search.md)、[sql-reference.md](https://github.com/IoTSharp/SonnetDB/blob/main/docs/sql-reference.md)。
