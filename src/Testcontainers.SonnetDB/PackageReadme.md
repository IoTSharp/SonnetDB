# Testcontainers.SonnetDB

`Testcontainers.SonnetDB` 提供 SonnetDB Server 的 Testcontainers for .NET 模块，适合集成测试中临时启动 `iotsharp/sonnetdb` 容器。

## 安装

```bash
dotnet add package Testcontainers.SonnetDB
```

## 基本用法

```csharp
using Testcontainers.SonnetDB;

await using var sonnetdb = new SonnetDbBuilder()
    .WithDatabase("iotsharp")
    .Build();

await sonnetdb.StartAsync();

var connectionString = sonnetdb.GetConnectionString();
var telemetryConnectionString =
    sonnetdb.GetConnectionString("telemetry") + ";Measurement=TelemetryData;AutoCreate=true";
```

默认容器端口为 `5080`，健康检查使用 `/healthz`。模块会在容器启动后自动创建 `WithDatabase(...)` 指定的默认数据库；如需更多数据库，可调用 `CreateDatabaseAsync(...)`。

```csharp
await sonnetdb.CreateDatabaseAsync("events");
await sonnetdb.CreateDatabaseAsync("cache");
```

如果需要使用测试构建出的本地镜像，可以传入镜像名或 `IImage`：

```csharp
await using var sonnetdb = new SonnetDbBuilder("iotsharp/sonnetdb:test")
    .WithAdminToken("test-token")
    .Build();
```
