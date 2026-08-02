---
layout: default
title: Document Store 能力矩阵
description: SonnetDB Document Store 的能力、MongoDB-like 边界、不支持项、推荐规模和管理入口。
permalink: /document-store/
---

# Document Store 能力矩阵

SonnetDB Document Store 面向嵌入式、边缘和单节点工业应用中的 JSON 文档负载。对外定位是 **MongoDB-like document workloads**：常用文档语义有参考 parity，但 SonnetDB 不实现 MongoDB wire protocol、BSON command 或官方 MongoDB Driver 直连。

机器可读的权威差异清单是 [`document-mongodb-gap.json`](document-mongodb-gap.json)。每项能力只使用 `supported`、`partial`、`planned`、`not_planned` 四种状态，并附实现或测试证据；本文是供人阅读的摘要。

## 能力

| 领域 | 当前能力 | 边界 |
|---|---|---|
| 集合与 CRUD | create/drop collection、insert one/many、find one/many、replace、delete one/many | ID 是 SonnetDB 字符串 ID，不是 MongoDB `ObjectId` 自动生成语义 |
| 查询 | `_id`/JSON path；常用比较、`$elemMatch/$regex/$type/$size/$all`、递归 AND/OR/NOT；显式数组下标；.NET 类型化 builder | regex 有资源边界；不是完整 MQL；不支持 `$expr/$where` 或 BSON 类型排序全集 |
| Collation | M32 基础范围支持 ordinal 与 ordinal-ignore-case 过滤、排序和 cursor fingerprint | locale、strength、Unicode normalization 与 locale-aware index 属于后续候选且没有交付日期；ignore-case 查询不会误用 ordinal index |
| 投影与排序 | 多字段 projection、稳定排序、limit/skip；.NET projection/sort builder | 复杂数组投影和 MongoDB expression projection 不在当前契约内 |
| 分页 | 有过期时间和 snapshot version 的 continuation token；.NET cursor 支持逐页读取和 `await foreach` | 不是 MongoDB cursor/wire protocol；token 只能交回 SonnetDB API |
| 局部更新 | `$set/$unset/$inc/$mul/$min/$max/$rename/$push/$pull/$pop/$addToSet/$currentDate`、upsert、multi、原子 `findOneAndUpdate` before/after | 不支持 positional `$/$[]` 或 `arrayFilters`；复杂数组改写使用整体替换或 expectedVersion |
| 索引 | 单字段/复合、multikey、wildcard subtree、unique、sparse、partial、TTL；在线 rebuild；planner/EXPLAIN | compound parallel arrays 稳定拒绝；wildcard 不支持 unique/TTL；不支持 hashed、2d/2dsphere |
| 聚合 | 基础 pipeline；project/group 的 field/literal/arithmetic/concat/ifNull/cond；push/addToSet；unwind array index | `$lookup/$facet/$bucket` 经评估不进入原生子集；使用 SQL JOIN/CASE、多个有界 aggregate 或应用组合 |
| Schema governance | required/type/range/enum/pattern validator；`error`/`warn` action | 规则是 SonnetDB validator DTO，不是完整 MongoDB JSON Schema 方言 |
| 批量写 | mixed insert/replace/update/delete/upsert；ordered/unordered；逐项结果；24 小时 `requestId` 重放 | 非空且最多 1000 项，严格拒绝与操作类型无关的字段；Core canonical payload 上限 16 MiB；原子边界是单 collection 的一个 batch |
| 搜索 | Document full-text index、持久化向量索引、Hybrid Search | 使用 SonnetDB 自有 SQL/API，不映射 Atlas Search 或 MongoDB vector command |
| 变更读取 | SonnetDB change feed，带 sequence、保留期与 resume 位置 | 不是 MongoDB change stream/wire resume token |
| 持久性 | 主文档、path index、change feed 与 sequence 同一 CRC WAL batch；repair marker 在重开时修复 fulltext/vector 派生索引 | 单节点，无 replica set、sharding、跨节点 failover |
| 迁移 | `sndb document import` 读取 JSON/NDJSON、常用 Extended JSON 和 mongodump BSON 子集；dry-run、checkpoint、索引建议、JSON report | Decimal128 与少用 BSON/code 类型先导出 canonical Extended JSON；建议不会自动创建索引 |

## 接入入口

- .NET：`SonnetDB.Data.Documents.SndbDocumentClient`，嵌入式和远程使用同一 API；filter/projection/sort/update builder 生成现有 DTO，`FindCursor` 负责 continuation token 续页。
- HTTP：`/v1/db/{db}/documents/{collection}` 下的私有 JSON API。
- SQL：`CREATE DOCUMENT COLLECTION`、`CREATE INDEX`、`ALTER DOCUMENT COLLECTION ... SET VALIDATOR` 及 JSON path 查询。
- CLI：`sndb document import` 支持嵌入式/远程 profile、确定性 batch request ID、resume checkpoint 和机器报告。
- 管理面：Web Admin 的统一 Explorer 选择 Collections，进入 Document Explorer；SonnetDB Studio 桌面复用同一管理面。当前 VS Code 扩展只承担明确的只读/开发者子集，不宣称完整 Document 编辑面。
- 示例：[`SonnetDB.DocumentQuickstart`](../samples/SonnetDB.DocumentQuickstart/README.md) 用同一份代码运行嵌入式或远程模式，覆盖 builder、cursor、`findOneAndUpdate`、mixed Bulk 与幂等重放。

读操作要求数据库 `Read` 权限，写入、导入、validator 和索引维护要求 `Write` 或对应管理权限。Studio 的导入、rebuild 和 validator 保存必须经过 preview/dry-run/confirm 中至少一种防误操作步骤。

游标 token 无效、查询形状错配、过期或快照变化分别使用 `document_cursor_invalid`、`document_cursor_mismatch`、`document_cursor_expired`、`document_cursor_stale`。这些错误要求调用方明确重新发起查询；SDK 不会静默跳过、复用旧快照或自动从头读取。

## 不支持项

当前明确不支持，或只作为无日期后续候选：

- MongoDB wire protocol、BSON command、MongoDB URI 直连和官方 MongoDB Driver 复用。
- replica set、sharding、分布式事务、读偏好、write concern 多副本确认。
- MongoDB session/transaction、change stream、oplog、Atlas Search 管理协议。
- 完整 MQL、完整 aggregation pipeline、全部 BSON 类型和 BSON-specific comparison semantics；locale collation 仅列为无日期后续候选。
- `$lookup/$facet/$bucket` 原生 stage、positional array update、hashed/geospatial Document index；替代路径见结构化 gap catalog。
- 自动把现有 MongoDB index/validator/user/role 元数据原样导入。

这些差异不是临时隐藏的兼容项。应用迁移前必须按 [MongoDB-like 迁移指南](mongodb-migration.md) 改造客户端入口并逐项验收语义。

## 规模建议

当前完整发布验收在默认持久性下实测通过 1 万文档级单集合。100 万和 1,000 万 profile 已提供，但必须在目标硬件取得 PASS 报告后才能形成对应容量承诺。详细数据、复现命令和门禁见 [Document Store 容量与长稳报告](benchmarks/document-store-capacity.md)。
