---
layout: default
title: "SQL 参考"
description: "当前版本真实支持的数据面与控制面 SQL 语法、限制和示例。"
permalink: /sql-reference/
---

想直接复制完整场景化示例，可先看 [SQL Cookbook]({{ '/sql-cookbook/' | relative_url }})；本页更偏向能力边界与精确语法说明。

## 数据面 SQL

### 标量表达式与数值语义

`SELECT` 投影和关系表 `UPDATE ... SET` 支持由列、字面量、括号、标量函数及以下运算符组成的数值表达式：

```sql
SELECT 2 * 5 + 1 AS constant_value;
SELECT value + 1 AS next_value, (high - low) * 0.5 AS adjusted FROM readings;
UPDATE counters SET value = value + 1 WHERE id = 1;
```

- 支持二元 `+`、`-`、`*`、`/`、`%` 和一元 `+`、`-`；优先级为一元运算 > 乘除取模 > 加减，括号可显式改变顺序。
- 整数加、减、乘、取模保留 `Int64`；任一操作数为浮点时返回 `Float64`；除法始终返回 `Float64`，所以 `5 / 2` 返回 `2.5`。
- 任一算术操作数为 `NULL` 时结果为 `NULL`。除数或模数为 `0` 时抛出执行错误，不返回 `Infinity` / `NaN`。
- `+` 只做数值加法，不把字符串隐式转成数字，也不做字符串拼接。字符串连接使用 `concat(...)`；其中 `NULL` 参数按空字符串处理。
- 支持聚合结果外包算术或标量函数，例如 `count(*) + 1`、`round(avg(value) + 0.25, 2)`；关系表、measurement 和 document collection 查询遵循同一规则。
- 普通关系表和 measurement 投影还支持 searched `CASE WHEN`、比较、`AND` / `OR` / `NOT`、`IS [NOT] NULL` 及不含子查询的 `IN` / `NOT IN`，并按 SQL 三值逻辑保留 `UNKNOWN`；BOOL 列也可在 `UPDATE SET` 右值中直接接收这类谓词结果。
- 支持无 `FROM` 的常量表达式查询，例如 `SELECT 2 * 5 + 1`，便于探活和计算。
- 相同基础投影语义也适用于 JOIN、JSON 虚拟表、向量/混合搜索、`INFORMATION_SCHEMA` 以及内置 `forecast(...)` / `knn(...)` 表值函数。
- `a++`、`a--`、`a += 1`、`a -= 1` 不是 SQL 赋值语法，不支持；应写 `SET a = a + 1`。`SET a = +2` 合法，但含义只是把正数 `2` 赋给 `a`。

关系表单条 `UPDATE` 还遵循以下规则：

- 同一个 `SET` 中的所有右值都读取更新前的原行，因此 `SET a = b, b = a` 会正确交换两列。
- 表达式先对所有命中行求值并校验，再统一提交；任一行除零、溢出或类型不匹配时，整条语句不留下部分更新。
- 非显式事务中的单条 `UPDATE` 把候选扫描、表达式求值和提交放在同一个表管理锁内，并发执行 `SET value = value + 1` 不会丢更新。
- 通过 `Tsdb.Functions` 注册的用户标量函数属于任意应用回调，UPDATE 会在表管理锁外执行它，避免回调等待其他 SQL 线程时死锁；该分支需要并发冲突检测时应使用 `ROWVERSION`。
- `ROWVERSION` 由数据库自动维护，不能出现在 `SET` 左侧；需要乐观并发控制时在 `WHERE` 中携带旧版本值。

### Modbus 32 位寄存器解码

以下标量函数把两只已经由 Modbus 协议层解析为 `0..65535` 的 16 位寄存器，还原成一个 32 位值：

| 函数 | 返回类型 | 说明 |
| --- | --- | --- |
| `modbus_int32(first_register, second_register, byte_order)` | `Int64` | 按有符号 32 位二补码解码 |
| `modbus_uint32(first_register, second_register, byte_order)` | `Int64` | 按无符号 32 位解码；完整覆盖 `0..4294967295` |
| `modbus_float32(first_register, second_register, byte_order)` | `Float64` | 按 IEEE-754 binary32 解码，再提升为 SQL Float64 |

`byte_order` 不区分大小写，表示设备送来的四字节源布局：

| 源布局 | 第一个寄存器 | 第二个寄存器 | 还原动作 |
| --- | --- | --- | --- |
| `ABCD` | `AB` | `CD` | 保持原序 |
| `BADC` | `BA` | `DC` | 各 16 位字内交换字节 |
| `CDAB` | `CD` | `AB` | 交换两个 16 位字 |
| `DCBA` | `DC` | `BA` | 反转全部四字节 |

例如以下四个表达式都把源寄存器还原为 `0x12345678`，返回十进制 `305419896`：

```sql
SELECT modbus_uint32(4660, 22136, 'ABCD');
SELECT modbus_uint32(13330, 30806, 'BADC');
SELECT modbus_uint32(22136, 4660, 'CDAB');
SELECT modbus_uint32(30806, 13330, 'DCBA');
```

有符号与浮点示例：

```sql
SELECT modbus_int32(65535, 65534, 'ABCD'); -- -2
SELECT modbus_float32(16256, 0, 'ABCD');   -- 1.0
```

任一参数为 `NULL` 时结果为 `NULL`；寄存器越界、寄存器含小数、顺序名未知或参数类型错误时会抛出执行错误。

### Modbus TCP 内建映射、master/slave 与写治理

当前版本已经支持 `CREATE MODBUS SOURCE`、`CREATE MODBUS ENDPOINT`、列级 `FROM MODBUS` / `EXPOSE AS MODBUS`、表级 `USING MODBUS`，以及 `SHOW/DESCRIBE MODBUS` 本地元数据查询。DDL 会把连接定义和规范化地址持久化到独立的版本化 catalog，并在建表时校验四类地址空间、跨度、访问权限、字节序、缩放及 wire type。

Server 的 Modbus runtime 默认关闭；只有 `SonnetDBServer:Modbus:Enabled=true` 与对象自身 `ENABLED TRUE` 同时满足时，对应 worker 才会运行。TCP master 会批量轮询四类地址并把采样写入本地 LATEST/HISTORY 表；source timeout、取消、按 `RETRY` 指数退避、断线重连和指标均在后台执行。`INT QUALITY` 列公开 `GOOD/STALE/BAD/PARTIAL/NO_VALUE` 位，最终失败按 `KEEP_LAST/NULL/SKIP/MARK_BAD` 落表并更新稳定诊断。TCP slave 会按 endpoint 固定 `ROW KEY` 响应 `0x01`～`0x04` 读取，并接收 `0x05/0x06/0x0F/0x10` 写请求；`REJECT` 直接拒绝，默认 `STAGED` 只有 durable 入队后才返回协议成功，表值须经 `Write + Admin` 审批后按 `STAGE_ONLY/UPDATE_TABLE` 处理。普通 `SELECT` 始终只读取本地状态，不同步访问 PLC。Source 受限写提供 preview/confirm，`SHOW MODBUS WRITE AUDIT` 统一查询两条写路径的脱敏事件。创建 source、endpoint 或映射表需要当前数据库 Admin，`SHOW/DESCRIBE MODBUS` 与 Modbus 概览只需 Read；Endpoint 队列和审计需要 Admin。

完整语法、安全边界、地址归一化、类型表和示例见 [Modbus TCP 内建映射表合同]({{ site.docs_baseurl | default: '/help' }}/modbus-tcp/)。

### `CREATE TABLE`

定义关系表 schema。关系表 MVP 使用 KV-backed rowstore 存放在数据库目录的 `tables/` 下，不修改时序 `.SDBWAL` / `.SDBSEG` 格式。

```sql
CREATE TABLE devices (
    id INT AUTO_INCREMENT,
    site_id INT NULL,
    name STRING NOT NULL,
    enabled BOOL NOT NULL DEFAULT TRUE,
    retry_count INT DEFAULT 0,
    version INT ROWVERSION,
    installed_at DATETIME NULL,
    metadata JSON NULL,
    payload BLOB NULL,
    PRIMARY KEY (id),
    FOREIGN KEY (site_id) REFERENCES sites (id),
    CONSTRAINT ck_devices_name CHECK (name IN ('pump', 'fan', 'valve'))
)
```

规则：

- 当前必须声明 `PRIMARY KEY (...)`；主键列会强制为 `NOT NULL`。
- 支持类型：`INT`、`FLOAT`、`BOOL`、`STRING`、`DATETIME`、`BLOB`、`JSON`。
- `DATETIME` 可写 Unix 毫秒整数或 ISO-8601 字符串，查询时返回 UTC `DateTime`。
- `BLOB` 可写 base64 字符串；ADO.NET 参数可直接传 `byte[]`。
- `JSON` 当前按 UTF-8 字符串存储；可用 `json_value(json_col, '$.path')` 做 path 投影和过滤。
- 普通列可声明 `DEFAULT <expr>`；默认表达式支持字面量、常量算术和内置标量函数，不能引用列、参数、聚合或子查询。`ROWVERSION` 列不能声明默认值。
- `INSERT` 省略带默认值的列，或在 `VALUES` 对应位置写 `DEFAULT` 时，会在每一行写入时求值并应用目标列的默认表达式；显式写入 `NULL` 不会改用默认值。目标列没有声明显式默认值时，`DEFAULT` 按 SQL 常规语义产生隐式 `NULL`：可空列成功，非空列由现有约束拒绝。
- `INSERT INTO table DEFAULT VALUES` 会为每个非 `ROWVERSION` 列使用其默认值；没有显式默认值的列产生隐式 `NULL`。`UPDATE table SET column = DEFAULT WHERE ...` 会按每个命中行重新求值默认表达式，轻事务路径保持相同语义。
- `VALUES(DEFAULT)`、`DEFAULT VALUES` 和 `UPDATE SET ... = DEFAULT` 只适用于关系表；measurement 与文档集合会明确拒绝。
- 二级索引使用 `CREATE INDEX` 单独声明。
- `FOREIGN KEY (...) REFERENCES parent (...)` 第一版只支持表级声明，引用列必须等于被引用表 `PRIMARY KEY`；外键列任一为 `NULL` 时跳过校验。
- `CHECK (expression)` 支持命名或未命名表级约束；表达式可引用当前表列、字面量、基础运算、`IN`、`IS NULL`、`CASE` 和当前关系执行器支持的标量函数，不支持限定列名、参数、聚合或子查询。
- CHECK 按 SQL 三值逻辑执行：只有明确 `FALSE` 拒绝写入，`TRUE` 和由 `NULL` 传播得到的 `UNKNOWN` 均通过。
- `ROWVERSION` 只能声明在一个 `INT` 列上；`INSERT` 自动写入 `1`，`UPDATE` 自动递增，禁止通过 `SET` 显式赋值，可用 `WHERE id = ... AND version = ...` 获得乐观并发冲突检测。
- 每张表最多可有一个 `INT AUTO_INCREMENT` 列；兼容拼写 `AUTOINCREMENT` 和 `IDENTITY`。该列隐式为 `NOT NULL`，不能同时声明 `NULL`、`DEFAULT` 或 `ROWVERSION`。
- `INSERT` 省略自增列、写入 `NULL` 或 `DEFAULT` 时，从 `1` 开始分配单调递增值；显式整数仍可写入，且高于当前高水位时会推进后续分配。自增属性本身不创建唯一约束，需要唯一性时仍应把该列加入 `PRIMARY KEY` 或唯一索引。
- 自增列由数据库维护，不允许通过 `UPDATE SET` 显式修改；底层 `TableStore` 批量 mutation 若写入更大的显式值，仍会推进高水位以保护后续分配。
- 自增高水位持久化在表的 KV/WAL 中，并在并发写入前预留；约束失败、触发器失败或事务回滚可能留下间隙，已分配值不会复用。`DELETE` 不重置序列，`TRUNCATE TABLE` 切换 generation 后从 `1` 重新开始；超过 `INT` 的 `Int64` 上限会明确报错。
- 普通 `INSERT` 在提交阶段的表管理锁内分配自增值；事务或触发器为了向 `NEW` 行提前暴露生成值而进行的预留会记录当时的 generation。若并发 `TRUNCATE TABLE` 已切换 generation，陈旧事务会在提交前明确失败，不会把重置前的预留值写入新 generation。
- 关系表 `INSERT` 支持 `RETURNING column [, ...]` 和 `RETURNING *`；返回行使用完成默认值、`ROWVERSION` 与 `AUTO_INCREMENT` 生成后的最终值，多行结果保持 `VALUES` 的插入顺序。首版只允许列名或 `*`，不支持表达式和别名；measurement 与文档集合会明确拒绝 `RETURNING`。
- ADO.NET 可用 `ExecuteScalar("INSERT ... RETURNING id")` 取得本条语句生成的首个 ID，作为语句级 last-insert-id；`ExecuteReader` 可读取完整返回行，同时 `RecordsAffected` 保留实际插入行数。SonnetDB 不维护连接级 `LAST_INSERT_ID()` 状态。
- EF Core provider 会把常规 `int` / `long` `ValueGenerated.OnAdd` 列建为 `INT AUTO_INCREMENT`，INSERT 不发送临时跟踪键，并通过 `RETURNING` 把数据库生成值回填实体；`ValueGeneratedNever()` 仍按显式客户端键处理。

关系查询可在投影和谓词中使用以下日期标量函数：

```sql
SELECT DATE_ONLY(installed_at),
       DATE_PART('year', installed_at),
       DATE_ADD(installed_at, 7, 'day'),
       TO_UNIX_MILLISECONDS(installed_at)
FROM devices
WHERE installed_at <= CURRENT_UTC_DATETIME();
```

- `CURRENT_DATETIME()` / `CURRENT_UTC_DATETIME()` 返回服务器本地时间 / UTC 时间；`CURRENT_DATETIME_OFFSET()` / `CURRENT_UTC_DATETIME_OFFSET()` 返回对应的 `DateTimeOffset`。
- `DATE_ONLY(value)` 返回日期零点。
- `DATE_PART(part, value)` 支持 `year`、`quarter`、`month`、`day`、`day_of_year`、`day_of_week`、`hour`、`minute`、`second`、`millisecond`、`microsecond`、`nanosecond`；`day_of_week` 与 .NET 一致，星期日为 `0`。
- `DATE_ADD(value, amount, part)` 支持 `year`、`month`、`day`、`hour`、`minute`、`second`、`millisecond`、`microsecond`、`tick`；`year`、`month`、`tick` 要求整数增量。
- `TO_UNIX_MILLISECONDS(value)` / `TO_UNIX_SECONDS(value)` 返回 Unix 时间。
- 日期函数接受 `DATETIME` 或 Unix 毫秒；输入为 `NULL` 时结果为 `NULL`。时序分桶仍使用 `GROUP BY time(1m)` 等语法，不使用 `DATE_TRUNC`。

### `ALTER TABLE`

关系表支持新增、修改、删除和重命名列，以及重命名表：

```sql
ALTER TABLE devices ADD COLUMN region STRING NOT NULL DEFAULT 'north';
ALTER TABLE devices ALTER COLUMN retry_count TYPE FLOAT;
ALTER TABLE devices ALTER COLUMN region SET DATA TYPE STRING;
ALTER TABLE devices ALTER COLUMN region SET NOT NULL;
ALTER TABLE devices ALTER COLUMN region DROP NOT NULL;
ALTER TABLE devices ALTER COLUMN region SET DEFAULT 'east';
ALTER TABLE devices ALTER COLUMN region DROP DEFAULT;
ALTER TABLE devices RENAME COLUMN region TO site;
ALTER TABLE devices DROP COLUMN site;
ALTER TABLE devices RENAME TO managed_devices;
```

`ALTER COLUMN` 的完整语法为：

```sql
ALTER TABLE table_name ALTER [COLUMN] column_name
    [TYPE data_type | SET DATA TYPE data_type | data_type]
    [NULL | NOT NULL | SET NOT NULL | DROP NOT NULL]
    [SET DEFAULT expression | DROP DEFAULT];
```

- 每条语句至少要指定一种变更；类型、空值约束和默认值动作各自最多出现一次，可以组合在同一条语句中。
- `TYPE FLOAT` 与 `SET DATA TYPE FLOAT` 是两种等价的类型变更写法；省略 `TYPE` 的 `FLOAT` 形式用于 SQL Server 风格组合，例如 `ALTER TABLE devices ALTER COLUMN retry_count FLOAT NOT NULL`。
- 类型变更会转换全部存量非空值并重建派生索引。当前不支持 `USING expression` 自定义转换；任一值无法转换，或转换后违反唯一索引、CHECK、NOT NULL 等约束时，在进程内整条 DDL 恢复原 schema、数据和索引。行 payload 与 catalog 发布之间尚无迁移 journal，因此不将该恢复边界解释为进程终止或掉电原子性。
- 真正改变物理类型的迁移当前使用单个 KV 原子 batch，受 `KvOptions.MaxOverlayEntries` 和 `KvOptions.MaxWalBytes` 限制（默认分别为 100,000 条 mutation 和 256 MiB WAL）。超出预算时会在写 WAL 前拒绝；应先提高预算，或通过新表分批迁移。`SET/DROP DEFAULT`、`DROP NOT NULL` 和列重命名只发布元数据，不受该 batch 行数限制。
- `SET NOT NULL` 会分页扫描并验证全部存量行，但不会重写 row payload；`DROP NOT NULL` 与直接写 `NULL` 等价，`NOT NULL` 与 `SET NOT NULL` 等价。
- `SET DEFAULT` 只影响之后省略该列的 `INSERT`，不会回填存量行；`DROP DEFAULT` 只移除后续插入的默认行为。目标类型或空值约束发生变化时，保留或新设的默认表达式也会按新定义重新校验。
- PRIMARY KEY 列不能修改类型或改为可空；ROWVERSION 列不能修改类型、空值约束或默认值；参与外键的引用列或被引用列不能修改类型，必须先删除相应外键约束。
- 被逻辑视图、物化视图、存储过程或触发器依赖的表仍受既有依赖保护，必须先移除依赖对象再修改 schema。
- `ADD COLUMN ... NOT NULL` 当前即使目标表为空也要求 `DEFAULT`；有入站外键引用的父表不能 `RENAME TABLE` 或 `DROP TABLE`，必须先删除子表外键。

已有表还可追加或删除外键和检查约束。追加约束前会扫描存量行；任一行违反约束时 DDL 失败且 catalog 保持原状。

```sql
ALTER TABLE devices
ADD CONSTRAINT fk_devices_site FOREIGN KEY (site_id) REFERENCES sites (id);

ALTER TABLE devices
ADD CONSTRAINT ck_devices_name CHECK (name IN ('pump', 'fan', 'valve'));

ALTER TABLE devices DROP CONSTRAINT fk_devices_site;
ALTER TABLE devices DROP CONSTRAINT ck_devices_name;
```

### 逻辑视图

逻辑视图保存一条 SELECT 定义，不保存查询结果。读取视图时，SonnetDB 将定义展开为派生表并复用现有查询执行器，因此视图可以基于关系表、measurement、document collection、其他视图以及当前已经支持的 JOIN、UNION 和子查询。

```sql
CREATE VIEW active_devices AS
SELECT id, name, site_id
FROM devices
WHERE enabled = TRUE;

CREATE VIEW IF NOT EXISTS north_devices AS
SELECT d.id, d.name
FROM active_devices d
JOIN sites s ON d.site_id = s.id
WHERE s.name = 'north';

SELECT name FROM north_devices ORDER BY name;
```

管理语法：

```sql
SHOW VIEWS;
DESCRIBE VIEW active_devices;
DROP VIEW active_devices;
DROP VIEW IF EXISTS active_devices;
```

`SHOW VIEWS` 返回 `name`、`created_utc`。`DESCRIBE VIEW` 返回单行 `name`、`definition`、逗号分隔的直接 `dependencies` 和 `created_utc`。`information_schema.tables` 以 `table_type = 'VIEW'` 列出视图，`information_schema.views` 提供 `table_schema`、`table_name`、`view_definition`、`created_utc`。

当前行为与限制：

- 定义以独立的 `views/views.sdbview` 目录文件持久化，重启后重新解析；不修改表、measurement、document 或 Segment 的现有二进制格式。
- 视图采用读取时展开和晚绑定数据语义，基础数据变更会立即反映到后续查询；视图本身不能作为 `INSERT`、`UPDATE` 或 `DELETE` 目标。
- 持久化定义不能包含 `?`、`@name` 或 `:name` 参数占位符；调用方参数只能用于查询视图的外层 SELECT。
- 创建时会拒绝不存在的数据源、与基础对象重名的视图和自引用；运行时还会拒绝直接或间接循环，并限制最多 32 层展开。
- 被视图引用的基础对象不能执行 `DROP` 或 schema `ALTER`；被其他视图引用的视图也不能删除。首版不支持 `CASCADE`、`OR REPLACE` 或跨数据库依赖，应先按依赖顺序显式删除视图。
- `EXPLAIN SELECT ... FROM view` 将访问路径标记为 `view_expansion`；当前不会把展开后各基础扫描的估算值汇总到视图层。

### 物化视图

物化视图保存 SELECT 定义和最近一次成功刷新的物理结果。创建只登记定义，不隐式执行查询；首次读取前必须显式刷新：

```sql
CREATE MATERIALIZED VIEW active_device_cache AS
SELECT id, name, site_id
FROM active_devices;

REFRESH MATERIALIZED VIEW active_device_cache;
SELECT name FROM active_device_cache ORDER BY id;
```

基础数据后续变化不会自动进入已发布快照。再次执行 `REFRESH MATERIALIZED VIEW` 会全量计算一个新代际；新代际完整写入并落盘后才原子切换读指针。刷新期间读者继续读取旧代际，刷新失败也保留旧代际，并把状态改为 `failed`、记录错误。尚无成功代际时读取会明确提示先刷新。

管理语法：

```sql
CREATE MATERIALIZED VIEW IF NOT EXISTS active_device_cache AS SELECT * FROM active_devices;
SHOW MATERIALIZED VIEWS;
DESCRIBE MATERIALIZED VIEW active_device_cache;
DROP MATERIALIZED VIEW active_device_cache;
DROP MATERIALIZED VIEW IF EXISTS active_device_cache;
```

`SHOW MATERIALIZED VIEWS` 返回 `name`、`status`、`definition_version`、`active_generation`、`row_count`、最近成功刷新的 `refreshed_utc` 和 `error`。`DESCRIBE MATERIALIZED VIEW` 另返回 SELECT `definition`、直接 `dependencies`、`created_utc`、最近尝试结束时间 `last_refresh_utc` 与 `last_successful_refresh_utc`。状态为 `uninitialized`、`refreshing`、`ready` 或 `failed`。

`information_schema.tables` 以 `table_type = 'MATERIALIZED VIEW'` 列出物化视图；`information_schema.materialized_views` 提供相同定义和刷新元数据。`EXPLAIN SELECT ... FROM materialized_view` 使用 `materialized_view_snapshot` 访问路径并按活动代际行数估算扫描。

当前行为与限制：

- 定义目录和物理结果位于独立的 `materialized-views/`；目录和快照均有版本与 CRC，不修改已有表、measurement、document 或 Segment 格式。
- 物化快照是只读关系源，可参与外层过滤、投影、聚合、子查询和关系 JOIN；定义本身仍复用现有表、measurement、document、逻辑视图和当前 SELECT 执行路径。
- 定义不能包含参数占位符、未知数据源或自引用；物化视图与基础对象、逻辑视图共用名称空间和依赖删除/ALTER 保护。
- `REFRESH` 需要数据库写权限，不能在活动轻事务内执行，同一物化视图不允许并发刷新；`SHOW`、`DESCRIBE` 和读取只需要读权限并继续经过现有 Frame、MCP、Copilot 与审计入口。
- 首版只支持显式全量刷新。不支持增量刷新、定时调度、后台自动刷新、`OR REPLACE`、`CASCADE` 或跨数据库依赖。

### SQL 存储过程

存储过程首版只支持 `LANGUAGE SQL`、有序 IN 参数和静态 SQL body。参数类型为 `INT`、`FLOAT`、`BOOL`、`STRING`，body 通过 `@参数名` 引用参数；绑定发生在 AST 上，不执行文本替换。

```sql
CREATE PROCEDURE add_device (
    IN p_id INT,
    IN p_name STRING,
    IN p_enabled BOOL
)
LANGUAGE SQL AS BEGIN
    INSERT INTO devices (id, name, enabled)
    VALUES (@p_id, @p_name, @p_enabled);

    SELECT id, name, enabled
    FROM devices
    WHERE id = @p_id;
END;

CALL add_device(1, 'pump-01', TRUE);
```

管理语法：

```sql
SHOW PROCEDURES;
DESCRIBE PROCEDURE add_device;
DROP PROCEDURE add_device;
DROP PROCEDURE IF EXISTS add_device;
```

`SHOW PROCEDURES` 返回 `name`、`parameters`、`language`、传递计算后的 `requires_write` 和 `created_utc`。`DESCRIBE PROCEDURE` 返回 `name`、`parameters`、`language`、`body`、`object_dependencies`、`procedure_dependencies`、`requires_write`、`created_utc`。

执行合同：

- body 允许 `SELECT`、`INSERT`、`UPDATE`、`DELETE` 和 `CALL`；写目标必须是关系表，首版不允许过程写 measurement 或 document collection。SELECT 继续复用当前支持的数据源与查询执行器。
- 定义时解析全部语句并校验命名参数、数据对象和已存在的被调用过程。过程不能重载，不支持默认参数、OUT/INOUT、动态 SQL、DDL 或外部语言运行时。
- 多语句过程只向调用方返回 body 最后一条语句的结果；中间 SELECT 参与结果行数治理，但不形成多个远程结果集。
- 直接或传递包含写入的过程自动使用轻事务；失败时整次调用回滚。位于调用方已有事务中时使用保存点，只撤销该次失败调用新增的 mutation。
- 默认单次调用链最多执行 64 条 body 语句、嵌套 8 层、累计产生 10,000 行 SELECT 结果；拒绝直接或间接递归，并在语句边界检查取消。
- 写权限按完整调用图传递计算。只读凭据可调用只读过程，不能通过外层只读过程调用内层写过程提升权限；Frame SQL query 通道固定为只读，因此也只能调用只读过程。
- `CREATE/DROP PROCEDURE` 不能在活动轻事务中执行。基础对象 `DROP/ALTER` 和被调用过程 `DROP` 会在仍有依赖时返回 `routine_dependency`。

过程与触发器定义共同保存在数据库目录的 `routines/routines.sdbrtn`。该目录使用独立版本、little-endian 编码、CRC32、大小/数量上限和临时文件原子替换；备份恢复自动包含该目录，打开时拒绝损坏或未知版本。

### SQL 触发器

触发器首版只支持关系表 `AFTER INSERT`、`AFTER UPDATE`、`AFTER DELETE` 的 `FOR EACH ROW` 语义。每个定义只绑定一个事件，`OLD` / `NEW` 是只读行上下文。

```sql
CREATE TRIGGER audit_device_insert
AFTER INSERT ON devices
FOR EACH ROW
WHEN (NEW.enabled = TRUE)
LANGUAGE SQL AS BEGIN
    INSERT INTO device_audit (event_id, device_id, action, old_name, new_name)
    VALUES (NEW.id * 10 + 1, NEW.id, 'insert', NULL, NEW.name);
END;

CREATE TRIGGER audit_device_update
AFTER UPDATE ON devices
FOR EACH ROW
WHEN (OLD.name != NEW.name)
LANGUAGE SQL AS BEGIN
    INSERT INTO device_audit (event_id, device_id, action, old_name, new_name)
    VALUES (NEW.id * 10 + 2, NEW.id, 'update', OLD.name, NEW.name);
END;

CREATE TRIGGER audit_device_delete
AFTER DELETE ON devices
FOR EACH ROW
LANGUAGE SQL AS BEGIN
    INSERT INTO device_audit (event_id, device_id, action, old_name, new_name)
    VALUES (OLD.id * 10 + 3, OLD.id, 'delete', OLD.name, NULL);
END;
```

管理语法：

```sql
SHOW TRIGGERS;
SHOW TRIGGERS ON devices;
DESCRIBE TRIGGER audit_device_insert;
DROP TRIGGER audit_device_insert;
DROP TRIGGER IF EXISTS audit_device_insert;
```

`SHOW TRIGGERS` 返回 `name`、`table_name`、`event`、`when`、`created_utc`；`ON table` 只保留指定关系表。`DESCRIBE TRIGGER` 返回 `name`、`table_name`、`event`、`when`、`language`、`body`、`dependencies`、`created_utc`。

当前语义与限制：

- INSERT 事件只能引用 `NEW`，DELETE 事件只能引用 `OLD`，UPDATE 可同时引用两者。`WHEN` 中的列必须显式写为 `OLD.column` / `NEW.column`，不允许参数或子查询。
- body 只允许以关系表为目标的 `INSERT`、`UPDATE`、`DELETE`，不允许 SELECT、CALL、DDL、measurement/document 写入或外部副作用。
- 执行顺序固定为原 DML 行顺序，其次是触发器创建时间，最后是触发器名称。触发器链共享同一个语句数和嵌套深度预算；同一调用链中的触发器递归会被拒绝。
- 原 DML 与全部触发动作使用同一轻事务提交边界。任一 `WHEN` 求值、body 执行或最终约束提交失败都会撤销原行和触发动作；调用方已有事务中则回滚到本条 DML 的保存点。
- 目标表或 body 依赖的关系表仍被触发器引用时，`DROP/ALTER` 会被阻断。`CREATE/DROP TRIGGER` 不能在活动轻事务中执行。
- V1 不支持 BEFORE、FOR EACH STATEMENT、transition tables、启用/禁用、显式顺序子句、deferred/constraint trigger、多事件合并、Document 或 measurement 触发器。这些能力按 M39 的成本和恢复证据逐项准入。

嵌入式调用可通过 `RoutineManager.Diagnostics` 读取最近 256 条不含参数值/行内容的调用审计和累计指标。Server `/metrics` 按数据库公开 `sonnetdb_procedure_*` 与 `sonnetdb_trigger_*` 调用、失败和累计耗时指标。稳定错误码包括 `procedure_not_found`、`trigger_not_found`、`routine_invalid_arguments`、`routine_recursive_call`、`trigger_recursion`、`routine_depth_limit`、`routine_statement_limit`、`routine_result_row_limit`、`routine_cancelled`、`routine_forbidden`、`routine_dependency`、`trigger_context` 和 `routine_execution_failed`。

M39 #333 的证据入口位于 `tests/SonnetDB.Benchmarks`：`--m39-trigger-evidence` 会固定审计 outbox、
派生汇总和状态流转保护 journey，并以 1/100/10,000 行 INSERT、UPDATE、DELETE 记录无触发器、V1 行触发器和客户端候选
statement 参考的吞吐、关系表 WAL/rowstore 有符号差值、托管内存、分配与失败回滚成本。成功样本会核对源表和审计表
的精确内容，失败样本会逐行确认源值与审计 sentinel 均已恢复；候选路径仍只是显式事务参考实现，不增加 SQL 语法。
关系表使用独立 keyspace/WAL，进程终止可能在源表和触发
目标表之间留下 partial commit；此边界由 CrashTests 的真进程强杀与重启 replay 场景固定，不能
解释为跨 keyspace 掉电原子性或 exactly-once。

### `CREATE INDEX` / `DROP INDEX`

关系表支持普通二级索引和唯一索引。索引声明随 table schema 持久化，索引内容从 rowstore 派生，打开表或 schema 变更时可重建。

```sql
CREATE INDEX idx_devices_tenant ON devices (tenant);
CREATE UNIQUE INDEX ux_devices_serial ON devices (serial);
CREATE INDEX IF NOT EXISTS idx_devices_site ON devices (site, name);

DROP INDEX idx_devices_site ON devices;
```

当前行为：

- 索引名在单表内唯一。
- 索引列必须存在，可包含 1 个或多个列。
- 唯一索引会在 `INSERT` / `UPDATE` / 轻事务提交时校验现有数据和同批数据冲突。
- `SELECT` / `UPDATE` / `DELETE` 会使用联合索引从首列开始的最长连续等值前缀；索引未覆盖的条件继续对候选行执行完整 `WHERE` 过滤。
- 连续等值前缀后的下一列为 `INT` 或 `DATETIME` 时，`<`、`<=`、`>`、`>=` 可继续作为索引范围。例如索引 `(a, b, c, d)` 可服务 `a = ? AND b = ? AND c >= ? AND c < ?`，`d` 和其它条件作为残余过滤。
- 联合索引缺少首列等值或范围条件时不能从中间列开始使用；遇到第一个未绑定列后，后续列只作为残余条件。
- 索引内容不作为第二份权威数据保存；rowstore 是主数据，索引可重建。

### 关系表 DML

```sql
INSERT INTO devices (id, name, enabled)
VALUES (1, 'pump-01', TRUE), (2, 'fan-02', FALSE);

INSERT INTO devices (name, enabled)
VALUES ('valve-03', TRUE), ('motor-04', FALSE)
RETURNING id, version;

SELECT id, name
FROM devices
WHERE enabled = TRUE AND id > 1
ORDER BY id DESC
LIMIT 10;

UPDATE devices
SET name = 'pump-01b', retry_count = retry_count + 1
WHERE id = 1;

DELETE FROM devices
WHERE id = 2;

DELETE FROM acquisitions
WHERE guid IN (
    SELECT guid
    FROM acquisitions
    WHERE upload_time < @cutoff
    ORDER BY capture_time
    LIMIT 500
);
```

当前行为：

- `INSERT` 按主键插入；主键已存在时返回错误，不会静默覆盖。
- `INSERT ... RETURNING` 在同一语句中返回成功插入后的列值；`RETURNING *` 按表 schema 顺序返回全部列。未知列会在写入前报错，不留下部分数据。
- `UPDATE` 支持把列、字面量、算术和标量函数组合成右值表达式；当前不支持更新主键或显式更新 `ROWVERSION` 列。
- `SELECT` 支持 `*`、列投影、字面量投影、标量表达式投影，以及 `WHERE` 中的 `AND` / `OR` / `NOT` 和基础比较。
- 关系表 `JSON` 列支持 `json_value(metadata, '$.site')` 这类 path 表达式；对象或数组结果会以紧凑 JSON 字符串返回。
- `WHERE` 覆盖完整主键等值条件时会走主键读取；二级索引按最长连续左前缀选择，并可在首个未绑定的 `INT` / `DATETIME` 列继续做范围扫描；其它条件在候选行上过滤。
- `ORDER BY` 支持结果集中的任意列名；`LIMIT` / `OFFSET` / `FETCH` 语法与 measurement 查询一致。
- 关系表 `UPDATE` / `DELETE` 的 `WHERE` 支持非相关 `IN (SELECT ...)`；子查询必须只返回一列，可使用参数、`JOIN`、派生表、`UNION`、`ORDER BY` 和分页。结果会在修改目标行前物化，并保持 `IN` / `NOT IN` 对 `NULL` 和空集的三值逻辑。
- 写操作的 `IN` 子查询当前只接受普通关系表或 measurement 来源，不支持 document、vector/hybrid search、表值函数或引用待修改外层行的相关子查询。轻事务中如果目标表已有缓冲写，也会明确拒绝对该表执行带 `IN` 子查询的后续 `UPDATE` / `DELETE`，避免子查询看到不一致视图。

### 关系表轻事务

`SqlExecutor.ExecuteScript(...)` 和服务端 `/sql/batch` 支持关系表小批量 DML 轻事务：

```sql
BEGIN;
INSERT INTO devices (id, name, enabled) VALUES (3, 'valve-03', TRUE);
UPDATE devices SET enabled = FALSE WHERE id = 1;
DELETE FROM devices WHERE id = 2;
COMMIT;
```

也可以显式写 `BEGIN TRANSACTION`；`ROLLBACK` 会放弃当前轻事务中排队的变更。

当前边界：

- 轻事务支持同一数据库内多个关系表的 `INSERT` / `UPDATE` / `DELETE` 原子提交与回滚。
- 不支持嵌套事务、measurement / document 写入事务、DDL 事务、`IMPORT JSON` 或跨数据库事务。在事务上下文内执行 measurement（时序）`INSERT` / `DELETE`、文档集合写入、DDL 或文件导入会抛 `NotSupportedException`（这些操作不进入关系表事务缓冲，`ROLLBACK` 无法撤销，故显式拒绝而非静默执行造成"假回滚"）。
- `COMMIT` 前会校验 NOT NULL、主键、唯一索引、外键、CHECK 和 ROWVERSION 乐观并发列；任一失败时，不会留下已应用的 rowstore / index 变更。
- 同一轻事务内的后续 `UPDATE` 会读取并合并该事务已缓冲的同一行变更，因此连续两次 `SET value = value + 1` 会累计两次。跨事务并发仍是 ReadCommitted；需要检测排队后到提交前的覆盖冲突时，应为表声明 `ROWVERSION` 并在 `WHERE` 中携带旧版本。
- 同一轻事务内的 `INSERT` / `UPDATE` / `DELETE` 会按主键归并最终净变化，例如 `INSERT→DELETE` 不产生提交写入，`UPDATE→DELETE` 只提交删除，`DELETE→INSERT` 提交替换；每条多行 INSERT/DELETE 只有在全部行求值和校验成功后才写入事务缓冲。
- 稳定约束错误码：`table_unique_violation`、`table_foreign_key_violation`、`table_check_violation`、`table_concurrency_conflict`。
- 隔离级别边界：ADO.NET 仅接受默认 / `ReadCommitted` 轻事务；当前语义是单连接排队、提交时获取表管理器锁并一次性校验/应用，不提供 MVCC、可重复读、序列化隔离或跨进程长事务。

### 时序 JOIN 关系维表

MM4 第一版支持把 measurement 与一个关系表做内连接，用设备、资产、租户、站点等维表补充时序结果：

```sql
SELECT t.time, d.name, d.site, t.value
FROM temperature AS t
JOIN devices AS d ON t.device_id = d.id
WHERE d.tenant = 'tenant-1'
  AND t.time >= 1713676800000
ORDER BY t.time DESC
LIMIT 100;
```

当前行为：

- 支持 `JOIN` / `INNER JOIN`，语义为 inner join。
- JOIN 左侧必须是 measurement，右侧必须是关系表。
- `ON` 当前仅支持一个等值条件，且 measurement 侧连接键必须是 `TAG` 列；关系表侧可连接主键列或普通列。
- measurement 侧 `tag = '...'` 和 `time` 范围过滤会先下推到时序查询；关系表侧 `WHERE` 条件会先用主键、二级索引等值前缀或数值/时间范围取得候选行，再做完整过滤。
- 输出投影支持 `time`、measurement tag / field、table 列、字面量及标量算术/函数表达式；有歧义的列名必须使用 `alias.column`。
- `ORDER BY` 可引用 JOIN 结果中的 measurement 或 table 列，并在分页前执行。
- `EXPLAIN` 支持 JOIN 查询，`access_path` 会显示 measurement 与 table 双侧下推路径，例如 `measurement:tag_index;table:secondary_index;join:hash`。

当前限制：

- 不支持 `LEFT JOIN` / `RIGHT JOIN` / `FULL JOIN`。
- 不支持 measurement 与 measurement JOIN、table 与 table JOIN、多表 JOIN、子查询 JOIN。
- JOIN 查询暂不支持聚合、`GROUP BY` 或窗口函数。

### JSON 文档集合

MM5 第一版支持 JSON 文档集合作为一等数据模型。集合主数据存放在数据库目录的 `documents/` 下，使用 KV-backed 存储、独立 schema 文件和可重建 JSON path 索引；不修改时序 `.SDBWAL` / `.SDBSEG` 格式。

```sql
CREATE DOCUMENT COLLECTION device_docs;

INSERT INTO device_docs (id, document)
VALUES
  ('dev-1', '{"type":"pump","site":"north","metrics":{"temp":21.5}}'),
  ('dev-2', '{"type":"fan","site":"south","metrics":{"temp":18}}');

SELECT id,
       json_value(document, '$.type') AS type,
       json_value(document, '$.metrics.temp') AS temp
FROM device_docs
WHERE json_value(document, '$.site') = 'north';

UPDATE device_docs
SET document = '{"type":"pump","site":"north","metrics":{"temp":22}}'
WHERE id = 'dev-1';

DELETE FROM device_docs
WHERE id = 'dev-2';
```

元数据与索引：

```sql
SHOW DOCUMENT COLLECTIONS;
DESCRIBE DOCUMENT COLLECTION device_docs;

CREATE JSON INDEX idx_device_type ON device_docs ('$.type');
SHOW JSON INDEXES ON device_docs;
DROP JSON INDEX idx_device_type ON device_docs;
DROP DOCUMENT COLLECTION device_docs;
```

当前行为：

- 文档集合固定暴露 `id` 和 `document` / `json` 两个伪列；`SELECT *` 展开为 `id, document`。
- `INSERT` 需要提供 `id` 与 `document` 或 `json`，JSON 文本会用 `System.Text.Json` 校验并规范化为紧凑 JSON。
- SQL `UPDATE` 使用 `SET document = '<json>'` 做整体替换；局部更新操作符通过 Document HTTP API 或 `SndbDocumentClient.UpdateOneAsync/UpdateManyAsync` 调用。
- 单条 SQL `UPDATE` / `DELETE` 会先完成候选规划，再用一个 KV 原子 batch 提交；取消、校验失败或批次预算拒绝不会留下部分修改。该 batch 受 `KvOptions.MaxOverlayEntries` 和 `KvOptions.MaxWalBytes` 限制，超限时会在追加 WAL 前整体拒绝；引擎不会为绕过限制自动拆批，因为拆批会破坏语句原子性。
- `json_value(document, '$.path')` 支持 `$`、点属性、`$['property']` 和数组下标，例如 `$.metrics.temp`、`$['display-name']`、`$.tags[0]`。
- `CREATE JSON INDEX` 建立基础 path 等值索引；`WHERE json_value(document, '$.type') = 'pump'` 可走该索引，`EXPLAIN` 的 `access_path` 会显示 `json_path_index`。
- `id = '...'` 会走文档 ID 读取；其它条件走集合扫描后过滤。
- 不提供 MongoDB wire/Driver 兼容 API 或跨文档复杂事务；集合 validator 支持 SonnetDB 自有 required/type/range/enum/pattern 子集。

Document Store 也提供私有 JSON HTTP API 与 `SndbDocumentClient`，用于不想拼 SQL 的应用代码。端点统一位于 `/v1/db/{db}/documents/{collection}` 下，读操作需要数据库 `Read` 权限，写操作需要 `Write` 权限：

```http
POST   /v1/db/{db}/documents/{collection}
DELETE /v1/db/{db}/documents/{collection}
POST   /v1/db/{db}/documents/{collection}/insert-one
POST   /v1/db/{db}/documents/{collection}/insert-many
POST   /v1/db/{db}/documents/{collection}/find
POST   /v1/db/{db}/documents/{collection}/find-one
POST   /v1/db/{db}/documents/{collection}/update-one
POST   /v1/db/{db}/documents/{collection}/update-many
POST   /v1/db/{db}/documents/{collection}/update-preview
POST   /v1/db/{db}/documents/{collection}/delete-one
POST   /v1/db/{db}/documents/{collection}/delete-many
POST   /v1/db/{db}/documents/{collection}/count
POST   /v1/db/{db}/documents/{collection}/distinct
POST   /v1/db/{db}/documents/{collection}/aggregate
POST   /v1/db/{db}/documents/{collection}/indexes
DELETE /v1/db/{db}/documents/{collection}/indexes/{index}
POST   /v1/db/{db}/documents/{collection}/indexes/validate
POST   /v1/db/{db}/documents/{collection}/change-feed
```

```json
// insert-one
{ "id": "dev-1", "document": { "site": "north", "kind": "pump" } }

// find：支持 id/ids 快捷条件、filter AST、projection、sort、limit/skip
{ "id": "dev-1" }
{ "ids": ["dev-1", "dev-2"] }
{ "limit": 100, "skip": 0 }
{
  "filter": {
    "and": [
      { "path": "$.site", "op": "eq", "value": "north" },
      { "path": "$.score", "op": "gte", "value": 5 },
      { "path": "$.tags", "op": "contains", "value": "hot" }
    ]
  },
  "projection": [
    { "name": "_id", "path": "_id" },
    { "name": "temp", "path": "$.metrics.temp" }
  ],
  "sort": [{ "path": "$.score", "descending": true }],
  "limit": 20,
  "skip": 0
}

// update-one：整文档替换
{ "id": "dev-1", "document": { "site": "north", "kind": "pump", "status": "ok" } }

// update-preview / update-one / update-many：同一套局部更新执行器
{
  "filter": { "path": "$.site", "op": "eq", "value": "north" },
  "update": { "set": { "$.status": "active" }, "inc": { "$.revision": 1 } },
  "many": true,
  "limit": 20,
  "upsert": false
}

// compound + unique + sparse + partial index
{
  "name": "ux_site_serial",
  "paths": ["$.site", "$.serial"],
  "isUnique": true,
  "isSparse": true,
  "partialFilter": { "path": "$.active", "operator": "eq", "valueScalar": "true" }
}

// change feed：首请求从 now 或 beginning 开始，后续原样回传 resumeToken
{ "startAt": "now", "operations": ["insert", "update", "delete"], "limit": 100 }
{ "resumeToken": "<opaque token>", "operations": ["insert", "update", "delete"], "limit": 100 }

// distinct：按 JSON path 返回标量 distinct 值
{ "path": "$.site" }

// aggregate：SonnetDB-native JSON aggregation pipeline
{
  "pipeline": [
    { "$match": { "path": "$.score", "op": "gte", "value": 5 } },
    {
      "$group": {
        "keys": [{ "name": "site", "path": "$.site" }],
        "accumulators": [
          { "name": "count", "op": "count" },
          { "name": "total", "op": "sum", "path": "$.score" },
          { "name": "avgScore", "op": "avg", "path": "$.score" }
        ]
      }
    },
    { "$sort": [{ "path": "$.total", "descending": true }] }
  ]
}
```

`filter` 操作符支持 `eq/ne/gt/gte/lt/lte/in/nin/exists/contains` 与 `and/or/not` 组合；`path` 可写 `_id` / `id`、`document` / `json` 或 JSON path。`exists` 会区分 path 缺失与 JSON `null`：path 存在且值为 `null` 时仍视为存在。
`aggregate` 支持 `$match` / `$project` / `$group` / `$sort` / `$limit` / `$skip` / `$unwind` / `$count` / `$distinct` 等价阶段；`$group.accumulators[].op` 支持 `count`、`sum`、`avg`、`min`、`max`、`first`、`last`、`distinct`。SQL 侧也可以直接在 document collection 上使用 `GROUP BY json_value(document, '$.path')` 与 `count/sum/avg/min/max/first/last`。

find 支持 cursor 分页：首个请求传 `limit`（服务器最大 batch size 为 1000），响应中的 `continuationToken` 不为空时，把它原样放入下一次 find 请求即可继续读取；续页 token 绑定 collection、查询形状、只读快照版本和 15 分钟过期时间，不能与 `skip` 混用。写入导致集合版本变化后，旧 token 会被拒绝，需要重新发起首个 find 请求。

`update-preview` 使用与真实提交相同的服务端局部更新执行器，但不写 WAL、不维护索引、不推进 change feed；响应提供逐文档 `before/after/isUpsert/changed`。索引管理契约支持 compound、unique、sparse、partial 与 TTL 声明，`indexes/validate` 只读比对主文档与索引条目。Change feed 记录所有通过 Document store 写入点产生的 insert/update/delete，持久化于集合 KV/WAL，事件保留 7 天；resume token 绑定数据库、集合、操作过滤和 document ID 过滤，有效期 24 小时，超出保留窗口返回 `410 resume_token_expired`。大于 256 KiB 的单个前后镜像会标记 `payloadTruncated`，但事件元数据与续传序号仍保留。

当前 Document API 契约刻意不实现 MongoDB wire protocol / BSON command，也不承诺官方 MongoDB Driver 直连。OpenAPI 片段见 [document-api.yaml](openapi/document-api.yaml)。

### JSON 文件虚拟表与导入

MM5 第二批支持把本地 JSON 文件作为只读虚拟表查询，或导入到 document collection / 关系表。JSON 文件能力用于临时查询、迁移和批量导入；导入完成后的主数据仍由 SonnetDB 的 document collection 或 table 托管。

```sql
SELECT id,
       json_value(document, '$.site') AS site
FROM json_each('/data/devices.json', 'array', '$.id')
WHERE json_value(document, '$.enabled') = TRUE;

EXPLAIN SELECT id FROM json_each('/data/devices.ndjson', 'lines');
```

`json_each(...)` 和兼容别名 `json_table(...)` 暴露三列：

- `ordinal`：文件内从 0 开始的行号。
- `id`：默认读取 `$.id`；也可用第 3 个参数指定 ID path；缺失时使用 `ordinal`。
- `document`：规范化后的紧凑 JSON 文本。

导入语法：

```sql
CREATE DOCUMENT COLLECTION device_docs;
IMPORT JSON '/data/devices.ndjson'
INTO device_docs
FORMAT LINES
ID PATH '$.device.id';

CREATE TABLE devices (
  id INT,
  name STRING,
  metadata JSON,
  PRIMARY KEY (id)
);

IMPORT JSON '/data/devices.json'
INTO devices
FORMAT ARRAY;
```

当前行为：

- 格式支持 `AUTO`、`ARRAY` 和 `LINES`；`AUTO` 会识别顶层数组 / 单对象 / JSON Lines。
- 导入 document collection 时每条记录整体写入 `document`，ID 来自 `ID PATH`、默认 `$.id` 或 `ordinal`。
- 导入 table 时要求每条记录是对象，并按列名映射到表列；对象 / 数组可写入 `JSON` 列。缺失属性按关系 INSERT 的 `DEFAULT` 语义处理，显式 JSON `null` 仍写入 `NULL`。
- table 导入复用普通关系 INSERT 的单批提交和触发器路径，会统一校验 NOT NULL、主键、唯一索引、外键与 CHECK，并自动生成 ROWVERSION；JSON 中显式提供 ROWVERSION 属性会拒绝整批导入。任一记录或触发器失败时不会留下部分关系行。document collection 导入仍按记录执行 upsert，不承诺整文件原子性。
- JSON 文件虚拟表不维护索引；`EXPLAIN` 的 `access_path` 显示 `json_file_virtual_table`。

### 关系表 JSON path 索引

关系表 `JSON` 列也支持基础 path 等值索引：

```sql
CREATE TABLE devices (
  id INT,
  metadata JSON,
  PRIMARY KEY (id)
);

CREATE JSON INDEX idx_devices_site
ON devices (metadata, '$.site');

SELECT id
FROM devices
WHERE json_value(metadata, '$.site') = 'north';
```

当前行为：

- 关系表 JSON path 索引只能引用一个 `JSON` 列和一个 JSON path。
- 仅支持 `json_value(json_col, '$.path') = literal` 形式的等值下推；其它谓词仍会扫描过滤。
- path 缺失或结果为 `null` 的行不写入 path 索引。
- `SHOW INDEXES ON <table>` 的 `columns` 会显示为 `json_col->$.path`；`EXPLAIN` 的 `access_path` 会显示 `json_path_index`。

### 文档全文索引

MM6 第一批把 SonnetDB 内置全文引擎接入 JSON 文档集合，全文索引是从 document collection 主数据派生出的可重建索引。当前实现的索引目录由 SonnetDB 托管在 `documents/fulltext/` 下，主数据仍以文档集合为准。

```sql
CREATE FULLTEXT INDEX ft_logs_message
ON logs ('$.message')
USING unicode;

SHOW FULLTEXT INDEXES ON logs;
DROP FULLTEXT INDEX ft_logs_message ON logs;
```

字段和分词器：

- 字段可写 `document` / `json`，表示索引整份 JSON；也可写字符串 JSON path，例如 `'$.message'`、`'$.title'`。
- 支持分词器：`unicode`、`cjk`、`jieba`。不写 `USING` 时默认 `unicode`。`jieba` 使用 SonnetDB.Core 内置中等中文词库；外部词库加载、`.dat` 编译和索引重建要求见 [全文中文词库](fulltext-dictionaries.md)。
- 同一个全文索引可包含多个字段，搜索时可指定某个字段，或用 `*` 搜索该索引内全部字段。

查询示例：

```sql
SELECT id, bm25_score() AS score
FROM logs
WHERE match(ft_logs_message, '$.message', 'pump alarm', 20)
ORDER BY score DESC
LIMIT 20;

SELECT id
FROM logs
WHERE match(ft_logs_all, *, 'pump', 20);
```

当前行为：

- `match(index_name, field, query[, topK])` 必须作为 `WHERE` 中独立的 `AND` 谓词使用；当前一个查询只支持一个全文谓词。
- `topK` 省略时默认取 100；带分页时会按 `OFFSET + FETCH/LIMIT` 预取候选，再执行完整 WHERE、排序和分页。
- `bm25_score()` 只能在包含 `match(...)` 的文档集合查询中用于投影或排序，返回 SonnetDB 全文引擎的 BM25 相关性分数。
- `INSERT` / `UPDATE` / `DELETE` 会同步维护全文索引；索引目录缺失时会从 document collection 主数据重建。
- `EXPLAIN SELECT ... WHERE match(...)` 的 `access_path` 会显示 `fulltext_index`，`index_name` 会显示命中的全文索引名。

### 文档 Hybrid Search

MM8 第一批支持在 document collection 上用全文 BM25 与 JSON embedding 数组做融合排序。文档主数据仍归 document collection 管理；全文索引由 SonnetDB 内置全文引擎派生维护，JSON 向量字段按查询时计算距离。

```sql
SELECT id,
       bm25_score() AS text_score,
       vector_distance() AS distance,
       hybrid_score() AS score
FROM hybrid_search(
  source => logs,
  text_index => ft_logs_message,
  text_field => '$.message',
  text => 'pump alarm',
  vector_field => '$.embedding',
  vector => [1, 0, 0],
  k => 20,
  text_weight => 0.6,
  vector_weight => 0.4
)
WHERE site = 'north'
ORDER BY score DESC;
```

也可以在 measurement KNN + 知识文档融合结果上连接一个关系维表。Planner 会先把 `d.tenant = ...` 下推给关系表索引，再用命中的 `d.id` 收窄 measurement `measurement_join_tag` 的候选 series：

```sql
SELECT measurement.device_id AS device,
       d.site AS site,
       document_id,
       hybrid_score() AS score
FROM hybrid_search(
  source => incidents,
  documents => knowledge,
  vector_field => embedding,
  vector => [1, 0, 0],
  measurement_join_tag => device_id,
  document_join_path => '$.device_id',
  text => 'pump alarm'
)
JOIN devices d ON measurement.device_id = d.id
WHERE d.tenant = 'tenant-1'
  AND measurement.time >= 1713676800000
  AND category = 'fault'
ORDER BY score DESC;
```

当前行为：

- `source` 必须是 document collection；`text_index` 可省略但集合中必须只有一个全文索引。
- `text` 是全文查询文本，`vector` 是查询向量；`vector_field` 默认 `$.embedding`，目标 JSON 值必须是 number array。
- `text_field` 默认 `*`，可指定全文索引中的 JSON path 字段。
- `metric` 可选，支持 `'cosine'`、`'l2'`、`'inner_product'`；默认 `'cosine'`。
- `hybrid_score = text_weight * normalized_bm25 + vector_weight * vector_score`；不写权重时两者各占 0.5。
- 结果伪列支持 `bm25_score()`、`vector_distance()`、`vector_score()`、`hybrid_score()`，也可直接投影 `id`、`document/json` 和 JSON 顶层字段名。
- `WHERE` 支持对结果伪列或 JSON 顶层字段做基础比较，例如 `site = 'north'`；复杂文档过滤可用 `json_value(document, '$.path')`。
- `EXPLAIN` 的 `access_path` 会显示 `hybrid_search`，`index_name` 显示使用的全文索引。
- document collection 内融合只读取 document collection 主数据和派生全文索引，不会把文档主数据交给外部全文或向量数据库。

### 文档向量搜索

`vector_search(...)` 用于在 document collection 上执行纯向量检索，不要求全文索引或文本查询。它主要服务 `SonnetDB.Data.VectorData` adapter，也可直接在 SQL 中使用。

```sql
SELECT id,
       json_value(document, '$.title') AS title,
       vector_distance() AS distance,
       vector_score() AS score
FROM vector_search(
  source => logs,
  vector_field => '$.embedding',
  vector => [1, 0, 0],
  k => 20,
  metric => 'cosine'
)
WHERE site = 'north'
ORDER BY distance;
```

当前行为：

- `source` 必须是 document collection；`vector_search` 不把通用记录映射到 measurement。
- `vector_field` 默认 `$.embedding`，目标 JSON 值必须是 number array，并且维度必须与查询向量一致。
- `metric` 可选，支持 `'cosine'`、`'l2'`、`'inner_product'`；默认 `'cosine'`。
- 结果伪列支持 `vector_distance()`、`vector_score()`，也可投影 `id`、`document/json` 和 JSON 顶层字段名。
- `WHERE` 支持对结果伪列或 JSON 顶层字段做基础比较；复杂路径可用 `json_value(document, '$.path')`。
- `EXPLAIN` 的 `access_path` 会显示 `document_vector_scan`（全表暴力扫）或 `document_vector_index`（命中持久 ANN 索引），`index_name` 显示使用的 JSON vector path 或索引名。

#### 持久向量索引（HNSW ANN）

默认 `vector_search` 对整个 collection 做 `O(N·dim)` 暴力扫。为集合声明持久向量索引后，无 `WHERE`、按距离升序（默认或 `ORDER BY distance`）的查询会走 HNSW ANN，亚线性加速：

```sql
CREATE VECTOR INDEX idx_logs_embedding ON logs ('$.embedding')
  WITH (dimensions = 384, metric = 'cosine', m = 16, ef_construction = 200, ef_search = 64);

DROP VECTOR INDEX idx_logs_embedding ON logs;
```

- `dimensions` 必填，须与文档向量数组长度一致；`metric` 支持 `'cosine'`/`'l2'`/`'inner_product'`（默认 cosine）；`m`/`ef_construction`/`ef_search` 为可选 HNSW 参数（默认 16/200/64）。
- 索引在 `insert`/`update`/`delete` 时增量维护，随集合重开从主数据的持久化向量重建图（崩溃自愈）。缺少该 path、维度不匹配或坏向量字段的文档不进索引。
- **走索引的条件**：查询 `vector_field` 等于索引 path、`metric` 与维度均匹配、且**无 `WHERE`**、无自定义降序 `ORDER BY`。有 `WHERE` 时回落暴力扫——暴力扫先按谓词过滤再取 Top-K，ANN 先取 Top-K 会漏掉被过滤器排除但更近的行，语义不等价。

### Measurement KNN 与知识文档融合

MM8 第二批支持以 measurement 的 `VECTOR` 字段做 KNN 召回，再通过 measurement tag 与 document collection JSON path 关联知识条目，并可叠加知识文档全文 BM25 与可选知识向量评分：

```sql
SELECT measurement.device_id AS device,
       document_id,
       json_value(document, '$.title') AS title,
       measurement_distance() AS m_distance,
       bm25_score() AS text_score,
       hybrid_score() AS score
FROM hybrid_search(
  source => incidents,
  documents => knowledge,
  vector_field => embedding,
  vector => [1, 0, 0],
  k => 20,
  measurement_join_tag => device_id,
  document_join_path => '$.device_id',
  document_join_index => idx_knowledge_device,
  text_index => ft_knowledge_body,
  text_field => '$.body',
  text => 'pump alarm overheating',
  measurement_weight => 0.7,
  text_weight => 0.3
)
WHERE time >= 1713676800000 AND category = 'fault'
ORDER BY score DESC;
```

当前行为：

- `source` 是带 `VECTOR` 字段的 measurement；`documents` 是关联的 document collection。
- `vector_field` 默认 `embedding`，也可写 `measurement_vector_field`；`vector` 必须与该列维度一致。
- `measurement_join_tag` / `join_tag` 指定 measurement TAG，`document_join_path` 指定知识文档 JSON path；若有同 path 的 JSON index 或显式 `document_join_index`，关联会优先走索引。
- `text` 可选；提供时会用 `text_index` / `text_field` 读取知识文档全文 BM25。未提供 `text` 时仅做 measurement KNN + 关联文档融合。
- `document_vector_field` 可选；提供时会对知识文档中的 JSON number array 再计算一次向量分数。
- 结果伪列支持 `measurement_distance()`、`measurement_score()`、`bm25_score()`、`text_score()`、`document_vector_distance()`、`document_vector_score()` 和 `hybrid_score()`；`vector_distance()` / `vector_score()` 在该模式下兼容指向 measurement KNN 分数。
- `WHERE` 中 measurement `time` / tag 谓词会下推给 KNN；关系维表谓词会先走主键 / 二级索引候选行并收窄 measurement join tag；剩余谓词可过滤知识文档顶层字段、`json_value(document, '$.path')` 或融合分数。
- `EXPLAIN` 的 `access_path` 会显示 `hybrid_search_measurement_knn_documents`；带关系维表过滤时会追加 `relation_filter:<table_access_path>`。

### `CREATE MEASUREMENT`

定义 measurement schema：

```sql
CREATE MEASUREMENT IF NOT EXISTS cpu (
    host TAG,
    region TAG STRING,
    usage FIELD FLOAT NULL,
    count FIELD INT,
    ok FIELD BOOL,
    label FIELD STRING NOT NULL
)
```

规则：

- `TAG` 列默认为字符串，`TAG` 和 `TAG STRING` 等价。
- `FIELD` 列支持 `FLOAT`、`INT`、`BOOL`、`STRING`、`VECTOR(N)`、`GEOPOINT`。
- schema 中至少要有一个 `FIELD` 列。
- `time` 不属于 schema 定义的一部分。
- `IF NOT EXISTS` 提供并发安全的幂等创建；同名 measurement 已存在时直接成功并保留现有 schema，不使用本次列定义覆盖它。
- `NULL` / `NOT NULL` 可作为 DDL 兼容修饰符出现在列类型后；当前仅保留在 SQL AST 中，执行层不把它持久化为 catalog 约束，也不强制 `NOT NULL`。
- `DEFAULT <expr>` 目前会被 parser 接受，但执行 `CREATE MEASUREMENT` 时会返回明确的 `DEFAULT` 暂不支持错误。

稀疏字段语义：

- SonnetDB 的 field 是稀疏的：同一个 measurement 的不同时间点可以携带不同 field 集合。
- 如果某个时间点没有写入某个 field，查询该列时结果为 `NULL`；这表示“该时间点未记录该字段”，不是 schema 约束失败。
- measurement 不支持关系表 DML 的 `DEFAULT` 形式；`VALUES(DEFAULT)` 与 `DEFAULT VALUES` 会明确拒绝。表达缺值时请省略该 field，或在应用侧写入具体值；显式 `NULL` 也不是 field 的默认值。

### `INSERT INTO ... VALUES`

```sql
INSERT INTO cpu (time, host, region, usage, count, ok, label)
VALUES
    (1713676800000, 'server-01', 'cn-hz', 0.71, 10, TRUE, 'ok'),
    (1713676860000, 'server-01', 'cn-hz', 0.73, 11, TRUE, 'ok')
```

规则：

- `time` 是保留伪列，表示 Unix 毫秒时间戳。
- `time` 省略时会使用当前 UTC 毫秒时间。
- 每一行至少需要提供一个 `FIELD` 列值。
- `TAG` 列必须是字符串字面量。
- `FIELD FLOAT` 可以接受整数或浮点字面量。
- 目标 measurement 不存在时，`INSERT` 会按列值自动创建 schema；已有 measurement 缺失列时也会自动补齐。
- SQL `INSERT` 的未知字符串列会推断为 `TAG`，未知非字符串列会推断为 `FIELD`。
- 已有 `INT` 字段遇到浮点值时会提升为 `FLOAT`；已有 `FLOAT` 字段接收整数时会转换为浮点保存，不会降级为 `INT`。
- `NULL` 不能作为当前 `INSERT` 的显式列值；要表达某个 field 在该时间点缺失，请从列列表中省略它。

### 原始查询 `SELECT`

查询所有列：

```sql
SELECT * FROM cpu WHERE host = 'server-01'
```

显式投影：

```sql
SELECT time, host, usage
FROM cpu
WHERE host = 'server-01' AND time >= 1713676800000 AND time < 1713677400000
ORDER BY time ASC
```

标量函数与算术投影：

```sql
SELECT abs(-usage), round(usage / 3, 2), sqrt(count), log(count, 10), coalesce(label, 'n/a')
FROM cpu
WHERE host = 'server-01'

SELECT usage + 1 AS next_usage, 2 * 5 + 1 AS constant_value
FROM cpu
WHERE host = 'server-01'
```

单表别名与限定列名：

```sql
SELECT c.time, c.host, c."usage"
FROM cpu AS c
WHERE c.host = 'server-01'
ORDER BY c.time DESC
LIMIT 10
```

兼容常见探活查询的字面量投影：

```sql
SELECT 1 AS ok FROM cpu LIMIT 1
```

当前行为：

- `SELECT *` 会展开为 `time + 所有 tag 列 + 所有 field 列`。
- 支持字面量投影（如 `SELECT 1 ... LIMIT 1`），会按匹配到的时间轴返回常量列。
- 当某个时间点缺少某个 field 时，结果列会返回 `NULL`。
- 标量函数支持 `abs`、`round`、`sqrt`、`log`、`coalesce`、`concat`、`lower`、`upper`、`regexp_like`、`modbus_int32`、`modbus_uint32`、`modbus_float32` 及上述日期函数。
- 标量函数可嵌套并接收算术表达式参数；算术表达式也可直接作为顶层投影。
- 支持 `FROM measurement [AS] alias` 单表别名，以及 `alias.column` / `alias."Column"` 限定列名；执行前会校验限定符必须匹配当前别名。
- `coalesce(...)` 只会在当前结果行存在时参与求值；它不会额外扩展原始查询的时间轴。
- 结果按时间升序返回。

分页子句（兼容两种风格）：

```sql
-- SQL 标准风格
SELECT time, host, usage
FROM cpu
WHERE host = 'server-01'
ORDER BY time ASC
OFFSET 20 ROWS FETCH NEXT 10 ROWS ONLY;

-- MySQL/PostgreSQL 常见风格
SELECT time, host, usage
FROM cpu
WHERE host = 'server-01'
ORDER BY time DESC
LIMIT 10 OFFSET 20;
```

说明：

- 支持 `ORDER BY time [ASC|DESC]`，排序会在分页前应用；当前要求查询结果中包含 `time` 列。
- 支持 `OFFSET n`（仅跳过，不限制返回行数）。
- 支持 `FETCH FIRST|NEXT n ROW|ROWS ONLY`。
- 支持 `LIMIT n [OFFSET m]` 兼容语法。
- `ORDER BY/OFFSET/FETCH/LIMIT` 作用在最终结果集（投影/聚合之后）。

### 聚合查询

聚合函数按自身语义声明可接受的 FIELD 类型：

| 聚合函数 | 可接受类型 | 结果与规则 |
|---|---|---|
| `count(*)` / `count(field)` | 所有 FIELD 类型 | 返回行数或字段存在数（`long`）。 |
| `first(field)` / `last(field)` | Float64、Int64、Boolean、String、Vector、GeoPoint | 按时间戳选择首值/末值，保留原始结果类型。 |
| `min(field)` / `max(field)` | Float64、Int64、Boolean、String | 字符串固定使用 `StringComparison.Ordinal`；布尔按 `false < true`。 |
| `mode(field)` | Float64、Int64、Boolean、String | 出现次数相同则选择最小值；字符串仍按 Ordinal。 |
| `distinct_count(field)` | Float64、Int64、Boolean、String | 返回 HyperLogLog 基数估算（`long`）。 |
| `sum` / `avg` / `stddev` / `variance` / `spread` | Float64、Int64、Boolean | Boolean 延续历史数值转换规则（`false=0`、`true=1`）。 |
| `median` / `percentile` / `p50` / `p90` / `p95` / `p99` | Float64、Int64、Boolean | 数值分位统计。 |
| `histogram` / `tdigest_agg` / `pid` / `pid_estimate` | Float64、Int64、Boolean | 数学统计或控制聚合，不接受字符串。 |
| `centroid` | Vector | 返回各维均值向量。 |
| `trajectory_*` | GeoPoint | 返回轨迹距离、中心、边界框或速度统计。 |

选择器与分类聚合可以直接用于状态字符串和布尔遥测：

```sql
SELECT time,
       first(status) AS first_status,
       last(status) AS last_status,
       mode(status) AS common_status,
       distinct_count(status) AS status_kinds
FROM device_state
WHERE device_id = 'device-01'
  AND time >= 1713676800000
  AND time <= 1713680400000
GROUP BY time(60s)
```

`sum(status)`、`avg(status)` 等数学聚合仍会报错，并明确提示该函数需要数值字段。

示例：

```sql
SELECT sum(usage), avg(usage), min(usage), max(usage)
FROM cpu
WHERE host = 'server-01'
```

`count(*)` 与 SQL 兼容写法 `count(1)` 也受支持：

```sql
SELECT count(*) FROM cpu WHERE host = 'server-01'
SELECT count(1) FROM cpu WHERE host = 'server-01'
SELECT count(*) + 1 AS rows_with_sentinel FROM cpu WHERE host = 'server-01'
SELECT round(avg(usage) + 0.25, 2) AS adjusted_average FROM cpu WHERE host = 'server-01'
```

> `count(*)` 计的是**行/时刻**数：同一 series 下多个 field 列写在同一时间戳算作一行，不同时间戳（含只写了部分 field 的稀疏行）取并集去重。多 series 场景下不同 series 的同一时间戳属于不同的行。`count(field)` 则只计该 field 列有值的时刻数。

### `GROUP BY time(...)`

按时间桶聚合：

```sql
SELECT avg(usage) AS mean, count(usage)
FROM cpu
WHERE host = 'server-01'
GROUP BY time(1m)
```

当前限制和真实行为：

- 仅支持 `GROUP BY time(duration)`。
- 仅可用于聚合查询。
- 不支持 `GROUP BY host` 这类按列分组。
- 可在投影中显式写 `time`（或 `time AS bucket`）返回桶起始时间；不会自动添加该列。
- duration 例子：`1000ms`、`30s`、`1m`。

### `DELETE FROM ... WHERE ...`

```sql
DELETE FROM cpu
WHERE host = 'server-01' AND time >= 1713676800000 AND time <= 1713677400000
```

也可以只按 tag 或只按时间范围删除：

```sql
DELETE FROM cpu WHERE host = 'server-01'
DELETE FROM cpu WHERE time >= 1713676800000 AND time <= 1713677400000
```

当前删除语义：

- 删除底层通过 tombstone 实现，不会原地改写旧 segment。
- 后续查询会过滤 tombstone 覆盖的点。
- compaction 会逐步消化已删除数据。

常见保留策略也可以直接写成相对时间：

```sql
DELETE FROM cpu
WHERE time >= now() - 30d
```

## WHERE 子句的当前限制

虽然解析器支持更多表达式形态，但当前执行器的稳定支持范围是：

- tag 等值条件，例如 `host = 'server-01'`
- `time` 的范围比较，例如 `time >= 1713676800000 AND time < 1713763200000`，或者 `time >= now() - 1d AND time < now() + 1d`
- 多个条件使用 `AND` 连接

当前不建议在生产示例中使用：

- `OR`
- tag 不等式
- field 条件过滤，例如 `usage > 0`
- 混合聚合列与普通列，例如 `SELECT host, sum(usage) ...`

这些写法中的不少在当前版本会直接报错。

## 元数据查询

### `SHOW MEASUREMENTS` / `SHOW TABLES` / `SHOW VIEWS` / `SHOW MATERIALIZED VIEWS` / `SHOW PROCEDURES` / `SHOW TRIGGERS`

`SHOW MEASUREMENTS` 列出当前数据库中所有时序 measurement，`SHOW TABLES` 列出当前数据库中所有关系表，两者都按字典序升序返回单列 `name`。`SHOW VIEWS` 按名称升序返回 `name` 与 `created_utc`。`SHOW MATERIALIZED VIEWS` 返回名称、刷新状态、定义/活动代际、行数、最近成功刷新时间与错误。`SHOW PROCEDURES` 与 `SHOW TRIGGERS [ON table]` 的列合同见前文对应章节。

```sql
SHOW MEASUREMENTS;
SHOW TABLES;
SHOW VIEWS;
SHOW MATERIALIZED VIEWS;
SHOW PROCEDURES;
SHOW TRIGGERS ON devices;
```

| name |
|------|
| cpu  |
| mem  |

### `SHOW INDEXES ON <table>`

列出指定关系表的二级索引：

```sql
SHOW INDEXES ON devices;
```

| 列 | 类型 | 说明 |
|----|------|------|
| `index_name` | string | 索引名 |
| `is_unique` | bool | 是否唯一索引 |
| `columns` | string | 逗号分隔的索引列 |
| `created_utc` | string | UTC ISO-8601 创建时间 |

### `DESCRIBE TABLE <name>`

描述指定关系表的列结构，按 `CREATE TABLE` 声明顺序返回：

| 列 | 类型 | 说明 |
|----|------|------|
| `column_name` | string | 列名 |
| `data_type` | string | `int64` / `float64` / `boolean` / `string` / `datetime` / `blob` / `json` |
| `is_nullable` | bool | 是否允许 `NULL` |
| `is_primary_key` | bool | 是否属于主键 |
| `ordinal` | int64 | 声明顺序 |
| `column_default` | string/null | 规范化默认表达式；没有默认值时为 `NULL` |
| `is_auto_increment` | bool | 是否为数据库自动分配递增整数的列 |

```sql
DESCRIBE TABLE devices;
```

### `DESCRIBE VIEW <name>`

返回逻辑视图的名称、SELECT 定义、直接依赖和 UTC 创建时间：

```sql
DESCRIBE VIEW active_devices;
```

| 列 | 类型 | 说明 |
|----|------|------|
| `name` | string | 视图名称 |
| `definition` | string | 不含 `CREATE VIEW ... AS` 前缀的 SELECT SQL |
| `dependencies` | string | 按字典序排列、逗号分隔的直接数据源 |
| `created_utc` | datetime | UTC 创建时间 |

### `DESCRIBE MATERIALIZED VIEW <name>`

返回物化视图定义、依赖、活动代际和最近刷新状态；完整列合同见前文“物化视图”。

```sql
DESCRIBE MATERIALIZED VIEW active_device_cache;
```

### `DESCRIBE [MEASUREMENT] <name>` / `DESC <name>`

描述指定 measurement 的列结构，按 `CREATE MEASUREMENT` 声明顺序返回三列：

| 列 | 类型 | 说明 |
|----|------|------|
| `column_name` | string | 列名 |
| `column_type` | string | `tag` 或 `field` |
| `data_type`   | string | `float64` / `int64` / `boolean` / `string` |

关键字 `MEASUREMENT` 可省略，`DESC` 是 `DESCRIBE` 的兼容别名。

```sql
DESCRIBE MEASUREMENT cpu;
DESCRIBE cpu;       -- 等价
DESC cpu;           -- 等价
```

| column_name | column_type | data_type |
|-------------|-------------|-----------|
| host        | tag         | string    |
| usage       | field       | float64   |

若指定 measurement 不存在，会抛出 `InvalidOperationException`。

### `EXPLAIN <read-only statement>`

`EXPLAIN` 返回一组 `key` / `value` 结果行，用于估算查询会扫描的 series、segment、block 与行数。

```sql
EXPLAIN SELECT usage
FROM cpu
WHERE host = 'server-01' AND time >= now() - 1d;

EXPLAIN SHOW MEASUREMENTS;
EXPLAIN SHOW INDEXES ON devices;
EXPLAIN DESCRIBE MEASUREMENT cpu;
```

当前支持范围：

- `SELECT ...`
- `SHOW MEASUREMENTS` / `SHOW TABLES` / `SHOW VIEWS` / `SHOW MATERIALIZED VIEWS` / `SHOW DOCUMENT COLLECTIONS`
- `SHOW INDEXES ON <table>` / `SHOW JSON INDEXES ON <collection>` / `SHOW FULLTEXT INDEXES ON <collection>`
- `DESCRIBE [MEASUREMENT] <name>` / `DESC <name>`
- `DESCRIBE TABLE <name>`
- `DESCRIBE VIEW <name>`
- `DESCRIBE MATERIALIZED VIEW <name>`
- `DESCRIBE DOCUMENT COLLECTION <name>`

当前不支持对 `INSERT`、`DELETE`、`CREATE`、`DROP`、用户/授权/Token 控制面 SQL 做 `EXPLAIN`。
返回字段包括 `database`、`statement_type`、`measurement`、`matched_series_count`、`estimated_segment_count`、`estimated_block_count`、`estimated_scanned_rows`、`estimated_memtable_rows`、`estimated_segment_rows`、`has_time_filter`、`tag_filter_count`、`access_path` 与 `index_name`。关系表查询的 `access_path` 可能是 `primary_key`、`secondary_index`、`secondary_index_prefix`、`secondary_index_range`、`json_path_index` 或 `table_scan`；文档集合查询可能是 `document_id`、`json_path_index`、`fulltext_index` 或 `document_scan`；JSON 文件虚拟表会显示 `json_file_virtual_table`。

## 控制面 SQL

控制面 SQL 仅在服务端模式可用。

### 用户与密码

```sql
CREATE USER alice WITH PASSWORD 'pa$$'
CREATE USER admin2 WITH PASSWORD 'secret' SUPERUSER
ALTER USER alice WITH PASSWORD 'new-password'
DROP USER alice
```

### 数据库

```sql
CREATE DATABASE metrics
DROP DATABASE metrics
SHOW DATABASES
```

### 授权

```sql
GRANT READ ON DATABASE metrics TO alice
GRANT WRITE ON DATABASE metrics TO alice
GRANT ADMIN ON DATABASE * TO admin2
REVOKE ON DATABASE metrics FROM alice
```

### 查询用户、授权与 Token

```sql
SHOW USERS
SHOW GRANTS
SHOW GRANTS FOR alice
SHOW TOKENS
SHOW TOKENS FOR alice
ISSUE TOKEN FOR alice
REVOKE TOKEN 'tok_abcdef'
```

说明：

- `SHOW TOKENS` 只返回 Token 元数据，不返回明文。
- `ISSUE TOKEN FOR ...` 会在结果里一次性返回明文 Token。
- `REVOKE TOKEN 'tok_xxx'` 按 token id 吊销。

## HTTP 端点

| 端点 | 用途 |
| --- | --- |
| `POST /v1/db/{db}/sql` | 单条 SQL，主要用于数据面；admin 也可通过它执行部分控制面语句 |
| `POST /v1/db/{db}/sql/batch` | 批量 SQL 脚本 |
| `POST /v1/sql` | 专用控制面 SQL 端点，仅 admin |
| `GET /v1/db/{db}/modbus` | Modbus runtime、source、endpoint 与 binding 概览，需要数据库 Read |
| `GET /v1/db/{db}/modbus/writes` | Endpoint 外部写待审批队列，需要数据库 Admin |
| `GET /v1/db/{db}/modbus/write-audit` | Endpoint 写治理事件，需要数据库 Admin |
| `POST /v1/db/{db}/modbus/writes/{requestId}/approve` | 批准 staged 写，需要数据库 Write + Admin |
| `POST /v1/db/{db}/modbus/writes/{requestId}/reject` | 拒绝 staged 写，需要数据库 Admin |

## 角色与权限

- `readonly`：仅查询
- `readwrite`：可写入和查询
- `admin`：可管理数据库、执行控制面 SQL、进入完整管理能力

## 相关页面

- [批量写入]({{ site.docs_baseurl | default: '/help' }}/bulk-ingest/)
- [ADO.NET 参考]({{ site.docs_baseurl | default: '/help' }}/ado-net/)
- [CLI 参考]({{ site.docs_baseurl | default: '/help' }}/cli-reference/)
