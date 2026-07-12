---
layout: default
title: "生态接入与 Profile 边界"
description: "SonnetDB 嵌入式、远程、EF Core、缓存和对象桶通用接入样例，以及上层 Profile 的职责边界。"
permalink: /ecosystem-integration/
---

## 通用连接边界

SonnetDB 的 .NET 生态入口统一消费 `SonnetDB.Data` 连接字符串：

| 模式 | 连接字符串 | 适用范围 |
| --- | --- | --- |
| 嵌入式 | `Data Source=./data/app` | 单进程、边缘节点、桌面或本地工具 |
| 远程 | `Data Source=sonnetdb+http://127.0.0.1:5080/app;Token=<token>` | 独立 Server、多进程或受控网络访问 |

ADO.NET、EF Core、KV 客户端、缓存 Provider 和对象桶客户端都应使用公开连接字符串和客户端契约，不应直接解析 SonnetDB 数据目录中的 WAL、segment、KV 或对象文件。

## 可运行样例

[`samples/SonnetDB.EcosystemSample`](../samples/SonnetDB.EcosystemSample/README.md) 是可编译的端到端样例，覆盖：

- `UseSonnetDB(...)` 的 EF Core 建表和 CRUD；
- `SndbConnection` 的 ADO.NET 查询；
- `AddDistributedSonnetDBCache(...)` 的 KV、namespace 和 TTL；
- `SndbObjectStorageClient` 的 bucket、object 上传和列表。

嵌入式运行：

```powershell
dotnet run --project samples/SonnetDB.EcosystemSample
```

切到远程 Server 只替换连接字符串：

```powershell
$env:SONNETDB_CONNECTION='Data Source=sonnetdb+http://127.0.0.1:5080/app;Token=<token>'
dotnet run --project samples/SonnetDB.EcosystemSample
```

## EF Core

应用通过 `SonnetDB.EntityFrameworkCore` 使用 `UseSonnetDB(connectionString)`。生产升级应使用 EF migrations 和 migrations history；`EnsureCreated` 仅用于样例、测试或一次性本地数据库。

Provider 的通用承诺包括关系 DDL、参数化查询、轻事务、迁移历史和受支持 LINQ 翻译。某个上层应用的实体兼容矩阵、迁移顺序和回滚窗口不属于 Provider 契约。

## KV 与缓存

应用可以直接使用 `SndbKvClient`，也可以选择：

- `SonnetDB.Caching.Distributed`：`IDistributedCache`；
- `SonnetDB.Caching.EasyCaching`：EasyCaching Provider。

两种 Provider 都复用 SonnetDB KV 的 namespace、TTL、惰性过期和后台清理语义。缓存只保存可重建数据；不能把缓存切换等同于关系主数据迁移。

## 对象桶

`SndbObjectStorageClient` 同时支持嵌入式和远程模式，公开 bucket/object、range read、multipart、版本、生命周期、retention、legal hold、审计和 quota 契约。

对象桶是 SonnetDB 通用存储能力，不承诺 AWS S3 wire protocol 完整兼容。应用迁移应使用客户端 API 或通用迁移包，不应复制对象桶内部 keyspace。

## 迁移与校验

`MigrationService` 提供 `Export`、`Scan`、`Checksum`、`ImportDryRun` 和 `Import` 原语。迁移包复用一致性备份 manifest 和逐文件 SHA-256，适合同一兼容数据库格式间的离线迁移、校验和回滚准备。

该迁移包不是跨数据库产品的逻辑转换格式。跨产品字段映射、双写比对和业务回滚应由调用方使用公开 SQL、KV、对象或文档 API 实现。

## Profile 职责边界

SonnetDB 仓库负责：

- 嵌入式和远程公开客户端；
- ADO.NET 与 EF Core Provider；
- KV/cache Provider；
- 对象桶 API；
- 通用迁移、校验、备份、恢复和数据库级可靠性报告。

上层项目仓库负责：

- 具体 Profile 名称和配置组合；
- 租户、权限、业务审计和人工确认；
- 灰度范围、双写周期、业务校验、切流和回滚；
- 上层服务 SLA、容量规划和跨项目兼容矩阵。

IoTSharp 的对应内容在 IoTSharp `ROADMAP.md` 的 RD-10 和 `docs/docs/operations/sonnetdb-compat-matrix.md` 维护，SonnetDB 不复制这些上层计划。
