# M43 #398 DBDB.io / DB-Engines 资料包

**状态：`DRAFT_NOT_SUBMITTED`；核对日期：2026-09-30。** 本资料包尚未向任何外部目录提交，不表示已收录、审核通过或进入排名。#399/#400 的提交记录与维护反馈须在实际发生后另行记录。

## 基础字段 / Common Fields

| 字段 / Field | 内容 / Value |
|---|---|
| 名称 / Name | SonnetDB |
| 仓库 / Source | https://github.com/IoTSharp/SonnetDB |
| 文档 / Documentation | 仓库 README、README.en.md 和 docs；[English README](../README.en.md)、[中文 README](../README.md) |
| 许可证 / License | MIT；[LICENSE](../LICENSE) |
| 实现语言 / Implementation | C# / .NET 10 |
| 部署 / Deployment | 进程内嵌入式库、独立服务 / Embedded library and standalone server |
| 产品分类 / Category | 多模型数据引擎 / Multi-model data engine |
| 主要接口 / Interfaces | 实用 SQL 子集、.NET API / ADO.NET、REST、HTTP/2 Frame、CLI 与管理工具 / Practical SQL subset, .NET API / ADO.NET, REST, HTTP/2 Frame, CLI and management tools |
| 版本 / Version | 提交时绑定真实 release tag 和 commit；不由资料草稿推断最新稳定版 / Bind an actual release tag and commit at submission time |
| 成熟度来源 / Maturity source | [十四能力证据索引](audits/fourteen-capability-evidence-index.json)、[公开状态定义](capability-maturity.md) |
| 发布证据 / Release evidence | [M43 汇总工具](m43-release-evidence.md)；固定硬件、安装、真实模型和长稳按实际结果列 `NOT_READY` / `DEFERRED` |

资料可按目标目录的实际字段映射；本文件不推断外部目录尚未核对的表单、提交渠道或必填项。

## 中文简介

SonnetDB 是使用 C# / .NET 10 实现的多模型数据引擎，提供九种原生数据模型：时序、关系表、持久键值、JSON 文档、全文检索、向量检索、对象/内容存储、持久日志/消息队列，以及原生属性图（Graph Beta）。模型保留各自语义，并通过 SQL、标准 API 和管理工具接入同一引擎。它可嵌入应用，也可部署为独立服务。

空间/轨迹、流处理/订阅、CDC/边缘同步、多模态 AI/RAG，以及治理/备份/恢复/观测构成另外五项平台能力。当前十四项能力的整体成熟度均有未闭环边界；Graph 为 `beta`，其它项目为 `partial`。本地合同与自动化测试不能替代固定目标硬件、真实模型质量、干净安装、外部数据库对拍或 168 小时稳定性证据。

## English Description

SonnetDB is a multi-model data engine implemented in C# on .NET 10. Its nine native models are time series, relational tables, persistent key-value, JSON documents, full-text search, vector search, object/content storage, persistent logs/message queues, and a native property graph (Graph Beta). Each model keeps its own semantics and is accessible through SQL, standard APIs and management tools. SonnetDB can run as an embedded library or a standalone server.

Five platform capabilities cover spatial/trajectory workloads, streaming/subscriptions, CDC/edge synchronization, multimodal AI/RAG, and governance/backup/recovery/observability. Overall acceptance remains partial for thirteen capabilities; the graph is beta. Local contracts and automated tests do not establish fixed-hardware capacity, real-model quality, clean installation, external database parity or 168-hour production stability.

## 十四能力映射 / Capability Mapping

下表使用证据索引的稳定 ID、分类和状态。分类与状态的中英文含义由成熟度文档统一定义。

| ID | 中文名称 | English Name | Category | Maturity |
|---|---|---|---|---|
| `timeseries` | 时序数据库 | Time-series database | `native_model` | `partial` |
| `relational` | 关系 SQL | Relational SQL | `native_model` | `partial` |
| `kv` | 持久键值 | Persistent key-value | `native_model` | `partial` |
| `document` | JSON 文档 | JSON document store | `native_model` | `partial` |
| `fulltext` | 全文搜索 | Full-text search | `native_model` | `partial` |
| `vector` | 向量检索 | Vector search | `native_model` | `partial` |
| `object` | 对象/内容存储 | Object and content storage | `native_model` | `partial` |
| `mq` | 持久日志/消息队列 | Persistent log and message queue | `native_model` | `partial` |
| `graph` | 原生属性图 | Native property graph | `native_model` | `beta` |
| `geo` | 空间、地理与轨迹 | Spatial, geography and trajectory | `cross_model` | `partial` |
| `streaming` | 流处理与订阅 | Stream processing and subscriptions | `platform_capability` | `partial` |
| `cdc` | CDC、边缘同步与复制 | CDC, edge synchronization and replication | `platform_capability` | `partial` |
| `ai-rag` | 多模态 AI 与 RAG | Multimodal AI and RAG | `platform_capability` | `partial` |
| `governance` | 治理、备份、恢复、观测与安全 | Governance, backup, recovery, observability and security | `platform_capability` | `partial` |

## 兼容与证明边界 / Compatibility and Evidence Boundaries

| 中文 | English |
|---|---|
| SQL 为实用子集，不宣称完整 ANSI SQL。 | SQL is a practical subset, not complete ANSI SQL conformance. |
| Document 为 MongoDB-like 单机子集，不提供 MongoDB wire compatibility 承诺。 | Documents implement a documented MongoDB-like single-node subset; MongoDB wire compatibility is not claimed. |
| 对象 API 为 S3 风格，不宣称完整 S3/SigV4 兼容。 | Object APIs are S3-style; full S3/SigV4 compatibility is not claimed. |
| Graph Beta 不宣称完整 GQL、Cypher、Neo4j 兼容或 Production gate 已通过。 | Graph Beta does not imply full GQL, Cypher or Neo4j compatibility, or a passed production gate. |
| MQ/订阅为有界本地持久合同，不承诺分布式复制或 exactly-once。 | MQ/subscription contracts are bounded and local; distributed replication and exactly-once delivery are not claimed. |
| CDC 不等于已完成远程复制拓扑、冲突处理或 schema migration。 | CDC does not imply a completed remote replication topology, conflict policy or schema migration. |
| 单库备份不包含实例级 `.system/mq`；恢复不能冒称九模型原子快照。 | A single-database backup excludes instance-scoped `.system/mq`; it is not an atomic nine-model snapshot. |
| tiny fixture、hash fallback 和 mock 不计作真实模型语义、成本或 Recall@K 证据。 | Tiny fixtures, hash fallback and mocks do not establish real-model semantics, cost or Recall@K. |

## 外部动作记录 / External Actions

| 项目 / Target | 状态 / Status | 提交日期 / Submitted At | 外部结果 / External Result |
|---|---|---|---|
| DBDB.io（#399） | `NOT_SUBMITTED` | 无 / None | 无 / None |
| DB-Engines（#400） | `NOT_SUBMITTED` | 无 / None | 无 / None |

正式提交前应把本资料包、能力索引、真实 release tag/commit 和 verifier 报告放在同一个可审阅版本中。提交后的回执、维护者反馈、收录页面和排名分别记录；任何一项都不能由本地文档更新推断。
