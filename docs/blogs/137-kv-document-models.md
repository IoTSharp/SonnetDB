---
title: SonnetDB KV 与 JSON 文档：从 TTL 到有界查询
categories: SonnetDB,KV,JSON,数据库
draft: false
---

除了 measurement，SonnetDB 还提供持久化 KV 和 JSON 文档模型。两者都适合保存应用状态，但使用方式和一致性合同不同：KV 关注单 key 原子操作、TTL 和前缀访问；文档模型关注 JSON path、索引、局部更新和 Change Feed。

## KV 的常用操作

KV keyspace 支持 `NX`/`XX` 条件写入、`GetAndSet`、`GetAndDelete`、`PutMany`、TTL、前缀扫描以及有界游标。一个典型的缓存写入会先限制 keyspace，再设置过期时间：

```text
keyspace = telemetry-cache
put device:line-01:last-seen = 2026-10-05T10:00:00+08:00
ttl device:line-01:last-seen = 3600s
```

单 key 条件操作是合同重点。大 keyspace 的容量、远程批量读取和长期热点分布需要单独的基准，不能把本地小样例的延迟当成 Redis 兼容性承诺。SonnetDB 也没有宣称 Redis wire protocol 或跨 keyspace 事务。

## 文档的 JSON path 与索引

文档 API 支持 CRUD、JSON path 查询、局部更新、复合/多键/通配索引、TTL、validator 和 Change Feed。批量写入可以携带 request id 做幂等处理，读取则使用 continuation token 保持有界分页。

```json
{
  "_id": "machine-01",
  "site": "A",
  "metrics": { "temperature": 21.4, "rpm": 1480 },
  "labels": ["pump", "critical"]
}
```

查询时应按索引覆盖的 path 组织条件，并为分页保存 continuation token。大文档、嵌套数组、排序和向量搜索会触发不同的物化预算；预算不足时应该看到明确拒绝，而不是得到悄悄截断的结果。

## 迁移边界

SonnetDB 文档模型是自己的单节点 JSON 合同。它不承诺 MongoDB 的 BSON、wire protocol、MQL、复制、分片或官方 Driver 兼容。迁移时建议先列出实际使用的 path、索引、TTL、更新和 Change Feed，再逐项映射并保留无法映射的语义。

参考：[`kv-keyspace.md`](../kv-keyspace.md)、[`document-store.md`](../document-store.md)、[`json-query-contract.md`](../json-query-contract.md)。
