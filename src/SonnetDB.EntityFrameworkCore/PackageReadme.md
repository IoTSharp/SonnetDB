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
- `ToJson` 支持整容器读写和固定标量路径查询。根 `OwnsOne(...).ToJson(...)` 容器只修改一个直接标量属性时，`SaveChanges` 使用 `json_set_value` 保留其它成员；支持 `string`、`bool`、`byte`、`short`、`int`、`long`、有限 `float`/`double` 及其可空形式。值沿用 EF 参数绑定，属性名按模型的 JSON 名称精确转义；可添加缺失的末端属性，但容器必须已经是有效对象。
- 上述单属性更新拒绝嵌套 Owned、数组、子树、值转换器、其它类型及非有限浮点值。`ExecuteUpdate` 的 JSON 更新仍不支持；ComplexProperty 或多个属性修改由 EF 生成的整容器替换保留既有行为，不能据此承诺保留模型外成员。EF Provider 不声明 Native AOT 兼容；源码验证不代表公开包或官方 BookStore/Passkey 已验收。
- 支持 `StartsWith`、`EndsWith`、`Contains` 到 `LIKE` 的基础字符串模式翻译。
- 支持 `DateTime` / `DateTimeOffset` 的 `Now`、`UtcNow`、`Today`、`Date`、常用日期分量、`AddYears` 至 `AddTicks`，以及 `DateTimeOffset.ToUnixTimeSeconds()` / `ToUnixTimeMilliseconds()` 翻译。

该 Provider 依赖 SonnetDB 当前关系表能力，完整兼容性以仓库测试和发布说明为准。
