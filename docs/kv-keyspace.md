---
layout: default
title: "KV Keyspace"
description: "SonnetDB 内置轻量 KV Keyspace 的嵌入式 API、持久化布局、恢复和压实规则。"
permalink: /kv-keyspace/
---

# KV Keyspace

KV Keyspace 是 SonnetDB Core 的持久键值存储能力，也用于内部 metadata、关系表和文档集合底座。除嵌入式 API 外，Server 已提供 HTTP/Frame KV 入口与管理工作台；本页重点描述嵌入式合同，不表示远程服务不存在。NX/XX、原子 `GetAndSet/GetAndDelete` 与类型化 namespace 是嵌入式首切片，远程对应合同仍由 M36 #316 推进；不承诺 Redis wire protocol 或跨 keyspace 事务。

## 基本用法

```csharp
using System.Text;
using SonnetDB.Engine;
using SonnetDB.Kv;

using var db = Tsdb.Open(new TsdbOptions
{
    RootDirectory = "./data",
});

var kv = db.Keyspaces.Open("devices");

KvSetResult created = kv.Set(
    "device:1001",
    KvValueCodec.EncodeUtf8("online"),
    KvSetCondition.IfNotExists,
    expiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(5));

byte[]? value = kv.Get("device:1001");

foreach (var row in kv.ScanPrefix("device:", limit: 100))
{
    Console.WriteLine($"{Encoding.UTF8.GetString(row.Key.Span)} v{row.Version}");
}

KvExchangeResult removed = kv.GetAndDelete("device:1001");
```

`Set(..., IfNotExists)` 对应 NX，`Set(..., IfExists)` 对应 XX。已经过期的 key 在条件判断时视为不存在；成功写入返回提交版本，条件不成立时 `Applied=false` 且 `Version=null`。`GetAndSet` 与 `GetAndDelete` 原子返回变更前可见记录的副本，调用方不需要用一次独立 `Get` 拼接竞态窗口。

逻辑 namespace 复用同一个 keyspace 和 WAL，只在 key 前增加稳定前缀；返回的 `KvEntry.Key` 会去掉此前缀：

```csharp
KvNamespace tenant = kv.Namespace("tenant-42");
tenant.Set("device:1001", KvValueCodec.EncodeUtf8("online"));
KvExchangeResult previous = tenant.GetAndSet(
    "device:1001",
    KvValueCodec.EncodeUtf8("maintenance"));
```

raw bytes 始终是权威 value 语义。需要字符串或 JSON 时显式使用 codec；JSON 调用方必须提供 source-generated `JsonTypeInfo<T>`，不能退回反射序列化：

```csharp
using System.Text.Json.Serialization;

byte[] payload = KvValueCodec.EncodeJson(
    new DeviceState("online"),
    AppJsonContext.Default.DeviceState);
DeviceState? state = KvValueCodec.DecodeJson(
    payload,
    AppJsonContext.Default.DeviceState);

internal sealed record DeviceState(string Status);

[JsonSerializable(typeof(DeviceState))]
internal sealed partial class AppJsonContext : JsonSerializerContext;
```

`KvValueCodec` 的 UTF-8 编解码拒绝不成对的 surrogate 和非法字节序列。所有 string key/prefix/range 入口与 namespace 名称都复用严格编码；写操作会在锁和 WAL 前校验 UTF-8 及 key 字节预算，避免不同非法字符串折叠为同一替换字符 key。

## API 边界

| API | 说明 |
| --- | --- |
| `Tsdb.Keyspaces.Open(name)` | 打开或创建 keyspace。名称只允许字母、数字、点、下划线和短横线。 |
| `Put(key, value, expiresAtUtc?)` | 写入或覆盖 key，返回单调递增版本号。可选 `DateTimeOffset` 指定到期时间；到期后读到 `false`/`null`，由后台 GC 真正回收。 |
| `Set(key, value, condition, expiresAtUtc?)` | 按 Always/NX/XX 条件写入；过期记录按不存在处理，返回 `KvSetResult`。 |
| `PutMany(values, expiresAtUtc?)` | 把多个 key 编码为单条原子 WAL batch；所有返回项共享同一个 batch commit 版本。 |
| `Get(key)` / `TryGet(key, out value)` | 读取当前值，返回 value 副本。 |
| `GetAndSet(key, value, expiresAtUtc?)` | 原子返回旧 `KvEntry` 并写入新值；新 TTL 完全由本次参数决定。 |
| `GetAndDelete(key)` | 原子返回旧 `KvEntry` 并删除；不存在或已过期时返回空 previous/mutation，过期记录仍可能按既有惰性过期语义写入清理 tombstone。 |
| `Delete(key)` | 删除 key。不存在时返回 `false`。 |
| `ScanPrefix(prefix, limit)` | 按 key 字节序升序返回当前快照。 |
| `Namespace(name)` | 创建同一 keyspace 上的逻辑前缀视图；不是独立存储或跨 namespace 事务边界。 |
| `KvValueCodec` | 严格 UTF-8 与调用方 `JsonTypeInfo<T>` JSON 转换；不改变 raw bytes 权威语义。 |
| `CreateSnapshot()` | 写出完整快照并截断快照版本之前的 KV WAL。 |
| `Compact()` | 写出不可变 KV 段文件并截断已压实版本之前的 KV WAL。 |

当前 key 和 value 都是字节序列。字符串重载只负责 key 编码；value 编码由调用方决定。条件写和交换操作复用现有锁、版本、WAL 与恢复路径，没有第二套 KV 存储。WAL append 或 sync 的提交结果无法确定时会 fail closed 并阻止实例继续写入，调用方不能把异常解释为“确定未提交”后盲目重试。

## 持久化布局

每个 keyspace 存在于数据库目录的 `kv/keyspaces/<name>/` 下：

```text
<root>/
  kv/
    keyspaces/
      devices/
        wal/
          active.SDBKVWAL
        snapshots/
          00000000000000000001.SDBKVSNP
        segments/
          00000000000000000002.SDBKVSEG
```

KV 使用独立文件格式，不复用时序写入路径的 `.SDBWAL` 或 `.SDBSEG`。因此新增 KV 不改变已有 measurement 的 WAL、Segment、Catalog 二进制格式。

当前 state 写格式为 v5：有序 key 使用每 16 条强制 restart 的前缀压缩，entry CRC 仍覆盖解压后的完整 key 与 value。读取路径兼容 v1-v4；写出 v5 后，旧版 SonnetDB 二进制不能直接降级打开该 state，降级前应使用当前版本导出/备份并由目标版本重新导入。

关系表 MVP 基于同一套 KV 存储能力实现，但目录独立放在 `tables/rowstore/<table-name-hex>/`，schema 放在 `tables/tables.tblschema`。这些文件同样不改变时序 measurement 的二进制格式。

## 崩溃恢复

启动时恢复顺序：

1. 加载最新 `segments/*.SDBKVSEG` 或 `snapshots/*.SDBKVSNP`。
2. 回放 `wal/active.SDBKVWAL` 中高于该版本的记录。
3. 遇到 WAL 尾部截断、header CRC 或 payload CRC 不匹配时停止在最后一条合法记录。

KV WAL v3 增加 mixed put/delete `MutationBatch` record。batch 由单个 header CRC + payload CRC 保护，恢复时只会整批应用或整批忽略；v1/v2 仍可读取，旧 active WAL 在打开时会先密封，再创建 v3 active WAL。该原子性边界仅限单个 keyspace，不包含跨 table/keyspace 事务。

`KvOptions.SyncWalOnEveryWrite` 默认开启，适合 metadata 和小对象场景。高吞吐场景可以关闭每写 fsync，再由调用方按需通过快照或压实形成更稳定的恢复点。

## 当前不做

- 不提供 KV SQL 语法。
- 不提供 MVCC 事务和跨 keyspace 事务。
- 不提供独立 TCP / HTTP KV 服务。
- 不在本切片提供异步 cursor、pipeline/batch 分项结果、hot-key/expiry/容量诊断或远程 parity。
- 不引入 SharpDB 文件格式或 NetMQ 协议。
