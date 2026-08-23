# M40 #354/#358 Graph SQL V1 与属性感知规划合同

## 决策

M40 步骤 5 冻结 `graph_sql_v1`。Graph V1 已为每个 label 和每个非空 property 自动维护等值派生索引，因此本版本不增加只有名称、没有不同物理行为的 `CREATE/DROP PROPERTY INDEX`。`SHOW/DESCRIBE GRAPH` 公开以下稳定能力字段：

- `sql_contract=graph_sql_v1`
- `label_index_policy=automatic_all_labels`
- `property_index_policy=automatic_all_non_null_properties`

该选择不修改 Graph record/key、WAL、catalog 或 backup 格式。未来若需要 partial/range/composite property index，必须以新合同和新 DDL 扩展，不能改变 V1 自动等值索引语义。

## DML

V1 使用正整数 element、label 和 property ID。属性列写作 `property_<id>`；SQL `NULL` 或 `DEFAULT` 表示属性不存在，显式 `GraphPropertyKind.Null` 仍只通过 typed API 表达。

```sql
INSERT INTO GRAPH topology VERTEX
    (id, labels, property_7, unique_property_ids)
VALUES (1, '1,2', 'pump', 7);

UPSERT INTO GRAPH topology VERTEX
    (id, element_version, labels, property_7)
VALUES (1, 1, 2, 'pump-v2');

UPDATE GRAPH topology VERTEX
SET labels = '2,4', property_7 = 'pump-v3', property_8 = NULL
WHERE id = 1 AND element_version = 2;

DELETE FROM GRAPH topology EDGE
WHERE id = 10 AND element_version = 1;
```

`INSERT` 固定期望 version 0。`UPSERT` 是完整替换，必须逐行提供 `element_version`，新建时为 0。`UPDATE` 是部分替换；`DELETE` 保持 vertex `RESTRICT`。`UPDATE`/`DELETE` 的 WHERE 必须且只能由 `id` 和 `element_version` 两个等值条件组成。每条 values 语句映射到一个 `GraphTransaction`，不拆批、不进入关系表轻事务；任一行冲突时整条语句不发布。

SQL 属性接受 Int64、有限 Float64、Boolean 和 String。`unique_property_ids` 是完整唯一属性 ID 集；部分 UPDATE 未赋值时保留当前 claim，属性被移除时对应 claim 同步移除。

## 统计与规划

`ANALYZE GRAPH <name>` 在固定 statement snapshot 上刷新可重建内存统计，并把 sequence 发布给同一 `GraphStore`。统计不写入 Graph V1 持久格式；重开后状态为 `missing`，后续写入使其变为 `stale`。

`graph_cost_v1` 从 `MATCH WHERE` 提取 `variable.property_<id> = scalar`，比较左右端 value cardinality，并把选择出的谓词编译为既有 `GraphNodeScanPlan` property seek。没有统计时仍可使用 property index，但以有界启发式估算；非等值或不支持的属性谓词回退 label index并保留残余过滤。

`EXPLAIN [ANALYZE] GRAPH_TABLE` 报告：

- `anchor_side`、`anchor_variable`、`execution_direction`
- `anchor_access_path`、`anchor_index`、`anchor_property_id`
- `statistics_sequence`、`statistics_freshness`、`estimate_source`
- `anchor_expand_order`、`edge_access_path`、`fallback_reason`
- Analyze 的 `actual_anchor_access_path`、`actual_anchor_index`、rows/expansions/frontier/elapsed

普通 `EXPLAIN` 只读已发布统计，不为估算扫描业务数据。属性 seek 后仍执行完整 MATCH 谓词，结果语义与 label scan 对拍一致。

## 未包含

V1 不承诺命名 label/property index、范围/复合/partial property index、无版本 last-write-wins、跨 graph/关系表原子事务、`DETACH DELETE`、完整 GQL/Cypher 写语法或持久统计文件。固定硬件、外部数据库对拍和发布证据继续归 M40 步骤 8。
