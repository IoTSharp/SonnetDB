---
title: SonnetDB 基准测试方法论：BenchmarkDotNet、统一数据与多 DB 对比
categories: SonnetDB,性能,基准测试
draft: false
---

一个可审核的性能结论，应能追溯到代码、输入、环境和原始样本。SonnetDB 的基准项目使用 BenchmarkDotNet，并提供写入、查询、聚合、Compaction 与多模型读取入口。框架减少计时误差，但不能自动保证工作负载等价或结果正确。

## 先冻结输入与执行边界

记录 commit、.NET 与数据库版本、CPU、内存、磁盘、操作系统和容器资源。数据集记录时间分布、设备/series 数、字段数、随机种子和维度，区分“行”“点”“字段值”。

嵌入式与服务端分开：前者走进程内 API，后者包含认证、网络、解析和传输。持久化合同、flush、批大小和客户端缓存也要写入报告。

## 从定向入口开始

在依赖服务已经配置的前提下，可以使用：

```bash
dotnet run -c Release --project tests/SonnetDB.Benchmarks -- --filter '*Insert*'
dotnet run -c Release --project tests/SonnetDB.Benchmarks -- --filter '*Query*'
dotnet run -c Release --project tests/SonnetDB.Benchmarks -- --filter '*Aggregate*'
```

仓库的环境编排入口还可以启动所需外部服务，但运行前应确认它将创建的容器、卷和数据目录。设定总超时与取消方式，只回收本次创建且不再需要的对象。

本文没有执行这些基准命令。缺失服务、skip 与失败都应明确记录，不能拿部分成功表替代完整比较。

## 正确性先于速度

写后核验条数、字段与持久化恢复；范围查询核验半开边界、缺值与完整消费；聚合核验桶边界和统计值。HNSW 延迟之外还要测 Recall@K，并保留精确真值。

避免每轮向同一个目录累计数据后仍假设规模不变。Setup、计时范围和 Cleanup 应保持清晰，参考代码最好直接使用仓库已有实现，而不是发布无法编译的伪 API。

## 统计值与资源报告

报告保留原始迭代、均值、波动和置信区间，注明预热策略。BenchmarkDotNet 的托管分配只覆盖被测 .NET 路径，不能代表另一个数据库进程的内存。

框架分位列可能来自迭代均值，不能直接解释为单次请求 P95/P99。当前多模型延迟证据入口采用逐次请求计时，并保存可复算样本；其 quick 模式仍属于本机 smoke，不是固定硬件生产门禁。

## 历史摘要与新结果

README 的历史数字有助于追踪变化，但本次未找到对应旧原始完整报告，因此这里不重新发布倍数排名。新版本应保存新报告、命令、输入哈希和失败记录，并把本机 smoke、真实服务、固定硬件和长期回归证据分开。

参考：[基准项目](https://github.com/IoTSharp/SonnetDB/blob/main/tests/SonnetDB.Benchmarks/README.md)、[多库对比说明](https://github.com/IoTSharp/SonnetDB/blob/main/tests/SonnetDB.Benchmarks/Benchmarks/DATABASE_COMPARISON_BENCHMARK_README.md)、[4.0.0 正式发行](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。
