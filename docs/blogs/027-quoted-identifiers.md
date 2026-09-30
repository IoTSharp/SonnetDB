## 标识符引用：双引号的使用场景

在数据库设计中，列名和表名通常使用字母和下划线的组合。然而在实际业务中，我们有时需要使用包含特殊字符、空格或保留字的标识符。SonnetDB 支持使用双引号（`"`）引用标识符，让你可以突破常规命名限制。

### 为什么需要引号标识符

大多数场景下，SonnetDB 的标识符（列名、表名）只支持字母、数字和下划线，且不能以数字开头。但在以下情况中，引号标识符显得尤为重要：

- 列名包含特殊字符（如 `%`、`#`、空格）
- 列名与 SQL 保留字冲突（如 `time`、`group`、`select`）
- 需要按声明时的拼写精确引用列名
- 需要兼容外部系统或旧数据源的命名规范

### 基本用法

使用双引号包裹标识符，SonnetDB 就会将其视为字面标识符而非关键字或普通列名：

```sql
-- 查询时引用包含特殊字符的列名
SELECT "host-name", "usage(%)", "cores#1"
FROM cpu
WHERE "host-name" = 'server-01';

-- 使用保留字作为列名
SELECT "select", "group", "from"
FROM my_table;
```

名称保留创建时的拼写：`CREATE TABLE Host` 的目录名称是 `Host`，普通引用 `HOST` 或 `host` 均可命中。双引号引用要求精确拼写，`"Host"` 能命中，`"host"` 不能命中。新建或重命名时禁止产生仅大小写不同的同作用域名称；旧目录中的此类冲突须用双引号精确检查并显式迁移。

### 实际应用场景

以下是一些在实际业务中可能会用到引号标识符的场景：

```sql
-- 场景 1：兼容外部系统的列名
CREATE MEASUREMENT sensor_data (
    device_id   TAG,
    "sensor#1"  FIELD FLOAT,
    "sensor#2"  FIELD FLOAT,
    "value-avg" FIELD FLOAT
);

-- 场景 2：列名包含空格
CREATE MEASUREMENT iot_log (
    device    TAG,
    "raw value" FIELD FLOAT,
    "status code" FIELD INT
);

-- 场景 3：查询时引用
SELECT time, "raw value", "status code"
FROM iot_log
WHERE device = 'sensor-01';
```

### 引号标识符与字符串字面量的区别

理解双引号在 SonnetDB 中的语义很重要：双引号用于引用标识符（列名、表名），而字符串字面量使用单引号（`'`）。两者不可混用：

```sql
-- 正确：双引号引用列名，单引号引用字符串
SELECT "usage(%)" AS usage_pct
FROM cpu
WHERE host = 'server-01';

-- 错误：列名不能用单引号
SELECT 'usage(%)' FROM cpu;  -- 会被视为字符串常量

-- 错误：字符串字面量不能用双引号
WHERE host = "server-01";   -- "server-01" 会被解析为标识符
```

### 使用建议

虽然双引号标识符提供了灵活性，但在 Schema 设计中建议慎用：

- **优先使用常规命名**：字母、数字和下划线的组合在大多数场景下就足够了
- **避免过度使用**：包含特殊字符的列名在应用层处理时可能带来额外的转义负担
- **只在必要时使用**：如数据迁移、兼容旧系统等场景

引号标识符允许复杂业务名称，并在引用时要求精确拼写；具体大小写合同见 [SQL 参考](../sql-reference.md#标识符大小写合同gh-issue-211)。
