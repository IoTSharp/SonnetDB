---
title: ORDER BY 排序：让时序查询结果井然有序
categories: SonnetDB,SQL,查询
draft: false
---

# ORDER BY 排序：让时序查询结果井然有序

数据库的扫描顺序不应成为应用的排序合同。展示最新读数、生成报告或分页时，要用 ORDER BY 明确结果顺序，并了解所查询模型和版本的限制。本文主要说明当前主分支；文中示例未在本次准备中执行。

## 时间序列先使用明确的时间排序

```sql
CREATE MEASUREMENT order_demo (device TAG, value FIELD FLOAT);
INSERT INTO order_demo (time, device, value) VALUES
  (1000, 'd1', 10.0), (3000, 'd1', 30.0), (2000, 'd1', 20.0);

SELECT time, value FROM order_demo
WHERE device = 'd1' ORDER BY time ASC;

SELECT time, value FROM order_demo
WHERE device = 'd1' ORDER BY time DESC LIMIT 2;

SELECT time, value FROM order_demo
WHERE device = 'd1' ORDER BY time ASC LIMIT 2 OFFSET 1;
```

ASC 为升序，DESC 为降序。排序发生在分页之前；最后一条先建立时间顺序，再跳过一行、最多返回两行。传统 measurement 执行路径要求 SELECT 结果中包含 `time`。

同一时间戳可能属于不同 series，仅 `ORDER BY time` 不能形成所有行的唯一顺序。需要跨请求稳定分页时，应选择能够提供完整唯一排序键和一致读取边界的工作流；独立 SQL 分页请求不自动共享快照。

## 当前关系表可以多键排序

当前主分支的关系表排序支持多项结果列，可加入唯一键消除并列顺序：

```sql
CREATE TABLE sort_devices (
  id INT, site STRING, score FLOAT, PRIMARY KEY (id)
);
INSERT INTO sort_devices (id, site, score) VALUES
  (1, 'north', 90.0), (2, 'north', 90.0), (3, 'south', 95.0);

SELECT id, site, score FROM sort_devices
ORDER BY site ASC, score DESC, id ASC LIMIT 10;
```

这个例子明确使用关系表，不能把它直接套用到所有 measurement 聚合查询。当前 measurement TAG 分组路径仍只支持对未改名的 `time` 投影进行单键排序，TAG、别名和多键排序有明确限制。带执行物化预算的 raw measurement 路径另有排序能力和形状检查；使用前应核对入口与选项。

## 4.0 与主分支需要分别阅读

4.0.0 标签中的 measurement 执行器使用单个 `OrderBy`，其时间排序示例有明确支持；当前主分支已使用 `OrderByList`，关系表多键排序及其他路径的能力应依据对应源码和文档核查。本文的当前分支多键示例不作为 4.0 安装包的功能承诺。

## LIMIT 并不保证所有成本都有界

部分查询可以推下时间分页或使用有界 Top-N。其他查询可能仍读取较多输入、构建结果或执行排序。`LIMIT 10` 只定义最终输出上限，不是通用的扫描数量、CPU 或堆内存上限。

实际数据量较大时，应先加入必要的时间和 series 过滤，再检查查询计划、执行预算及结果正确性。展示和分页测试还应覆盖空集、并列键、不同升降序及数据变化，不以一次小样本成功推断所有路径。

参考：[当前时序排序实现](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Sql/Execution/SelectExecutor.cs)、[当前关系表执行器](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Sql/Execution/TableSqlExecutor.cs)、[4.0 时序执行器](https://github.com/IoTSharp/SonnetDB/blob/v4.0.0/src/SonnetDB.Core/Sql/Execution/SelectExecutor.cs)。
