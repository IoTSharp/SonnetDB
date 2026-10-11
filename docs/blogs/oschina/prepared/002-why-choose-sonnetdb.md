---
title: 为什么选择 SonnetDB：五大核心优势解析
categories: SonnetDB,数据库,架构
draft: false
---

选择数据库，先看应用如何部署、数据如何组织、故障后如何恢复，再看性能。SonnetDB 基于 C# / .NET 10，既能嵌入应用，也能作为独立 Server 运行。它已从时序引擎扩展为九模型数据引擎；本文保留五个选型角度，并按当前实现说明适用范围。

版本阅读入口是已经正式发布的 [SonnetDB 4.0.0](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。仓库 `main` 仍在演进，链接中的开发文档可能包含该发行物之后的改动；使用生产包时应核对自己的包版本、Server 版本和对应标签。

## 一、嵌入式和 Server 两种部署方式

本地应用可以直接打开一个数据库目录，免去单独维护服务进程。需要远程访问、用户授权和管理界面时，再部署 Server。ADO.NET 在两种方式下保留相似的调用接口，但网络、认证、事务会话和恢复合同仍需分别处理。

```text
本地：Data Source=./demo-data
远程：Data Source=sonnetdb+http://127.0.0.1:5080/metrics;Token=<your-token>
```

这适合桌面应用、边缘节点以及单机业务服务的分阶段接入。是否满足目标机器的容量、延迟和长期运行要求，需要实际工作负载验证，不能由部署方式直接推断。

## 二、以安全 C# API 约束核心实现

核心存储实现遵循 safe-only 约束，通过 `Span<T>`、`BinaryPrimitives`、池化缓冲和明确的文件校验处理底层数据，而不引入 `unsafe` 代码块。这使二进制布局、边界检查和代码审查更容易追踪。

安全语言和 API 不能消除所有问题。并发、资源耗尽、磁盘损坏和第三方原生资产仍须单独验证；Server 的图片与模型组件也有自己的依赖和平台边界。

## 三、SQL 与九模型保留各自语义

时序、关系表、KV、JSON 文档、全文、向量、对象、SonnetMQ 和 Graph 共用引擎与管理入口。SQL 提供查询、聚合、受支持的 JOIN、CTE 和窗口语法，但它是有界子集，关系表与 measurement 的能力矩阵也有差别。

```sql
CREATE MEASUREMENT sensor_data (
    device_id TAG,
    temperature FIELD FLOAT,
    humidity FIELD FLOAT
);

INSERT INTO sensor_data (time, device_id, temperature, humidity)
VALUES (1713676800000, 'sensor-01', 22.8, 66.1);

SELECT time, temperature
FROM sensor_data
WHERE device_id = 'sensor-01'
ORDER BY time;
```

向量、对象和文档还提供专用 API。Graph 继续标为 Beta；统一入口不表示九模型事务或恢复具备同一保证。Server 的 SonnetMQ 位于实例级 `.system/mq`，单库备份不包含完整 MQ 实例状态。

## 四、相应生产路径支持 Native AOT

Server 和原生连接器提供 Native AOT 发布路径，JSON 使用 source-generated 元数据。这样可以交付独立程序并明确原生资产清单，具体支持以对应 RID 的发行物为准。

普通 `dotnet build`、某个 AOT 产物或开发机测试，不能替代所有平台的安装、升级和运行验收。EF Core、Studio 等外围组件按各自部署要求使用，不能统一称为全部 Native AOT，也不能预先承诺启动毫秒数或镜像体积。

## 五、MIT 许可证与可检查的工程资料

SonnetDB 主项目采用 MIT 许可证，使用和再分发时须保留相应版权与许可声明。第三方依赖和原生组件仍以各自许可证为准，产品集成前应核对发行物中的 NOTICE 和依赖清单。

评估时建议先建立一条自己的写入、查询、备份和恢复旅程，再逐项扩大数据量和接入模型。可继续阅读 [项目介绍](https://github.com/IoTSharp/SonnetDB/blob/main/README.md)、[SQL 参考](https://github.com/IoTSharp/SonnetDB/blob/main/docs/sql-reference.md)、[成熟度口径](https://github.com/IoTSharp/SonnetDB/blob/main/docs/capability-maturity.md)和[许可证](https://github.com/IoTSharp/SonnetDB/blob/main/LICENSE)。
