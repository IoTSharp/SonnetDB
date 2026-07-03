---
layout: default
title: "SQL 参考"
description: "当前版本真实支持的数据面与控制面 SQL 语法、限制和示例。"
permalink: /sql-reference/
---

想直接复制完整场景化示例，可先看 [SQL Cookbook]({{ '/sql-cookbook/' | relative_url }})；本页更偏向能力边界与精确语法说明。

## 数据面 SQL

### `CREATE TABLE`

定义关系表 schema。关系表 MVP 使用 KV-backed rowstore 存放在数据库目录的 `tables/` 下，不修改时序 `.SDBWAL` / `.SDBSEG` 格式。

```sql
CREATE TABLE devices (
    id INT NOT NULL,
    site_id INT NULL,
    name STRING NOT NULL,
    enabled BOOL,
    version INT ROWVERSION,
    installed_at DATETIME NULL,
    metadata JSON NULL,
    payload BLOB NULL,
    PRIMARY KEY (id),
    FOREIGN KEY (site_id) REFERENCES sites (id)
)
```

规则：

- 当前必须声明 `PRIMARY KEY (...)`；主键列会强制为 `NOT NULL`。
- 支持类型：`INT`、`FLOAT`、`BOOL`、`STRING`、`DATETIME`、`BLOB`、`JSON`。
- `DATETIME` 可写 Unix 毫秒整数或 ISO-8601 字符串，查询时返回 UTC `DateTime`。
- `BLOB` 可写 base64 字符串；ADO.NET 参数可直接传 `byte[]`。
- `JSON` 当前按 UTF-8 字符串存储；可用 `json_value(json_col, '$.path')` 做 path 投影和过滤。
- 二级索引使用 `CREATE INDEX` 单独声明。
- `FOREIGN KEY (...) REFERENCES parent (...)` 第一版只支持表级声明，引用列必须等于被引用表 `PRIMARY KEY`；外键列任一为 `NULL` 时跳过校验。
- `ROWVERSION` 只能声明在一个 `INT` 列上；`INSERT` 自动写入 `1`，`UPDATE` 自动递增，可用 `WHERE id = ... AND version = ...` 获得乐观并发冲突检测。

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
- `SELECT` / `UPDATE` / `DELETE` 的 `WHERE` 覆盖索引全部列的等值条件时，可先走二级索引候选行，再执行完整 WHERE 过滤。
- 索引内容不作为第二份权威数据保存；rowstore 是主数据，索引可重建。

### 关系表 DML

```sql
INSERT INTO devices (id, name, enabled)
VALUES (1, 'pump-01', TRUE), (2, 'fan-02', FALSE);

SELECT id, name
FROM devices
WHERE enabled = TRUE AND id > 1
ORDER BY id DESC
LIMIT 10;

UPDATE devices
SET name = 'pump-01b'
WHERE id = 1;

DELETE FROM devices
WHERE id = 2;
```

当前行为：

- `INSERT` 按主键插入；主键已存在时返回错误，不会静默覆盖。
- `UPDATE` 支持更新非主键列；当前不支持更新主键列。
- `SELECT` 支持 `*`、列投影、字面量投影、`WHERE` 中的 `AND` / `OR` / `NOT`、基础比较和简单数值运算。
- 关系表 `JSON` 列支持 `json_value(metadata, '$.site')` 这类 path 表达式；对象或数组结果会以紧凑 JSON 字符串返回。
- `WHERE` 覆盖完整主键等值条件时会走主键读取；覆盖完整二级索引等值条件时会走二级索引候选行；其它条件走表扫描后过滤。
- `ORDER BY` 支持结果集中的任意列名；`LIMIT` / `OFFSET` / `FETCH` 语法与 measurement 查询一致。

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
- 不支持嵌套事务、measurement / document 写入事务、DDL 事务或跨数据库事务。在事务上下文内执行 measurement（时序）`INSERT` / `DELETE` 或文档集合写入会抛 `NotSupportedException`（这类写入直接落 WAL/tombstone，`ROLLBACK` 无法撤销，故显式拒绝而非静默写入造成"假回滚"）。
- `COMMIT` 前会校验 NOT NULL、主键、唯一索引、外键和 ROWVERSION 乐观并发列；任一失败时，不会留下已应用的 rowstore / index 变更。
- 稳定约束错误码：`table_unique_violation`、`table_foreign_key_violation`、`table_concurrency_conflict`。
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
- measurement 侧 `tag = '...'` 和 `time` 范围过滤会先下推到时序查询；关系表侧 `WHERE` 条件会先用主键或二级索引候选行，再做完整过滤。
- 输出投影支持 `time`、measurement tag / field、table 列和字面量；有歧义的列名必须使用 `alias.column`。
- `ORDER BY` 可引用 JOIN 结果中的 measurement 或 table 列，并在分页前执行。
- `EXPLAIN` 支持 JOIN 查询，`access_path` 会显示 measurement 与 table 双侧下推路径，例如 `measurement:tag_index;table:secondary_index;join:hash`。

当前限制：

- 不支持 `LEFT JOIN` / `RIGHT JOIN` / `FULL JOIN`。
- 不支持 measurement 与 measurement JOIN、table 与 table JOIN、多表 JOIN、子查询 JOIN。
- JOIN 查询暂不支持聚合、`GROUP BY`、窗口函数或标量函数投影。

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
- `UPDATE` 当前仅支持 `SET document = '<json>'`，按命中文档整体替换。
- `json_value(document, '$.path')` 支持 `$`、点属性、`$['property']` 和数组下标，例如 `$.metrics.temp`、`$['display-name']`、`$.tags[0]`。
- `CREATE JSON INDEX` 建立基础 path 等值索引；`WHERE json_value(document, '$.type') = 'pump'` 可走该索引，`EXPLAIN` 的 `access_path` 会显示 `json_path_index`。
- `id = '...'` 会走文档 ID 读取；其它条件走集合扫描后过滤。
- 第一版不提供 MongoDB 兼容 API、跨文档复杂事务或 JSON schema 校验。

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
POST   /v1/db/{db}/documents/{collection}/delete-one
POST   /v1/db/{db}/documents/{collection}/delete-many
POST   /v1/db/{db}/documents/{collection}/count
POST   /v1/db/{db}/documents/{collection}/distinct
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

// update-one：第一版为整文档替换，不是 $set/$inc 局部更新
{ "id": "dev-1", "document": { "site": "north", "kind": "pump", "status": "ok" } }

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

find 支持 cursor 分页：首个请求传 `limit`（服务器最大 batch size 为 1000），响应中的 `continuationToken` 不为空时，把它原样放入下一次 find 请求即可继续读取；续页 token 绑定 collection、查询形状、只读快照版本和 15 分钟过期时间，不能与 `skip` 混用。写入导致集合版本变化后，旧 token 会被拒绝，需要重新发起首个 find 请求。当前 Document API 契约刻意不实现 MongoDB wire protocol / BSON command，也不承诺官方 MongoDB Driver 直连；局部更新操作符和批量写事务语义会在 Milestone 21 后续 PR 中补齐。OpenAPI 片段见 [document-api.yaml](openapi/document-api.yaml)。

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
- 导入 table 时要求每条记录是对象，并按列名映射到表列；对象 / 数组可写入 `JSON` 列。
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
- `EXPLAIN` 的 `access_path` 会显示 `document_vector_scan`，`index_name` 显示使用的 JSON vector path。

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
CREATE MEASUREMENT cpu (
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
- `NULL` / `NOT NULL` 可作为 DDL 兼容修饰符出现在列类型后；当前仅保留在 SQL AST 中，执行层不把它持久化为 catalog 约束，也不强制 `NOT NULL`。
- `DEFAULT <expr>` 目前会被 parser 接受，但执行 `CREATE MEASUREMENT` 时会返回明确的 `DEFAULT` 暂不支持错误。

稀疏字段语义：

- SonnetDB 的 field 是稀疏的：同一个 measurement 的不同时间点可以携带不同 field 集合。
- 如果某个时间点没有写入某个 field，查询该列时结果为 `NULL`；这表示“该时间点未记录该字段”，不是 schema 约束失败。
- 写入时不要用 `DEFAULT` 或显式 `NULL` 表达缺值；请省略该 field，或在应用侧写入具体默认值。

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

标量函数投影：

```sql
SELECT abs(-usage), round(usage / 3, 2), sqrt(count), log(count, 10), coalesce(label, 'n/a')
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
- 标量函数当前支持 `abs`、`round`、`sqrt`、`log`、`coalesce`。
- 标量函数当前仅支持出现在 `SELECT` 投影中，可嵌套，也可接收算术表达式参数。
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

支持的聚合函数：

- `count`
- `sum`
- `min`
- `max`
- `avg`
- `first`
- `last`

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
- 结果当前只返回聚合列，不会自动带出桶起始时间列。
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

### `SHOW MEASUREMENTS` / `SHOW TABLES`

`SHOW MEASUREMENTS` 列出当前数据库中所有时序 measurement，`SHOW TABLES` 列出当前数据库中所有关系表。两者都按字典序升序返回单列 `name`。

```sql
SHOW MEASUREMENTS;
SHOW TABLES;
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

```sql
DESCRIBE TABLE devices;
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
- `SHOW MEASUREMENTS` / `SHOW TABLES` / `SHOW DOCUMENT COLLECTIONS`
- `SHOW INDEXES ON <table>` / `SHOW JSON INDEXES ON <collection>` / `SHOW FULLTEXT INDEXES ON <collection>`
- `DESCRIBE [MEASUREMENT] <name>` / `DESC <name>`
- `DESCRIBE TABLE <name>`
- `DESCRIBE DOCUMENT COLLECTION <name>`

当前不支持对 `INSERT`、`DELETE`、`CREATE`、`DROP`、用户/授权/Token 控制面 SQL 做 `EXPLAIN`。
返回字段包括 `database`、`statement_type`、`measurement`、`matched_series_count`、`estimated_segment_count`、`estimated_block_count`、`estimated_scanned_rows`、`estimated_memtable_rows`、`estimated_segment_rows`、`has_time_filter`、`tag_filter_count`、`access_path` 与 `index_name`。关系表查询的 `access_path` 可能是 `primary_key`、`secondary_index`、`json_path_index` 或 `table_scan`；文档集合查询可能是 `document_id`、`json_path_index`、`fulltext_index` 或 `document_scan`；JSON 文件虚拟表会显示 `json_file_virtual_table`。

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

## 角色与权限

- `readonly`：仅查询
- `readwrite`：可写入和查询
- `admin`：可管理数据库、执行控制面 SQL、进入完整管理能力

## 相关页面

- [批量写入]({{ site.docs_baseurl | default: '/help' }}/bulk-ingest/)
- [ADO.NET 参考]({{ site.docs_baseurl | default: '/help' }}/ado-net/)
- [CLI 参考]({{ site.docs_baseurl | default: '/help' }}/cli-reference/)
