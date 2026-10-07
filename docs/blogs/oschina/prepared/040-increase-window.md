---
title: '计数器重置感知的增长计算：increase() 函数'
categories: SonnetDB,数据库
draft: false
---

正式发行见 [4.0.0 Release](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。本文按 2026-10-07 当前 main 文档与源码修订旧稿；主线改动不自动代表全部 v4 发行物与客户端的已验收能力。以下 SQL/API 为复现示例，本次没有实际执行或性能测量。

increase 用于抑制累计计数器回落产生的负差。当前 measurement 行级实现返回非负相邻差，遇到负差返回 NULL；它不会重建丢失的业务增量。

## 计数器重置的例子

假设 network 包含 host TAG、bytes_total FIELD INT：

```sql
SELECT time, bytes_total,
       difference(bytes_total) AS raw_change,
       increase(bytes_total) AS positive_change
FROM network
WHERE host = 'gateway-01'
  AND time >= 1700000000000 AND time < 1700003600000
ORDER BY time;
```

对计数器 100、150、10、30，increase 的定义结果是 NULL、50、NULL、20。reset 行不是 0，也不是自动推算成 10。实现中的旧注释写过 max(0,difference)，发布说明应以求值器实际分支为准。

## NULL 不代表没有业务

缺失输入同样输出 NULL，并保留上一有效值。reset 后当前值成为后续差分的新起点。应用如果要汇总增量，应分别统计有效区间、reset 次数和采集缺口，不能把所有 NULL 静默填零后称作准确总流量。

当前语义不是 PromQL increase(range-vector) 的边界外推或完整 reset 校正。时间间隔也不参与 increase；需要每秒速率时使用 rate(field,1s)。高精度大整数输入的差分仍走数值转换路径，应结合实际范围验证精度。

依据：[PointDifferenceFunctions.cs](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Query/Functions/Window/PointDifferenceFunctions.cs)。
