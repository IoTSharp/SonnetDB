---
title: 删除数据：SonnetDB 的 DELETE 与 Tombstone 机制
categories: SonnetDB,SQL,存储
draft: false
---

对时序存储而言，删除不必立即重写整个历史 segment。SonnetDB 的 measurement 删除通过 Tombstone 记录删除范围，查询过滤已删除点，后续 compaction 再逐步回收物理空间。

## 用明确范围删除练习数据

以下示例只用于一个独立练习数据库。先建立数据，再观察指定设备的时间范围。

```sql
CREATE MEASUREMENT retention_sample (host TAG, usage FIELD FLOAT);

INSERT INTO retention_sample (time, host, usage) VALUES
    (1700000000000, 'server-01', 42.5),
    (1700000001000, 'server-01', 85.0),
    (1700000002000, 'server-02', 60.0);

SELECT time, host, usage
FROM retention_sample
WHERE host = 'server-01'
  AND time >= 1700000000000
  AND time < 1700000002000
ORDER BY time;
```

确认数据库、measurement 和范围后，使用相同条件删除：

```sql
DELETE FROM retention_sample
WHERE host = 'server-01'
  AND time >= 1700000000000
  AND time < 1700000002000;

SELECT time, host, usage FROM retention_sample ORDER BY time;
```

删除成功后，后续查询会排除覆盖的点。前面的 `SELECT` 是一次观察，不能保证并发写入下的后续删除目标完全相同；它不是事务快照或自动批准机制。

## Tombstone 的作用

measurement 删除记录由 WAL 与删除清单持久化路径管理，包括 `tombstones.tslmanifest`。旧 segment 可以暂时保留原始字节，读取时根据删除记录过滤；compaction 重写数据时再消化删除范围。

这样避免了每次删除都原地修改旧 segment，但并不意味着没有写放大或 I/O 成本。删除范围数量、查询读取量和 compaction 调度都会影响资源消耗。

Tombstone 不是撤销按钮。不能承诺“compaction 前可以恢复”，也不能通过手工移除 WAL 或清单文件来撤销删除。需要恢复时应使用经过验证的备份与恢复流程。

## 模型与运维边界

关系表的 rowstore 删除使用自己的路径，不应把 measurement Tombstone 行为套用到所有模型。当前实现还支持受约束的 FIELD 残余删除谓词，但复杂过滤与资源预算必须核对具体版本；本文采用 TAG 与时间范围说明最常见的边界。

保留策略是否启用、何时运行与何时完成空间回收取决于配置和后台维护。生产清理应明确库、对象、时间单位、备份点和预期保留范围，并核验删除结果。

本文按当前仓库合同与测试校对：[SQL 参考](https://github.com/IoTSharp/SonnetDB/blob/main/docs/sql-reference.md)、[DELETE 测试](https://github.com/IoTSharp/SonnetDB/blob/main/tests/SonnetDB.Core.Tests/Sql/SqlExecutorDeleteTests.cs)、[4.0.0 正式发行](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。
