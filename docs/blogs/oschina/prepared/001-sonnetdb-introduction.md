---
title: SonnetDB 简介：开源时序数据库的新星
categories: SonnetDB,数据库,DotNet
draft: false
---

SonnetDB 从时序存储起步，面向设备指标、传感器采集和应用中的时间序列分析。现在它已经扩展为一个基于 C# / .NET 10 的多模型数据引擎：时序、关系表、KV、JSON 文档、全文、向量、对象、消息队列和原生属性图共享引擎与管理入口，同时保留各自的数据语义。

本文保留系列入门主题，并将旧稿中的 v0.6.0 说明更新为正式的 [SonnetDB 4.0.0 Release](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。仓库 `main` 包含持续开发的改动，阅读开发文档时要核对自己的包和 Server 版本，不能将尚未发布的主线功能直接视为 4.0.0 能力。

## 嵌入式使用与独立 Server

如果应用只需要本地数据库，可以在进程内通过 `Tsdb.Open` 打开数据库目录。需要远程访问、用户授权和管理工具时，可以部署 Server，并通过 HTTP、ADO.NET、CLI 或 Web Admin 使用。

嵌入式方式适合桌面应用和边缘节点，Server 适合集中接入与治理。两者仍要分别验证资源上限、网络故障和恢复行为；同一套 API 不表示所有部署条件具备相同保证。

## 从第一条 measurement 开始

时序数据由 measurement、TAG、FIELD 和保留时间列 `time` 组成。以下 SQL 可以在已选定数据库的 SQL 控制台中执行：

```sql
CREATE MEASUREMENT cpu (
    host TAG,
    usage FIELD FLOAT,
    cores FIELD INT
);

INSERT INTO cpu (time, host, usage, cores)
VALUES (1713676800000, 'server-01', 0.71, 8);

SELECT time, usage * 100 AS pct
FROM cpu
WHERE host = 'server-01'
ORDER BY time;
```

这里的 `time` 使用 Unix 毫秒，`host` 是字符串标签，`usage` 和 `cores` 是有声明类型的测量值。不要把 measurement 等同于任意关系表；关系查询、事务型 DML、向量和地理字段都有各自支持矩阵。

## 分析、向量与 AI 接入

内置函数覆盖数值、统计、时间窗口、轨迹、预测和 PID 等分析任务。工业分析示例不是现场硬实时控制的替代品；算法可运行也不表示真实设备闭环或模型质量已经验收。

向量路径提供 HNSW 与精确检索入口，Copilot 通过配置的模型 Provider 辅助 SQL 草稿和知识检索。模型调用、权限校验和实际写操作仍须分开；当前 typed HTTP MCP 是只读工具入口，不能把它宣传成任意数据库写权限或已经交付的 stdio bridge。

## 多模型与恢复边界

当前模型成熟度按 `partial`、`beta` 等状态展示。Graph 继续为 Beta，不代表完整 GQL/Cypher、固定硬件容量或长期生产门禁通过。Server 的 SonnetMQ 日志位于实例级 `.system/mq`，数据库备份不能冒称包含完整 MQ 实例状态，也不能描述成九模型原子恢复。

SonnetDB 主项目采用 MIT 许可证，相关生产路径保留 source-generated JSON 和 Native AOT 要求。评估时可以先完成一条写入、查询、备份、恢复旅程，再用目标机器和真实数据扩大范围。

入门资料：[项目 README](https://github.com/IoTSharp/SonnetDB/blob/main/README.md)、[开始使用](https://github.com/IoTSharp/SonnetDB/blob/main/docs/getting-started.md)、[数据模型](https://github.com/IoTSharp/SonnetDB/blob/main/docs/data-model.md)、[SQL 参考](https://github.com/IoTSharp/SonnetDB/blob/main/docs/sql-reference.md)和[能力成熟度](https://github.com/IoTSharp/SonnetDB/blob/main/docs/capability-maturity.md)。
