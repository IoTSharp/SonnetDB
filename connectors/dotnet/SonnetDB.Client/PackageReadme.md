# SonnetDB.Client

M49-D01 的原生 .NET HTTP/2 Frame 远程入口。只依赖 SonnetDB.Protocol 和 .NET，不加载 Core、Data 或 AOT 引擎。
当前提供单请求/单响应原始 Frame 基础 API、受有界终态合同保护的多帧读取入口、SQL Execute=2/payload v1 的关系表自动提交写入，以及 KV Get/Put/单页 Scan typed API；其余九模型 typed API、SQL 多帧结果的模型级解码和事务仍待 F01/C03 后续切片。

```csharp
using SonnetDB.Client;

using var client = new SndbFrameClient(new Uri("http://127.0.0.1:5080/"), token: "configured-token");
```

上例只展示配置。`SendUnaryAsync` 的 payload 必须来自所选模型的二进制 codec；`ExecuteSqlAsync` 支持关系表 CREATE/DROP TABLE、无 RETURNING 的 INSERT/UPDATE/DELETE，传输精确参数和类型化 NULL，返回影响行数。事务控制、`RETURNING`、过程多结果和其它模型操作显式拒绝。请求 payload v1 累计不超过 4 MiB，SQL 不超过 1 MiB，参数不超过 256 个。协议和真实产品 Server 验收见仓库 `docs/sql-execute-frame.md` 与 `docs/audits/m49/f01-20261017`。

`SendStreamingAsync` 仅接受 SQL Query、Vector Search、Graph Expand 和 Object Get 等已登记的多帧操作。调用方必须用对应 codec 判断终态 payload，并可设置最大帧数和累计响应字节预算；客户端会在最终帧确认 EOF 后才返回该帧，缺终态、身份不匹配、半帧、额外尾部、超预算或取消均保留 OutcomeUnknown。该入口只提供持有型原始帧，不把模型级查询解码或远程 Server/真实 HTTP/2 验收混入包合同。

KV 使用已有 Frame v1 的 Get=1、Put=2、Scan=3，无 JSON/Base64 或 REST 回落。`GetKvAsync` 返回独立拥有的值、Int64 版本和完整 UTC ticks 过期时间；缺失键为 `null`，空二进制值为零长度数组。`PutKvAsync` 返回完整确认的版本，默认覆盖写；条件写、CAS、TTL 修改等其它操作不在本片 typed API 范围。

```csharp
byte[] key = [0, 255];
long version = await client.PutKvAsync("app", "cache", key, new byte[] { 0, 128, 255 });
var value = await client.GetKvAsync("app", "cache", key);
var page = await client.ScanKvPageAsync("app", "cache", limit: 100);
// 非空页的末条 Key 可作为下一次 afterKey；调用方控制总页数、累计预算及期限。
```

`ScanKvPageAsync` 只读取一页，显式 limit 为 1～10,000，默认100；响应条目数在数组分配前受 limit 和 payload 长度约束。按字节顺序返回，afterKey 为排除性边界，不承诺跨请求快照或自动遍历全部结果。请求先按客户端 payload 预算检查再分配编码缓冲，响应继续受同一帧预算与总期限约束。期限、取消、早退释放和未知写入结果沿用传输合同；KV schema/生命周期和其它语言 SDK 另行交付。当前真实产品 Server 测试为 Windows 本机 h2c，不能据此宣称 TLS/ALPN、其它平台或正式公开包已通过。

请求与实际响应均要求 HTTP/2；不自动重试、重定向或切换 REST。
单帧编码 payload 和整个请求时间有显式上限。响应长度、关联字段、版本、标志、截断和额外帧均验证。
发送后的错误或取消保守保留 OutcomeUnknown，调用方须按操作对账，不能盲目重发写入。
payload 上限不代表进程总堆硬上限，也不能证明引擎端到端有界。
