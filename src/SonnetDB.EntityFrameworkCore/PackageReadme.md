# SonnetDB.EntityFrameworkCore

`SonnetDB.EntityFrameworkCore` 是 SonnetDB 的 Entity Framework Core Provider，基于 `SonnetDB` ADO.NET 包提供关系表 CRUD、基础查询翻译、类型映射和 migrations SQL 支持。

## 安装

```bash
dotnet add package SonnetDB.EntityFrameworkCore --version 0.1.0
```

## 最小示例

```csharp
using Microsoft.EntityFrameworkCore;
using SonnetDB.EntityFrameworkCore.Extensions;

var options = new DbContextOptionsBuilder<DeviceContext>()
    .UseSonnetDB("Data Source=./demo-data")
    .Options;

using var context = new DeviceContext(options);
await context.Database.MigrateAsync();

context.Devices.Add(new Device { Id = 1, Name = "pump", Enabled = true });
await context.SaveChangesAsync();

var online = await context.Devices
    .Where(device => device.Enabled)
    .ToListAsync();
```

## 当前范围

- `UseSonnetDB(...)` 支持连接字符串和已有 `DbConnection`。
- 支持关系表基础 CRUD、类型映射、SQL 生成和 `ToQueryString()`。
- 支持 migrations SQL、默认 `__EFMigrationsHistory` 和自定义 history table。
- 支持 `StartsWith`、`EndsWith`、`Contains` 到 `LIKE` 的基础字符串模式翻译。

该 Provider 依赖 SonnetDB 当前关系表能力，完整兼容性以仓库测试和发布说明为准。
