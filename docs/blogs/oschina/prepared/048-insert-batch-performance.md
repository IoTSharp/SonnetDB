---
title: 'SonnetDB INSERT 批处理性能优化：批量写入与 flush 策略'
categories: SonnetDB,数据库
draft: false
---

正式发行见 [4.0.0 Release](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。本文按 2026-10-07 当前 main 文档与源码修订旧稿；主线改动不自动代表全部 v4 发行物与客户端的已验收能力。以下 SQL/API 为复现示例，本次没有实际执行或性能测量。

批量 INSERT 可以减少解析、命令和网络调用次数，但“批量”不能自动保证吞吐、事务原子性或断电持久性。调优应从当前入口合同和实际测量开始。

## 使用多行 VALUES

```sql
CREATE MEASUREMENT cpu (host TAG, usage FIELD FLOAT);
INSERT INTO cpu (time, host, usage) VALUES
  (1700000000000, 'server-01', 20),
  (1700000001000, 'server-01', 40),
  (1700000002000, 'server-02', 30);
```

一条命令容纳多行，可以减少每点一条命令的开销。实际批大小还受请求上限、内存、响应时间和失败重放成本影响；本文没有固定硬件测量，不给出未经验证的每秒点数。

## 直接批量入口

SonnetDB 的 ADO.NET CommandType.TableDirect 支持 Line Protocol、JSON points 和 Bulk VALUES 快路径，适合已有序列化 payload。它们与普通 INSERT 是不同路径，尤其远程模式需要明确目标 measurement；完整写法见批量写入文档。

## Flush 与持久性

MemTable、WAL、段落盘和 compaction 各自承担不同职责。频繁 flush 可能增加小段数量与后续整理成本，延迟 flush 又会增加内存和恢复工作量。不能把每批手工 flush、返回成功或执行 SQL batch 自动解释为多语句事务提交、已 fsync 或断电安全。

应在明确数据库选项与发布版本下记录批大小、并发、写入延迟、分配、WAL 策略、段数量和重启恢复结果。超时后的重试要结合主键/时间戳与幂等合同，不能无条件重新发送整批并假定没有重复。

依据：[bulk-ingest.md](https://github.com/IoTSharp/SonnetDB/blob/main/docs/bulk-ingest.md)、[sql-reference.md](https://github.com/IoTSharp/SonnetDB/blob/main/docs/sql-reference.md)。
