---
title: 注释语法：SonnetDB SQL 支持的四种注释方式
categories: SonnetDB,SQL,语法
draft: false
---

给 SQL 脚本写注释，可以记录时间范围、字段含义和操作目的。SonnetDB 的词法分析器支持四种注释形式：`--`、`//`、`REM` 行注释，以及 `/* ... */` 块注释。它们各有适用边界。

## 两种常用行注释

```sql
-- 计算一个常量表达式
SELECT 2 + 3 AS value;

SELECT 10 / 2 AS quotient; // 本行余下内容是注释
```

`--` 和 `//` 都会跳过所在行的剩余内容，到换行或输入结束为止。为了让脚本易于迁移，通常优先选择常见的 `--`；`//` 是 SonnetDB 支持的另一种写法。

行注释不会自动终结 SQL 语句。需要分隔多条语句时，仍应写明确的分号。

## REM 的识别位置

```sql
REM 这是一条独立行注释
SELECT 1 AS first_value;

SELECT 2 AS second_value; REM 分号之后也可以写REM注释
```

`REM` 不区分大小写，但必须位于行首或语句终止符之后的合法位置，后面还必须是空白或输入结束。`remember` 这样的标识符不会因为以 `rem` 开头就成为注释。

不要假设 `REM` 可以像 `--` 一样放在任意表达式后面。例如在未结束的表达式中插入 `REM`，并不构成通用的行尾注释合同。

## 块注释

```sql
/*
  多行说明：
  本查询只演示投影中的算术表达式。
*/
SELECT 10 /* 基数 */ + 5 AS total;
```

块注释可以跨行，也可以出现在两个 token 之间。必须保留必要的空白边界，避免删去注释后把两个标识符意外连接起来。

当前词法实现到第一个 `*/` 就结束块注释，不支持嵌套块注释。未闭合的块注释会产生解析错误，不能把脚本余下部分当成成功执行。

## 字符串里的符号仍是数据

```sql
SELECT '--不是注释' AS text_value;
```

字符串和双引号标识符内部的注释符号按各自的引用规则读取。单引号里的文本不会因为包含 `--` 或 `/*` 而变成注释。

注释有助于审阅，但不会限制执行权限，也不能代替事务、参数验证或操作前的目标确认。发布示例应避免把令牌和密码写入注释。

本文按当前仓库词法实现校对：[SqlLexer](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Sql/SqlLexer.cs)、[SQL 参考](https://github.com/IoTSharp/SonnetDB/blob/main/docs/sql-reference.md)、[4.0.0 正式发行](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。
