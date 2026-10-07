## 用户自定义函数：使用 RegisterScalar、RegisterAggregate 等扩展 SonnetDB 分析能力

SonnetDB 的嵌入式宿主可以通过 `db.Functions` 注册标量、聚合、窗口和表值函数。注册属于当前 `Tsdb` 实例，进程重启后需要重新注册；UDF 直接以宿主权限执行，所以应只加载可信代码。

本文依据当前 UDF 文档和接口整理。正式 4.0.0 是独立标签，扩展项目应固定核心包版本并据对应接口编译。Server 默认关闭用户函数，不能把下面代码当作向 HTTP 服务上传 C# 插件的方法。

### 完整标量示例

```csharp
using SonnetDB.Engine;
using SonnetDB.Sql.Execution;

using var db = Tsdb.Open(new TsdbOptions
{
    RootDirectory = "./udf-demo",
    AllowUserFunctions = true,
});
db.Functions.RegisterScalar("device_health", args =>
{
    if (args.Any(value => value is null)) return null;
    double temperature = Convert.ToDouble(args[0]);
    double vibration = Convert.ToDouble(args[1]);
    double uptime = Convert.ToDouble(args[2]);
    return Math.Clamp(100 - Math.Max(0, temperature - 50) * 0.5
        - vibration * 2 + Math.Min(10, uptime / 1000), 0, 100);
}, minArgumentCount: 3, maxArgumentCount: 3);

SqlExecutor.Execute(db, """
CREATE MEASUREMENT machine_metrics (
    device TAG, temperature FIELD FLOAT,
    vibration FIELD FLOAT, uptime_hours FIELD FLOAT
)
""");
SqlExecutor.Execute(db, """
INSERT INTO machine_metrics (time, device, temperature, vibration, uptime_hours)
VALUES (1713676800000, 'pump-01', 58.0, 1.2, 1200.0)
""");
var result = (SelectExecutionResult)SqlExecutor.Execute(db, """
SELECT device, time, device_health(temperature, vibration, uptime_hours) AS health
FROM machine_metrics LIMIT 10
""")!;
foreach (var row in result.Rows) Console.WriteLine(string.Join(", ", row));
```

这个“健康分”只是演示公式，实际阈值需要设备和领域数据验证，不能把结果当作故障概率。

### 聚合函数使用可合并累加器

`RegisterAggregate` 接受 `IAggregateFunction`，不是一个收取任意二维数组的 `Aggregate` 方法。接口要求解析目标字段，并创建 `IAggregateAccumulator`。下面实现对正值计算几何平均，可追加在前面的程序末尾。

```csharp
db.Functions.RegisterAggregate(new PositiveGeomean());
var aggregate = SqlExecutor.Execute(db,
    "SELECT positive_geomean(vibration) FROM machine_metrics");

public sealed class PositiveGeomean : SonnetDB.Query.Functions.IAggregateFunction
{
    public string Name => "positive_geomean";
    public SonnetDB.Query.Aggregator? LegacyAggregator => null;

    public string? ResolveFieldName(
        SonnetDB.Sql.Ast.FunctionCallExpression call,
        SonnetDB.Catalog.MeasurementSchema schema)
    {
        if (call.Arguments.Count != 1 ||
            call.Arguments[0] is not SonnetDB.Sql.Ast.IdentifierExpression id)
            throw new InvalidOperationException("需要一个 FIELD 标识符");
        var column = schema.TryGetColumn(id.Name)
            ?? throw new InvalidOperationException("未知字段");
        if (column.Role != SonnetDB.Catalog.MeasurementColumnRole.Field)
            throw new InvalidOperationException("需要 FIELD 列");
        return column.Name;
    }

    public SonnetDB.Query.Functions.IAggregateAccumulator CreateAccumulator(
        SonnetDB.Sql.Ast.FunctionCallExpression call,
        SonnetDB.Catalog.MeasurementSchema schema) => new Accumulator();

    private sealed class Accumulator : SonnetDB.Query.Functions.IAggregateAccumulator
    {
        private double _sumLog;
        public long Count { get; private set; }
        public void Add(double value)
        {
            if (!double.IsFinite(value) || value <= 0) return;
            _sumLog += Math.Log(value);
            Count++;
        }
        public void Merge(SonnetDB.Query.Functions.IAggregateAccumulator other)
        {
            var part = (Accumulator)other;
            _sumLog += part._sumLog;
            Count += part.Count;
        }
        public object? Finalize() => Count == 0 ? null : Math.Exp(_sumLog / Count);
    }
}
```

`Merge` 应正确合并不重叠的部分结果并满足结合性；重复合并同一份数据会重复计数，不能要求普通求和累加器天然幂等。当前聚合输入合同主要围绕一个字段，不应照搬没有字段绑定实现的任意双列加权平均。

### 窗口与表值扩展合同

`IWindowFunction.CreateEvaluator(call, schema)` 返回 `IWindowEvaluator`，其 `Compute(long[] timestamps, FieldValue?[] values)` 输出与输入一一对应的值。每个 series 独立处理，函数自行决定前后行或窗口规则；不应假定注册后就获得任意 SQL `OVER (...)` 语法。

`RegisterTableValuedFunction(name, executor)` 注册返回 `SelectExecutionResult` 的执行委托，SQL 形态为 `SELECT * FROM my_function(measurement, ...)`。第一个参数必须是 measurement 标识符，所以旧稿的 `generate_series(1,10,2)` 不符合这一 parser 合同。TVF 还要限制输出行数、时间与取消，并核对外层查询处理。

`Unregister(name)` 可以移除注册；标量同名 UDF 可遮盖内置函数，应采用业务前缀减少冲突。内置 `forecast` 的表值名称不可覆盖。Server 的 `AllowUserFunctions=false` 是既定边界，不能为了启用插件绕过 Native AOT 合同。

参考：[UDF 文档](https://github.com/IoTSharp/SonnetDB/blob/main/docs/extending-functions.md)、[注册表源码](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Query/Functions/UserFunctionRegistry.cs)、[正式 4.0.0](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。
