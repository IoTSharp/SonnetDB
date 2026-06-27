# SonnetDB.EntityFrameworkCore

`SonnetDB.EntityFrameworkCore` 是 SonnetDB 的 Entity Framework Core Provider，基于 `SonnetDB` ADO.NET 包提供关系表 CRUD、基础查询翻译、类型映射和 migrations SQL 支持。

本地连接字符串中的 `Data Source=./demo-data` 指向 SonnetDB 数据库目录，不是单个数据库文件。Provider 复用 `SonnetDB` ADO.NET 包，因此本地 / 远程连接边界与 ADO.NET 包一致。

本包未声明 Native AOT 兼容。EF Core 与 ADO.NET provider 都依赖运行时模型、表达式树和反射相关能力；需要 Native AOT 的嵌入式场景建议直接使用 `SonnetDB.Core` 的 `Tsdb` API。

## 安装

```bash
dotnet add package SonnetDB.EntityFrameworkCore
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
