---
title: PID 基准通稿：控制律、时间桶控制与自动整定的 SQL 性能
categories: SonnetDB,PID,基准测试
draft: false
---

# PID 基准通稿：控制律、时间桶控制与自动整定的 SQL 性能

仓库现有 PID 基准使用五万条合成反应器阶跃响应，写入后执行 flush，并关闭该基准环境的 compaction 和 retention，以减少背景任务干扰。本次未取得旧稿性能数字的完整原始报告，因此只介绍实现合同和测试方法；本文没有执行这些 SQL 或基准。

## 三种输出不能当成同一种查询

`pid_series` 输出逐样本控制序列；时间桶内的 `pid` 输出对应桶的控制结果；`pid_estimate` 返回整定参数与相关信息。它们的返回规模和计算目标不同，报告应分别描述。

以下数据仅用于展示 SQL 形状，不代表真实设备或推荐整定参数：

```sql
CREATE MEASUREMENT reactor (device TAG, temperature FIELD FLOAT);
INSERT INTO reactor (time, device, temperature) VALUES
  (1700000000000, 'demo-01', 20.0),
  (1700000001000, 'demo-01', 20.0),
  (1700000002000, 'demo-01', 22.0),
  (1700000003000, 'demo-01', 25.0),
  (1700000004000, 'demo-01', 29.0),
  (1700000005000, 'demo-01', 33.0),
  (1700000006000, 'demo-01', 36.0),
  (1700000007000, 'demo-01', 38.0),
  (1700000008000, 'demo-01', 39.0),
  (1700000009000, 'demo-01', 40.0),
  (1700000010000, 'demo-01', 40.0),
  (1700000011000, 'demo-01', 40.0);

SELECT time, pid_series(temperature, 75.0, 0.6, 0.1, 0.05)
FROM reactor WHERE device = 'demo-01' ORDER BY time;

SELECT time, pid(temperature, 75.0, 0.6, 0.1, 0.05)
FROM reactor WHERE device = 'demo-01'
GROUP BY time(1m) ORDER BY time;

SELECT pid_estimate(temperature, 'zn', 1.0, 0.1, 0.1, NULL)
FROM reactor WHERE device = 'demo-01';

SELECT pid_estimate(temperature, 'imc', 1.0, 0.1, 0.1, 50)
FROM reactor WHERE device = 'demo-01';
```

整定要求足够样本和有效阶跃响应，包含必要的百分比穿越条件。拥有十二条数据不自动证明每种参数组合都适合这条曲线；应核查返回结果及模型前提。

## 时间和状态的边界

逐点控制通过时间戳计算以秒为单位的时间间隔。首点主要使用比例项；非正时间间隔不累加积分和微分。时间桶聚合会在每个桶重置控制器，不能当作跨桶连续维持积分状态的控制过程。

这类 SQL 适合历史数据分析、算法验证和参数评估。一次数据库查询的延迟不能证明硬件实时闭环时限、设备联锁或安全等级。将分析结果用于现场控制需要独立工程验证。

## 如何测量

```bash
dotnet run -c Release --project tests/SonnetDB.Benchmarks -- --filter '*Pid*'
```

实际运行设置独立目录、总超时与取消方式，保存提交号、硬件、参数、样本顺序、原始输出和错误。报告按逐点序列、桶结果和整定输出分别验证正确性与延迟，结束后只清理本次拥有的进程和临时目录。

其他数据库没有同名原生 PID 函数，只说明接口能力不同。与应用端计算比较时，应对齐算法、采样和数据传输，不能直接把“缺少函数”写成“性能更慢”。

参考：[PID 基准源码](https://github.com/IoTSharp/SonnetDB/blob/main/tests/SonnetDB.Benchmarks/Benchmarks/PidBenchmark.cs)、[PID 控制文档](https://github.com/IoTSharp/SonnetDB/blob/main/docs/pid-control.md)。
