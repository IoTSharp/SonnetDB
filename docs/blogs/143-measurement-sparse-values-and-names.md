---
title: SonnetDB Measurement 实用建模：TAG、FIELD、稀疏值与原始名称
categories: SonnetDB
tags: 时序数据库, 数据建模, SQL
description: 用同一份设备 schema 检查稀疏值、原始名称和 Point 摄取的一致性，避免把缺值与关系表约束混为一谈。
draft: false
---

# SonnetDB Measurement 实用建模：TAG、FIELD、稀疏值与原始名称

在 SonnetDB 中，设备每次采样不一定上报全部字段。有时只有温度，有时还带健康状态；采集器升级后可能把 `Temperature` 写成 `temperature`。这两件事应分别处理：缺少观测值用稀疏字段表达，名称变化则绑定到已经保存的 schema 拼写。

![SonnetDB Measurement 建模：TAG、FIELD 与稀疏值](https://img2024.cnblogs.com/blog/1725297/202610/1725297-20261010102457698-543809467.png)

图：AI 主题示意图，复用本篇公众号封面，并非实测截图；示例与支持边界以正文为准。

本文接续已发布的[数据模型基础](https://www.cnblogs.com/IoTSharp/p/20023349)和[SQL 名称合同](https://www.cnblogs.com/IoTSharp/p/23202334)，用一组可核查的采样说明两者如何组合。它不重新宣布已有功能。

事实核查日期为 **2026-10-09**，源码基线为 `5424b66672e37f49a2a5633fdde0b54a9b1f0290`。GitHub 已有 `v4.0.0` Release，NuGet.org 已有 `SonnetDB.Core 4.0.0`；以下步骤引用该源码基线，不把后来开发树中的实现自动算作发行包能力。本次检查了源码和已有测试，**未执行以下程序，也未验证远程 Server、固定硬件性能或长期生产恢复**。时序模型总体仍为 `partial`；项目中的 Graph 仍是 Beta。

## 1. 先决定身份，再决定观测值

给泵遥测定义以下 schema：

```sql
CREATE MEASUREMENT PumpTelemetry (
    DeviceID TAG,
    Temperature FIELD FLOAT,
    Healthy FIELD BOOL
);
```

| 名称 | 角色 | 在这个例子中的含义 |
|---|---|---|
| `DeviceID` | TAG STRING | 设备身份，参与 series key |
| `Temperature` | FIELD FLOAT | 本次采样的温度 |
| `Healthy` | FIELD BOOL | 本次采样是否记录了健康状态及其值 |
| `time` | 保留时间列 | Unix 毫秒时间戳，不写在 schema 定义中 |

核心引擎使用 measurement 与按 Ordinal 排序的 TAG 键值生成规范化 series key。TAG 的输入顺序改变不应产生第二条序列；改变 TAG 的值或 TAG 集合会改变序列身份。因此，不宜把每次采样的随机请求 ID、完整消息或温度放进 TAG。

TAG/FIELD **名称**绑定到已有 schema 的原始拼写，不会顺带改写 TAG 的**字符串值**。例如 `pump-01` 与 `PUMP-01` 是两组不同的 series 身份输入；名称的忽略大小写合同不提供设备编号值的自动统一。若业务要求统一编号，应由接入层制定并执行明确规则。

## 2. 用省略字段表达“这次没采到”

按顺序执行两条写入：

```sql
INSERT INTO PumpTelemetry (time, DeviceID, Temperature, Healthy)
VALUES (1000, 'pump-01', 21.5, TRUE);

INSERT INTO PumpTelemetry (time, DeviceID, Temperature)
VALUES (2000, 'pump-01', 22.0);

SELECT time, DeviceID, Temperature, Healthy
FROM PumpTelemetry
WHERE time >= 1000 AND time < 3000
ORDER BY time ASC;
```

在新建且只写入这两点的数据库中，合同预期为：

| time | DeviceID | Temperature | Healthy |
|---|---|---|---|
| 1000 | pump-01 | 21.5 | TRUE |
| 2000 | pump-01 | 22.0 | NULL |

第二行的 `NULL` 表示没有记录 `Healthy`。它不等于 `FALSE`，也不等于应用已经检查过设备并得出某个结论。把缺值在界面上画成“故障”会制造误判；应用应分别展示“正常”“异常”“未采集”。

当前 measurement `INSERT` 不接受用显式 `NULL` 填充字段缺值。下面两种写法也不会为 measurement 创建默认观测值：

```sql
-- 下面是失败示例，应单独执行并观察错误。
INSERT INTO PumpTelemetry (time, DeviceID, Healthy)
VALUES (3000, 'pump-01', NULL);

INSERT INTO PumpTelemetry DEFAULT VALUES;
```

要表达字段缺失，省略该 FIELD；每点仍须至少带一个 FIELD。若本次连一个观测值也没有，不应通过一条仅含 time/TAG 的写入制造空采样。

## 3. COUNT 的差异能帮助检查缺值

继续在只有前面两点的数据库上执行：

```sql
SELECT COUNT(*), COUNT(Temperature), COUNT(Healthy)
FROM PumpTelemetry
WHERE time >= 1000 AND time < 3000;
```

合同预期分别是 **2、2、1**。这些是两点演示输入的行数，不能当成性能结果。

`COUNT(*)` 计时序行：同一 series、同一时间戳上的多个 FIELD 合成一行。`COUNT(Healthy)` 只计有该字段值的时刻；若另一 series 也在同一毫秒写入，它是另一行。用 FIELD 总点数代替采样行数会把宽采样重复计算。

测量完整率时，先限定同一个 measurement 和同一个实际采样窗口，再解释 `COUNT(field)` 与 `COUNT(*)` 的差距。不能把未出现的采样时间自动算成已存储的 `NULL` 行：数据库返回的时间轴来自实际记录。

## 4. 原始名称贯穿 SQL 与 Point 摄取

普通 SQL 引用忽略名称大小写，双引号引用精确匹配：

```sql
-- 普通引用可以写成小写，结果列元数据仍来自保存的名称。
SELECT temperature, healthy FROM pumptelemetry;

-- 精确引用使用创建时的拼写。
SELECT "Temperature" FROM "PumpTelemetry";

-- 单独执行：应报名称不匹配，不能静默选另一列。
SELECT "temperature" FROM "PumpTelemetry";
```

不能在同一命名空间新建一个仅大小写不同的 measurement 或列，即使给名称加双引号也不可以。旧 catalog 如果已有大小写冲突，需要显式检查和迁移，不能用普通引用猜选其中一个对象。

Point 摄取遇到已有 `PumpTelemetry`、`DeviceID`、`Temperature` 时，也会将普通大小写变体归一到原名。可以用下面的独立小程序检查整个过程。

在 PowerShell 7 中创建示例项目，并引用已经准备好的指定提交源码。`D:\source\SonnetDB` 是本例源码位置，读者按自己的 checkout 修改；示例目录使用全新目录：

```powershell
dotnet new console -n MeasurementNamesDemo -f net10.0
dotnet add .\MeasurementNamesDemo\MeasurementNamesDemo.csproj reference D:\source\SonnetDB\src\SonnetDB.Core\SonnetDB.Core.csproj
```

把 `Program.cs` 换成：

```csharp
using SonnetDB.Engine;
using SonnetDB.Model;
using SonnetDB.Sql.Execution;

using var db = Tsdb.Open(new TsdbOptions
{
    RootDirectory = "./measurement-names-demo-data",
});

SqlExecutor.Execute(db, """
    CREATE MEASUREMENT PumpTelemetry (
        DeviceID TAG, Temperature FIELD FLOAT, Healthy FIELD BOOL)
    """);

db.Write(Point.Create("pumptelemetry", 1000,
    new Dictionary<string, string> { ["deviceid"] = "pump-01" },
    new Dictionary<string, FieldValue>
    {
        ["temperature"] = FieldValue.FromDouble(21.5),
    }));

var schema = db.Measurements.TryGet("PumpTelemetry")!;
Console.WriteLine(string.Join(", ", schema.Columns.Select(c => c.Name)));
Console.WriteLine($"series={db.Catalog.Count}");

var result = (SelectExecutionResult)SqlExecutor.Execute(db,
    "SELECT time, DeviceID, Temperature, Healthy FROM PumpTelemetry");
Console.WriteLine(string.Join(", ", result.Columns));
foreach (var row in result.Rows.Take(10))
    Console.WriteLine(string.Join(" | ", row.Select(v => v?.ToString() ?? "NULL")));
```

```powershell
dotnet run --project .\MeasurementNamesDemo
```

预期 schema 仍是 `DeviceID, Temperature, Healthy`，series 为 1，查询列名称保留原拼写，未写入的 `Healthy` 返回 `NULL`。程序必须在新数据目录运行；重复运行会先撞到已有 measurement 的显式创建检查。不要删除包含真实数据的目录来迁就示例。

Line Protocol 的 measurement/TAG/FIELD 名称也遵循已有 schema 的名称绑定合同，但协议时间精度及服务入口应按目标端点单独核验。不能只把 SQL 文本大小写改通，就宣称全部远程摄取路径已完成现场验收。

## 5. 避免把关系表修饰符当成时序约束

measurement DDL 当前允许解析 `NULL` / `NOT NULL` 修饰符，但执行层没有将它们持久化为 catalog 约束，也没有强制 `NOT NULL`。为 `Healthy` 写上 `NOT NULL` 并不能替代接入层的必填校验。

`DEFAULT` 出现在 measurement 建表中会明确报暂不支持；关系表的默认值、约束和事务也不能直接移植到时序写入。需要每次都上报健康状态时，应在调用方检查输入并拒绝缺值，保留缺测原因，而不是依靠尚未实施的约束。

这些例子证明的是建模与名称/稀疏合同如何组合。WAL、崩溃恢复、远程认证、取消、结果预算、真实服务 parity 与长期生产证据各有独立验收边界，不能从两行结果外推。

## 核查来源

- [数据模型](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/docs/data-model.md)
- [SQL 参考：measurement 与稀疏字段](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/docs/sql-reference.md)
- [Point 写入类型](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/src/SonnetDB.Core/Model/Point.cs)与[series key](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/src/SonnetDB.Core/Model/SeriesKey.cs)
- [SQL 名称合同回归源码](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/tests/SonnetDB.Core.Tests/Sql/SqlIdentifierCaseContractTests.cs)与[measurement DDL 回归源码](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/tests/SonnetDB.Core.Tests/Sql/SqlExecutorCreateMeasurementTests.cs)
- [当前能力成熟度](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/docs/capability-maturity.md)、[路线图](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/ROADMAP.md)与[4.0.0 Release](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)
