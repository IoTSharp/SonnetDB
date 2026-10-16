# SonnetDB.Client

M49-D01 的原生 .NET HTTP/2 Frame 远程入口。只依赖 SonnetDB.Protocol 和 .NET，不加载 Core、Data 或 AOT 引擎。
当前为单请求/单响应原始 Frame 基础 API；九模型 typed API、SQL 多帧结果、参数和事务仍待 F01/C03。

```csharp
using SonnetDB.Client;

using var client = new SndbFrameClient(new Uri("http://127.0.0.1:5080/"), token: "configured-token");
```

上例只展示配置。`SendUnaryAsync` 的 payload 必须来自所选模型的二进制 codec；不接受 REST/JSON RPC，也拒绝 SQL/向量/图等多帧操作。完整的干净包 HTTP/2 传输演示保存在仓库 `docs/audits/m49/d01-20261016/consumer`；该演示使用协议回显宿主，不是九模型 Server 验收。

请求与实际响应均要求 HTTP/2；不自动重试、重定向或切换 REST。
单帧编码 payload 和整个请求时间有显式上限。响应长度、关联字段、版本、标志、截断和额外帧均验证。
发送后的错误或取消保守保留 OutcomeUnknown，调用方须按操作对账，不能盲目重发写入。
payload 上限不代表进程总堆硬上限，也不能证明引擎端到端有界。
