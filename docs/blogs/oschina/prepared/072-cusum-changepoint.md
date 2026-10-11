---
title: 结构变化检测：CUSUM 与 Changepoint 分析
categories: SonnetDB,变点检测,时序
draft: false
---

孤立尖峰与持续偏移是不同现象。CUSUM 将偏离参考均值的变化累计起来，帮助发现持续漂移。SonnetDB 的 `changepoint(field, 'cusum', threshold[, drift])` 是逐行输出布尔标记的窗口函数，不采用旧稿显式 time 和命名参数的写法。

## 准备变化序列

```sql
CREATE MEASUREMENT reactor_data (reactor_id TAG, temperature FIELD FLOAT);
INSERT INTO reactor_data (time, reactor_id, temperature) VALUES
    (1700000000000, 'R-101', 20.0),
    (1700000001000, 'R-101', 20.2),
    (1700000002000, 'R-101', 19.8),
    (1700000003000, 'R-101', 20.1),
    (1700000004000, 'R-101', 19.9),
    (1700000005000, 'R-101', 25.0),
    (1700000006000, 'R-101', 25.2),
    (1700000007000, 'R-101', 25.1);

SELECT time, temperature,
       changepoint(temperature, 'cusum', 4.0, 0.5) AS shift_detected
FROM reactor_data
WHERE reactor_id = 'R-101'
ORDER BY time;
```

这是合成变化样本，不是实际设备退化记录，也没有在本次整理中执行。

## 基线、阈值与复位

实现以开头一段非空样本估计基线均值和标准差，再推进双边累积和。`threshold` 是触发阈值对应的基线标准差倍数；`drift` 是可容忍漂移对应的倍数，省略时默认 0.5。触发后累积器复位，以便继续寻找变化。

因此 true 是算法的触发时间，不一定等于物理变化精确开始时间。基线区间已含故障、采样不足或标准差退化，都可能影响结果。当前唯一内置方法是 CUSUM，不能推断已经支持 PELT 或贝叶斯变点检测。

## 比较灵敏度

```sql
SELECT time, temperature,
       changepoint(temperature, 'cusum', 3.0, 0.5) AS sensitive,
       changepoint(temperature, 'cusum', 5.0, 0.5) AS conservative
FROM reactor_data
WHERE reactor_id = 'R-101'
ORDER BY time;
```

降低阈值通常更敏感，也更容易产生触发。增大 drift 会降低对小幅持续变化的敏感度。应通过标注历史与目标采样周期选择参数，不能普遍给定所有工业设备的安全阈值。

需要比较变化前后均值时，可由客户端根据触发点划分区间，再分别查询各区间的统计量。不要把未核实的复杂 CTE、累计 OVER 与别名嵌套写成已经可用的分段查询。

正式发行入口为 [4.0.0](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)；主线实现持续更新。变点只是候选线索，不能单独归因于故障、部署或硬件老化。

参考：[变点检测合同](https://github.com/IoTSharp/SonnetDB/blob/main/docs/forecast.md)、[窗口函数实现](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Query/Functions/Window/AnomalyFunctions.cs)、[SQL 参考](https://github.com/IoTSharp/SonnetDB/blob/main/docs/sql-reference.md)。
