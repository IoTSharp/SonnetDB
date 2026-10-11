---
title: SQL 兼容性基础：SELECT 1 与 count(1) 支持
categories: SonnetDB,SQL,兼容性
draft: false
---

# SQL 兼容性基础：SELECT 1 与 count(1) 支持

探活工具常使用 `SELECT 1`，统计代码常使用 `count(1)`。这两种语法的价值在于清晰而稳定的语义，不只是让 parser 接受字符串。本文核查当前主分支，并对照已发布的 4.0.0 标签；相关常量表达式说明及行计数测试在该标签中已有记录，不将它们重新包装为未发布新功能。

文中 SQL 和预期结果用于教学，本次准备没有执行测试或查询。

## 没有 FROM 的常量查询

```sql
SELECT 1 AS ok;
SELECT 2 * 5 + 1 AS constant_value;
```

SonnetDB 支持无 FROM 的常量表达式查询。它适合确认连接和 SQL 执行入口可用，但一次成功响应不能证明持久数据恢复、写入持久性或业务权限全部正常。服务端就绪检查和真实读写验收应根据实际目标分别设计。

带 FROM 的常量投影含义不同：

```sql
CREATE MEASUREMENT count_demo (host TAG, usage FIELD FLOAT, other FIELD INT);
INSERT INTO count_demo (time, host, usage) VALUES
  (1000, 'h1', 1.0), (2000, 'h1', 2.0);
INSERT INTO count_demo (time, host, other) VALUES (3000, 'h1', 7);

SELECT 1 AS ok FROM count_demo WHERE host = 'h1' LIMIT 1;
```

这里的常量为匹配数据行投影。空 measurement 没有匹配行时返回零行，所以这不是无条件返回一行的探活合同。

## count(1) 计行，count(field) 计字段

继续使用上面的稀疏数据：

```sql
SELECT count(*) AS rows_star,
       count(1) AS rows_one,
       count(usage) AS usage_values
FROM count_demo WHERE host = 'h1';
```

按该样本的行计数合同，`count(*)` 与 `count(1)` 都应为 3，`count(usage)` 应为 2。第三个时间点只写入 `other`，仍构成一行，却没有 `usage` 值。这些是依据合同推导的预期值，不是本次实测记录。

measurement 的行由同一 series 内各 FIELD 时间戳的并集构成。同一时间点有两个 FIELD，不能计成两行；跨 series 则分别计入各自的行。仓库已有对应测试覆盖多字段、稀疏时间戳和跨 series 情况。

`count(1)` 是常见 SQL 兼容写法，不应被解释为计所有字段值。也不能普遍把 `count(usage)` 改写成 `count(*)`，因为稀疏字段会改变结果。

## 客户端检查要对准版本

连接工具可以把无 FROM 的 `SELECT 1` 用作基本 SQL 检查，再对实际数据源执行独立验证。发布包、开发分支和远程实例可能处于不同提交，应记录客户端与服务端版本，并用该版本对应文档解释结果。

参考：[当前 SQL 参考](https://github.com/IoTSharp/SonnetDB/blob/main/docs/sql-reference.md)、[当前行计数测试](https://github.com/IoTSharp/SonnetDB/blob/main/tests/SonnetDB.Core.Tests/Sql/SqlExecutorSelectTests.cs)、[4.0 标签行计数测试](https://github.com/IoTSharp/SonnetDB/blob/v4.0.0/tests/SonnetDB.Core.Tests/Sql/SqlExecutorSelectTests.cs)。
