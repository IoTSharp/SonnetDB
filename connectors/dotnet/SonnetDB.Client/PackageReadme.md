# SonnetDB.Client

M49-D01 的原生 .NET HTTP/2 Frame 远程入口。只依赖 SonnetDB.Protocol 和 .NET，不加载 Core、Data 或 AOT 引擎。
当前提供单请求/单响应原始 Frame 基础 API、受有界终态合同保护的多帧读取入口，以及 SQL Execute=2/payload v1 的关系表自动提交写入入口；九模型 typed API、SQL 多帧结果的模型级解码和事务仍待 F01/C03 后续切片。

```csharp
using SonnetDB.Client;

using var client = new SndbFrameClient(new Uri("http://127.0.0.1:5080/"), token: "configured-token");
```

上例只展示配置。`SendUnaryAsync` 的 payload 必须来自所选模型的二进制 codec；`ExecuteSqlAsync` 支持关系表 CREATE/DROP TABLE、无 RETURNING 的 INSERT/UPDATE/DELETE，传输精确参数和类型化 NULL，返回影响行数。事务控制、`RETURNING`、过程多结果和其它模型操作显式拒绝。请求 payload v1 累计不超过 4 MiB，SQL 不超过 1 MiB，参数不超过 256 个。协议和真实产品 Server 验收见仓库 `docs/sql-execute-frame.md` 与 `docs/audits/m49/f01-20261017`。

`SendStreamingAsync` 仅接受 SQL Query、Vector Search、Graph Expand 和 Object Get 等已登记的多帧操作。调用方必须用对应 codec 判断终态 payload，并可设置最大帧数和累计响应字节预算；客户端会在最终帧确认 EOF 后才返回该帧，缺终态、身份不匹配、半帧、额外尾部、超预算或取消均保留 OutcomeUnknown。该入口只提供持有型原始帧，不把模型级查询解码或远程 Server/真实 HTTP/2 验收混入包合同。

请求与实际响应均要求 HTTP/2；不自动重试、重定向或切换 REST。
单帧编码 payload 和整个请求时间有显式上限。响应长度、关联字段、版本、标志、截断和额外帧均验证。
发送后的错误或取消保守保留 OutcomeUnknown，调用方须按操作对账，不能盲目重发写入。
payload 上限不代表进程总堆硬上限，也不能证明引擎端到端有界。
