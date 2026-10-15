---
title: SonnetDB 有界时序查询：范围、结果准入、取消与首行成本
categories: SonnetDB
draft: false
description: 区分 SQL 结果预算、扫描成本与预检采样，解释排序、LIMIT、取消和首行物化的支持边界。
---

# SonnetDB 有界时序查询：范围、结果准入、取消与首行成本

SonnetDB 当前源码为一部分直接 measurement 原始 SELECT 提供显式结果物化预算，也有真实时序元数据和采样预检入口。本文通过小数据说明如何设置行数额度、处理超限和取消，并区分返回结果少、扫描工作少与首行返回快这三件事。

![SonnetDB 有界时序查询：范围、结果预算与取消](https://img2024.cnblogs.com/blog/1725297/202610/1725297-20261010111753426-1411140998.png)

图：AI 主题示意图，并非实测截图；预算仪表仅为概念表达，不代表总内存或延迟实测。

## 版本与复现方式

2026-10-10 核查基线为 `78045e8b407e9caceae2548116390ffb985fc959`。当日 GitHub 最新正式 Release 与 NuGet SonnetDB.Core 页面显示 4.0.0；ROADMAP 的 4.5 是目标。以下是当前源码的部分支持合同，**不保证这些后续切片全部已包含在正式 4.0.0 包中**。

本文没有执行示例、产品构建、压测或远程服务验收。读者可用 .NET 10 控制台引用这个源码版本的 Core 项目，使用新的空数据库目录，记录实际提交与结果：

```powershell
dotnet new console --framework net10.0
dotnet add reference D:/source/SonnetDB/src/SonnetDB.Core/SonnetDB.Core.csproj
```

写入下文 Program.cs 后执行 dotnet run。已有生产数据库不要用于演示；路径替换为自己的检出位置。

公开参考和复现源码固定到提交 5424b66672e37f49a2a5633fdde0b54a9b1f0290；相关专题及本文使用的 Core/测试文件与本地复核基线逐文件比较无差异。后续工作台交接变动不作为本文能力证据。

## 先用一个小查询建立对照

下例写入 10 个点，保持简单的单 series、单 FIELD。时间示例按毫秒解释。

```csharp
using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;

using var db = Tsdb.Open(new TsdbOptions
{
    RootDirectory = Path.GetFullPath("./demo-146")
});
SqlExecutor.Execute(db,
    "CREATE MEASUREMENT readings (host TAG, value FIELD FLOAT)");
for (int i = 0; i < 10; i++)
{
    SqlExecutor.Execute(db,
        $"INSERT INTO readings (time, host, value) VALUES ({1000 + i}, 'h1', {i}.0)");
}

SelectExecutionResult Query(string sql, SqlExecutionOptions options)
{
    return (SelectExecutionResult)SqlExecutor.Execute(
        db, null, sql, null, null, options);
}

var page = Query(
    "SELECT time, host, value FROM readings " +
    "WHERE time >= 1000 AND time < 1010 LIMIT 2",
    new SqlExecutionOptions
    {
        MaxMaterializedRows = 2,
        MaxMaterializedBytes = 1024 * 1024
    });
Console.WriteLine($"page rows: {page.Rows.Count}");

try
{
    Query("SELECT time, value FROM readings",
        new SqlExecutionOptions
        {
            MaxMaterializedRows = 2,
            MaxMaterializedBytes = 1024 * 1024
        });
    Console.WriteLine("unexpected success");
}
catch (InvalidOperationException)
{
    Console.WriteLine("result admission rejected");
}
```

根据源码，首个查询预期返回 2 行。第二个查询没有 LIMIT，保留第三行前应因行数额度不足而抛出 InvalidOperationException，不能把已经生成的两行当前缀成功返回。这里的 2 是演示参数，不是产品推荐值。

只有启用了 MaxMaterializedRows 或 MaxMaterializedBytes 才进入这套显式预算支持路径；未设置时沿用原执行语义。预算错误应与数据为空分别处理，不能 catch 后返回空集合。

## 无排序时：LIMIT 限制保留行，OFFSET 仍需要扫描

无 ORDER BY 的支持路径逐 series 合并字段点流：先过滤，再跳过 OFFSET，随后投影并保留 SQL 页。实际保留的行在加入结果列表之前计费；被过滤的行和 OFFSET 跳过的行不计结果行预算。

因此 `LIMIT 2 OFFSET 100000` 可能只保留两行，却需要寻找大量满足过滤条件的输入行。高选择性 FIELD 过滤也会产生“返回少、扫描多”的查询。时间范围和可下推 TAG 条件可用于约束任务范围，但是否减少底层工作仍要按真实数据和执行路径观察。

没有 ORDER BY 时不承诺多个 series 的全局时间排序。LIMIT 到完整页后停止，不会检查页外行的投影 CAST 等错误；SQL 页成功只能证明这个页的求值。LIMIT 0 不启动点流，但不等于取消所有解析、绑定和目录成本。

## 排序时：先准入候选与排序键，再取页

较新的直接 raw SELECT 排序切片已支持标量多键 ORDER BY，包括未投影 FIELD、TAG、time、别名和标量表达式。旧的无排序专题中“ORDER BY 拒绝”描述的是首次切片，不能忽略 2026-10-03 排序增量。

在前述小数据库执行：

```csharp
try
{
    Query("SELECT time FROM readings ORDER BY value DESC, time ASC LIMIT 1",
        new SqlExecutionOptions
        {
            MaxMaterializedRows = 3,
            MaxMaterializedBytes = 1024 * 1024
        });
    Console.WriteLine("unexpected success");
}
catch (InvalidOperationException)
{
    Console.WriteLine("sorting candidates exceeded budget");
}
```

虽然最终只要求一行，排序仍需先接收满足条件的候选。当前路径对候选行、辅助键与最终输出行分别累计计费，引用相同行或值也不退还配额；本例 10 个候选会超过演示额度。应用不能按最终 LIMIT 计算排序预算。

如果业务只需要较新的一小段数据，可以先明确时间范围，再评估排序形状与额度；不要自动无限增加预算。超限应使整个查询失败，而不是把不完整 Top-N 说成正确答案。

## 字节预算怎样解释

现有 SQL 保留行估算为：

`64 + 8 × 列数 + 值载荷`

NULL 计 1，固定宽度标量计 24，字符串计 `24 + 2 × UTF-16 长度`。专题中的三列 time、value、100 字符 label 示例估算为 360 字节：360 允许，359 拒绝。这是准入算法的可核查算术，不是进程 RSS 或 heap 测量。

解析与绑定、catalog、MemTable 快照、乱序桶排序、块解码和表达式瞬时分配不等于 SQL 保留行额度。MaxMaterializedBytes 不能宣称限制了整个 CLR heap；成功返回后由调用方保留的结果也不再受该查询预留治理。超限、取消、求值失败和正常结束会释放查询拥有的预留及点流租约，后续调用有独立累计预算。

## 把取消当作协作式终止

可以先用已取消令牌核对调用方错误处理：

```csharp
using var cancellation = new CancellationTokenSource();
cancellation.Cancel();

try
{
    Query("SELECT time, value FROM readings LIMIT 2",
        new SqlExecutionOptions
        {
            MaxMaterializedRows = 2,
            MaxMaterializedBytes = 1024 * 1024,
            CancellationToken = cancellation.Token
        });
}
catch (OperationCanceledException)
{
    Console.WriteLine("query cancelled");
}
```

这是预取消验证方法，不是长查询即时中断证据。实际使用中应由请求超时、用户取消或服务关闭传入令牌，保留原异常和查询身份，不把取消伪装成成功空页。一次同步锁等待、块解码或外部回调并不一定可以被强制打断。

HTTP 断连、Core 取消和 OS 进程退出属于不同证据。Frame 分块只说明传输形状，不能据此宣布执行器到客户端已全链路流式，也不能推出首字节延迟。

## 预检采样用于什么

TimeSeriesQueryBuilder 的嵌入式 Preflight 会读取真实 schema、series/tag 目录、retention 配置及目标 series/field/range 内的有界可见原始点。它不执行聚合、补桶、flush、retention 清理或 schema 修复。

当前配置包含 MaxSamplePoints、MaxSourcePoints、MaxSources 与 MaxSourceBytes。预检的源预算和 SQL 的结果预算不同：为了拒绝“输出很小但来源很多”的诊断，它可以在采样前因预算不足返回 TSP010，不自动拿未经完整合并的部分来源当全量样本。

查看报告时至少同时记录以下字段或提示：

| 观察 | 应怎样解释 |
|---|---|
| DataChecked=false | 没有完成数据检查 |
| SampleComplete=false / TSP012 | 样本不完整，只覆盖查询方向上的前缀 |
| TSP010 | 源工作量预算不足，应缩小范围或安排离线验证 |
| TSP011 | 没有可见点，未取得质量或类型样本 |
| HasErrors=false | 不能单独推导“全量健康” |
| SampleComplete=true | 仍只对本次目标范围和引擎快照负责 |

已有专题记录了 29 个新增预检用例及合计 168 项本地定向回归通过；数据为构造的真实本地引擎 fixture。该历史结果不等于本轮的新测试、远程 parity、现场恢复、真实容量或性能证据。

## 在支持范围内安排查询

显式 measurement 预算仍提前拒绝聚合/GROUP BY、窗口、JOIN、DISTINCT、集合运算、派生来源、CTE、子查询、Geo、VECTOR/GEOPOINT FIELD 依赖和用户函数回调等不在支持矩阵内的形状。raw SELECT 排序增量没有放行这些形状。未启用预算时的已有能力也不能冒称受同样额度治理。

接入顺序可采用：确认直接 raw SELECT 形状；限定时间与 TAG；按保留行和字符串估算额度；分别核查无排序页与阻塞排序；处理超限及取消；最后在固定机器与真实数据上测扫描工作、heap、首行和断连行为。当前 M42 的固定 x64/ARM64、长期运行和端到端门禁仍需独立报告。

## 可核查资料

- [直接 measurement 结果物化预算](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/docs/benchmarks/m42-measurement-result-bounds.md)
- [2026-10-03 raw SELECT 排序增量](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/docs/benchmarks/m42-measurement-order-bounds.md)
- [时序 Query API 与真实元数据预检](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/docs/timeseries-query-preflight.md)
- [SelectExecutor 源码](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/src/SonnetDB.Core/Sql/Execution/SelectExecutor.cs)
- [当前 ROADMAP 与未闭环门禁](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/ROADMAP.md)

先证明查询得到完整而符合额度的 SQL 结果，再单独取得扫描、首行、总内存和长期稳定性证据。它们需要不同的测量与验收记录。
