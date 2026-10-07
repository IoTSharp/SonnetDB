---
title: 性能对比：SonnetDB vs InfluxDB vs TDengine vs SQLite
categories: SonnetDB,性能,基准测试
draft: false
---

对比 SonnetDB、InfluxDB、TDengine 和 SQLite，首先要确定比较的是哪条调用路径。嵌入式写入、HTTP Server 批量写入、数据库进程内存和客户端托管分配，是四种不同的测量范围。

本篇保留性能选型主题，修订旧稿中没有可复核原始材料支撑的排名、百万点吞吐、空闲内存和二进制体积表。它们不能作为正式 [SonnetDB 4.0.0](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0) 的成绩，也不能据此认定任何数据库在所有场景更快。

## 先固定调用路径

仓库基准工程覆盖以下接入方式：

| 对象 | 基准中的主要接入方式 | 需要额外控制的变量 |
| --- | --- | --- |
| SonnetDB 引擎 | 进程内 WAL/MemTable/Segment | WAL 同步、flush、批大小、查询结果物化 |
| SonnetDB Server | HTTP JSON 批量端点 | 网络、认证、JSON 编解码、服务进程资源 |
| SQLite | 文件模式、WAL、事务批量提交 | 事务边界、同步级别、索引、SQL 计划 |
| InfluxDB 2.x | Line Protocol 与 Flux | 版本、批次、服务端配置、查询形状 |
| TDengine 3.x | REST 与超级表 | 版本、schema、批次、服务端配置 |

SQLite 是通用关系数据库，另两者是独立服务。可以比较端到端任务，但应把这些架构差异公开，而不是将进程内调用和远程服务开销隐藏在同一张排名表中。许可证和产品能力也随版本变化，选型时分别核对上游发布资料。

## 写入基准要说明写的是什么

至少记录数据点、行和字段值三个口径。一个设备一行包含多个 FIELD 时，“一百万行”和“一百万字段值”并不等价。

固定设备数、TAG 基数、字段类型、采样频率、单批数量和并发，再说明计时是否包含建库、schema 创建、网络与最终 flush。SonnetDB 的 `FlushWalToOsOnWrite` 与 `SyncWalOnEveryWrite` 会改变成本和持久性，不应拿最弱持久性配置与另一边的同步事务直接比较。

## 查询基准要核对相同结果

可以分别测量时间范围读取、TAG 过滤、聚合和时间窗口查询。计时前先对拍返回行、时间边界、NULL、列类型和聚合结果；读取前几行与完整读取结果集也须分开记录。

冷缓存与热缓存、索引建立成本、磁盘上 Segment 数量和后台维护状态都会改变结果。内存应同时记录数据库服务进程的 RSS 与客户端分配，单独的 `Allocated` 指标不是 Server 总内存。

## 使用仓库基准入口

以下命令来自基准工程说明；它会准备外部数据库容器并运行对应测试，需要 Docker 与 .NET 环境：

```bash
# 仅运行写入场景
dotnet run --project eng/benchmarks/run-benchmarks/run-benchmarks.csproj -- --filter '*Insert*'

# 仅运行范围查询等查询场景
dotnet run --project eng/benchmarks/run-benchmarks/run-benchmarks.csproj -- --filter '*Query*'

# 仅运行聚合场景
dotnet run --project eng/benchmarks/run-benchmarks/run-benchmarks.csproj -- --filter '*Aggregate*'
```

保存实际 commit、各数据库版本、CPU/内存/OS、配置、命令、原始 CSV/日志和失败样本。准备环境失败或参考服务未执行，应如实记录，不能生成“通过”的对比数字。本文整理时未重新运行这些基准。

## 历史 Server 对比的引用边界

基准 README 还登记了一组 2026-05-06 的 SonnetDB Server 与 IoTDB Server 测试，采用两边都走 Server 的路径，并记录设备、字段、轮次和执行顺序。这有助于理解正确的对比范围，但其版本和机器属于历史运行，不能自动代表 4.0.0，也不能外推到 InfluxDB、TDengine 或 SQLite。引用数值前应取得并重新核验该运行的原始材料。

选择 SonnetDB 的理由可以是嵌入式部署、.NET 接入或多模型协作，性能结论则必须来自自己的目标工作负载。固定硬件、恢复、长期稳定与真实模型质量还需要独立报告。

参考：[基准工程](https://github.com/IoTSharp/SonnetDB/blob/main/tests/SonnetDB.Benchmarks/README.md)、[基准运行器](https://github.com/IoTSharp/SonnetDB/tree/main/eng/benchmarks)、[持久性选项](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Engine/TsdbOptions.cs)和[能力成熟度](https://github.com/IoTSharp/SonnetDB/blob/main/docs/capability-maturity.md)。
