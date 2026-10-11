# 窗口函数执行器：从 object 数组走向 typed streaming

窗口函数需要按 series 的时间顺序推进。通用 `Compute(long[],FieldValue?[]) -> object?[]` 容易产生输入数组、完整输出数组和数值装箱，SonnetDB 当前通过 typed 输出与逐行状态机降低其中一部分成本。

本文依据当前窗口接口、DoubleWindowEvaluatorBase 与 SelectExecutor 整理，正式 4.0.0 与 main 的实现边界分别核对。这里的 streaming 是窗口计算路径，不是所有 SQL、HTTP 和结果对象都变成恒定内存。

## 三种兼容接口

| 接口 | 输出方式 | 作用 |
| --- | --- | --- |
| `IWindowEvaluator` | `object?[]` | 保留通用兼容路径 |
| `IWindowDoubleEvaluator` | 数值数组与有效位 | 延后数值装箱 |
| `IWindowStreamingEvaluator` | `CreateState()` | 为独立 series 创建状态机 |

`IWindowState.Update(timestamp,value)` 返回 `WindowStateOutput`，区分 NULL、Double 与兼容 Object。数值状态不必先装箱，最终进入通用 SQL 行时仍可能调用 `ToObject()`。

## 逐行推进不等于消除所有材料化

SelectExecutor 检查当前所有窗口 evaluator 是否支持 streaming；存在未迁移 evaluator 时，保留材料化路径。每个 series 使用自己的状态，不能把上一设备的 EWMA 状态带入下一设备。

当前 raw streaming 路径仍使用时间集合、字段 lookup 和最终 `List` 行结果。因此它减少窗口输入/输出数组和部分计算开销，但没有证明整个查询对任意行数使用常量内存。时间范围、行数与读取预算仍需要限制。

## 小样本 SQL

下面在新的演示数据库中使用一条 series：

```sql
CREATE MEASUREMENT cpu (host TAG, usage FIELD FLOAT);
INSERT INTO cpu (time, host, usage) VALUES
    (1710000000000, 'edge-1', 1.0),
    (1710000001000, 'edge-1', 2.0),
    (1710000002000, 'edge-1', 3.0),
    (1710000003000, 'edge-1', 4.0);
SELECT time, moving_average(usage, 3) AS ma,
       running_sum(usage) AS total
FROM cpu WHERE host = 'edge-1' ORDER BY time LIMIT 10;
```

前三点窗口的 moving average，在窗口尚未满足时输出 NULL；它的暖启动规则不能套给 EWMA、running_sum、PID 或 anomaly。每个函数的 NULL、缺失值与初始化规则应分别对照实现。

typed 和 streaming 路径必须与兼容路径逐行一致，包括值、NULL 和时间对齐。加入第二条 series 时，还要验证状态隔离。

## Span 批量计算的意义

DoubleWindowEvaluatorBase 内部通过 `ReadOnlySpan`/`Span` 处理输入与输出，并把兼容 API 转接到相同状态逻辑。具体函数可以使用环形缓冲或专用实现，但不能把所有函数都宣称成使用栈上小窗口。

Span 减少切片与临时对象成本，却仍需要正确管理状态、数据寿命和目标长度。它不是 `unsafe` 的理由，也不会使全窗口统计算法自动变为因果在线算法。

## 用分配基准与语义测试分别验证

仓库提供 `WindowEvaluatorAllocationBenchmark` 比较不同输出路径。基准应固定点数、函数参数、缺失值比例和运行环境，同时报告时间、分配与结果校验。

只有结构检查时，应描述“减少哪些中间对象”，不要给出未经本次运行验证的吞吐倍数。PID、异常检测等未支持相同路径的函数继续按既有接口执行，不能为了统一宣传而忽略 fallback。

参考：[状态接口](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Query/Functions/IWindowState.cs)、[数值基类](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Query/Functions/Window/DoubleWindowEvaluatorBase.cs)、[SQL 执行器](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Sql/Execution/SelectExecutor.cs)、[分配基准](https://github.com/IoTSharp/SonnetDB/blob/main/tests/SonnetDB.Benchmarks/Benchmarks/WindowEvaluatorAllocationBenchmark.cs)。
