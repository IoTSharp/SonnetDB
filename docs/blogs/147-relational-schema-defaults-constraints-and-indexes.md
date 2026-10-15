---
title: SonnetDB 业务表从建表到演进：主键、默认值、约束与索引
categories: SonnetDB
description: 用独立小库核对关系表的默认值、精确十进制、TIME、约束和受支持的 ALTER 操作。
draft: false
---

# SonnetDB 业务表从建表到演进：主键、默认值、约束与索引

SonnetDB 的关系表适合保存设备台账、业务配置等有主键和列约束的数据。本文用一张设备表串起建表、写入、默认值、DECIMAL 与 TIME、约束拒绝以及结构演进，并说明哪些能力不能从 measurement 或其它数据库直接照搬。

![SonnetDB 业务表：主键、默认值、约束与索引](https://img2024.cnblogs.com/blog/1725297/202610/1725297-20261011111318349-705713271.png)

图：AI 主题示意图，并非实测截图；接口、类型和约束范围以正文与对应源码为准。

## 1. 先对齐版本，再建独立实验库

本稿于 2026-10-11 对照本地源提交 `d4e4b7fdb15dfcb47e81ac598778ad64aba25173`、SQL 参考、关系类型边界及相关回归源码复核。博客园任务当日读取的 GitHub 最新正式 Release 和 NuGet `SonnetDB.Core` 页面均指向 **4.0.0**。本文相关文档与核心代码切片和公开提交 `5424b66672e37f49a2a5633fdde0b54a9b1f0290` 相同，下文参考链接固定到该提交；不能据此认定 4.0.0 包完整包含当前源码合同。

建议在匹配的源码、SDK 与 Server 上复现。不要用旧 Server 加新 SDK 的能力元数据代替版本核对。本文展示的是当前实现的功能合同，能力状态为 `partial`，没有执行生产迁移或性能验收。

嵌入式方式可在引用对应 `SonnetDB.Core` 的 .NET 10 控制台中打开一个全新的实验目录：

```csharp
using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;

using var db = Tsdb.Open(new TsdbOptions
{
    RootDirectory = Path.Combine(Path.GetTempPath(), "sonnetdb-relational-demo-" + Guid.NewGuid().ToString("N"))
});

var result = SqlExecutor.Execute(db, "SHOW TABLES");
if (result is SelectExecutionResult rows)
    foreach (var row in rows.Rows)
        Console.WriteLine(string.Join(" | ", row.Select(v => v?.ToString() ?? "NULL")));
```

后续每段 SQL **按语句逐条执行**，把字符串传给同一个 `SqlExecutor.Execute(db, sql)`；查询结果用上面的方式读取。失败示例单独捕获并打印异常，再继续下一项。不要把整篇文章作为一个 SQL 字符串，也不要在现有业务库里执行这些 DDL。实验完成后关闭句柄，再自行清理这个明确属于实验的目录。

## 2. 建表时写清主键、空值与模型

```sql
CREATE TABLE Devices (
    Id INT,
    Name STRING NOT NULL,
    Enabled BOOL NOT NULL DEFAULT TRUE,
    Budget DECIMAL(12,2) NULL,
    Starts TIME NULL,
    PRIMARY KEY (Id),
    CONSTRAINT ck_devices_budget CHECK (Budget >= 0)
);

SHOW TABLES;
DESCRIBE TABLE Devices;
```

关系表开始写入前必须具备 `PRIMARY KEY (...)`；主键列会强制为非空。当前也允许先创建用于结构演进的无主键空表，但补齐主键前任何行写入都会以 `table_schema_evolution_unsupported` 拒绝；这不是可直接写入的普通业务表。本文从建表时就声明主键。这里 `INT` 对应 `Int64`，不是 C# 的 32 位 `int`。表名、列名保留创建时拼写；不加双引号时忽略大小写解析，加双引号时精确匹配，例如 `"Devices"` 可引用上表，`"devices"` 不能代替它。

关系表列支持 `INT`、`FLOAT`、`BOOL`、`STRING`、`DECIMAL/NUMERIC`、`DATETIME`、`TIME`、`BLOB`、`JSON`。`VECTOR(N)` 与 `GEOPOINT` 属于 measurement FIELD 范围，不能声明为这里的关系列。把向量编码为普通 JSON 载荷是应用显式建模选择，不会附带原生向量索引或空间检索能力。

## 3. 默认值只作用于省略列或 DEFAULT

```sql
INSERT INTO Devices (Id, Name, Budget, Starts)
VALUES (1, 'pump', '1250.30', '08:30:00');

INSERT INTO Devices (Id, Name, Enabled, Budget, Starts)
VALUES (2, 'fan', DEFAULT, '50.05', '12:34:56.1234567');

SELECT Id, Name, Enabled, Budget, Starts FROM Devices ORDER BY Id;
```

依据实现合同，两行的 `Enabled` 都应为 `TRUE`。第一行省略该列，第二行明确使用 `DEFAULT`。字符串形式的金额由目标 DECIMAL 列转换，避免先经二进制浮点再转换；业务程序也可以绑定 CLR `decimal` 参数。

下面的语句应拒绝，因为显式 `NULL` 不会触发默认值，且 `Enabled` 非空：

```sql
INSERT INTO Devices (Id, Name, Enabled) VALUES (3, 'valve', NULL);
```

打印异常后查询 `WHERE Id = 3`，核对失败行未写入。普通默认表达式允许字面量、常量算术和受支持标量函数，不允许引用另一列、参数、聚合或子查询。`UPDATE Devices SET Enabled = DEFAULT WHERE Id = 2` 会对命中行重新求值；无显式默认值的普通列使用 `DEFAULT` 会得到隐式 `NULL`，随后仍须通过非空约束。

## 4. DECIMAL 和 TIME 的名字不是无限能力承诺

```sql
SELECT CAST('9007199254740993.125' AS DECIMAL(20,3));
SELECT CAST('12:34:56.1234567' AS TIME);
```

第一个示例用于检查十进制精度是否被保留；嵌入式结果应是 CLR `decimal`。声明精度允许 `1..38`，小数位满足 `0 <= s <= p`，但实际值仍受 **System.Decimal** 的可表示范围限制。不能因为声明允许 38，就宣传任意 38 位整数都能存储；非法声明和超出 Decimal 范围的值会拒绝。

`TIME` 对应 `TimeOnly`，以 ticks 精度持久化，支持 7 位小数秒，范围是 `[00:00:00, 24:00:00)`。它没有日期或时区，不能作为跨天时间戳。单独执行以下失败用例并核对诊断：

```sql
SELECT CAST('24:00:00' AS TIME);
```

不要假定远程 JSON 字符串能自动恢复所有 typed TIME 信息；传输类型须另查协议合同。本文的 CLR 类型解释限定为嵌入式路径。

## 5. 分别验证主键、CHECK 和唯一索引

先单独提交两条应该失败的语句：

```sql
INSERT INTO Devices (Id, Name) VALUES (1, 'duplicate_id');
INSERT INTO Devices (Id, Name, Budget) VALUES (4, 'negative', '-1.00');
```

第一条与已有主键冲突，第二条使 CHECK 明确为 `FALSE`。每次失败后重新读取表，确认原两行保留，失败 ID 没有新增。错误形状会随嵌入式或远程路径变化，不能要求所有入口返回相同 CLR 异常类型。

CHECK 使用 SQL 三值逻辑：只有 `FALSE` 拒绝，`TRUE` 和因 NULL 传播得到的 `UNKNOWN` 均通过。所以 `Budget >= 0` 不能替代 `Budget NOT NULL`。如预算必填，应在设计阶段同时声明非空；不要把一个允许空值的 CHECK 宣传成完整业务校验。

二级索引用独立 DDL 声明：

```sql
CREATE UNIQUE INDEX ux_devices_name ON Devices (Name);
INSERT INTO Devices (Id, Name) VALUES (5, 'pump');
```

第二条应因唯一性冲突失败。索引是在现有数据上建立的；实际业务迁移前应先查重复值。唯一索引不是分布式锁，也不证明当前业务查询已经使用该索引。计划和性能应另用受支持的 EXPLAIN、实际负载和固定环境验证。

## 6. 小步演进并检查已有行

```sql
ALTER TABLE Devices ADD COLUMN Site STRING NOT NULL DEFAULT 'north';
SELECT Id, Name, Site FROM Devices ORDER BY Id;
ALTER TABLE Devices RENAME COLUMN Site TO Region;
DESCRIBE TABLE Devices;
SELECT Id, Name, Region FROM Devices ORDER BY Id;
```

核对新增列在已有行上的默认值、重命名后的原始拼写以及行数。受支持的 ALTER 还有删列、表重命名和部分类型/约束调整，各自有前置条件；本文不把其中两条成功语法扩展成任意 schema 迁移能力。已有数据、索引和约束的依赖关系必须逐项核验。

迁移前保留独立备份并验证恢复；把变更拆成可检查步骤。涉及行重写的 ALTER 受单个 KV batch 预算限制，不能放在活动轻事务中，也不提供进程终止或掉电时的整体原子性保证。本文没有验证并发 schema 迁移、故障中断回滚或任意跨版本降级。简单关闭并重开实验库只能检查该小库的持久化，不能证明崩溃恢复或长期生产稳定性。

## 7. 如何复核结果与支持范围

本文没有实跑上面的数据库示例，列出的结果是从当前源码与测试断言核对得到的**预期结果**，不是本次运行日志。需要实证时，可先在对应源码执行已有的小型回归，再保存自己的数据库输入、异常与结果：

```text
dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj --filter FullyQualifiedName~SqlExecutorTableTests
dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj --filter FullyQualifiedName~SqlDecimalTests
dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj --filter FullyQualifiedName~SqlTimeTests
```

这些功能测试不等于固定硬件吞吐、真实 Server 网络、生产迁移或长期可靠性报告。本篇从未将未确认发布的旧 120 号 DDL 底稿当作公开教程复用。

参考：[SQL 参考](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/docs/sql-reference.md)、[关系类型边界](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/docs/relation-type-boundary.md)、[关系表回归](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/tests/SonnetDB.Core.Tests/Sql/SqlExecutorTableTests.cs)、[项目路线图](https://github.com/IoTSharp/SonnetDB/blob/main/ROADMAP.md)、[正式 Release](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。
