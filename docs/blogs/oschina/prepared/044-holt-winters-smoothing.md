---
title: 'SonnetDB 的 Holt-Winters 平滑：holt_winters() 双指数平滑与趋势提取'
categories: SonnetDB,数据库
draft: false
---

正式发行见 [4.0.0 Release](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。本文按 2026-10-07 当前 main 文档与源码修订旧稿；主线改动不自动代表全部 v4 发行物与客户端的已验收能力。以下 SQL/API 为复现示例，本次没有实际执行或性能测量。

SonnetDB 的 holt_winters(field,alpha,beta) 行级函数实现 Holt 加法双指数平滑，维护水平与趋势。函数名不能证明它包含完整季节性 Holt-Winters 模型。

```sql
SELECT time, load,
       holt_winters(load, 0.3, 0.2) AS fitted_value
FROM workloads
WHERE host = 'server-01'
  AND time >= 1700000000000 AND time < 1700003600000
ORDER BY time;
```

load 应是数值 FIELD，alpha 和 beta 都在 `(0,1]` 范围内。alpha 控制水平响应，beta 控制趋势更新。

## 递推状态如何初始化

首个有效值初始化 level 并直接输出。第二个有效值以首两点差初始化 trend；后续使用新的观测修正 level，再修正 trend，输出 level+trend。尚未初始化时缺失输入输出 NULL，初始化后缺失行沿用已有拟合状态。

当前窗口函数没有周期长度参数和季节状态，也没有未来时间点的生成过程。forecast 表值函数是另一项能力，不能把当前每行拟合值标成已经完成未来预测。

趋势更新按采样顺序进行，参数也没有直接按不等时间间隔归一化。对不规则采样，先核对采样策略并保存缺口信息。上线前用历史留出区间验证误差与漂移，而不是把 alpha/beta 示例当作默认最佳参数。

依据：[SmoothingFunctions.cs](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Query/Functions/Window/SmoothingFunctions.cs)。
