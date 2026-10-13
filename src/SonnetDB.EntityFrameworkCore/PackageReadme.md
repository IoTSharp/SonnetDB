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
- 嵌套 `OwnsOne` 的属性也支持上述更新；每一级属性名均取自唯一 ownership 链的模型元数据，固定路径最多 64 个属性片段和 1024 字符，现有父级必须为对象。数组、Guid、日期、二进制和带转换器的属性使用 EF 自身的 JSON metadata writer 生成完整片段并参数化写入；数组支持整替及 EF 检测到的原地元素修改。没有 JSON writer 的类型与非有限浮点值仍拒绝。
- 同一嵌套 `OwnsOne` 多属性变化时支持 EF 生成的子树替换；只从当前修改命令的最多 1024 个 entry/type 及其唯一 ownership 祖先解析路径，歧义或集合索引路径拒绝。替换保留目标外的兄弟，目标子树内部及整容器替换中的模型外成员不承诺保留。`OwnsMany` 元素路径和 JSON `ExecuteUpdate` 仍不支持。EF Provider 不声明 Native AOT 兼容；源码验证不代表公开包或官方 BookStore/Passkey 已验收。
- SQL `json_set_value(json, path, value, TRUE)` 显式将第三个参数当 JSON 片段，可替换对象/数组；省略第四参数或传 `FALSE` 时保持原 SQL 标量语义，JSON 外观字符串仍为字符串。片段与源文本各限 8 MiB UTF-8，共用 100000 输入节点预算，结果另限 8 MiB/100000 节点/64 层，保留 10 秒协作式预算及 SQL 取消。EF/DOM/输出分配不计入编码 scratch 的 5120 字节上限，不能把该数值当总内存上限。
- 支持 `StartsWith`、`EndsWith`、`Contains` 到 `LIKE` 的基础字符串模式翻译。
- 支持 `DateTime` / `DateTimeOffset` 的 `Now`、`UtcNow`、`Today`、`Date`、常用日期分量、`AddYears` 至 `AddTicks`，以及 `DateTimeOffset.ToUnixTimeSeconds()` / `ToUnixTimeMilliseconds()` 翻译。

该 Provider 依赖 SonnetDB 当前关系表能力，完整兼容性以仓库测试和发布说明为准。
