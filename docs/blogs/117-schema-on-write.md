## SonnetDB 受控 Schema-on-Write：写入时自动补齐 Tag 与 Field

时序数据库经常面对一个现实问题：数据源比数据库 schema 变化得更快。

一批设备固件升级后多上报了 `firmware` tag；一个采集器从只报 `temperature` 扩展到同时上报 `humidity`；某个计数字段过去一直是整数，后来因为计算方式变化开始出现小数。如果每一次变化都要求先人工执行 DDL，再恢复写入链路，接入体验就会变得很脆。

SonnetDB 新增了受控的 schema-on-write 能力：写入路径可以在入库时自动创建 measurement，也可以为已有 measurement 自动补齐缺失的 `TAG` / `FIELD`。同时，它并不是完全无约束的 schema-less。已有列仍然会做角色和类型兼容校验，避免数据悄悄漂移成不可查询的状态。

### 新能力覆盖哪些写入路径

这次改动覆盖四条主写入路径：

- 普通 SQL `INSERT INTO ... VALUES`
- Line Protocol 批量写入
- JSON points 批量写入
- Bulk VALUES 快路径

也就是说，下面这种首次写入可以直接创建 schema：

```sql
INSERT INTO weather (time, station TAG, temperature, humidity)
VALUES (1713676800000, 'beijing', 28.5, 61.2)
```

SonnetDB 会推断出：

```sql
CREATE MEASUREMENT weather (
    station TAG,
    temperature FIELD FLOAT,
    humidity FIELD FLOAT
)
```

如果之后又写入新列：

```sql
INSERT INTO weather (time, station, firmware TAG, pressure)
VALUES (1713676860000, 'beijing', '1.2.0', 1008.5)
```

已有 measurement 会被扩展为新增 `firmware TAG` 和 `pressure FIELD FLOAT`。

### LP 和 JSON 的推断更自然

Line Protocol 和 JSON points 本身就区分 tags 与 fields，所以自动补 schema 非常直接。

Line Protocol：

```text
weather,station=beijing,firmware=1.2.0 temperature=28.5,humidity=61.2 1713676800000
```

JSON points：

```json
{
  "m": "weather",
  "points": [
    {
      "t": 1713676800000,
      "tags": { "station": "beijing", "firmware": "1.2.0" },
      "fields": { "temperature": 28.5, "humidity": 61.2 }
    }
  ]
}
```

在这两种格式中：

- `tags` 里的新 key 自动追加为 `TAG STRING`
- `fields` 里的新 key 按值类型追加为 `FIELD FLOAT` / `FIELD INT` / `FIELD BOOL` / `FIELD STRING`

这让 SonnetDB 更接近 InfluxDB Line Protocol、TDengine schemaless、QuestDB ILP 这类接入体验：先让数据流进来，再由数据库维护可查询的 schema。

### SQL INSERT 的推断规则

SQL 的难点是：列名本身并不告诉数据库它是 tag 还是 field。

例如：

```sql
INSERT INTO cpu (time, host TAG, usage)
VALUES (1, 'server-01', 0.72)
```

如果 `cpu` 还不存在，`host TAG` 明确声明该列为身份维度，`usage` 是浮点数。SQL 字符串值本身不能证明其基数适合做 tag，规则如下：

- 未知列默认推断为 `FIELD`，包括字符串列；高基数的请求 ID、消息等不会仅因类型是字符串而进入 series key
- 在列列表中写 `name TAG` 或 `name FIELD` 可以显式指定新列角色；`TAG` 值必须是字符串
- 已存在列永远按 schema 解释；提示与现有角色不一致时拒绝写入

因此上面的 SQL 会推断为：

```sql
CREATE MEASUREMENT cpu (
    host TAG,
    usage FIELD FLOAT
)
```

如果你需要一个字符串 field，比如 `status FIELD STRING`，可以直接写入，或先显式建表：

```sql
CREATE MEASUREMENT device_state (
    device TAG,
    status FIELD STRING
)
```

这样后续 `INSERT` 会严格按 schema 写入。Bulk VALUES 快路径也采用相同的字符串默认 FIELD 和显式列角色提示；Line Protocol、JSON points 继续依赖格式中的 tags/fields 边界。

### 类型兼容与提升

自动补列不等于允许任意类型漂移。SonnetDB 当前支持一个明确的数值提升规则：

- 已有 `FLOAT` 字段接收整数值：保持 `FLOAT`，写入前转换成浮点
- 已有 `INT` 字段接收浮点值：schema 提升为 `FLOAT`
- `FLOAT` 不会降级为 `INT`
- `BOOL`、`STRING`、`VECTOR`、`GEOPOINT` 与其它类型之间不会自动互转

示例：

```sql
INSERT INTO meter (time, device TAG, reading)
VALUES (1, 'm1', 100);
```

首次写入后，`reading` 会是 `FIELD INT`。

后来出现小数：

```sql
INSERT INTO meter (time, device, reading)
VALUES (2, 'm1', 100.5);
```

SonnetDB 会把 `reading` 的 schema 提升为 `FIELD FLOAT`。之后查询 SQL 投影时，旧的整数值也会按浮点结果显示，避免应用层看到同一列一会儿是整数、一会儿是浮点。

### 为什么要先保存 schema，再写 WAL

这次实现里一个关键约束是：schema 变更必须先持久化，再写 WAL 和数据。

如果反过来，数据库可能在崩溃恢复后遇到一种尴尬状态：

```text
WAL 中已经有新字段数据
measurements.tslschema 中却没有这个字段
```

这样数据虽然存在，但 SQL 查询、`DESCRIBE MEASUREMENT`、Copilot schema 感知、MCP schema resource 都可能看不到它。

现在 SonnetDB 的写入顺序是：

```text
推断并校验候选 measurement schema
持久化 measurements.tslschema
发布内存 measurement schema 快照
写 WAL
写 MemTable
```

这样即使在 schema 保存后、WAL 写入前崩溃，最多只是留下一个还没有数据的新列；不会出现“数据存在但 schema 不可见”的情况。

### 批量写入先完整校验再发布 schema

`WriteMany` 会先在临时计划中合并每个写入块的新增列和类型提升，并校验块内每个点的列角色与类型。只有整个写入块校验全部通过后，才会先持久化 schema、一次性发布内存快照，再写 WAL 和 MemTable。

如果块内后面的点把已有 `TAG` 当作 `FIELD`，或者写入了不兼容类型，整个写入块会在发布前失败：前面点推断出的 measurement、新列和 `INT -> FLOAT` 提升都不会残留，当前 MemTable 也不会因未提交的类型提升被提前密封。调用方修正数据后可以安全重试，不需要清理部分 schema 变更。

超大 `WriteMany` 调用仍按最多 8192 点分块，以便在块间释放写锁并执行内存背压；已经成功提交的前序块不会因后续块失败而回滚。需要跨块全有或全无时，应使用具备明确事务边界的上层导入流程，而不能把普通批量写入当作无限大小事务。

`MaxNewColumnsPerWrite` 对一次 `Tsdb.Write` 或 `WriteMany` 的一个内部块生效。普通 SQL 多行 `INSERT` 目前逐行调用 `Tsdb.Write`，因此每行分别计入本额度，整条 SQL 语句累计新增列数不受该额度约束；后续行失败时，已写入的前序行保持成功。若需要整句扩列预算，应增加独立选项及预检，不能把现有 SQL 多行写入悄悄改为全有或全无。

### 它不是完全 schema-less

SonnetDB 仍然坚持“受控 schema-on-write”，不是完全 schema-less。

这样设计有几个好处：

- `SELECT *` 可以稳定展开 `time + tags + fields`
- `WHERE tag = 'value'` 可以继续依赖 tag schema 做校验和索引过滤
- 聚合函数可以知道字段是否为数值类型
- ADO.NET / REST / Copilot 能拿到一致的列定义
- 类型漂移会尽早暴露，而不是在查询阶段变成随机错误

换句话说，写入体验更像 schemaless，查询体验仍然保持 schema-first。

### 推荐使用方式

对于设备接入、边缘采集、日志转指标、Prometheus / OTLP 这类数据源，可以放心使用自动 schema 演进，让接入链路先跑起来。

对于业务核心表、字符串字段较多的 measurement、或者需要稳定对外 API 的场景，仍然建议显式执行 `CREATE MEASUREMENT`。这样能固定 TAG/FIELD 角色，也能让 schema 设计更可审查。

一个实用的折中方式是：

```sql
CREATE MEASUREMENT cpu (
    host TAG,
    region TAG,
    usage FIELD FLOAT
)
```

之后让自动 schema-on-write 只处理少量新增字段：

```sql
INSERT INTO cpu (time, host, region, usage, load1)
VALUES (1713676800000, 'server-01', 'cn-hz', 0.72, 1.8)
```

`load1` 会自动追加为 `FIELD FLOAT`，而 `host` / `region` / `usage` 仍按原 schema 校验。

### Server 中配置 schema 增长策略

服务端在 `SonnetDBServer:MeasurementSchema` 下统一配置新建、自动加载和挂载的数据库实例：

```json
{
  "SonnetDBServer": {
    "MeasurementSchema": {
      "Mode": "CreateOnly",
      "MeasurementModes": {
        "telemetry_hot": "CreateAndEvolve",
        "billing_events": "Disabled"
      },
      "MaxMeasurements": 4096,
      "MaxColumnsPerMeasurement": 128,
      "MaxNewColumnsPerWrite": 16
    }
  }
}
```

`Mode` 是默认策略；`MeasurementModes` 按区分大小写的 measurement 名称覆盖。`Disabled` 拒绝自动创建和演进，`CreateOnly` 只允许自动创建，`CreateAndEvolve` 允许自动创建、扩列和兼容的数值提升。三项额度分别限制数据库内 measurement 总数、单个 measurement 的 TAG/FIELD 列总数，以及一次写入规划的新增列数。配置在打开数据库实例时固定；修改后需要重启服务或重新打开对应数据库实例。

### 小结

这次 schema-on-write 升级让 SonnetDB 的写入体验更贴近真实时序数据接入：数据源可以逐步演进，数据库自动补齐缺失的 tag 与 field；同时，已有列的角色和类型仍然受到保护。

它解决的是“接入过程中的 schema 摩擦”，不是鼓励完全无约束的数据漂移。对于嵌入式时序数据库来说，这个平衡很重要：写入要顺手，查询也要可靠。
