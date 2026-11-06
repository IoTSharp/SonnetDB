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
- 同一嵌套 `OwnsOne` 多属性变化时支持 EF 生成的子树替换；只从当前修改命令的最多 1024 个 entry/type 及 ownership 祖先解析路径，歧义及无法匹配模型的路径拒绝。替换保留目标外的兄弟，目标子树内部及整容器替换中的模型外成员不承诺保留。EF Provider 不声明 Native AOT 兼容；源码验证不代表公开包或官方 BookStore/Passkey 已验收。
- `OwnsMany` 支持根/嵌套对象数组的跟踪属性、子树与整集合更新，数组序号只在模型集合位置有界解析。JSON null/缺失集合由当前 EF 物化为 null，与空数组不同。含嵌套集合的父数组结构变化后，继续变更前须显式清理跟踪并重新加载；保留 EF 10 的 identifying key 拒绝及无下一次写入回归，不自动修补 EF 内部身份或读改写。三协议、转换器、回滚和重开范围见 `docs/audits/issue-394-20261105.md`，新公开包与实际应用仍待验收。
- `ExecuteUpdate` / `ExecuteUpdateAsync` 支持 `OwnsOne` 或 `ComplexProperty` JSON 容器中的固定标量路径，类型与上述原生标量合同一致，属性路径和值均参数化。单表、无分组/排序/分页的更新在一条 SQL 中完成，最多 64 个 setter；同列按 `SetProperty` 顺序嵌套 `json_set_value`，重复路径以后一次为准，各右侧表达式读取更新前的行。可同时修改普通列，并保留 JSON 路径外成员。现有父级须为对象，允许新增缺失末端属性和写入 JSON null；无效父级使整条语句失败。
- JSON `ExecuteUpdate` 也支持根或嵌套 `OwnsMany` 的固定非负 Int32 常量元素下标，目标须由 EF 翻译为直接 JSON 标量路径。索引器及适用的 `ElementAt` 形状可更新元素属性与嵌套元素属性，数字 JSON 属性名仍作为属性键；路径整体参数化，不支持动态下标，也不追加不存在的数组元素。缺失元素或错误父级使整条 SQL 更新失败并保留各行原值。
- JSON `ExecuteUpdate` 不接受原始标量集合元素、条件元素目标（例如生成条件表达式的嵌套 `ElementAtOrDefault`）、集合元素右值、对象/数组子树替换、Guid、日期、二进制或带转换器属性；这些形状保持写前拒绝边界。批量更新不自动应用跟踪实体的并发 Token，需要在 `Where` 中显式携带 Token 并检查影响行数。事务、同列路径及真实嵌入式/REST/HTTP2 Frame 验证范围见 `docs/audits/issue-395-20261105.md` 与 `docs/audits/issue-395-collection-update-20261106.md`；源码切片不等于公开 NuGet 消费者已验收。
- SQL `json_set_value(json, path, value, TRUE)` 显式将第三个参数当 JSON 片段，可替换对象/数组；省略第四参数或传 `FALSE` 时保持原 SQL 标量语义，JSON 外观字符串仍为字符串。片段与源文本各限 8 MiB UTF-8，共用 100000 输入节点预算，结果另限 8 MiB/100000 节点/64 层，保留 10 秒协作式预算及 SQL 取消。EF/DOM/输出分配不计入编码 scratch 的 5120 字节上限，不能把该数值当总内存上限。
- 支持 `StartsWith`、`EndsWith`、`Contains` 到 `LIKE` 的基础字符串模式翻译。
- 支持 `DateTime` / `DateTimeOffset` 的 `Now`、`UtcNow`、`Today`、`Date`、常用日期分量、`AddYears` 至 `AddTicks`，以及 `DateTimeOffset.ToUnixTimeSeconds()` / `ToUnixTimeMilliseconds()` 翻译。

该 Provider 依赖 SonnetDB 当前关系表能力，完整兼容性以仓库测试和发布说明为准。
