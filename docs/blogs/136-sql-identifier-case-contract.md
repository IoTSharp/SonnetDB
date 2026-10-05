---
title: SonnetDB SQL 名称大小写合同：原名、双引号与安全迁移
categories: SonnetDB,SQL,数据库迁移
draft: false
---

数据库项目经常在表名、measurement 名称和列名上遇到大小写不一致。SonnetDB 现在把这件事写成统一的 SQL 名称绑定合同：创建时保存原始拼写；未加引号的名称按 `OrdinalIgnoreCase` 解析；双引号名称按 `Ordinal` 精确解析。

## 创建名与引用名

```sql
CREATE MEASUREMENT DeviceMetrics (
    DeviceID TAG,
    temperature FIELD FLOAT
);

SELECT DeviceID, temperature FROM devicemetrics;
SELECT "DeviceID", "temperature" FROM DeviceMetrics;
```

上面两个查询都可以命中创建的 measurement，因为未加引号的引用忽略大小写，而双引号引用精确匹配。`"deviceid"` 不会匹配 `DeviceID`。这种规则同样适用于关系表列、视图、物化视图、别名和 CTE 名称。

## 为什么要保留原始拼写

Catalog、`SHOW`、`DESCRIBE` 和结果列元数据应该展示用户真正创建的名字。若入口各自把名字转成小写，就会出现创建时一个名字、查询结果又是另一个名字的问题。Point、Line Protocol 等摄取入口也必须先解析到已有 schema 的原名，避免仅仅因为大小写变化就新增列或 series。

## 禁止仅大小写的重复对象

同一命名空间不能同时创建 `DeviceID` 和 `deviceid`，即使其中一个使用双引号。旧 catalog 如果已经存在这种冲突，普通引用必须报歧义；迁移工具可以使用双引号精确检查并显式处理。跨模型出现无法由引号消歧的精确同名时，必须在 DDL 中指明对象类型迁移，不能静默覆盖。

## 应用侧建议

1. 约定团队的创建拼写，并在 DDL 中固定使用。
2. 生成 SQL 时保存标识符是否加引号，不要在 ORM 或客户端里提前统一转小写。
3. 做 schema migration 前先用 `SHOW`/`DESCRIBE` 读取 catalog 原名。
4. 摄取端遇到角色或类型不兼容时让服务端拒绝，不要通过改大小写绕过校验。

该合同解决的是标识符解析，不改变字符串值、数据 collation、JSON 属性键或其他模型的数据键语义。详细规则见 [`docs/design/sql-identifier-case.md`](../design/sql-identifier-case.md) 和 `AGENTS.md` 的 GH-Issue #211 约束。
