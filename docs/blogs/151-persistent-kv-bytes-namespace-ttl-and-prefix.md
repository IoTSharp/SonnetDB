---
title: SonnetDB 持久 KV 入门：字节值、Namespace、TTL 与前缀扫描
categories: SonnetDB
tags: KV, Namespace, TTL, CSharp
draft: false
---

# SonnetDB 持久 KV 入门：字节值、Namespace、TTL 与前缀扫描

SonnetDB 的持久 KV 可以在数据库目录内保存设备状态，并用逻辑 Namespace、UTC 到期时间和前缀扫描组织读取。本文从“保存某站点的设备在线状态”这一任务出发，说明字节值的编码、空值与缺失、TTL 的解释以及重开验证方法。

![SonnetDB 持久 KV 入门：字节值、Namespace 与 TTL](https://img2024.cnblogs.com/blog/1725297/202610/1725297-20261013111310844-700738112.png)

图：AI 主题示意图，并非实测截图；Namespace、到期和持久化边界以正文及对应源码为准。

## 先确定使用的源码与版本

截至 2026-10-13，本次只读核查的 GitHub 最新正式 Release 和 NuGet `SonnetDB.Core` 为 **4.0.0**。本文 API 对照本地提交 `dcadffc7137f5653ec46c55a4171cf5f2d83de03`；列出的 KV 文档、Namespace 和样例与公开固定提交 `5424b66672e37f49a2a5633fdde0b54a9b1f0290` 一致。示例是该源码合同的预期行为，本次写稿没有执行数据库程序，不保证所有当前源码功能均存在于已发行包。

需要按本文复现时，使用独立目录检出固定源码，阅读随源码的 [KV Quickstart](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/samples/SonnetDB.KvQuickstart/README.md)，并准备 .NET 10 SDK。不要在已有生产数据库上试写或验证格式降级。

## 第一步：打开库，明确 keyspace 和 Namespace

下面的代码适合放在引用该版本 Core 的控制台程序中。数据目录只用于本次示例；第一次运行会保存状态，重复运行会覆盖这些 key。

```csharp
using SonnetDB.Engine;
using SonnetDB.Kv;

using var db = Tsdb.Open(new TsdbOptions
{
    RootDirectory = "./kv-device-demo",
});

var devices = db.Keyspaces.Open("devices");
var east = devices.Namespace("site_east");
var expiry = DateTimeOffset.UtcNow.AddMinutes(5);

long version = east.Put("device:1001",
    KvValueCodec.EncodeUtf8("online"), expiry);
Console.WriteLine($"written version={version}");

byte[]? value = east.Get("device:1001");
Console.WriteLine(value is null ? "missing" : KvValueCodec.DecodeUtf8(value));
```

在未到期且没有并发修改时，读出的文本应为 `online`，写入版本应为正数。不要把示例输出中的版本写成固定数字：版本来自该 keyspace 的实际提交状态。

`devices` 是实际 keyspace；`site_east` 是同一 keyspace 上的前缀视图。上面的物理 key 类似 `site_east:device:1001`，通过 Namespace 读取时使用局部 key `device:1001`。这有助于业务组织，但 **Namespace 不是权限隔离，也不是独立事务边界**。不同应用仍应核对数据库访问权限和实际 key 规则。

## 第二步：按局部前缀读取，保留条目预算

```csharp
east.Put("device:1002", KvValueCodec.EncodeUtf8("maintenance"), expiry);
east.Put("device:1003", [], expiry);

foreach (var entry in east.ScanPrefix("device:", limit: 100))
{
    string localKey = KvValueCodec.DecodeUtf8(entry.Key.Span);
    string text = KvValueCodec.DecodeUtf8(entry.Value.Span);
    Console.WriteLine($"{localKey}: '{text}', v{entry.Version}");
}
```

返回的 key 会剥离 Namespace 前缀，按 key 字节序升序排列。`limit: 100` 限制返回条目数，不应解释为固定的扫描耗时、磁盘 I/O 或进程内存上限。大 keyspace 的分页和容量是另一个专题；这里不把一次列表查询包装成无限规模游标。

第三条记录的空数组是**存在但内容为空**。`east.Get("device:1003")` 应得到长度为零的数组；缺失或到期得到 `null`。调用方若把空数组与 `null` 合并，会把有效业务状态误判为不存在。

## 第三步：正确解释 TTL

```csharp
KvTtlResult ttl = east.GetTimeToLive("device:1001");
Console.WriteLine($"ttl={ttl.Milliseconds}, expiry={ttl.ExpiresAtUtc:O}");

bool madePersistent = east.Persist("device:1001");
Console.WriteLine($"persisted={madePersistent}");
Console.WriteLine(east.GetTimeToLive("device:1001").Milliseconds);
```

| TTL 返回值 | 意义 | 业务解释 |
|---|---|---|
| `-2` | key 不存在或已到期 | 本次读取没有可见记录 |
| `-1` | key 存在且无到期时间 | 不受 TTL 自动到期约束 |
| 非负毫秒 | 距 UTC 到期时刻的剩余时间 | 是时效信息，不是持久性等级 |

为尚未到期的记录成功执行 `Persist` 后，应读到 `-1`。缺失、已过期或本来没有 TTL 时，`Persist` 返回 `false`。TTL 到期后记录在普通读取中不可见；底层文件空间不必在同一瞬间释放，惰性清理与后台 GC 负责后续回收。

新值的 TTL 由本次写入参数决定：不传到期时间表示没有 TTL，并不会自动保留旧到期时间。生产状态更新需要显式决定续期还是转为持久状态。远程 SDK 要求到期时间 offset 为零；示例使用 `DateTimeOffset.UtcNow`。

## 字符串和 JSON 是显式编码

KV 的权威 value 是 raw bytes，存储层不会猜测它是文本、JSON 或图像。`KvValueCodec` 的 UTF-8 编解码拒绝非法字节和未配对 surrogate，而不是静默替换字符。key 的预算按编码后的字节计算；默认单 key 上限 64 KiB，Namespace 前缀和分隔符也计入；默认 value 上限 16 MiB，宿主和协议还可能有更小的整体限制。

需要 JSON 时提供 source-generated 元数据：

```csharp
using System.Text.Json.Serialization;
using SonnetDB.Kv;

byte[] payload = KvValueCodec.EncodeJson(
    new DeviceState("online"), AppJsonContext.Default.DeviceState);
DeviceState? state = KvValueCodec.DecodeJson(
    payload, AppJsonContext.Default.DeviceState);

internal sealed record DeviceState(string Status);

[JsonSerializable(typeof(DeviceState))]
internal sealed partial class AppJsonContext : JsonSerializerContext;
```

这段独立编码示例不采用反射序列化，不引入第二套 KV 数据格式。JSON 属性键也不会因 SQL 标识符大小写规则而自动折叠。

## 第四步：用仓库样例核对持久重开

仓库提供了完整程序，固定 30 秒期限并支持取消。下面两次命令使用同一个独立数据目录；首轮写入 JSON 状态并验证原子操作，第二轮只核对重开后的值、版本、TTL 和删除记录。请在十分钟 TTL 到期前执行重开核查。

```powershell
$env:SONNETDB_CONNECTION = 'Data Source=./kv-quickstart-data;Timeout=10'
dotnet run --project samples/SonnetDB.KvQuickstart -c Release
dotnet run --project samples/SonnetDB.KvQuickstart -c Release -- --verify-reopen
```

这些命令对应仓库 Quickstart，不是上面 `./kv-device-demo` 的数据核验。样例预期输出 `PASS` 检查信息；失败时先核对数据目录、到期时间、版本和异常，不能通过反复执行写入覆盖证据。正常退出后的重开只证明该次重开合同，不代表断电耐久、备份可恢复或长期生产验收。

KV 数据位于数据库目录的 `kv/keyspaces/<name>/`，包含独立 WAL、snapshot 和 segment。恢复先加载状态，再回放更高版本的合法 WAL；损坏尾部会止于最后合法记录。当前 state 写格式 v5 可读取 v1–v4；写出新格式后，不应直接用旧二进制降级打开。每写 WAL 同步默认开启；关闭同步是持久性取舍，不能据此承诺已确认写入在断电后全部保留。

## 支持边界与排查顺序

本篇是已有模型概览之后的 KV 单任务教程，整体能力仍记为 `partial`。已有 Core、REST、Frame 和 SDK 入口；没有 Redis wire protocol、KV SQL 或跨 keyspace 事务。WAL append/sync 结果不确定时实例会拒绝继续写入，异常不能解释为“确定未提交”。先修复存储并重开，核对实际值和版本，再由业务作新决策。

[2026-09-05 远程验证记录](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/docs/audits/kv-remote-closure-20260905.md)分别记载了当时的本地合同、原生进程和真实浏览器结果；它不是本次运行结果，也不能代替修复后 Parity、七天窗口、固定硬件或生产恢复证据。当前路线以本地 ROADMAP 为准，历史报告中的“下一切片”不是本文核查日的新派单。

复查可从 [KV Keyspace 文档](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/docs/kv-keyspace.md)、[Namespace 实现](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/src/SonnetDB.Core/Kv/KvNamespace.cs)和 [KV 原子合同](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/docs/kv-atomic-contract.md)开始。源码合同、发行包和实际环境验证应分别记录。
