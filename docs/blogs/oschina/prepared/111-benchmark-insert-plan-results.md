---
title: 写入基准通稿：SonnetDB 在嵌入式与服务端两条路径上的吞吐对比
categories: SonnetDB,性能,基准测试
draft: false
---

# 写入基准通稿：SonnetDB 在嵌入式与服务端两条路径上的吞吐对比

写入吞吐只有放在明确的接口、数据量和持久化条件下才有意义。本文介绍仓库现有基准的组织方式和复现方法。本次整理未取得旧稿数字所对应的完整原始报告，因此不再把历史吞吐、倍数或内存数字作为当前结果发布；本文也没有重新执行基准。

## 两条路径分别回答什么问题

嵌入式路径在进程内调用数据库 API，适合观察数据构建、WAL、内存表和落盘的成本。`InsertBenchmark` 中的百万点写入使用 `WriteMany`，随后调用 `FlushNow`；每次迭代重建数据目录。它不是只测一个不等待落盘的函数调用。

服务端路径还包括客户端编码、网络、鉴权、服务端解析和响应。SQL Batch、Line Protocol、JSON 和 Bulk 等入口有各自的数据表示与批次合同。把其中一个入口的结果称为“SonnetDB 总吞吐”，会隐藏这些差别。

先明确计量单位。一行中有多个 FIELD 时，行数、数据点数和字段值数可能不同；报告必须说明“点/秒”的点究竟指什么。BenchmarkDotNet 的客户端分配量也不能替代服务端进程的峰值内存。

## 复现入口

在仓库根目录、独立的测试数据目录和可控负载环境中运行：

```bash
dotnet run -c Release --project tests/SonnetDB.Benchmarks -- --filter '*Insert*'
dotnet run -c Release --project tests/SonnetDB.Benchmarks -- --filter '*ServerInsert*'
```

这是执行示例，本文未运行。正式测试应设置总超时与取消方式，记录由本次测试启动的进程，并在结束后回收这些进程和本次专属临时目录。不要用按名称终止进程或清空共享临时目录的方式清理环境。

## 一份可比较的报告应该保存什么

至少保存提交号、运行时、操作系统、CPU、内存、磁盘、数据生成规则、批量大小、并发、接口及原始日志。还要列出 WAL、flush、持久性和响应成功的定义。吞吐计时应覆盖选定合同的完整过程；数据生成和目录重置是否计入也要写清。

服务端测试应保存请求数、实际写入数、错误数和服务端资源采样。重试后的成功不能覆盖首次失败，跳过或不可连接也不能计作成功样本。对照数据库必须采用可解释的持久性和批量设置，而不是仅让参数名称相同。

仓库文档曾记录交替执行的 AB/BA 对照设计。这种设计有助于观察运行顺序和环境漂移，但本文未取得对应完整日志，不能据此重申旧数字或性能排名。新的结论应链接可下载的原始报告，并先验证写入后的行数、字段和恢复结果。

有了这些条件，“嵌入式更适合什么负载”“HTTP 多付出了多少成本”才成为可以复核的问题。

参考：[基准测试说明](https://github.com/IoTSharp/SonnetDB/blob/main/tests/SonnetDB.Benchmarks/README.md)、[嵌入式写入基准](https://github.com/IoTSharp/SonnetDB/blob/main/tests/SonnetDB.Benchmarks/Benchmarks/InsertBenchmark.cs)。
