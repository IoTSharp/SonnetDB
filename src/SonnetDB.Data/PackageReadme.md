# SonnetDB

`SonnetDB` NuGet 包提供 SonnetDB 的 ADO.NET 访问层。源码项目、程序集和命名空间仍为 `SonnetDB.Data`，统一支持本地嵌入式和远程 SonnetDB 连接。

本地嵌入式连接的 `Data Source=./demo-data` 指向数据库目录，不是单个数据库文件。目录布局与 `SonnetDB.Core` 一致，适合随应用进程内打开；远程连接则通过 HTTP 访问 SonnetDB Server。

本包未声明 `IsAotCompatible=true`。ADO.NET 基类和 `DbDataReader.GetSchemaTable()` 等路径在 BCL 内部存在反射边界；需要 Native AOT 的嵌入式场景建议直接使用 `SonnetDB.Core` 的 `Tsdb` API。远程客户端内部使用 source-generated JSON 上下文，但包级别仍按 ADO.NET 兼容边界声明为非 AOT 包。

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

## Microsoft.Extensions.VectorData

`SonnetDB.Data.VectorData` 提供 `Microsoft.Extensions.VectorData` adapter。默认情况下，一个 VectorData collection 会映射为 SonnetDB `DOCUMENT COLLECTION`：record key 存为文档 `id`，数据字段和向量字段存入 JSON document，向量搜索通过 `vector_search(...)` 读取 JSON number array。

```csharp
using Microsoft.Extensions.VectorData;
using SonnetDB.Data;
using SonnetDB.Data.VectorData;

await using var connection = new SndbConnection("Data Source=./demo-data");
using var store = new SonnetDBVectorStore(connection);

var collection = store.GetCollection<string, KnowledgeRecord>("knowledge");
await collection.EnsureCollectionExistsAsync();

await collection.UpsertAsync(new KnowledgeRecord
{
    Id = "kb-1",
    Title = "Pump alarm guide",
    Site = "north",
    Embedding = [1, 0, 0],
});

var results = await collection
    .SearchAsync(
        new ReadOnlyMemory<float>([1, 0, 0]),
        top: 5,
        new VectorSearchOptions<KnowledgeRecord>
        {
            IncludeVectors = true,
            Filter = record => record.Site == "north",
        })
    .ToArrayAsync();

public sealed class KnowledgeRecord
{
    [VectorStoreKey]
    public string Id { get; set; } = string.Empty;

    [VectorStoreData]
    public string Title { get; set; } = string.Empty;

    [VectorStoreData]
    public string Site { get; set; } = string.Empty;

    [VectorStoreVector(3, DistanceFunction = DistanceFunction.CosineDistance)]
    public float[] Embedding { get; set; } = [];
}
```

`measurement` 仍用于时序数据和时序 `VECTOR(N)` 列；通用 VectorData collection 不默认映射到 measurement，避免把非时序记录强行绑定到时间轴。

发布包和默认示例配置见仓库根目录 `docs/releases/`。
