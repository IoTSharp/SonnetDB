---
title: 查询元数据：SHOW 与 DESCRIBE 的使用
categories: SonnetDB,SQL,元数据
draft: false
---

在写查询前，先确认连接到了哪个数据库、有哪些对象、字段如何定义。SonnetDB 的 `SHOW` 和 `DESCRIBE` 提供这类元数据入口，适合交互检查与管理工具读取 schema。

## 区分 measurement 与关系表

在练习数据库建立两个不同模型的对象：

```sql
CREATE MEASUREMENT Metrics (host TAG, usage FIELD FLOAT);
CREATE TABLE Devices (id INT PRIMARY KEY, label STRING);

SHOW MEASUREMENTS;
SHOW TABLES;
```

当前合同中，`SHOW MEASUREMENTS` 列出时序 measurement；`SHOW TABLES` 列出关系表。两者不是别名。结果为单列 `name`，按名称字典序升序排列，并保留对象创建时保存的拼写。

逻辑视图与物化视图也有独立入口：

```sql
SHOW VIEWS;
SHOW MATERIALIZED VIEWS;
```

物化视图还会返回刷新状态、活动代际、行数和最近错误等信息，不能把它理解为普通对象名称列表。

## 检查列结构

```sql
DESCRIBE MEASUREMENT Metrics;
DESC Metrics;
DESCRIBE TABLE Devices;
```

measurement 描述包含 `column_name`、`column_type` 与 `data_type`，分别对应原始列名、TAG/FIELD 分类与保存的数据类型。普通数值、布尔和字符串类型有自己的元数据名称，向量及地理字段应按其实际 schema 解释。

关系表描述使用另一套列合同，包括 `column_name`、`data_type`、`is_nullable`、`is_primary_key`、`ordinal`、`column_default` 和 `is_auto_increment`。管理工具应根据对象类型读取结果，不应拿一套固定列去解释所有 `DESCRIBE` 输出。

关系表二级索引可以单独查看：

```sql
SHOW INDEXES ON Devices;
```

索引信息不应从 measurement 的列描述中自行推断。

## 名称、目标与权限

名称保留创建时拼写。未加引号的引用忽略大小写；双引号引用精确匹配。创建 `Metrics` 后，`metrics` 与 `"Metrics"` 可以引用它，`"metrics"` 不会匹配。

元数据属于当前连接或 API URL 指定的数据库。控制面的 `SHOW USERS`、`SHOW GRANTS` 和 `SHOW TOKENS` 还涉及不同的路由与权限范围，不应假设所有用户都能读取相同内容。`SHOW TOKENS` 不返回令牌秘密。

本文按当前仓库合同校对：[SQL 参考](https://github.com/IoTSharp/SonnetDB/blob/main/docs/sql-reference.md)、[4.0.0 正式发行](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。
