# SonnetDB Ecosystem Sample

该示例用同一条 `SonnetDB.Data` 连接字符串运行：

- EF Core 关系数据 CRUD；
- ADO.NET 查询；
- `IDistributedCache` KV/TTL；
- 对象桶创建、上传和列表。

嵌入式模式：

```powershell
dotnet run --project samples/SonnetDB.EcosystemSample
```

远程模式：

```powershell
$env:SONNETDB_CONNECTION='Data Source=sonnetdb+http://127.0.0.1:5080/app;Token=<token>'
dotnet run --project samples/SonnetDB.EcosystemSample
```

远程 Token 需要具备建库、SQL、KV 和对象桶写权限。生产系统应预先建库并按最小权限发放 Token。
