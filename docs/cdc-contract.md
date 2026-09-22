# CDC 版本化事件合同

M43 #385 的首批本地合同定义跨进程传输的单事件格式。实现位于 `SonnetDB.Cdc.CdcEventCodec`，使用手写 `Utf8JsonWriter`/`JsonDocument`，因此不依赖反射 JSON 元数据。M43 #386~#390 的首个本地存储切片位于 `SonnetDB.Cdc.CdcEventSpool`：它把事件写入带 magic/version/长度/分区位点/CRC32 的固定帧，并提供有界回放、确认和重开校验。

## 格式

事件使用固定字段和严格对象结构：

```json
{
  "eventId": "evt-1",
  "source": "db-a",
  "entity": "documents",
  "key": "doc-7",
  "sequence": 42,
  "occurredAtUtc": "2026-09-22T08:30:00+00:00",
  "metadata": {
    "contractVersion": 1,
    "schema": "documents",
    "schemaVersion": 1,
    "operation": "update",
    "checkpoint": { "partition": 2, "offset": 99 }
  },
  "before": { "value": 1 },
  "after": { "value": 2 }
}
```

`contractVersion` 和 `schemaVersion` 当前都为 `1`。未知字段、重复字段、未知 operation、未知版本和非 UTC 时间会 fail closed；接收方不能把未知版本降级为当前版本。

## 有界规则

- 单事件 UTF-8 总大小最多 1 MiB；`eventId`、`source`、`entity`、`key` 和 `schema` 各最多 4 KiB UTF-8 字节。
- `before`/`after` 各最多 256 KiB，必须是有效 JSON，最大嵌套深度为 32，不接受注释或尾逗号。
- `sequence`、`partition` 和 `offset` 必须非负；同一分区的 checkpoint 由上层保证单调。
- `insert` 不得带 `before`，`delete` 不得带 `after`；`update` 可同时带两者。
- `ReadAsync`、`WriteAsync` 使用取消令牌和有界缓冲，输入超限或流为空时返回合同错误。

## Append-only spool 首切片

`CdcEventSpool` 使用单个 `.lock` lease 约束同一路径的写者；实例内的 append、回放和确认由单写者闸门串行化。`MaxEvents`、`MaxBytes`、`MaxPartitions` 和每批回放上限在打开及运行时都受校验。metadata v2 会在写帧前以 CRC32 原子发布每个分区的 append high-watermark（未确认时 `acknowledged = -1`），再写入并 `Flush(true)` 固定帧；这样掉电、截断或数据文件丢失时，重开会因缺少未确认帧而 fail closed。确认位点同样先发布 metadata，再重写数据文件；重开时会重新扫描每个帧并校验正文 checkpoint、帧 CRC 和 high-water mark。现有 metadata v1 仍可读取。追加取消或 I/O 失败会尝试把 partial tail 截回，但由于 append intent 已持久化，实例会进入 faulted 状态，必须关闭并重开核对结果。

该 spool 只提供单进程编排可使用的 durable append/replay/ack 边界，不提供复制拓扑、冲突解决、schema migration、快照切换或 exactly-once。目录 fsync 失败发生在原子替换之后时，提交结果可能未知，调用方应停止当前实例并重开核对文件；不能把异常当作已回滚。

## 尚未承诺的能力

尚未承诺的快照/增量衔接、离线队列、冲突解决、schema migration、客户端路由和复制拓扑仍归入 M43 #386~#390 的后续切片；远程 parity、断网/重启和固定硬件容量属于后续验证计划。

定向回归：`dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj --filter FullyQualifiedName~CdcEventSpoolTests`（当前 14 项）及 `FullyQualifiedName~CdcEventCodecTests`。
