---
layout: default
title: "KV Keyspace"
description: "SonnetDB 内置轻量 KV Keyspace 的嵌入式 API、持久化布局、恢复和压实规则。"
permalink: /kv-keyspace/
---

# KV Keyspace

KV Keyspace 是 SonnetDB Core 的轻量键值存储能力，面向内部 metadata、小对象、关系表和文档集合后续底座。当前版本先提供嵌入式库级 API，不新增 SQL 语法，也不把 KV 暴露为独立网络服务。

## 基本用法

```csharp
using System.Text;
using SonnetDB.Engine;

using var db = Tsdb.Open(new TsdbOptions
{
    RootDirectory = "./data",
});

var kv = db.Keyspaces.Open("devices");

kv.Put("device:1001", Encoding.UTF8.GetBytes("online"));

byte[]? value = kv.Get("device:1001");

foreach (var row in kv.ScanPrefix("device:", limit: 100))
{
    Console.WriteLine($"{Encoding.UTF8.GetString(row.Key.Span)} v{row.Version}");
}

kv.Delete("device:1001");
```

## API 边界

| API | 说明 |
| --- | --- |
| `Tsdb.Keyspaces.Open(name)` | 打开或创建 keyspace。名称只允许字母、数字、点、下划线和短横线。 |
| `Put(key, value, expiresAtUtc?)` | 写入或覆盖 key，返回单调递增版本号。可选 `DateTimeOffset` 指定到期时间；到期后读到 `false`/`null`，由后台 GC 真正回收。 |
| `Get(key)` / `TryGet(key, out value)` | 读取当前值，返回 value 副本。 |
| `Delete(key)` | 删除 key。不存在时返回 `false`。 |
| `ScanPrefix(prefix, limit)` | 按 key 字节序升序返回当前快照。 |
| `CreateSnapshot()` | 写出完整快照并截断快照版本之前的 KV WAL。 |
| `Compact()` | 写出不可变 KV 段文件并截断已压实版本之前的 KV WAL。 |

当前 key 和 value 都是字节序列。字符串重载使用 UTF-8 编码 key；value 编码由调用方决定。

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

关系表 MVP 基于同一套 KV 存储能力实现，但目录独立放在 `tables/rowstore/<table-name-hex>/`，schema 放在 `tables/tables.tblschema`。这些文件同样不改变时序 measurement 的二进制格式。

## 崩溃恢复

启动时恢复顺序：

1. 加载最新 `segments/*.SDBKVSEG` 或 `snapshots/*.SDBKVSNP`。
2. 回放 `wal/active.SDBKVWAL` 中高于该版本的记录。
3. 遇到 WAL 尾部截断、header CRC 或 payload CRC 不匹配时停止在最后一条合法记录。

`KvOptions.SyncWalOnEveryWrite` 默认开启，适合 metadata 和小对象场景。高吞吐场景可以关闭每写 fsync，再由调用方按需通过快照或压实形成更稳定的恢复点。

## 当前不做

- 不提供 KV SQL 语法。
- 不提供 MVCC 事务和跨 keyspace 事务。
- 不提供独立 TCP / HTTP KV 服务。
- 不引入 SharpDB 文件格式或 NetMQ 协议。
