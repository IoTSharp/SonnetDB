## Flush 模式深入解析：?flush=false|true|async 的持久性与延迟权衡

批量摄入的 `flush` 参数决定本批写入后是否触发 segment flush，以及是否等待它完成。WAL 写入、用户态缓冲、操作系统缓存与物理磁盘同步是不同边界，不能把 `flush=false` 简化成“只有内存”，也不能把 `async` 写成“断电最多丢 1 秒”。

本文依据当前 bulk handler、`BulkIngestor` 和 `TsdbOptions` 整理。正式 4.0.0 与 main 分开核对；恢复和故障表现仍需要在实际文件系统及设备上验证。

### 参数的准确含义

| `flush` | 本批写入后的动作 | HTTP 返回意味着什么 |
| --- | --- | --- |
| 省略或 `false` | 不额外强制批后 flush | 写入完成，WAL 行为依引擎配置 |
| `async` | 调用 `SignalFlush()` | 信号已经发出，未等待 flush 完成 |
| `true` / `sync` / `1` / `yes` | 调用 `FlushNow()` | 等待本次同步 flush 路径完成 |

这个参数适用于专用批量 HTTP 端点或 ADO.NET `TableDirect` 的参数，不是连接字符串中的通用 `Flush=` 开关。

### 同一批 JSON，三种调用策略

先准备一个 `points.json` 文件：

```json
{"m":"sensor_data","points":[{"t":1713676800000,"tags":{"device_id":"s01"},"fields":{"temperature":23.5}}]}
```

假设目标库存在，使用有写权限的 token：

```bash
curl --fail-with-body --max-time 30 \
  "http://127.0.0.1:5080/v1/db/metrics/measurements/sensor_data/json?flush=true" \
  -H "Authorization: Bearer <token>" \
  -H "Content-Type: application/json" --data-binary @points.json
```

把 query 改为 `flush=false` 或 `flush=async` 就分别选择另外两种策略。不要在同一业务数据上为了测试三种参数重复执行，除非已经明确重复点的处理合同。

ADO.NET 对应写法：

```csharp
using System.Data;
using SonnetDB.Data;

using var connection = new SndbConnection("Data Source=./flush-demo");
connection.Open();
using var command = connection.CreateCommand();
command.CommandType = CommandType.TableDirect;
command.CommandText = "sensor_data,device_id=s01 temperature=23.5 1713676800000";
command.Parameters.AddWithValue("measurement", "sensor_data");
command.Parameters.AddWithValue("flush", "true");
Console.WriteLine(command.ExecuteNonQuery());
```

### WAL 配置决定另一层边界

当前核心选项中 `FlushWalToOsOnWrite` 默认 `true`，写入后将 WAL 缓冲交给 OS，但这一步不执行 fsync。`SyncWalOnEveryWrite` 默认 `false`，启用后通过 WAL/group-commit 路径完成更强的同步。

| WAL 选项组合 | 持久化边界 |
| --- | --- |
| 两者都关闭 | 最近 WAL 仍可留在进程缓冲，进程崩溃可能损失未刷部分 |
| `FlushWalToOsOnWrite=true` | WAL 已交给 OS，进程崩溃与内核崩溃/掉电的边界不同 |
| `SyncWalOnEveryWrite=true` | 写入等待更强 WAL 同步，延迟和吞吐也相应变化 |

这是引擎合同的分级，不能超出磁盘、文件系统与缓存设备实际兑现的同步能力。`flush=true` 的 segment 完成也不是“任何故障情况下绝对零损失”的证明。

### 后台轮询不等于恢复 SLA

后台 worker 的轮询间隔与是否满足 MemTable 阈值是调度策略，`SignalFlush()` 也只是唤醒/触发机制。积压、磁盘故障和恢复发布边界会影响完成时间，不能从 1 秒轮询推导最大丢失窗口。应用需要监控 flush 等待、积压、WAL 同步和错误。

关键数据可以选择同步批后 flush 或更强 WAL 同步，但要做实际故障恢复演练。可重放遥测可选择有界批量与后台策略，保留上游重放来源。没有同机、同数据、同配置证据时，不应给出固定吞吐表。

请求超时后，数据可能已经写入但客户端没有收到回执；应先核对再重试。恢复测试、备份校验和接口成功是不同证据，生产运行需要分别验证。

参考：[批量写入](https://github.com/IoTSharp/SonnetDB/blob/main/docs/bulk-ingest.md)、[引擎选项](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Engine/TsdbOptions.cs)、[批量实现](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Ingest/BulkIngestor.cs)、[正式 4.0.0](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。
