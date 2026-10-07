---
title: 查询基准通稿：100 万点数据集上的时间范围检索对比
categories: SonnetDB,性能,查询
draft: false
---

# 查询基准通稿：100 万点数据集上的时间范围检索对比

标题中的“100 万点”指基准设计的数据集规模，不代表本文取得了新的百万点实测结果。本次整理未取得旧稿延迟和内存数字所对应的完整原始报告，因此只保留可核查的方法、查询合同和复现入口。文中的 SQL 和命令没有在本次发布准备中执行。

## 先把范围查询定义清楚

`QueryBenchmark` 的准备阶段写入数据并执行 flush，使查询面对持久化段。一个“最近 10%”范围可以在百万样本设计中覆盖约十万样本，但实际返回数还受 series、稀疏字段和边界影响，必须通过结果计数验证。

下面的小样本用于说明半开时间区间：

```sql
CREATE MEASUREMENT sensor_data (host TAG, value FIELD FLOAT);
INSERT INTO sensor_data (time, host, value) VALUES
  (1700000000000, 'sensor-01', 21.0),
  (1700000001000, 'sensor-01', 21.5),
  (1700000002000, 'sensor-01', 22.0);

SELECT time, value FROM sensor_data
WHERE host = 'sensor-01'
  AND time >= 1700000000000
  AND time < 1700000002000
ORDER BY time;
```

这里的上界不包含最后一个时间戳。性能对照应采用相同上下界、字段、series 条件和排序要求，并核查首末时间戳及完整结果。

## 引擎、SQL 和 HTTP 要分开测

直接 `QueryEngine` 调用测量引擎路径；SQL 路径增加解析、绑定和结果构建；HTTP 路径再增加网络、编码和服务端治理。三者都值得测，但不能混用列名或用一种路径的数字代表另一种。

对于流式 NDJSON，“收到第一行”和“读完全部结果”是两个指标。完整范围检索的吞吐应消费全部响应，并检查取消、断连和服务端错误。`LIMIT` 限制输出行数，不自动证明所有查询形状都只扫描有限输入或使用固定内存。

## 复现和报告

```bash
dotnet run -c Release --project tests/SonnetDB.Benchmarks -- --filter '*Query*'
```

运行前建立独立数据目录，记录提交号、硬件、缓存条件和数据分布，设置总超时和取消方式。冷热缓存应采用明确且可重复的定义；不要把首次读取与多次热缓存读取混成一个数字。

报告同时给出匹配行数、返回字节数、延迟分布和资源采样，并附原始输出。客户端托管分配量不能解释整个服务端的内存。并发测试还需记录并发数、请求失败和取消率。测试结束只回收由该测试创建的进程与临时目录。

比较之前先验证结果一致。少返回一半数据、少排序或只等待第一行，都可能看起来更快，却回答了另一个问题。当前本文不提供跨数据库排名；可复核报告取得后，再给出对应负载下的结论。

参考：[查询基准源码](https://github.com/IoTSharp/SonnetDB/blob/main/tests/SonnetDB.Benchmarks/Benchmarks/QueryBenchmark.cs)、[SQL 参考](https://github.com/IoTSharp/SonnetDB/blob/main/docs/sql-reference.md)。
