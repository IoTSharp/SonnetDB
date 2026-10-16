# SQL 约束、索引与自增维护

本页描述 #250 的源码合同。正式 SonnetDB NuGet/Server 版本与 SqlSugar 上游交付仍待发布；源码测试通过不表示公开包已提供这些能力。标准 `CodeFirst.InitTables` 的事务 DDL 边界仍由 #227 跟踪。

## 真实约束名称

```sql
CREATE TABLE devices (
    id INT AUTO_INCREMENT,
    code STRING,
    value INT,
    CONSTRAINT pk_devices_id PRIMARY KEY (id),
    CONSTRAINT ck_devices_value CHECK (value >= 0)
);

SELECT table_schema, table_name, constraint_name, constraint_type
FROM information_schema.table_constraints
WHERE table_name = 'devices';

ALTER TABLE devices DROP CONSTRAINT ck_devices_value;
ALTER TABLE devices DROP CONSTRAINT pk_devices_id;
ALTER TABLE devices ADD CONSTRAINT pk_devices_code PRIMARY KEY (code);
```

目录从实际表 schema 读取 `PRIMARY KEY`、`FOREIGN KEY`、`CHECK` 名称；不通过 ORM 命名习惯拼造名称。`COUNT(*)` 可用于目录存在性检查，支持先 WHERE 过滤再计数；本次不增加目录视图的通用聚合、GROUP BY 或 HAVING 合同。

- 未声明主键名称时，保存 `pk_<table>`；若与同表外键/CHECK 名称冲突，依次选择 `_1`、`_2` 等后缀。超出目录字符串长度的默认名称使用确定性 SHA-256 短名。显式超长名称拒绝。
- 名称保留原拼写。普通引用忽略大小写，双引号精确匹配；同表 PK/FK/CHECK 名称不能仅大小写不同。表或列重命名保留约束名称。
- 打开旧目录时，旧主键的默认名称先持久化，再暴露表与维护目录。例如已有 `pk_Legacy` 外键和 `PK_Legacy_1` CHECK 时，原未命名主键保存为 `pk_Legacy_2`。
- 删除主键使用已有主键迁移与稳定行身份机制，保留原数据、索引载荷和每行独立身份。之后允许旧主键值重复；唯一索引、NOT NULL 和其它约束仍按保存的 schema 生效。
- 有入站外键依赖主键时拒绝删除；先显式处理引用关系。主键重定义、类型转换、检查与恢复边界见 [主键迁移](sql-primary-key-migration.md)。

## 索引方向与包含列

```sql
CREATE INDEX ix_devices_code
ON devices (code DESC, id ASC)
INCLUDE (value);

CREATE INDEX ix_devices_value
ON devices (value DESC)
INCLUDE (code)
ONLINE;

SELECT index_name, column_name, ordinal_position, sort_direction, is_included
FROM information_schema.indexes
WHERE table_name = 'devices';
```

每个键列可声明 `ASC` 或 `DESC`，默认 `ASC`。DESC 改变真实索引键字节顺序，查询将逻辑范围与请求排序映射到相应物理方向。SQL 结果仍由请求的 ORDER BY 决定；索引声明不保证无 ORDER BY 查询的输出顺序。

`INCLUDE` 把指定列的类型化值保存在索引项内，不参与唯一性判断。键列和包含列必须存在，各自不重复且不能重叠。只更新包含列时会更新其载荷。覆盖查询在满足计划条件时从索引读出包含列，避免回表；它不保证任意表达式、残余过滤或排序都能采用覆盖计划。

有界 Top-N 仍优先使用匹配的范围访问计划。声明 DESC 不会扩展所有数据类型的范围规划能力；缺少适用计划时，执行器保留完整过滤与排序。默认 ASC 且没有 INCLUDE 的索引继续使用旧编码。

普通/唯一索引均支持方向和包含列；ONLINE 仍只支持普通非唯一关系表索引。在线断点保存方向、包含列及 schema 指纹；同名但定义不同的续建请求拒绝。取消、重开后的续建会补入构建期间的行变更。文档/JSON path 索引不接受本页的 DESC/INCLUDE 选项。

原生目录与 ADO `GetSchema("Indexes")` 每个键列和包含列各返回一行。ADO 列名为 `SORT_DIRECTION` 与 `IS_INCLUDED`；包含列没有排序方向。REST/Frame schema 元数据传递同一声明。

## 自增下一值

```sql
ALTER TABLE devices AUTO_INCREMENT = 200;
INSERT INTO devices (code, value) VALUES ('next', 1);
```

`200` 表示下一次自动分配的值。命令要求表已经具有 `INT AUTO_INCREMENT` 列，范围为 `1..Int64.MaxValue`。

- 下一值必须严格大于历史高水位；等于当前高水位加一可幂等设置。显式插入高值会推进高水位，删除最大行不会降低它。冲突、零、负数与超界输入拒绝且不改变状态。
- 命令在 schema/manager/store 锁内验证并保存 `next - 1`，成功返回前同步维护 WAL。普通写入未开启逐次同步时，这条已确认的维护状态也需可恢复。
- 设置 `Int64.MaxValue` 后允许最后一次自动分配，下一次分配明确溢出，不能环绕或生成额外记录。
- 不插入或删除占位行，不改变既有行数。该语句属于 DDL，在当前不支持 DDL 的活动轻事务中仍拒绝。
- 修改既有列是否具有 AUTO_INCREMENT 声明不是本命令的功能。SqlSugar 候选明确拒绝这种声明切换，不以删除并重建列模拟。

## 持久化与兼容范围

真实主键名称与 DESC/INCLUDE 使用 table catalog **v16**。引擎先保存永久版本屏障，再写新索引编码或在线断点；移除最后一个新特性后也不降版。支持更低目录版本的旧引擎必须在打开时拒绝，不能将新载荷当作旧索引值读取。

保存的主键名称也会启用 v16，包括旧带主键目录在新引擎中打开后的名称迁移。升级前需保留可恢复的旧目录备份；降级应恢复该备份，不能手工改版本号。DURATION v14 与 DATETIMEOFFSET v15 的既有能力保持；目录包含多个特性时使用其中最高永久屏障。

验证包括真实 public 4.0.0 的 v10 目录升级、实际旧引擎拒绝 v16（删除最后特性前后）、三协议宿主重开、identity 确认后强杀、主键删除与 INCLUDE 在迁移和恢复阶段强杀。旧引擎拒绝探针核对的是 schema 文件 SHA；未宣称目录所有文件零写入。进程终止测试不替代物理断电测试。

## SqlSugar 状态

本地候选补丁基于固定 PR1457 head `65e8226ce25a8e1cb9cca2590683f1fd6778cab3`，使用真实目录、命名主键与官方自增语句。维护消费者在嵌入式、REST、Frame HTTP/2 均取得 11/12；唯一失败是标准 CodeFirst 的事务 DDL。远程完整宿主重开后各 5/5。不能把这些维护结果写成完整 SugarIndex/CodeFirst 通过。

固定上游依赖的 NuGet 审计失败和构建警告保留；本次不声明上游依赖审计通过。复现实验、补丁和固定输入见 [探针说明](../eng/probes/issue250-maintenance/README.md)，证据矩阵见 [实施审计](audits/issue-250-maintenance-design-20261016.md)。
