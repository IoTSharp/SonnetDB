---
title: 'SonnetDB 状态分析函数：state_changes() 变化检测与 state_duration() 持续时间'
categories: SonnetDB,数据库
draft: false
---

正式发行见 [4.0.0 Release](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。本文按 2026-10-07 当前 main 文档与源码修订旧稿；主线改动不自动代表全部 v4 发行物与客户端的已验收能力。以下 SQL/API 为复现示例，本次没有实际执行或性能测量。

state_changes 计算累计状态变化次数，state_duration 计算当前状态已经持续的时间。它们可接受数值、Boolean 和 String FIELD，适合设备模式或告警状态观察。

```sql
SELECT time, status,
       state_changes(status) AS transition_count,
       state_duration(status) AS current_state_ms
FROM device_state
WHERE device_id = 'device-01'
  AND time >= 1700000000000 AND time < 1700003600000
ORDER BY time;
```

## 变化计数

首个有效状态不算一次变化，输出 0。后续有效值与上一有效状态不相等时，累计次数加一；相同状态不增加。缺失行沿用已有次数，并不把 NULL 视为一个新状态。

因此 off、off、on、NULL、on 对应变化计数 0、0、1、1、1。这是定义示例，不是实际运行结果。字符串状态应以实际值精确比较，不要依赖 SQL 标识符大小写规则去合并状态数据。

## 持续时间

state_duration 返回毫秒。首次有效状态从该点起算，变化时归零；之后输出当前时间与状态起始时间之差。缺失值不重置状态，但初始化之前的缺失输出 NULL。

这描述“当前观测状态段的年龄”，不是某状态在全部历史中的总在线时间。WHERE 截断范围后，持续时间也不能恢复范围外的状态起点；需要完整运行时长应读取必要前驱或在应用维护明确的状态账本。

依据：[StateFunctions.cs](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Query/Functions/Window/StateFunctions.cs)。
