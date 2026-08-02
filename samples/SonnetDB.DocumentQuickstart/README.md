# SonnetDB Document Quickstart

该示例用同一套 `SndbDocumentClient` API 演示：

- 类型化 filter、projection、sort 与分页 cursor；
- `$mul` 和 `findOneAndUpdate`；
- mixed Bulk 的 collection 内原子边界和 `requestId` 安全重试。

嵌入式模式：

```powershell
dotnet run --project samples/SonnetDB.DocumentQuickstart
```

远程模式：

```powershell
$env:SONNETDB_CONNECTION='Data Source=sonnetdb+http://127.0.0.1:5080/app;Token=<token>'
dotnet run --project samples/SonnetDB.DocumentQuickstart
```

远程 Token 需要目标数据库的 Document 读写权限。Bulk 的 `requestId` 在 24 小时窗口内绑定请求内容；相同 ID 携带不同 payload 会得到 `idempotency_conflict`，不能把它当作通用业务去重键。
