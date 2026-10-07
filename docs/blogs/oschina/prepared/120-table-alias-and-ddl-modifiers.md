---
title: 单表别名与 DDL 修饰符：写出更地道的 SQL
categories: SonnetDB,SQL,Schema
draft: false
---

# 单表别名与 DDL 修饰符：写出更地道的 SQL

别名和列修饰符让 SQL 更容易与客户端生成的语句配合，但“语法能够解析”和“约束被执行”必须分别说明。本文按当前主分支核查，并指出已发布 4.0.0 的别名边界；SQL 示例本次未执行。

## 单表别名在各引用处一致使用

```sql
CREATE MEASUREMENT alias_demo (host TAG, usage FIELD FLOAT);
INSERT INTO alias_demo (time, host, usage) VALUES
  (1000, 'h1', 0.5), (2000, 'h1', 0.7);

SELECT c.time, c.host, c.usage
FROM alias_demo AS c
WHERE c.host = 'h1' AND c.time >= 1000
ORDER BY c.time DESC LIMIT 2;
```

别名 `c` 可以用于投影、过滤和排序。引用未知别名会报错；显式声明别名后，应统一使用这个限定符。

当前主分支在没有显式别名时，也允许按数据源名称限定列。4.0 标签的 measurement 别名校验则要求限定列名对应 FROM 中声明的别名。因此上面的显式 `AS c` 方式更便于表达这两者共同的用法，不把新绑定规则归到旧版本。

当前 SQL 名称保留创建时的拼写：未加双引号按 `OrdinalIgnoreCase` 绑定，加双引号按 `Ordinal` 精确绑定。别名和限定符也遵循相应规则。同一命名空间不允许仅大小写不同的对象，双引号不能用来绕过创建冲突。旧 catalog 冲突需要明确检查和迁移。

## measurement 的 NULL 修饰符只是兼容语法

```sql
CREATE MEASUREMENT modifier_demo (
  device TAG,
  value FIELD FLOAT NOT NULL,
  note FIELD STRING NULL
);

INSERT INTO modifier_demo (time, device, value) VALUES (1000, 'd1', 1.0);
INSERT INTO modifier_demo (time, device, note) VALUES (2000, 'd1', 'value omitted');
SELECT time, device, value, note FROM modifier_demo ORDER BY time;
```

measurement 的 `NULL` / `NOT NULL` 当前保留在 AST 中，执行层不持久化为 catalog 约束，也不强制 NOT NULL。FIELD 采用稀疏语义，省略某字段后查询该位置得到 NULL。不能据此宣称数据库已校验这类非空约束。

measurement 的 `DEFAULT <expr>` 虽然 parser 接受，CREATE 执行会明确拒绝。显式 NULL 写入与省略稀疏 FIELD 也不是同一合同；不能通过默认值补齐把它当作普通关系表使用。

## 关系表的 NOT NULL 和 DEFAULT 有实际行为

```sql
CREATE TABLE constraints_demo (
  id INT,
  label STRING NOT NULL DEFAULT 'pending',
  PRIMARY KEY (id)
);
INSERT INTO constraints_demo (id) VALUES (1);
SELECT id, label FROM constraints_demo WHERE id = 1;
```

关系表会对省略的 `label` 应用默认表达式，这个样本的预期 label 是 `pending`。显式写入 NULL 不会触发默认值，而由 NOT NULL 约束拒绝。因此不能把 measurement 的兼容修饰符边界推广成“SonnetDB 所有 DDL 都不支持约束”。

`DEFAULT` 的支持位置、类型和表达式限制应查对应模型文档；PRIMARY KEY、关系约束和 measurement 的 TAG/FIELD 角色也不能相互替代。

## 对客户端生成的 SQL 做明确验证

接入 ORM 或迁移脚本时，应记录服务端版本、目标模型和实际执行结果，分别验证解析、建表、写入与重开后的行为。单表别名支持不等于任意 JOIN 合同，修饰符被解析也不等于跨模型约束一致。完整工作流验收能避免将语法兼容误判为业务语义兼容。

参考：[当前 SQL 参考](https://github.com/IoTSharp/SonnetDB/blob/main/docs/sql-reference.md)、[当前别名绑定](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Sql/Execution/SelectExecutor.cs)、[4.0 别名实现](https://github.com/IoTSharp/SonnetDB/blob/v4.0.0/src/SonnetDB.Core/Sql/Execution/SelectExecutor.cs)。
