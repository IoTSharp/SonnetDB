---
title: 'SonnetDB 平滑函数解析：moving_average() 与 ewma() 降噪技术'
categories: SonnetDB,数据库
draft: false
---

正式发行见 [4.0.0 Release](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。本文按 2026-10-07 当前 main 文档与源码修订旧稿；主线改动不自动代表全部 v4 发行物与客户端的已验收能力。以下 SQL/API 为复现示例，本次没有实际执行或性能测量。

moving_average 与 ewma 都能减少曲线抖动，但一个按采样点窗口计算，一个按递推权重计算。选参数前应先确定采样频率。

```sql
SELECT time, temperature,
       moving_average(temperature, 5) AS mean_5_points,
       ewma(temperature, 0.3) AS smooth_value
FROM sensors
WHERE device = 'sensor-01'
  AND time >= 1700000000000 AND time < 1700003600000
ORDER BY time;
```

## 五个点不等于五分钟

moving_average 的第二参数是正整数采样行数。前 n-1 行输出 NULL；满窗口后，只把窗口中存在的有效值计入分子与分母，全缺失窗口输出 NULL。缺失行仍占窗口位置，因此不是等待凑满 n 个有效样本才输出。

## EWMA 的权重

alpha 范围为 `(0,1]`。首个有效值初始化状态，之后使用 `s=alpha*x+(1-alpha)*previous_s`。alpha 越大越快跟随新值；alpha=1 相当于当前有效值。缺失输入在已初始化后返回已有平滑状态，不更新它。

这两项平滑都不会生成未存在的时间戳，也不自动识别季节性。不能用平滑值掩盖传感器停报；可以同时展示原值、有效样本和缺失状态。

参数选择应以业务延迟与噪声样本验证。本文不声称某个 alpha 对所有设备最优，也不把 toy 曲线视为真实异常检测证据。

依据：[SmoothingFunctions.cs](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Query/Functions/Window/SmoothingFunctions.cs)。
