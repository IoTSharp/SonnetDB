---
title: SonnetDB 受控 Schema-on-Write：从自动建列到拒绝坏批次
categories: SonnetDB
tags: 时序数据库, Schema-on-Write, 数据接入
description: 用列额度、类型冲突和内部块预检设计接入流程，区分已确认的校验失败与未知远程写入结果。
draft: false
---

# SonnetDB 受控 Schema-on-Write：从自动建列到拒绝坏批次

SonnetDB 的自动补列让设备接入更方便，但采集端仍需知道三个问题的答案：允许创建什么，允许扩展多少，失败后哪些部分已经写入。否则一次固件升级就可能把一个稳定 schema 变成宽表，或者让重试重复投递已经成功的前缀。

![SonnetDB Schema-on-Write：策略、写入预检与拒绝边界](https://img2024.cnblogs.com/blog/1725297/202610/1725297-20261010102509629-1843983319.png)

图：AI 主题示意图，复用本篇公众号封面，并非实测截图；示例与支持边界以正文为准。

本文接续仓库已有的 Schema-on-Write 技术底稿，新增调用方的“预检—失败—修正”旅程；不把已有实现重新称作新功能。核查日期 **2026-10-09**，源码基线 `5424b66672e37f49a2a5633fdde0b54a9b1f0290`。GitHub `v4.0.0` 和 NuGet `SonnetDB.Core 4.0.0` 已存在，但下文策略和边界按固定源码介绍，发行包与该源码是否逐项一致仍须对照目标版本。

本次只读了实现、配置和已有测试源码，**没有运行以下示例、真实 Server 或故障恢复测试**。时序模型总体为 `partial`；本篇没有新增性能、固定硬件或长期生产证据，也不涉及真实模型质量。Graph 继续为 Beta。

## 1. 把“能自动写入”变成明确策略

Core 使用 `MeasurementSchemaPolicy`，Server 在 `SonnetDBServer:MeasurementSchema` 绑定对应配置。

| 模式 | 自动新建 measurement | 已有 measurement 自动扩列 / INT→FLOAT |
|---|---|---|
| `Disabled` | 拒绝 | 拒绝 |
| `CreateOnly` | 允许 | 拒绝 |
| `CreateAndEvolve` | 允许 | 允许通过角色、类型与额度校验的变更 |

`Disabled` 禁止的是自动 schema 变更；它允许向显式创建且兼容的 schema 写数据。显式 DDL 创建也要接受 measurement 数量和列数上限检查，不能借它绕过所有额度。

Server 配置示例：

```json
{
  "SonnetDBServer": {
    "MeasurementSchema": {
      "Mode": "CreateOnly",
      "MeasurementModes": {
        "telemetry_hot": "CreateAndEvolve",
        "billing_events": "Disabled"
      },
      "MaxMeasurements": 4096,
      "MaxColumnsPerMeasurement": 128,
      "MaxNewColumnsPerWrite": 16
    }
  }
}
```

这是例子中的配置值，不是宣称经固定硬件测出的容量。当前源码默认值为 `CreateAndEvolve`、16384 个 measurement、每 measurement 256 个 TAG/FIELD 列、单次写入规划 32 个新增列。

按名称的 `MeasurementModes` 覆盖映射使用 **Ordinal、区分大小写**。填写 catalog 保存的原始 measurement 名称；不要把 SQL 普通标识符的忽略大小写规则当成配置键的规则。实例打开时复制和固定策略，修改配置后须重启服务或重新打开对应实例，并核对其实际采用的配置。

## 2. 先区分列角色，再讨论数值提升

SQL 不会因为某列值是字符串就自动把它当 TAG：

```sql
INSERT INTO AdmissionSql (time, DeviceID TAG, Reading, Message)
VALUES (1000, 'pump-01', 100, 'first sample');

DESCRIBE MEASUREMENT AdmissionSql;
```

在允许自动创建的全新数据库中，预期 `DeviceID` 为 TAG STRING、`Reading` 为 FIELD INT、`Message` 为 FIELD STRING。未知列默认 FIELD；新 TAG 用列列表中的 `TAG` 提示。已有列按保存的角色解释，提示与角色冲突时拒绝。

再单独执行：

```sql
INSERT INTO AdmissionSql (time, DeviceID, Reading)
VALUES (2000, 'pump-01', 100.5);
```

`CreateAndEvolve` 下允许 `Reading` 从 INT 提升为 FLOAT；`CreateOnly` 下这项变更会被拒绝。FLOAT 接收整数保持 FLOAT，不降回 INT。BOOL、STRING、VECTOR、GEOPOINT 与其他类型之间没有任意自动转换。

INT→FLOAT 是类型兼容路径，不能据此保证每个 Int64 数值都能被浮点精确表达。需要精确整数计数时，固定 INT 输入并拒绝带小数的数据，比让接入链路自行改变精度更可审查。

## 3. 用一个小批次观察拒绝前后的状态

下面程序引用指定提交的 Core，所有 `WriteMany` 输入最多三点，远小于内部块大小。它故意制造“累计新增列超额”和“后面的点类型冲突”，然后读取 schema 和 MemTable 状态。

在 PowerShell 7 创建全新示例目录：

```powershell
dotnet new console -n SchemaAdmissionDemo -f net10.0
dotnet add .\SchemaAdmissionDemo\SchemaAdmissionDemo.csproj reference D:\source\SonnetDB\src\SonnetDB.Core\SonnetDB.Core.csproj
```

替换 `Program.cs`：

```csharp
using SonnetDB.Engine;
using SonnetDB.Model;

using var db = Tsdb.Open(new TsdbOptions
{
    RootDirectory = "./schema-admission-demo-data",
    MeasurementSchemaPolicy = new MeasurementSchemaPolicy
    {
        Mode = MeasurementSchemaMode.CreateAndEvolve,
        MaxColumnsPerMeasurement = 8,
        MaxNewColumnsPerWrite = 2,
    },
});

db.Write(P(1000, ("BaseA", FieldValue.FromLong(1))));
string before = db.MeasurementSchemaRevision;

Point[] tooWide =
[
    P(2000, ("B", FieldValue.FromLong(2))),
    P(3000, ("C", FieldValue.FromLong(3))),
    P(4000, ("D", FieldValue.FromLong(4))),
];

try
{
    db.WriteMany(tooWide);
    throw new Exception("预期超额拒绝，但写入成功：请核对版本、配置和数据目录。");
}
catch (InvalidOperationException ex)
{
    Console.WriteLine(ex.Message);
}

Console.WriteLine($"revision unchanged={before == db.MeasurementSchemaRevision}");
Console.WriteLine($"points={db.MemTable.PointCount}");
Console.WriteLine(string.Join(", ",
    db.Measurements.TryGet("AdmissionDemo")!.Columns.Select(c => c.Name)));

// 修正已确认的本地校验失败，只保留两列。
Point[] corrected =
[
    P(2000, ("B", FieldValue.FromLong(2))),
    P(3000, ("C", FieldValue.FromLong(3))),
];
Console.WriteLine($"written={db.WriteMany(corrected)}");

string beforeConflict = db.MeasurementSchemaRevision;
Point[] typeConflict =
[
    P(5000, ("BaseA", FieldValue.FromDouble(1.5))),
    P(6000, ("BaseA", FieldValue.FromBool(true))),
];

try
{
    db.WriteMany(typeConflict);
    throw new Exception("预期类型冲突拒绝，但写入成功：请核对实际类型。");
}
catch (InvalidOperationException ex)
{
    Console.WriteLine(ex.Message);
}

Console.WriteLine($"conflict revision unchanged={beforeConflict == db.MeasurementSchemaRevision}");
Console.WriteLine($"BaseA type={db.Measurements.TryGet("AdmissionDemo")!.TryGetColumn("BaseA")!.DataType}");

static Point P(long time, params (string Name, FieldValue Value)[] fields)
    => Point.Create("AdmissionDemo", time,
        new Dictionary<string, string>(),
        fields.ToDictionary(x => x.Name, x => x.Value, StringComparer.Ordinal));
```

```powershell
dotnet run --project .\SchemaAdmissionDemo
```

请在无旧数据的新目录运行。已有 schema、已有数据和不同策略都可能改变测试前提，不要通过删除业务数据库来清空环境。

| 检查点 | 在这个全新小例子中的合同预期 | 如何解释 |
|---|---|---|
| 三个新增列 B/C/D | 抛出超额异常 | 额度累计到一个内部块，不是只检查每点各一列 |
| 第一次失败后 | revision 不变、MemTable 仍为 1 点、仅 BaseA 列 | 块内 schema 预检未发布前缀变更或数据 |
| 修正为 B/C 后 | `written=2` | 调用方修正已确认的校验失败后重新提交 |
| FLOAT 后又写 BOOL | 抛出类型冲突异常 | BOOL 不能沿用 INT→FLOAT 数值提升路径 |
| 第二次失败后 | revision 不变、BaseA 仍为 Int64 | 前面拟议的类型提升没有提前发布 |

这些预期来自策略测试与 `TsdbWriteTests` 中的相应回归源码，不是本次实跑输出。若实际观察不符，应保留输入、异常、提交身份和前后状态，先调查，不要降低校验来获得“成功”。

## 4. 预检拒绝、存储故障与跨块提交要分开

当前 `WriteMany(ReadOnlySpan<Point>)` 每内部块最多 **8192 点**。它先规划和校验块内 schema，校验成功后持久化 schema、发布内存快照，再执行 WAL/MemTable 写入。schema 保存失败不应被吞掉。

本例证明的边界是**块内角色、类型和额度预检失败**。不能把它泛化为任何 I/O 故障都具有无限大小事务的回滚语义。

超大调用会分块。前一个块已经成功，后一个块校验失败，前一个块不会回滚。普通 SQL 多行 `INSERT` 当前逐行调用写入路径：后行失败时，前行可能已成功；`MaxNewColumnsPerWrite` 逐行生效，不是整条 SQL 的新增列总预算。measurement 写入也不进入关系表轻事务，不能靠 `ROLLBACK` 撤销时序数据。

调用方应保存批次与内部业务条目的身份，并记录“已确认成功”“明确拒绝”“结果未知”。本地、确定未写入的 schema 预检失败可以在修正输入后重新提交；远程请求超时、断网或响应丢失不能直接推定未写入，更不能盲重放整批。

## 5. 接收额度与 schema 额度解决不同问题

高层 `SndbTimeSeriesWriter` 的 `MaxBatchPoints` 限制一次 `WriteBatchAsync` 可以接收的点数，默认 8192、允许范围 1～65536；`BatchSize` 控制拆分传输，`MaxPendingBatches` 控制队列和接收中的生产者数量。超出接收额度的调用在入队前整体拒绝。

这些限制不等于 `MaxNewColumnsPerWrite`，也不等于远程全链路写入事务。点数边界没有变成字节/总堆硬上限；同步 `IEnumerable.MoveNext` 需要调用方保证会返回，取消不能抢占任意同步用户代码。入口接收成功也不是已经持久化全部业务点的证据。

上线前分别检查 schema revision/诊断、每项结果、服务重启恢复、跨进程 parity、固定硬件容量和长期观察。schema 先落盘的顺序只能解释其恢复设计，不能代替这些独立验收记录。

## 核查来源

- [Schema-on-Write 技术底稿](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/docs/blogs/117-schema-on-write.md)与[数据模型](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/docs/data-model.md)
- [Core 策略及默认值](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/src/SonnetDB.Core/Engine/MeasurementSchemaPolicy.cs)、[Tsdb 规划和写入路径](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/src/SonnetDB.Core/Engine/Tsdb.cs)与[Server 配置绑定](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/src/SonnetDB/Configuration/ServerOptions.cs)
- [策略回归源码](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/tests/SonnetDB.Core.Tests/Engine/TsdbMeasurementSchemaPolicyTests.cs)与[批次角色/类型冲突回归源码](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/tests/SonnetDB.Core.Tests/Engine/TsdbWriteTests.cs)
- [时序批次接收与背压](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/docs/timeseries-write-admission.md)、[SQL 参考](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/docs/sql-reference.md)、[路线图](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/ROADMAP.md)与[4.0.0 Release](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)
