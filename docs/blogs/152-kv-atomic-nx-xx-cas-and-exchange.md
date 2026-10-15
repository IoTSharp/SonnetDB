---
title: SonnetDB KV 原子操作：NX/XX、CAS 与交换值
categories: SonnetDB
tags: KV, CAS, Concurrency, CSharp
draft: false
---

# SonnetDB KV 原子操作：NX/XX、CAS 与交换值

SonnetDB 的 KV 原子操作让设备状态更新在存储端判断存在性或版本，并把读取旧值与替换、删除合为一次操作。本文用 NX/XX、CAS 和交换值处理“两个客户端同时修改同一设备状态”的问题，并说明条件失败与未知提交结果为何必须分开。

![SonnetDB KV 原子操作：NX、XX、CAS 与交换值](https://img2024.cnblogs.com/blog/1725297/202610/1725297-20261013111346854-1786810352.png)

图：AI 主题示意图，并非实测截图；单 key 原子性、版本冲突与未知提交边界以正文合同为准。

## 版本和复现入口

本文于 2026-10-13 对照本地提交 `dcadffc7137f5653ec46c55a4171cf5f2d83de03`，原子合同、客户端和 Quickstart 与公开固定提交 `5424b66672e37f49a2a5633fdde0b54a9b1f0290` 一致。GitHub 最新正式 Release 与 NuGet `SonnetDB.Core` 的只读核查结果仍为 **4.0.0**。本文说明当前源码合同，不把源码接口存在等同于所有已发行包都支持。

先使用固定源码中的 [完整样例](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/samples/SonnetDB.KvQuickstart/Program.cs)。本次写稿没有运行下列代码，输出表是条件成立时的预期结果，不是本机并发、吞吐或生产实测。

```powershell
$env:SONNETDB_CONNECTION = 'Data Source=./kv-atomic-demo;Timeout=10'
dotnet run --project samples/SonnetDB.KvQuickstart -c Release
```

样例负责初始化数据库，操作期限 30 秒。远程应用应由管理员先建库，给应用目标库读写权限，再使用 `--existing-database`。REST 连接示例为 `Data Source=sonnetdb+http://localhost:5000/demo;Token=<token>;Timeout=10;Protocol=rest`；令牌从环境或凭据服务提供，不能写入代码。`frame-http2` 需要专用 HTTP/2 监听器，不能直接套用 HTTP/1.1 管理监听器。

## 第一步：用 NX 或 XX 判断是否应该写

下面片段可放入引用 `SonnetDB.Data` 的异步控制台程序；先按上述入口初始化同一数据库。使用独占 key，避免拿真实设备状态做练习。

```csharp
using SonnetDB.Data.Kv;
using SonnetDB.Kv;

using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
using var kv = new SndbKvClient(
    Environment.GetEnvironmentVariable("SONNETDB_CONNECTION")!);
var ct = deadline.Token;
const string space = "device_cache";
const string tenant = "site_east";
const string key = "atomic_demo_001";
var expiry = DateTimeOffset.UtcNow.AddMinutes(5);

var created = await kv.SetConditionalAsync(space, tenant, key,
    KvValueCodec.EncodeUtf8("online"), KvSetCondition.IfNotExists, expiry, ct);
Console.WriteLine($"NX applied={created.Applied}, version={created.Version}");

var duplicate = await kv.SetConditionalAsync(space, tenant, key,
    KvValueCodec.EncodeUtf8("duplicate"), KvSetCondition.IfNotExists, expiry, ct);
Console.WriteLine($"NX duplicate={duplicate.Applied}, version={duplicate.Version}");

var changed = await kv.SetConditionalAsync(space, tenant, key,
    KvValueCodec.EncodeUtf8("maintenance"), KvSetCondition.IfExists, expiry, ct);
Console.WriteLine($"XX applied={changed.Applied}, version={changed.Version}");
```

在 key 初始缺失、未到期且无其他写者时，第一条 NX 应成功，第二条 NX 应返回 `Applied=false, Version=null`，XX 应成功。条件未满足是明确业务结果，不是网络异常；它不会覆盖现有值，也不能伪造一个成功版本。

NX 只判断当前不存在，XX 只判断当前存在。XX 不知道客户端读到的是不是最新值，因此无法独自防止覆盖别人的修改。TTL 到期的记录在这些判断中视为不存在；“文件里还有旧记录”不意味着 XX 应成功。

## 第二步：用 CAS 拒绝过时版本

CAS 比较权威版本，匹配才提交新值。下面顺序模拟两个调用方持有同一个旧版本，便于检查合同；它不是多线程压测。

```csharp
var current = await kv.GetAsync(space, tenant, key, ct)
    ?? throw new InvalidOperationException("Record missing or expired.");

var first = await kv.CompareAndSetAsync(space, tenant, key, current.Version,
    KvValueCodec.EncodeUtf8("ready"), expiry, ct);
var stale = await kv.CompareAndSetAsync(space, tenant, key, current.Version,
    KvValueCodec.EncodeUtf8("stale proposal"), expiry, ct);

Console.WriteLine($"first={first.Succeeded}, new={first.NewVersion}");
Console.WriteLine($"stale={stale.Succeeded}, current={stale.CurrentVersion}");
```

若第一条成功且没有其他修改，第二条应冲突，`Succeeded=false`、`NewVersion=null`，并返回判断时的 `CurrentVersion`。重新读取后应由应用决定合并、拒绝或提交新的修改；不要用自动循环把 CAS 冲突变成无条件覆盖。期望版本 `0` 表示只在 key 不存在时创建，已经过期也按不存在判断。

版本使用 64 位整数。在 JavaScript 管理界面应使用合同提供的十进制 `*VersionText`，不能把大于 `2^53` 的版本先转为普通 number 再声称比较精确。Namespace 仍只是 key 前缀，不提供跨 namespace 的锁或权限隔离。

## 第三步：让旧值与变更来自同一原子操作

```csharp
var exchanged = await kv.GetAndSetAsync(space, tenant, "exchange_demo", [],
    expiry, ct);
var consumed = await kv.GetAndDeleteAsync(space, tenant, "exchange_demo", ct);
var repeated = await kv.GetAndDeleteAsync(space, tenant, "exchange_demo", ct);

Console.WriteLine($"old found={exchanged.PreviousEntry is not null}");
Console.WriteLine($"deleted empty={consumed.PreviousEntry?.Value.Length == 0}");
Console.WriteLine($"repeat missing={repeated.PreviousEntry is null}");
Console.WriteLine($"repeat mutation={repeated.MutationVersion}");
```

在独占且未到期的 key 上，取删返回的旧值应是长度零的数组，随后重复取删应没有 previous 和 mutation version。第一次交换是否有旧值，取决于执行前该 key 的状态。

独立 `Get` 再 `Set` 或 `Delete` 会留下竞争窗口；原子交换把旧记录副本与本次变更绑定在同一操作里。返回的旧值不是可继续修改的内部存储引用。`GetAndSet` 即使旧值不存在也写入新值，TTL 完全取决于本次参数，`null` 表示移除旧 TTL。

已过期 key 的取删没有可见 previous/mutation，但内部可能按惰性过期语义提交清理 tombstone。不要用空 mutation version 推断“没有任何底层 WAL 活动”。这些操作仍只覆盖单 key 合同，不等于跨 keyspace、跨模型事务。

## 远程合同也要核对，而不只看 HTTP 成功

REST 原子入口为 `POST /v1/db/{db}/kv/{keyspace}/{action}`：`set-conditional`、`get-and-set`、`get-and-delete`。value 是 Base64，空数组编码为 `""`；缺失与空值由 `previous.found` 区分。condition 使用 `Always=0`、NX `1`、XX `2`。SDK 直接调用原子 Core，拒绝缺字段、矛盾版本或损坏的原子响应。

Frame 在原有 v1 header 上追加 opcode，并保留旧 get/put/scan 布局。写请求发送后失败，不会自动转 REST 重发；KV HTTP handler 也禁用自动重定向，避免 307/308 隐式再次 POST。但连接协商、代理和服务网格仍可能有自己的策略，不能据此承诺端到端 exactly-once。

## 已知冲突与未知提交必须分开

| 得到的结果 | 能确认什么 | 下一步 |
|---|---|---|
| NX/XX 未应用 | 条件判断未提交本次替换 | 使用现有状态或作新的业务决策 |
| CAS 明确冲突 | 期望版本不匹配，没有提交本次新值 | 重新读取，人工或业务规则决定合并 |
| 参数/权限错误 | 请求合同或访问条件不满足 | 修正参数、库、权限；不要重复原参数 |
| 取消、断连、超时或存储 I/O 异常 | 可能没有完整提交确认 | 停止自动重试，核对目标、状态、版本与日志 |

Core 在第一次 WAL append 前检查取消；append 开始后不能用 token 撤回同步提交。网络取消又可能先于服务端结果返回，所以客户端收到取消不代表回滚。WAL append/sync 不确定时实例会 fail closed 拒绝继续写入，需解决存储问题后重开核对。

完整样例把应用 operation ID 放入 JSON，便于核对当前状态；它不是服务端幂等键、分布式租约或持久操作历史。尤其是原子取删：若旧值已经删除但响应丢失，再取删无法恢复旧值。需要确认、重投和可靠消费，应采用 MQ 的独立合同。

## 验证范围与支持边界

本专题仍为 `partial`。它与已发布的 KV/文档模型概览、上篇字节值和 TTL 入门分别解决不同任务。现有 [远程闭环审计](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/docs/audits/kv-remote-closure-20260905.md)记录的是 2026-09-05 的本地合同、原生重开与真实浏览器结果，本次没有重新运行这些检查。审计保留的旧失败和后续补丁顺序不能合并成“全部最终验证通过”。

固定硬件、ARM64、完整 Parity、七天窗口、断电/磁盘满、生产恢复与大 keyspace 容量分别验收。不能把 mock、一次正常重开或 API 成功响应当作这些证据。

按固定版本复查 [KV 原子合同](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/docs/kv-atomic-contract.md)、[SDK 原子实现](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/src/SonnetDB.Data/Kv/SndbKvClient.Atomic.cs)和 [条件操作测试](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/tests/SonnetDB.Core.Tests/Kv/KvConditionalOperationsTests.cs)，再在自己的独占测试库记录实际结果和异常边界。
