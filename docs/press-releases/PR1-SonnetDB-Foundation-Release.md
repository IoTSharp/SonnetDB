---
title: "SonnetDB 里程碑一：高性能时序数据库引擎正式发布"
date: 2026-04-25
---

# 新闻稿

## SonnetDB 里程碑一：高性能开源时序数据库引擎正式发布
### 嵌入式优先架构，写入性能达 180 万点/秒，全面超越 InfluxDB 与 TDengine

**[城市/日期]** —— 开源时序数据库项目 SonnetDB 今日宣布达成首个重要里程碑，发布基于 C# / .NET 10 构建的高性能时序数据库引擎。该版本涵盖了从存储引擎、SQL 查询层、HTTP 服务端到完整 Web 管理后台的全栈能力，标志着 SonnetDB 已具备生产级时序数据管理能力。

### 核心亮点

**极致写入性能**：在 100 万点单序列写入基准测试中，SonnetDB 嵌入式引擎仅需 545 毫秒（约 180 万点/秒），性能为 SQLite 的 1.5 倍、InfluxDB 2.7 的 9.6 倍、TDengine 3.3 REST 接口的 80 倍。

**范围查询性能**：~10 万行范围查询仅需 6.71 毫秒，为 InfluxDB 的 61 倍、SQLite 的 6.6 倍。

**聚合查询性能**：16,667 个时间桶的聚合查询仅需 42 毫秒。

### 架构亮点

SonnetDB 采用嵌入式优先（Embedded-First）设计理念，可在进程内直接使用，无需独立部署服务进程。核心架构包含：

- **预写日志（WAL）**：追加写入 + CRC 校验 + 崩溃恢复
- **内存表（MemTable）**：写入缓冲，高效合并
- **不可变段（Segment）**：只读数据文件，支持分层压缩
- **段压缩（Compaction）**：Size-Tiered 策略自动合并小文件
- **删除与保留**：Tombstone 删除机制 + TTL 自动过期

### 语言特性

- 纯安全代码（零 unsafe 关键字），基于 Span\<T\> + MemoryMarshal 实现高性能
- 原生 AOT 兼容，服务端零反射零警告
- 递归下降 SQL 解析器，零第三方依赖

### SQL 能力

数据面：CREATE MEASUREMENT、INSERT、SELECT、DELETE、SHOW、DESCRIBE，支持 LIMIT/OFFSET/FETCH 分页、WHERE 多条件过滤、聚合函数（count/sum/avg/min/max/first/last）和 GROUP BY time 时间桶聚合。

控制面：CREATE USER、ALTER USER、DROP USER、GRANT、REVOKE、CREATE DATABASE、DROP DATABASE 及 Token 管理。

### 部署选项

- **Docker 镜像**：`iotsharp/sonnetdb:latest`（Docker Hub + ghcr.io）
- **NuGet 包**：`SonnetDB.Core`（嵌入式引擎）、`SonnetDB`（ADO.NET，命名空间 `SonnetDB.Data`）、`SonnetDB.EntityFrameworkCore`（EF Core Provider）、`SonnetDB.Cli`（命令行工具）
- **安装器**：Windows MSI、Linux DEB/RPM
- **CLI 工具**：`sndb` 命令，支持本地/远程、REPL 模式

### 关于 SonnetDB

SonnetDB 是一款开源（MIT 许可证）时序数据库，由 maikebing 创建并维护。它专为 IoT 物联网、工业控制、运维监控和实时分析场景设计。项目地址：https://github.com/maikebing/SonnetDB

### 媒体联系

- 项目主页：https://github.com/maikebing/SonnetDB
- 文档地址：https://github.com/maikebing/SonnetDB/docs

# # #

*关于性能数据：测试环境为 i9-13900HX / Windows 11 / .NET 10.0.6 / Docker WSL2。具体数据可能因硬件和配置而异。*
