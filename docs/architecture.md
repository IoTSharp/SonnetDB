---
layout: default
title: "架构总览"
description: "从组件划分、写入路径、查询路径到服务端控制面，理解 SonnetDB 当前的真实系统结构。"
permalink: /architecture/
---

## 组件划分

| 组件 | 责任 |
| --- | --- |
| `SonnetDB` | 嵌入式引擎，负责 schema、写入、查询、删除、后台 flush、compaction、retention |
| `SonnetDB.Data` | ADO.NET 提供程序，统一本地和远程模式 |
| `SonnetDB.Cli` | 命令行工具，适合脚本化执行和交互式 REPL |
| `SonnetDB` | HTTP API、首次安装、认证授权、SSE、Admin UI、帮助文档 |
| `web/admin` | 产品首页、首次安装向导、登录与管理后台 |
| `docs` | JekyllNet 帮助站点源码 |

## 总体结构

```text
Application / Service / Tooling
        |
        +-- Tsdb + SqlExecutor
        +-- SndbConnection / SndbCommand
        +-- sndb CLI
        +-- HTTP / Admin UI
                |
                v
      SQL / TableDirect / Control Plane
                |
                v
      Query Engine / Auth / Registry / SSE
                |
                v
 WAL -> MemTable -> Flush -> Segment -> Compaction
```

## 写入路径

当前写入路径大致如下：

1. 通过 SQL `INSERT`、`Point.Create + WriteMany`、ADO.NET `TableDirect` 或 HTTP 批量端点进入系统
2. 写入先落到 WAL
3. 同步追加到 MemTable
4. 后台或显式触发 flush 时，将 MemTable 写成新的 immutable segment
5. Compaction 在后台合并旧 segment
6. Delete/Retention 通过 tombstone 参与查询过滤并由 compaction 消化

优点：

- 写入路径简单直接
- 崩溃恢复依赖 WAL replay
- 读写职责分离，segment 保持不可变

### 写入持久性分级

WAL 落盘强度由两个选项决定，从弱到强分三级：

| 配置 | 语义 | 进程崩溃 | 掉电 / 内核崩溃 | 代价 |
| --- | --- | --- | --- | --- |
| `FlushWalToOsOnWrite=false` | WAL 仅停留在进程内 BufferedStream，直到 segment flush / roll / dispose 才交给 OS | 丢失最近一个 flush 窗口的已确认写 | 丢失 | 最低（极限吞吐） |
| `FlushWalToOsOnWrite=true`（**默认**） | 每次写入后把 WAL 缓冲 flush 到 OS（page cache），不 fsync | **不丢**已确认写 | 可能丢 | 一次用户态→内核态拷贝 |
| `SyncWalOnEveryWrite=true` | 每批写入 fsync（group-commit 2ms 窗口批处理） | 不丢 | **不丢**已确认写 | 每批一次 fsync（写延迟最高） |

- **Flush 本身** 始终做 segment 文件 fsync + 目录 fsync（含 Windows，见 `DirectoryFsync`），并保证 segment 落盘早于 WAL 回收，因此已 flush 的数据在任何崩溃下都不丢。
- **Delete** 无条件同步 WAL（不受上表影响），保证删除不会因崩溃而"复活"。
- 需要极限写吞吐、可接受进程崩溃丢最近写的场景，显式设 `FlushWalToOsOnWrite=false`。

## 查询路径

查询侧主要由 `QueryEngine` 负责：

1. 解析 SQL 或 ADO.NET 命令
2. 根据 measurement schema 校验投影和过滤条件
3. 从 catalog 找到命中的 series
4. 合并 MemTable 与多个 segment 的候选数据
5. 应用 tombstone 过滤
6. 输出原始点结果或聚合结果

当前聚合支持：

- `count`
- `sum`
- `min`
- `max`
- `avg`
- `first`
- `last`

当前分组仅支持：

- `GROUP BY time(...)`

## 嵌入式与远程的关系

`SonnetDB.Data` 把两种运行方式统一成一套 ADO.NET API：

- 嵌入式模式：直接打开本地数据库目录
- 远程模式：通过 HTTP 调用 `SonnetDB`

切换方式主要由连接字符串决定：

```text
Data Source=./demo-data
Data Source=sonnetdb://./demo-data
Data Source=sonnetdb+http://127.0.0.1:5080/metrics;Token=...
```

这意味着：

- 应用侧代码可以尽量少改
- 本地开发可以先跑嵌入式
- 需要运维、权限和后台时再切到服务端

## 服务端控制面

`SonnetDB` 在引擎之上增加了一个控制面：

- 首次安装与 `installation.json`
- 用户、密码哈希、Token 与 `users.json`
- 数据库授权与 `grants.json`
- `/admin/` 前端管理界面
- `/help/` 文档站点
- `/v1/events` SSE 实时事件流
- `/healthz` 与 `/metrics`

服务端还通过 `TsdbRegistry` 管理多个数据库目录。

## 帮助文档与镜像

`docs/` 目录中的文档会在 Docker 构建时通过 JekyllNet 生成，并随 `SonnetDB` 一起打包到镜像中，运行时挂在 `/help`。

这让镜像本身就携带：

- 产品介绍
- SQL 文档
- API 示例
- 部署说明

## 当前设计取向

- 嵌入式优先，而不是只做远程服务
- 强调受控 schema-on-write，而不是完全 schema-less：写入可自动补 tag/field，但已有列仍做类型兼容校验
- 强调当前真实实现，而不是未来规划接口
- 数据库目录持久化优先于单文件目标
- 把帮助文档作为产品的一部分，而不是仓库外部说明
