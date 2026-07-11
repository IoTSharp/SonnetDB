---
layout: default
title: Document Store 能力矩阵
description: SonnetDB Document Store 的能力、MongoDB-like 边界、不支持项、推荐规模和管理入口。
permalink: /document-store/
---

# Document Store 能力矩阵

SonnetDB Document Store 面向嵌入式、边缘和单节点工业应用中的 JSON 文档负载。对外定位是 **MongoDB-like document workloads**：常用文档语义有参考 parity，但 SonnetDB 不实现 MongoDB wire protocol、BSON command 或官方 MongoDB Driver 直连。

## 能力

| 领域 | 当前能力 | 边界 |
|---|---|---|
| 集合与 CRUD | create/drop collection、insert one/many、find one/many、replace、delete one/many | ID 是 SonnetDB 字符串 ID，不是 MongoDB `ObjectId` 自动生成语义 |
| 查询 | `_id`/JSON path；`eq/ne/gt/gte/lt/lte/in/nin/exists/contains`；AND/OR/NOT | 不承诺 MongoDB MQL 全集、正则、`$expr`、`$where` 或 BSON 类型排序完全一致 |
| 投影与排序 | 多字段 projection、稳定排序、limit/skip | 复杂数组投影和 MongoDB expression projection 不在当前契约内 |
| 分页 | 有过期时间和 snapshot version 的 continuation token | 不是 MongoDB cursor/wire protocol；token 只能交回 SonnetDB API |
| 局部更新 | `$set/$unset/$inc/$min/$max/$rename/$push/$pull/$addToSet/$currentDate`、upsert、multi | 不支持 MongoDB 全部数组 positional/operator 语法 |
| 索引 | 单字段/复合、unique、sparse、partial、TTL；在线 rebuild；planner/explain | 不声明 MongoDB multikey、hashed、wildcard、2d/2dsphere 索引兼容 |
| 聚合 | `$match/$project/$group/$sort/$limit/$skip/$unwind/$count/$distinct`；count/sum/avg/min/max/first/last/distinct | 不是完整 MongoDB aggregation pipeline；不支持 `$lookup` 等跨集合 stage |
| Schema governance | required/type/range/enum/pattern validator；`error`/`warn` action | 规则是 SonnetDB validator DTO，不是完整 MongoDB JSON Schema 方言 |
| 批量写 | ordered 整批预检；unordered 有效项提交；稳定 per-item error code | 不等同于多文档 ACID transaction 或 MongoDB session transaction |
| 搜索 | Document full-text index、持久化向量索引、Hybrid Search | 使用 SonnetDB 自有 SQL/API，不映射 Atlas Search 或 MongoDB vector command |
| 变更读取 | SonnetDB change feed，带 sequence、保留期与 resume 位置 | 不是 MongoDB change stream/wire resume token |
| 持久性 | 单文档 mutation 统一维护主数据、path/fulltext/vector index；重开时可校验/重建派生索引 | 单节点，无 replica set、sharding、跨节点 failover |
| 备份恢复 | 多模型一致性备份、校验、离线恢复、恢复后索引校验 | 不读取 `mongodump` 二进制归档；迁移走 JSONL/NDJSON |

## 接入入口

- .NET：`SonnetDB.Data.Documents.SndbDocumentClient`，嵌入式和远程使用同一 API。
- HTTP：`/v1/db/{db}/documents/{collection}` 下的私有 JSON API。
- SQL：`CREATE DOCUMENT COLLECTION`、`CREATE INDEX`、`ALTER DOCUMENT COLLECTION ... SET VALIDATOR` 及 JSON path 查询。
- 管理面：Web Admin 的统一 Explorer 选择 Collections，进入 Document Explorer；SonnetDB Studio 桌面复用同一管理面。当前 VS Code 扩展只承担明确的只读/开发者子集，不宣称完整 Document 编辑面。

读操作要求数据库 `Read` 权限，写入、导入、validator 和索引维护要求 `Write` 或对应管理权限。Studio 的导入、rebuild 和 validator 保存必须经过 preview/dry-run/confirm 中至少一种防误操作步骤。

## 不支持项

当前明确不支持：

- MongoDB wire protocol、BSON command、MongoDB URI 直连和官方 MongoDB Driver 复用。
- replica set、sharding、分布式事务、读偏好、write concern 多副本确认。
- MongoDB session/transaction、change stream、oplog、Atlas Search 管理协议。
- 完整 MQL、完整 aggregation pipeline、全部 BSON 类型和 BSON-specific comparison semantics。
- 自动把现有 MongoDB index/validator/user/role 元数据原样导入。

这些差异不是临时隐藏的兼容项。应用迁移前必须按 [MongoDB-like 迁移指南](mongodb-migration.md) 改造客户端入口并逐项验收语义。

## 规模建议

当前完整发布验收在默认持久性下实测通过 1 万文档级单集合。100 万和 1,000 万 profile 已提供，但必须在目标硬件取得 PASS 报告后才能形成对应容量承诺。详细数据、复现命令和门禁见 [Document Store 容量与长稳报告](benchmarks/document-store-capacity.md)。
