---
title: SonnetDB 写入性能实录：545 ms 写入 100 万点，吞吐 1.83 M pts/s
categories: SonnetDB,性能,基准测试
draft: false
---

本文标题沿用历史文章的命名。仓库基准 README 保存了 PR #49 的历史结果摘要，但本次发布核对未找到对应完整原始报告；标题中的数字不作为 SonnetDB 4.0.0 或当前 main 的性能承诺。本文保留写入性能这个选题，说明怎样重新取得可审核的证据。

## 先明确测量路径

SonnetDB 同时有进程内引擎和服务端入口。嵌入式 `WriteMany` 没有 HTTP 往返；服务端写入还包含认证、解析、传输与响应。这两条路径需要分开报告。

当前基准项目的 `SonnetDB_Insert_1M` 覆盖 `WriteMany` 与 `FlushNow`。HTTP 则另有 SQL Batch、Line Protocol、JSON points 与 Bulk VALUES 路径。比较时必须记录方法名，不能把不同路径合并成一个“数据库吞吐量”。

## 从小样本核对 HTTP 合同

在已有练习数据库 `metrics` 中先定义字段：

```sql
CREATE MEASUREMENT sensor (host TAG, value FIELD FLOAT);
```

准备 `sample.lp`，内容为两条完整记录：

```text
sensor,host=server-01 value=1.0 1700000000000
sensor,host=server-01 value=2.0 1700000001000
```

下面是接口示例，执行前替换令牌并确认目标数据库：

```bash
curl --max-time 30 -X POST \
  'http://127.0.0.1:5080/v1/db/metrics/measurements/sensor/lp?flush=true' \
  -H 'Authorization: Bearer <token>' \
  --data-binary @sample.lp
```

再查询 `sensor` 核对时间、标签和值。小样本通过只能证明这次写入路径，不能直接外推到百万点吞吐。

## Flush 与持久化口径

批量入口的 `flush=false` 或缺省不等待 segment flush；`async` 发出后台信号；`true` 等同步选项等待 flush。它们的返回时点不同。

是否等待 segment 完成与 WAL 的持久化合同也要分别记录。不能仅凭一次 HTTP 成功，就推导出所有故障情形下的数据恢复保证。

## 重跑与交付证据

```bash
dotnet run -c Release --project tests/SonnetDB.Benchmarks -- --filter '*Insert*'
```

这是基准入口，不是本文已经完成的新实测。运行前确认数据目录、依赖服务与资源预算，设定总超时，并按测试生命周期回收本次创建的目录与服务。

报告至少包括 commit、数据库版本、硬件、数据分布、批大小、写入及 flush 配置、迭代原始样本、统计摘要和写后结果校验。点数和字段值数量应分开，托管分配也不能代替整个服务端进程内存。

参考：[基准项目说明](https://github.com/IoTSharp/SonnetDB/blob/main/tests/SonnetDB.Benchmarks/README.md)、[批量写入指南](https://github.com/IoTSharp/SonnetDB/blob/main/docs/bulk-ingest.md)、[4.0.0 正式发行](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。
