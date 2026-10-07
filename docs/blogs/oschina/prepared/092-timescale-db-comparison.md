---
title: 多数据库横向对比：SonnetDB vs SQLite vs InfluxDB vs TDengine
categories: SonnetDB,性能,数据库选型
draft: false
---

横向对比首先要回答“比较了哪条路径”。SonnetDB 与 SQLite 可以嵌入进程，InfluxDB 和 TDengine 常通过服务端协议访问；把这些结果直接排成胜负榜，容易把协议成本误当作引擎差异。

历史底稿中的耗时、倍数、空闲内存和安装体积缺少本次可核验的完整原始报告，因此本文不把它们作为当前版本成绩。这里保留四库对比的方法，供实际选型复测。

## 建立两张独立结果表

| 比较层次 | SonnetDB 路径 | 对照需要核对的内容 |
| --- | --- | --- |
| 嵌入式 | 进程内 API、WAL、flush、segment | SQLite 文件/WAL/事务提交合同 |
| 服务端 | HTTP/Frame、认证、解析与传输 | InfluxDB 写入/查询接口、TDengine REST 或 schemaless |

TDengine REST INSERT 与 schemaless Line Protocol 也不是同一条路径。报告应把协议和批大小写在方法名附近，而不是只写产品名称。

## 保持数据和结果等价

采用相同的时间范围、设备分布、字段数与随机种子；记录每条记录含多少字段，统一时间单位。查询不仅核对返回行数，还应核对时间边界、缺值、排序和聚合结果。

SonnetDB 的小样本可以这样建立：

```sql
CREATE MEASUREMENT comparison_cpu (host TAG, usage FIELD FLOAT);
INSERT INTO comparison_cpu (time, host, usage) VALUES
    (1700000000000, 'server-01', 42.5),
    (1700000001000, 'server-01', 61.2);

SELECT time, host, usage FROM comparison_cpu
WHERE host = 'server-01'
  AND time >= 1700000000000
  AND time < 1700000002000
ORDER BY time;
```

对照库需要建立语义等价的 schema 和查询，而不是仅让 SQL 文本相似。

## 运行已有基准入口

```bash
dotnet run -c Release --project tests/SonnetDB.Benchmarks -- --filter '*Insert*'
dotnet run -c Release --project tests/SonnetDB.Benchmarks -- --filter '*Query*'
```

外部库必须先按仓库说明配置。不可用服务、跳过项和失败项应写成 `NOT_RUN` 或实际失败，不能当作耗时零，也不能让不完整结果形成排名。每轮设置总超时并隔离数据目录，测试完成后回收本次创建的资源。

## 如何用结果选型

性能之外还要核对部署方式、查询语义、备份恢复、权限、生态和运维成本。不要假设 SQLite 必然全表扫描，或从一项查询推导所有工作负载均领先。许可证也应按具体组件和版本核查，不能从 SDK 许可证推断整个产品。

本文介绍复测方法，没有新的四库完整跑分。[基准说明](https://github.com/IoTSharp/SonnetDB/blob/main/tests/SonnetDB.Benchmarks/README.md)、[对比口径说明](https://github.com/IoTSharp/SonnetDB/blob/main/tests/SonnetDB.Benchmarks/Benchmarks/DATABASE_COMPARISON_BENCHMARK_README.md)、[4.0.0 正式发行](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)提供进一步核对入口。
