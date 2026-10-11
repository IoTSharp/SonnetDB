---
title: SonnetDB 范围查询速度揭秘：100k 行仅需 6.71 ms
categories: SonnetDB,查询,性能
draft: false
---

标题沿用历史文章命名。仓库 README 保存了旧范围查询摘要，但本次未取得对应完整原始报告；其中的耗时不作为 4.0.0 或当前 main 的查询保证。本文说明范围查询路径，以及怎样验证裁剪是否有效。

## 一个完整的范围查询

```sql
CREATE MEASUREMENT range_demo (
    host TAG,
    usage FIELD FLOAT,
    temperature FIELD FLOAT
);

INSERT INTO range_demo (time, host, usage, temperature) VALUES
    (1700000000000, 'server-01', 42.5, 30),
    (1700000001000, 'server-01', 61.2, 31),
    (1700000002000, 'server-02', 50.0, 29);

SELECT time, host, usage, temperature FROM range_demo
WHERE host = 'server-01'
  AND time >= 1700000000000
  AND time < 1700000002000
ORDER BY time;
```

TAG 等值与半开时间区间限定输入。时间列单位是 Unix 毫秒，终点是否包含必须与对照实现一致。

## Segment 与 block 裁剪

持久数据按 segment 和 block 保存。时间元数据可以排除不相交的候选，block 索引进一步缩小解码范围。MemTable 的当前数据还需要与持久段共同参与结果形成，删除可见性则受 Tombstone 合同约束。

裁剪收益取决于数据分布、段布局、时间重叠、投影与过滤形态。不能固定承诺能跳过某个百分比，也不能把示意代码当作真实源码中的公开方法签名。

## 分开计时计算与交付

嵌入式读取、SQL 执行、HTTP 响应和客户端结果物化具有不同边界。“取得第一行”与“全部行已消费”也不是同一件事。范围查询报告应明确测量到哪一步。

```bash
dotnet run -c Release --project tests/SonnetDB.Benchmarks -- --filter '*Query*'
```

运行时保留原始报告、commit、硬件与完整参数，设定总超时。分别记录冷缓存、热缓存、并发和持久段布局，不能只保留最快一轮。

同时核验返回行数、字段值和边界，防止把错误的短结果当成优化。托管分配不等于整个数据库的工作集；HTTP 传输、序列化和客户端缓冲也需要另计。

## 结果如何解释

`LIMIT` 控制输出，并不自动构成所有扫描和物化开销上限。具体执行路径的预算、取消和阻塞行为，应结合当前实现说明核对。

历史结果可以帮助确定回归方向，但新版本和新硬件必须取得自己的证据，不能把旧嵌入式数字用于网络服务延迟承诺。

参考：[基准说明](https://github.com/IoTSharp/SonnetDB/blob/main/tests/SonnetDB.Benchmarks/README.md)、[查询引擎](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Query/QueryEngine.cs)、[SQL 参考](https://github.com/IoTSharp/SonnetDB/blob/main/docs/sql-reference.md)。
