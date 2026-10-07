---
title: 标识符引用：双引号的使用场景
categories: SonnetDB,SQL,标识符
draft: false
---

SQL 中，名称和字符串是两种不同的东西。SonnetDB 使用双引号引用标识符，单引号表示字符串数据。双引号还会改变名称的匹配方式：未引用名称忽略大小写，双引号名称要求精确拼写。

## 名称保留创建时拼写

```sql
CREATE MEASUREMENT "SensorReadings" (
    "DeviceID" TAG,
    "Value" FIELD FLOAT
);

INSERT INTO "SensorReadings" (time, "DeviceID", "Value") VALUES
    (1700000000000, 'sensor-01', 23.5);

SELECT time, deviceid, value FROM sensorreadings;
SELECT time, "DeviceID", "Value" FROM "SensorReadings";
```

这两个查询都引用创建时的对象和列。catalog、`SHOW`、`DESCRIBE` 与直接列引用的结果元数据使用保存的名称，不把 schema 自动折叠成小写或大写。

未加双引号的解析采用 `OrdinalIgnoreCase`，双引号采用 `Ordinal`。它们不依赖操作系统语言区域或数据 collation。

因此下面的精确引用不会匹配上面的名称：

```sql
-- 预期名称绑定失败：原名是DeviceID，大小写不相同
SELECT "deviceid" FROM "SensorReadings";
```

## 特殊字符与转义

名称需要空格、保留字或特殊字符时，应使用双引号，并在所有精确引用处保持一致。双引号内部的 `""` 表示一个字面的双引号，例如 `"sensor""name"` 表示名称 `sensor"name`。

内置 `time` 也有自己的规则：未加引号的 `time` 表示时序时间列；引用实际 schema 名称时，双引号保留原名绑定语义。设计 schema 时优先选择清晰名称，避免不必要的冲突。

## 双引号不允许制造大小写重复

同一命名空间内不能新建或重命名出仅大小写不同的名称，即使加了双引号也不允许。measurement、关系表、视图与物化视图共享 SQL 数据源命名空间，不能通过模型差异绕过该约束。

旧 catalog 若已有大小写冲突，普通引用应报歧义；可用双引号精确检查并按对象类型迁移，不能静默选择、合并或覆盖。如果跨模型名称完全相同，引号也无法消歧，需要指明对象类型的 DDL 迁移。

## 对数据值没有额外影响

限定符、别名、CTE 名和 DDL/DML 名称引用遵循同一绑定合同。Point、Line Protocol 等摄取入口也应解析到已有 schema 的拼写，避免仅因大小写变化新增列或 series。

这些规则不改变字符串值、JSON 属性键、KV 键或其它模型的数据键语义。`'sensor-01'` 是数据值；`"DeviceID"` 是 schema 名称。

本文按当前仓库名称合同校对：[SQL 参考](https://github.com/IoTSharp/SonnetDB/blob/main/docs/sql-reference.md)、[项目名称约束](https://github.com/IoTSharp/SonnetDB/blob/main/AGENTS.md)、[4.0.0 正式发行](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。
