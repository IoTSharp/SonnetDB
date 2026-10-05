---
title: SonnetDB 当前能力全景：九种原生模型与一套数据库目录
categories: SonnetDB,数据库,架构
draft: false
---

SonnetDB 早期文章主要围绕时序数据展开。现在的产品边界已经扩大：同一个数据库目录可以承载九种原生模型，并通过 SQL、HTTP、CLI、SDK 和管理工具访问。本文先给出当前能力地图，再说明哪些能力已经有入口、哪些仍然属于局部合同或 Beta，避免把规划内容误写成已发布保证。

## 九种模型

| 模型 | 适合的数据 | 当前状态 |
|---|---|---|
| 时序 | measurement、TAG/FIELD、时间点 | partial：高基数、固定硬件和长期恢复仍需独立证据 |
| 关系表 | 精确类型、约束、JOIN、事务型 DML | partial：SQL 是有界子集，不承诺完整 ANSI |
| KV | key/value、TTL、前缀与原子操作 | partial：单 key 原子合同已覆盖，大 keyspace/远程容量待证 |
| JSON 文档 | 文档 CRUD、JSON path、索引、Change Feed | partial：不承诺 MongoDB wire/BSON/分片 |
| 全文 | analyzer、词典、BM25、短语与模糊检索 | partial：远程对拍、语料质量和容量待证 |
| 向量 | VECTOR、KNN、HNSW、语义检索 | partial：精确 fallback 和有界 Top-K 已有，Recall 与固定硬件待证 |
| 对象 | Bucket、版本、Range、Multipart、校验 | partial：大文件断电恢复和固定硬件待证 |
| SonnetMQ | Topic、Producer、Consumer、ACK、Replay | partial：本地至少一次与实例快照有实现，不承诺 exactly-once 或集群 |
| Graph | 顶点、边、属性、路径和 GRAPH_TABLE | beta：结果预算和本地合同存在，生产 gate 尚未完成 |

权威能力状态见 [`fourteen-capability-evidence-index.json`](../audits/fourteen-capability-evidence-index.json)。其中 `partial` 表示实现和部分自动化证据存在，但验收边界尚未闭合；`beta` 表示有界预览合同，不等于生产发布。

## 共享的数据库边界

数据库目录是持久化和权限边界。用户、角色、Token、数据库备份和 SQL 名称绑定都围绕这个边界工作。MQ 的逻辑权限属于数据库，但其物理追加日志位于实例级 `.system/mq`；所以单库备份不能被描述成包含全部 MQ 实例恢复状态。Graph 目前也保留 Beta 标签，不能从有 Graph SQL 入口推导出容量或外部对拍已经通过。

## 一个最小的跨模型旅程

```sql
CREATE DATABASE factory;

CREATE MEASUREMENT temperature (
    device TAG,
    value FIELD FLOAT
);

INSERT INTO temperature (time, device, value)
VALUES (1735689600000, 'line-01', 21.4);

SELECT device, avg(value)
FROM temperature
GROUP BY device;
```

同一目录还可以通过文档、KV、对象和 MQ 的专用 API 管理其他数据。跨模型不意味着所有模型共享相同的查询语法或事务语义；每个模型的入口、权限和恢复合同需要分别阅读对应文档。

## 阅读和选型建议

先按模型选择数据结构，再选择嵌入式、Server、REST/Frame、ADO.NET、CLI 或管理工作台入口。需要大规模、跨架构、长期运行或真实模型质量结论时，应查看对应审计和固定硬件报告。合成样例、fixture、hash embedding 和本地快速测试可以证明代码路径，但不能替代真实语料质量、容量或生产门禁。

参考：[`data-model.md`](../data-model.md)、[`document-store.md`](../document-store.md)、[`kv-keyspace.md`](../kv-keyspace.md)、[`object-transfer-manager.md`](../object-transfer-manager.md)、[`mq-high-level-client.md`](../mq-high-level-client.md)、[`native-graph-database-roadmap.md`](../native-graph-database-roadmap.md)。
