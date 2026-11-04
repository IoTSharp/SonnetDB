---
layout: default
title: 九模型 MCP 工具
description: 数据模型发现、读取、授权写入与响应预算。
---

# 九模型 MCP 工具

连接地址仍为 `https://<server>/mcp/<database>`，使用 Bearer Token 和 Streamable HTTP。
先通过 `tools/list` 读取实际 Server 的 inputSchema、outputSchema 与 annotation；旧 Server 的九个只读工具不等于下列新增能力已部署。
这些工具可供 WorkBuddy 及其它标准 MCP 客户端调用，连接器市场送审和真实 WorkBuddy 宿主验收另行记录。

合同版本保持 `1.0`，新增工具采用扩展方式。原有 `query_sql`、`explain_sql`、measurement 与文档/Skill 检索工具保留原名、输入、默认值和只读行为。

| 模型 | 发现和读取 | 写入 |
| --- | --- | --- |
| 时序 | `list_measurements`、`describe_measurement`、`sample_rows`、`query_sql` | `execute_sql` 的 CREATE MEASUREMENT、INSERT/DELETE |
| 关系表 | `list_tables`、`query_sql`、`explain_sql` | `execute_sql` 的 CREATE TABLE、INSERT/UPDATE/DELETE |
| KV | `list_keyspaces`、`get_kv`、`scan_kv` | `put_kv`、`delete_kv` |
| 文档 | `list_collections`、`describe_collection`、`get_document`、`scan_documents` | `execute_sql` 的 CREATE DOCUMENT COLLECTION、`put_document`、`delete_document` |
| 全文 | `describe_collection`、`search_fulltext` | `execute_sql` 的 CREATE FULLTEXT INDEX，文档写入维护原生索引 |
| 向量 | `describe_collection`、`search_vector`；已有 SQL 查询仍可使用 | `execute_sql` 的 CREATE VECTOR INDEX，`put_document` 写入显式向量字段 |
| 对象 | `list_buckets`、`list_objects`、`get_object` | `create_bucket`、`delete_bucket`、`put_object`、`delete_object` |
| MQ | `list_mq_topics`、`read_mq`、`get_mq_stats` | `publish_mq`、`ack_mq` |
| 原生图 | `list_graphs`、`get_graph_vertex`、`expand_graph`、已有 `query_sql` SQL/PGQ | `execute_sql` 的 CREATE GRAPH、`upsert_graph_vertex`/`upsert_graph_edge`、`delete_graph_vertex`/`delete_graph_edge` |

模型覆盖不表示所有原生管理、事务或批处理 API 都转成了 MCP，也不改变 Graph Beta 范围、向量模型质量或恢复合同。

## 数据库权限

endpoint 在协议处理前要求数据库存在且凭据具有 Read 权限。新增模型工具再次计算该数据库的有效权限；写工具额外要求 Write，数据库 grant 降为 Read 后下次调用立即拒绝。写工具的 `readOnly=false`；可能覆盖、删除或推进消费状态的工具声明 `destructive=true`。`create_bucket` 与 `publish_mq` 不删除既有数据，声明 `destructive=false`。所有新增写工具保守声明 `idempotent=false`，不允许客户端根据 annotation 自动重发。

`execute_sql` 只接受一条基础数据面建模或无 RETURNING 的 DML。用户、角色、授权、数据库控制面、BEGIN/COMMIT、存储过程调用、多语句与 INSERT SELECT 会被拒绝。它没有跨工具事务会话，不能据此承诺多模型原子性。

## 预算和分页

- 列表默认100条，`maxRows` 必须为1..1000。资源名称按 Ordinal 排序，通过 `after`/`nextCursor` 继续；返回原始目录拼写。名称列表使用已有目录快照，页大小约束返回负载，不代表目录获取完全流式或与存储量无关。
- `list_objects` 使用原生 `continuationToken`；响应字段 `nextCursor` 可原样传回。`scan_documents` 将 `nextCursor` 作为 `afterId` 继续；`scan_kv` 将 `nextCursor` 作为 `afterKeyBase64` 继续，保留原始key字节，不把UTF-8展示文本当作无损游标。KV游标必须属于相同前缀。
- KV/对象/MQ 二进制值显式使用 Base64。单值输入/读取最多64 KiB；原生接口保留更大对象传输能力。`get_object` 支持 `offset` 和1..65536的 `maxBytes`，返回实际版本、完整大小及 `truncated`，不会将部分内容标成完整对象。
- 新增模型工具每份成功 JSON 负载最多256 KiB；文本 content 和 structuredContent 各一份，协议包装另计。source-generated serializer写入固定容量 `IBufferWriter<byte>`，不能先生成整份巨大JSON再判断。预算超额返回 `limit_exceeded`。
- KV与文档页同时按条数和约76 KiB原始key/value总量截页。新增按调用指定的Core读取入口先枚举metadata，在复制内存value或读取磁盘value前检查单值64 KiB/剩余页预算；文档在原始JSON通过字节预算后才解码，不通过读取触发TTL删除。超大单记录明确报错，已返回的页不会越过未读取记录，继续位置保持有效。原生不带预算的Core API保留已有合同。
- 文档 JSON 与写 SQL 最多64 KiB，普通文本参数最多4096字符，资源名称沿用服务端命名/保留命名空间校验。显式向量最多4096个有限 float，k为1..1000，不自动调用 embedding provider。
- Graph写入在编码前分别检查labels、properties、uniquePropertyIds最多128项，属性文本各最多4096字符，并对全部字段（含与kind不匹配的可选字段）保守预约转义开销，再通过64 KiB固定编码缓冲确认总预算。Graph的kind遵循 `tools/list` 中的数字枚举schema；.NET调用方可用ServerJsonContext生成JsonElement，避免客户端SDK默认枚举字符串策略改变输入。
- 异步对象操作和 SQL 使用请求取消令牌与15秒截止令牌；图游标有结果/页字节上限。部分原生同步操作无法在调用中途抢占，不能把输入/输出预算宣传成所有执行路径的硬时间或内存上限。

## KV、对象与消息示例

有 Write 权限时，可调用 `put_kv`：

```json
{"keyspace":"settings","key":"device:1","valueBase64":"aGVsbG8="}
```

随后用只读凭据调用 `get_kv`，相同 keyspace/key 返回 `found`、Base64 值、版本和到期时间。缺失 keyspace 的只读访问返回错误，不通过 `Open` 隐式创建目录。

对象先使用 `create_bucket`，再使用 `put_object` 写入小对象。读取可传：

```json
{"bucket":"samples","key":"report.bin","offset":0,"maxBytes":4096}
```

`list_mq_topics` 只列出当前数据库限定的主题。`read_mq` 输入 `topic`、`offset`、`maxRows`，返回消息和 `nextOffset`；重复查看不会创建消费组或确认消息。`publish_mq` 发布后返回消息 offset；`ack_mq` 输入相同相对主题、`consumerGroup` 和已处理的 `offset`，确认按照原生 MQ 语义推进消费状态。客户端不要传其它数据库的内部限定前缀来尝试跨库访问。

Server MQ 仍持久化在实例 `.system/mq`，`storageScope: "instance"` 说明物理范围；endpoint 数据库名是授权和主题命名空间。单库备份不包含它，实例级快照/恢复仍走原生运维流程。

## 写入失败与恢复

新增工具的错误保留既有两段 content 兼容格式。Write 权限不足返回 `forbidden`，无写入；输入非法返回 `invalid_argument` 或 `invalid_sql`。在写入开始后遇到取消或 I/O 异常时，返回 `write_outcome_unknown` 且 `retryable=false`。调用方应读取 KV 版本、对象版本、文档、MQ 状态或图的 requestId/元素版本证据，核对后再决定后续动作。不要因超时或断连而盲目重复发布 MQ 消息或重发其它写操作。

图写入要求非空 requestId，并保留 expectedElementVersion 与原生提交合同；这不等于其它模型支持通用幂等键。AOT 构建、真实 MCP 协议测试、客户端宿主、正式发布和线上部署分别验收。
