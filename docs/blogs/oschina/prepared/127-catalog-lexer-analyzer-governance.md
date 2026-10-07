# 读多写少结构治理：FrozenDictionary、Lexer 快路径与 Analyzer

不可变字典适合发布后大量读取的结构，但构建快照也有成本。SonnetDB 当前按更新频率分别采用 frozen schema/catalog、增量并发 series 索引、ASCII lexer 快路径与编译器 analyzer，而不是把所有字典统一替换成 FrozenDictionary。

本文依据当前源码整理，正式 4.0.0 与 main 各自有版本边界。历史稿的结构描述需要跟随实现核查，不能继续把已经替换的旧方案称为“当前优化”。

## Frozen snapshot 与增量更新

MeasurementSchema、MeasurementCatalog 的查找索引可以通过不可变快照发布，读取者获得稳定视图。写端需要构建新版本，再发布给新读者。

但 SeriesCatalog 在高基数写入时可能频繁新增 series。当前实现使用 ConcurrentDictionary 增量维护 canonical/id 映射，写锁协调 ID 和双表插入，并对 miss 重查；不再每次新增都全量 ToFrozenDictionary。旧方案每次重建 O(N)，连续新增可能累积为 O(N²) 的构建工作。

Tag 倒排候选还要做防御性重校验，不能把“读路径查询字典”理解成跨多个索引的完整事务快照。选型应依据真实更新频率、可见性要求和构建成本。

## 名称比较保留 SQL 合同

schema 同时维护原名精确查找与未加引号的忽略大小写解析。存储名称保留创建拼写，不能为了字典快路径统一转小写。

```sql
CREATE MEASUREMENT DeviceMetrics (DeviceID TAG, Temperature FIELD FLOAT);
SELECT DeviceID, Temperature FROM devicemetrics LIMIT 10;
SELECT "DeviceID", "Temperature" FROM "DeviceMetrics" LIMIT 10;
```

未加双引号按 OrdinalIgnoreCase 绑定；带引号按 Ordinal 精确绑定，`"deviceid"` 不匹配保存的 `DeviceID`。同一命名空间也不能新建仅大小写不同的名称。性能索引要支持这一合同，不能改变用户看到的 schema 拼写。

## Options 使用值对象

`TsdbOptions` 是 sealed record，属性采用 init：

```csharp
using SonnetDB.Engine;

var options = new TsdbOptions { RootDirectory = "./options-demo" };
var scalarOptions = options with { UseSimdNumericAggregates = false };
Console.WriteLine(scalarOptions.RootDirectory);
```

`with` 创建新配置，原对象不会被该语句修改。record 也不自动使其内部引用的任何对象都深度不可变；嵌套配置和集合仍要按实际类型判断。

## Lexer 采用 ASCII 快路径并保留 fallback

SqlLexer 缓存 `SearchValues<char>` 用于高频字符分类，覆盖 ASCII 空白、标识符、数字等常见输入。非 ASCII 标识符、Unicode 空白和引号转义仍需正确 fallback。

这避免每次解析重复构造字符集合，但不能把优化简化成“只接受 ASCII”。测试应该同时覆盖英文常规查询、Unicode 名称、注释、duration、运算符及 `""` 引号转义，并与旧语义对拍。

## Analyzer 约束后续回退

仓库 `.editorconfig` 对空数组分配、Count/Any、Dictionary 查找、SearchValues 和字符串比较等规则配置 warning 或 suggestion。warning 与 TreatWarningsAsErrors 一起提供编译期反馈。

Analyzer 是候选提示，修改热路径仍需保证单次枚举、惰性求值和并发语义。不能为了“消掉一个 LINQ”重新枚举有副作用的数据源，也不能抑制 AOT/JSON 警告来通过构建。

这组治理的收益在于使结构选择可复核、后续退化可发现。具体解析时间和高基数写入收益需要对应基准，源码形态本身不能代替性能证据。

参考：[SeriesCatalog](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Catalog/SeriesCatalog.cs)、[MeasurementSchema](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Catalog/MeasurementSchema.cs)、[SqlLexer](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Sql/SqlLexer.cs)、[Analyzer 配置](https://github.com/IoTSharp/SonnetDB/blob/main/.editorconfig)。
