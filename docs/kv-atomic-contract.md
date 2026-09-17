# KV 原子客户端合同

本页对应 M36 #316 / KV-001，以及 #310/#311 的 KV 子集。既有 Core 存储、namespace、UTF-8/JSON codec 与 WAL 是唯一实现；REST、Frame 和 SDK 均直接调用原子操作，没有客户端先读再写的模拟。二进制存储格式未变更。

## 最小成功样例

约 20 行调用代码，使用 `SonnetDB.Data`，目标库需已存在。`SONNETDB_CONNECTION` 可设为 `Data Source=./kv-demo;Timeout=10`，或 `Data Source=sonnetdb+http://localhost:5000/demo;Token=<token>;Timeout=10;Protocol=rest`。`frame-http2` 指向专用 HTTP/2 监听器；`auto` 初始尝试 Frame，读请求已探测为 REST 后可直接使用 REST。

```csharp
using SonnetDB.Data.Kv;
using SonnetDB.Kv;

using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
using var kv = new SndbKvClient(Environment.GetEnvironmentVariable("SONNETDB_CONNECTION")!);
var ct = deadline.Token;
const string space = "device_cache";
const string tenant = "site_east";
const string key = "device_001";
var expiry = DateTimeOffset.UtcNow.AddMinutes(5);
byte[] online = KvValueCodec.EncodeUtf8("online");
var created = await kv.SetConditionalAsync(space, tenant, key, online,
    KvSetCondition.IfNotExists, expiry, ct);
Console.WriteLine($"NX applied={created.Applied}, version={created.Version}");
var changed = await kv.GetAndSetAsync(space, tenant, key, [], expiry, ct);
Console.WriteLine($"previous found={changed.PreviousEntry is not null}");
var removed = await kv.GetAndDeleteAsync(space, tenant, key, ct);
Console.WriteLine($"empty value={removed.PreviousEntry?.Value.Length == 0}");
var repeated = await kv.GetAndDeleteAsync(space, tenant, key, ct);
Console.WriteLine($"missing={repeated.PreviousEntry is null}, version={repeated.MutationVersion}");
```

[完整可运行样例](../samples/SonnetDB.KvQuickstart/README.md)补充 source-generated JSON、CAS 冲突、应用 operation ID、错误/取消处理和 `--verify-reopen`。应用操作 ID 是核对线索，不是服务端幂等键或持久操作历史。初始化数据库需要 admin；应用对现有库使用读写权限及 `--existing-database`。

## 原生语义

| 操作 | 合同 |
| --- | --- |
| `SetConditionalAsync` | `Always=0`、`IfNotExists=1` (NX)、`IfExists=2` (XX)。条件不满足返回 `Applied=false, Version=null`，不覆盖已有值。 |
| `GetAndSetAsync` | 原子返回旧记录副本和本次 mutation version，缺失旧记录时仍写入新值。 |
| `GetAndDeleteAsync` | 原子返回旧记录和删除版本；缺失时 previous/mutation 均为空。重复执行不会重获第一次删除的值。 |
| `CompareAndSetAsync` | 匹配可见版本才写入；期望 `0` 表示不存在，冲突返回当前版本而不写入。 |
| `ExpireAsync` / `PersistAsync` | 更新 UTC 到期时间或移除 TTL，返回是否成功。缺失时为 false。 |
| `GetTimeToLiveAsync` | 缺失为 -2 ms，永不过期为 -1 ms，否则返回剩余毫秒和绝对 UTC 到期时间。 |

空字节数组是存在的值，与缺失不同。到期记录在 NX/XX/CAS/交换中视为不存在；惰性过期清理可能写入 tombstone，即使没有可见 previous/mutation。新写入的 TTL 完全由本次参数决定，null 表示移除旧 TTL，不隐式保留。必须传 UTC offset 0；Frame 保留 100 ns ticks，不降为毫秒存储。

namespace 是 `name + ":" + key` 的前缀视图，root 使用空名称；不是权限或事务隔离边界。SDK 返回的 entry key 是局部 key。严格 UTF-8 拒绝未配对 surrogate；合成 key 必须非空、最多 64 KiB，namespace 的字节和分隔符计入预算。非空 namespace 允许空局部 key。Core 默认 value 上限 16 MiB，HTTP 请求和 Frame payload 另受宿主/协议整体上限约束；不是大对象传输接口。需要 JSON 时只能显式提供 source-generated `JsonTypeInfo<T>`。

## REST v1

以下均为 `POST /v1/db/{db}/kv/{keyspace}/{action}`，key 已包含 namespace 前缀。value 为 Base64，空值为 `""`，null 无效。

| action | 请求 | 响应 |
| --- | --- | --- |
| `set-conditional` | `{key,value,condition,expiresAtUtc?}`，condition 必填数字 0/1/2 | `{applied,version?,versionText?}` |
| `get-and-set` | `{key,value,expiresAtUtc?}` | `{previous:{found,value?,version?,expiresAtUtc?},mutationVersion?,previousVersionText?,mutationVersionText?}` |
| `get-and-delete` | `{key}` | 同上 |

原有 `set/cas/expire/persist/get/ttl` URL 和字段保持兼容。新响应的十进制 `*VersionText` 字段保留 64 位整数，供 JavaScript 显示；原数字字段仍保留。null 字段可省略，`previous.found` 区分缺失与空值。SDK 拒绝缺少 `applied`/`previous.found`、非正版本、存在性矛盾、坏 JSON 等响应，不把损坏解释为未写入。

原子端点处理器设置 `X-SonnetDB-Contract-Version: 1` 和 `X-Request-ID` (服务端 trace identifier)，包括其业务错误；在端点之前拒绝的匿名请求不保证这两个头。Frame 使用 stream ID 关联。不要把 request ID 当作幂等键。

| 错误 | REST / Frame 行为 | 调用方处理 |
| --- | --- | --- |
| `bad_request` | REST 400；已接收的 Frame 业务参数错误为带内错误 | 修正 key、value、条件、版本或 UTC；不要重试原参数。 |
| `db_not_found` / `forbidden` | REST 404/403；Frame 带内错误 | 核对目标库和权限。匿名在 HTTP 层拒绝 401。 |
| `unsupported_version` / `unsupported_op` | 不支持的首帧信封返回 HTTP 400 JSON；已开始响应后为带内错误 | 使用匹配的 Server/SDK，不自动重放写入。 |
| `bad_frame` | 畸形帧依是否开始响应返回 HTTP 或带内错误 | 修正协议；响应损坏时核对已发送写入的结果。 |
| `kv_write_timeout` | 写锁等待超时，REST 503 或 Frame 带内错误 | 检查负载/检查点并核对结果。 |
| `kv_io_error` | 存储错误及现有 checkpoint 背压 IOException，REST 500 或 Frame 带内错误 | 可能未知提交；检查服务端存储，修复后重开并核对。 |

SDK 通过 `SndbServerException.Error` 保留应用 code，Frame HTTP JSON 错误同样保留；不可解析的传输失败为 `frame_transport_error`。损坏 REST 合同为 `InvalidDataException`，请求取消/HTTP 客户端超时传播 `OperationCanceledException`；嵌入式锁等待超时为 `TimeoutException`，远程锁等待超时为 `SndbServerException` (`kv_write_timeout`)。不要把 HTTP 状态、异常或连接断开推断为一定未提交。

## Frame 扩展

Frame header 版本仍为 1，旧 KV opcode 1/2/3 的 get/put/scan 布局未变。新增 4 条件写、5 交换、6 取删、7 CAS、8 expire、9 persist、10 TTL。详见 [codec](../src/SonnetDB.Core/Protocol/KvAtomicFrameCodec.cs)及[独立字节 fixture](../tests/SonnetDB.Core.Tests/Protocol/KvAtomicFrameCodecTests.cs)。

4/5/7/8 请求顺序为 var-string db、var-string keyspace、var-byte-array key、byte condition、little-endian int64 expectedVersion、expiry 标记及可选 int64 UTC ticks、var-byte-array value。非条件操作 condition=0，非 CAS expectedVersion=0，expire 必须有 expiry 且 value 为空。6/9/10 复用 get 的 db/keyspace/key 请求体。版本用 int64，0 表示无 mutation；响应显式带旧值存在标记。单条原子响应必须匹配 version/service/op/stream ID，不能接受多帧或尾部残留。

## 取消与重试

Core 的新增 token 重载在进入前、锁等待、checkpoint 背压等待及第一次 WAL append 前检查取消。可取消锁等待每 50 ms 检查，受 `CheckpointWriteBackpressureTimeout` 上限控制；旧签名保留。append 已开始后不再因为 token 取消抛出“未完成”，包括已经提交过期清理后继续执行原子替换。同步 I/O 本身不能被 token 撤回。

WAL append/sync 结果不确定时，相关单 key 写路径进入拒写状态，必须解决存储问题后重开并核对恢复结果。网络取消可以先于服务端提交结果返回，因此 UI/SDK 仍可能得到未知结果，不能承诺回滚。

SDK 的 KV 写禁用发送后的 Frame -> REST 回落，KV HTTP handler 禁用自动重定向，307/308 不会自动再次 POST。协议库可以重试其明确认定尚未处理的连接协商，代理/服务网格也可能有自己的策略；这里不是网络层 exactly-once 承诺。旧 Server 不支持新 opcode 时应明确选择兼容协议或升级。未知取删结果不能通过重试恢复旧值；可靠消费应使用 MQ 合同。

工作台保留现有审批流程，固定数据库/keyspace/连接/凭据；变化时取消并使旧审批失效。已发送批次不自动排回队列，取消和网络失败标记 unknown，保留已完成项的原目标历史。单批最多 1000 操作/120 秒，不是跨 key 事务。旧值只展示在当前结果，历史不持久保存旧 value 内容。

## 验收边界

当前实现与本地证据见 [KV 远程验证记录](audits/kv-remote-closure-20260905.md)。M36 #317 的大 keyspace cursor/pipeline/诊断、九模型 #310/#311 的其余子集、M20 light/full 与七天窗口、备份和生产门禁分别验收。
