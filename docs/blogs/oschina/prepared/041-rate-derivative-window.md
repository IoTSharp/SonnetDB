---
title: '深入理解 SonnetDB 的差分类窗口函数：derivative / non_negative_derivative / rate / irate'
categories: SonnetDB,数据库
draft: false
---

正式发行见 [4.0.0 Release](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。本文按 2026-10-07 当前 main 文档与源码修订旧稿；主线改动不自动代表全部 v4 发行物与客户端的已验收能力。以下 SQL/API 为复现示例，本次没有实际执行或性能测量。

差值说明改变了多少，导数说明每单位时间改变多少。SonnetDB 的这些函数在 measurement 行级路径中比较相邻有效样本。

```sql
SELECT time, bytes_total,
       derivative(bytes_total, 1s) AS signed_per_second,
       non_negative_derivative(bytes_total, 1s) AS positive_per_second,
       rate(bytes_total, 1s) AS rate_value,
       irate(bytes_total, 1s) AS irate_value
FROM network
WHERE host = 'gateway-01'
  AND time >= 1700000000000 AND time < 1700003600000
ORDER BY time;
```

计算式是 `(current-previous)*unit_ms/delta_ms`，默认单位 1 秒。单位参数是正 duration 字面量，例如 1s 或 100ms，不是字符串 '1s'。

## 负差和时间戳

derivative 保留负值，可描述降温或电压下降。non_negative_derivative、rate 和 irate 当前都将负差输出为 NULL，适合观察累计计数器。首个有效点、缺失输入或非正时间间隔也返回 NULL；缺失值不更新前一个有效值与对应时间。

当前 rate 与 irate 使用同一求值器，而非 PromQL 的整段区间平均、边界外推和 reset 修复合同。名字兼容不能证明查询可无修改迁移。

先对设备或主机限定 series，再选合理时间范围。一次过滤会切断范围外前驱，展示首点 NULL 是信息边界的结果，不应简单计为零速率。本文没有执行真实监控、吞吐或异常告警验证。

依据：[PointDifferenceFunctions.cs](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Query/Functions/Window/PointDifferenceFunctions.cs)。
