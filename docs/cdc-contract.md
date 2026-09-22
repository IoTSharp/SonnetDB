# CDC 版本化事件合同

M43 #385 的首批本地合同定义跨进程传输的单事件格式。实现位于 `SonnetDB.Cdc.CdcEventCodec`，使用手写 `Utf8JsonWriter`/`JsonDocument`，因此不依赖反射 JSON 元数据。

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

## 尚未承诺的能力

本切片只定义事件与 checkpoint 元数据，不提供 change-feed 存储、快照/增量衔接、离线队列、冲突解决、schema migration、客户端路由或复制拓扑。上述能力分别归入 M43 #386~#390；远程 parity、断网/重启和固定硬件容量属于后续验证计划。

定向回归：`dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj --filter FullyQualifiedName~CdcEventCodecTests`。
