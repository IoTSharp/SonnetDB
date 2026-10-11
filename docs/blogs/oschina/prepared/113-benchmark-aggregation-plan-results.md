---
title: 聚合基准通稿：60,000 ms 时间桶上的 AVG/MIN/MAX/COUNT 对比
categories: SonnetDB,性能,聚合
draft: false
---

# 聚合基准通稿：60,000 ms 时间桶上的 AVG/MIN/MAX/COUNT 对比

本文保留一分钟时间桶聚合的基准方案。本次整理未取得旧稿性能数字对应的完整原始报告，因此不发布历史毫秒数、分配量或加速倍数，也没有重新执行基准。

## 用相同语义建立数据集

以每秒一个样本、百万样本为设计，一分钟为一个桶，完整范围大约得到 16,667 个桶；首末桶是否完整取决于起始时间对齐和范围边界。这是数据集推导，不是本文测得的输出数量。

小样本可先核查计算合同：

```sql
CREATE MEASUREMENT sensor_data (host TAG, value FIELD FLOAT);
INSERT INTO sensor_data (time, host, value) VALUES
  (1700000040000, 'sensor-01', 10.0),
  (1700000041000, 'sensor-01', 20.0),
  (1700000100000, 'sensor-01', 30.0);

SELECT time, avg(value), min(value), max(value), count(value)
FROM sensor_data
WHERE host = 'sensor-01'
  AND time >= 1700000040000
  AND time < 1700000160000
GROUP BY time(60000ms)
ORDER BY time;
```

这些 SQL 是未执行的示例。计数要指明对象：`count(value)` 统计该字段存在的值；稀疏数据里的行计数和字段计数不能直接互换。空桶、缺失字段、时间单位和区间边界都应进入正确性核查。

## 合并状态比平均数更重要

分段聚合时，AVG 应通过总和与数量合并。两个分段样本数不等时，直接对两个平均数再取平均会得到错误结果。MIN/MAX 合并极值，COUNT 合并符合合同的数量。

当前引擎存在利用块统计信息的路径，但只在块覆盖范围、时间桶归属等条件满足时使用。涉及边界或 Tombstone 的情况仍可能回退读取数据点。因此不能把“部分块可用元数据聚合”写成“聚合永远不扫描数据”。

对照实现若把数据传到客户端再分组，需要明确这是客户端工作流。它与数据库内部聚合具有不同的数据传输和分配成本；在同一张表里展示时应解释差异。

## 运行与交付

```bash
dotnet run -c Release --project tests/SonnetDB.Benchmarks -- --filter '*Aggregate*'
```

正式执行时使用独立目录、明确的总超时与取消方式，记录提交号、硬件、数据和缓存条件。报告先核查桶时间、数量和各聚合值，再附延迟分布、托管分配与服务端资源采样。任务结束只清理该任务创建的进程和临时目录。

原始报告缺失时，可靠的交付是可复现方案和证据缺口。新的性能结论应绑定完整日志、结果校验和具体版本，而不是沿用旧稿摘要。

参考：[基准测试说明](https://github.com/IoTSharp/SonnetDB/blob/main/tests/SonnetDB.Benchmarks/README.md)、[聚合引擎源码](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Query/QueryEngine.cs)、[SQL 参考](https://github.com/IoTSharp/SonnetDB/blob/main/docs/sql-reference.md)。
