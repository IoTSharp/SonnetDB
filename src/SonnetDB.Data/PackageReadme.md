# SonnetDB

`SonnetDB` NuGet 包提供 SonnetDB 的 ADO.NET 访问层。源码项目、程序集和命名空间仍为 `SonnetDB.Data`，统一支持本地嵌入式和远程 SonnetDB 连接。

## 安装

```bash
dotnet add package SonnetDB
```

## 本地嵌入式连接

```csharp
using SonnetDB.Data;

using var connection = new SndbConnection("Data Source=./demo-data");
connection.Open();

using var command = connection.CreateCommand();
command.CommandText = "SELECT count(*) FROM cpu";

var count = (long)(command.ExecuteScalar() ?? 0L);
Console.WriteLine(count);
```

## 远程服务器连接

```csharp
using SonnetDB.Data;

using var connection = new SndbConnection(
    "Data Source=sonnetdb+http://127.0.0.1:5080/metrics;Token=sonnetdb-admin-token");
connection.Open();

using var command = connection.CreateCommand();
command.CommandText = "SHOW DATABASES";

using var reader = command.ExecuteReader();
while (reader.Read())
{
    Console.WriteLine(reader.GetString(0));
}
```

发布包和默认示例配置见仓库根目录 `docs/releases/`。
