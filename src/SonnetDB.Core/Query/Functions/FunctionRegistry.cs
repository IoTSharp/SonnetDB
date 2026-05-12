using System.Diagnostics.CodeAnalysis;
using SonnetDB.Catalog;
using SonnetDB.Model;
using SonnetDB.Query.Functions.Aggregates;
using SonnetDB.Query.Functions.Control;
using SonnetDB.Query.Functions.Window;
using SonnetDB.Sql.Ast;
using SonnetDB.Storage.Format;

namespace SonnetDB.Query.Functions;

/// <summary>
/// 内置函数注册表；当前承载聚合函数、标量函数与窗口函数。
/// </summary>
public static class FunctionRegistry
{
    private static readonly IAggregateFunction[] _aggregateFunctionList = CreateAggregateFunctionList();
    private static readonly IScalarFunction[] _scalarFunctionList = CreateScalarFunctionList();
    private static readonly IWindowFunction[] _windowFunctionList = CreateWindowFunctionList();

    private static readonly IReadOnlyDictionary<string, IAggregateFunction> _aggregateFunctions =
        CreateFunctionsByName(_aggregateFunctionList);

    private static readonly IReadOnlyDictionary<string, IScalarFunction> _scalarFunctions =
        CreateFunctionsByName(_scalarFunctionList);

    private static readonly IReadOnlyDictionary<string, IWindowFunction> _windowFunctions =
        CreateFunctionsByName(_windowFunctionList);

    private static readonly IReadOnlyDictionary<Aggregator, IAggregateFunction> _aggregateFunctionsByLegacy =
        CreateAggregateFunctionsByLegacy(_aggregateFunctionList);

    /// <summary>返回所有已注册内置聚合函数。</summary>
    public static IReadOnlyCollection<IAggregateFunction> AggregateFunctions => _aggregateFunctionList;

    /// <summary>返回所有已注册内置标量函数。</summary>
    public static IReadOnlyCollection<IScalarFunction> ScalarFunctions => _scalarFunctionList;

    /// <summary>返回所有已注册内置窗口函数。</summary>
    public static IReadOnlyCollection<IWindowFunction> WindowFunctions => _windowFunctionList;

    /// <summary>按函数名查找聚合函数（大小写不敏感）。</summary>
    public static bool TryGetAggregate(string name, [MaybeNullWhen(false)] out IAggregateFunction function)
    {
        ArgumentNullException.ThrowIfNull(name);
        var udf = UserFunctionRegistry.Current;
        if (udf is not null && udf.TryGetAggregate(name, out function))
            return true;
        return _aggregateFunctions.TryGetValue(name, out function);
    }

    /// <summary>按函数名查找标量函数（大小写不敏感）。</summary>
    public static bool TryGetScalar(string name, [MaybeNullWhen(false)] out IScalarFunction function)
    {
        ArgumentNullException.ThrowIfNull(name);
        var udf = UserFunctionRegistry.Current;
        if (udf is not null && udf.TryGetScalar(name, out function))
            return true;
        return _scalarFunctions.TryGetValue(name, out function);
    }

    /// <summary>按函数名查找窗口函数（大小写不敏感）。</summary>
    public static bool TryGetWindow(string name, [MaybeNullWhen(false)] out IWindowFunction function)
    {
        ArgumentNullException.ThrowIfNull(name);
        var udf = UserFunctionRegistry.Current;
        if (udf is not null && udf.TryGetWindow(name, out function))
            return true;
        return _windowFunctions.TryGetValue(name, out function);
    }

    /// <summary>判断函数名属于哪一类内置函数。</summary>
    public static FunctionKind GetFunctionKind(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var udf = UserFunctionRegistry.Current;
        if (udf is not null)
        {
            if (udf.TryGetAggregate(name, out _)) return FunctionKind.Aggregate;
            if (udf.TryGetScalar(name, out _)) return FunctionKind.Scalar;
            if (udf.TryGetWindow(name, out _)) return FunctionKind.Window;
            if (udf.TryGetTableValuedFunction(name, out _)) return FunctionKind.TableValued;
        }
        if (_aggregateFunctions.ContainsKey(name)) return FunctionKind.Aggregate;
        if (_scalarFunctions.ContainsKey(name)) return FunctionKind.Scalar;
        if (_windowFunctions.ContainsKey(name)) return FunctionKind.Window;
        return FunctionKind.Unknown;
    }

    /// <summary>按 legacy 聚合枚举查找内置聚合函数。</summary>
    public static IAggregateFunction GetAggregate(Aggregator aggregator)
        => _aggregateFunctionsByLegacy.TryGetValue(aggregator, out var function)
            ? function
            : throw new InvalidOperationException($"未找到与 {aggregator} 对应的内置聚合函数。");

    private static IAggregateFunction[] CreateAggregateFunctionList() =>
    [
        new BuiltInAggregateFunction("count", Aggregator.Count, allowsStarArgument: true),
        new BuiltInAggregateFunction("sum", Aggregator.Sum),
        new BuiltInAggregateFunction("min", Aggregator.Min),
        new BuiltInAggregateFunction("max", Aggregator.Max),
        new BuiltInAggregateFunction("avg", Aggregator.Avg),
        new BuiltInAggregateFunction("first", Aggregator.First),
        new BuiltInAggregateFunction("last", Aggregator.Last),
        // Tier 2 — 扩展聚合（PR #52）
        new StddevFunction(),
        new VarianceFunction(),
        new SpreadFunction(),
        new ModeFunction(),
        new FixedPercentileFunction("median", 0.5),
        new PercentileFunction(),
        new FixedPercentileFunction("p50", 0.50),
        new FixedPercentileFunction("p90", 0.90),
        new FixedPercentileFunction("p95", 0.95),
        new FixedPercentileFunction("p99", 0.99),
        new TDigestAggFunction(),
        new DistinctCountFunction(),
        new HistogramFunction(),
        new CentroidFunction(),
        new TrajectoryLengthFunction(),
        new TrajectoryCentroidFunction(),
        new TrajectoryBboxFunction(),
        new TrajectorySpeedMaxFunction(),
        new TrajectorySpeedAvgFunction(),
        new TrajectorySpeedP95Function(),
        // Tier 4 — PID 控制律（PR #54）
        new PidAggregateFunction(),
        new PidEstimateFunction(),
    ];

    private static IScalarFunction[] CreateScalarFunctionList() =>
    [
        new BuiltInScalarFunction("abs", 1, 1, static args => Math.Abs(RequireDouble(args[0], "abs"))),
        new BuiltInScalarFunction("round", 1, 2, EvaluateRound),
        new BuiltInScalarFunction("sqrt", 1, 1, static args => Math.Sqrt(RequireDouble(args[0], "sqrt"))),
        new BuiltInScalarFunction("log", 1, 2, EvaluateLog),
        new BuiltInScalarFunction("coalesce", 1, int.MaxValue, EvaluateCoalesce),
        new BuiltInScalarFunction("cosine_distance", 2, 2, EvaluateCosineDistance),
        new BuiltInScalarFunction("l2_distance", 2, 2, EvaluateL2Distance),
        new BuiltInScalarFunction("inner_product", 2, 2, EvaluateInnerProduct),
        new BuiltInScalarFunction("vector_norm", 1, 1, EvaluateVectorNorm),
        new BuiltInScalarFunction("lat", 1, 1, static args => RequireGeoPoint(args[0], "lat").Lat),
        new BuiltInScalarFunction("lon", 1, 1, static args => RequireGeoPoint(args[0], "lon").Lon),
        new BuiltInScalarFunction("geo_distance", 2, 2, EvaluateGeoDistance),
        new BuiltInScalarFunction("geo_bearing", 2, 2, EvaluateGeoBearing),
        new BuiltInScalarFunction("geo_within", 4, 4, EvaluateGeoWithin),
        new BuiltInScalarFunction("geo_bbox", 5, 5, EvaluateGeoBbox),
        new BuiltInScalarFunction("geo_speed", 3, 3, EvaluateGeoSpeed),
        new BuiltInScalarFunction("geo_transform", 3, 3, EvaluateGeoTransform),
        new BuiltInScalarFunction("geo_wgs84_to_gcj02", 1, 1, static args => GeoCoordinateTransforms.Wgs84ToGcj02(RequireGeoPoint(args[0], "geo_wgs84_to_gcj02"))),
        new BuiltInScalarFunction("geo_gcj02_to_wgs84", 1, 1, static args => GeoCoordinateTransforms.Gcj02ToWgs84(RequireGeoPoint(args[0], "geo_gcj02_to_wgs84"))),
        new BuiltInScalarFunction("geo_gcj02_to_bd09", 1, 1, static args => GeoCoordinateTransforms.Gcj02ToBd09(RequireGeoPoint(args[0], "geo_gcj02_to_bd09"))),
        new BuiltInScalarFunction("geo_bd09_to_gcj02", 1, 1, static args => GeoCoordinateTransforms.Bd09ToGcj02(RequireGeoPoint(args[0], "geo_bd09_to_gcj02"))),
        new BuiltInScalarFunction("geo_wgs84_to_bd09", 1, 1, static args => GeoCoordinateTransforms.Transform(RequireGeoPoint(args[0], "geo_wgs84_to_bd09"), GeoCoordinateSystem.Wgs84, GeoCoordinateSystem.Bd09)),
        new BuiltInScalarFunction("geo_bd09_to_wgs84", 1, 1, static args => GeoCoordinateTransforms.Transform(RequireGeoPoint(args[0], "geo_bd09_to_wgs84"), GeoCoordinateSystem.Bd09, GeoCoordinateSystem.Wgs84)),
        new BuiltInScalarFunction("st_distance", 2, 2, EvaluateGeoDistance),
        new BuiltInScalarFunction("st_within", 4, 4, EvaluateGeoWithin),
        new BuiltInScalarFunction("st_dwithin", 4, 4, EvaluateGeoWithin),
    ];

    private static IWindowFunction[] CreateWindowFunctionList() =>
    [
        // Tier 3 — 差分类（PR #53）
        new DifferenceFunction(),
        new DeltaFunction(),
        new IncreaseFunction(),
        new DerivativeFunction(),
        new NonNegativeDerivativeFunction(),
        new RateFunction(),
        new IrateFunction(),
        // Tier 3 — 累计 / 积分
        new CumulativeSumFunction(),
        new RunningSumFunction(),
        new RunningMinFunction(),
        new RunningMaxFunction(),
        new IntegralFunction(),
        // Tier 3 — 平滑 / 预测
        new MovingAverageFunction(),
        new EwmaFunction(),
        new HoltWintersFunction(),
        // Tier 3 — 缺失值处理
        new FillFunction(),
        new LocfFunction(),
        new InterpolateFunction(),
        // Tier 3 — 状态分析
        new StateChangesFunction(),
        new StateDurationFunction(),
        // Tier 4 — PID 行级控制律（PR #54）
        new PidSeriesFunction(),
        // Tier 4 — 异常 / 变点检测（PR #55）
        new AnomalyFunction(),
        new ChangepointFunction(),
    ];

    private static IReadOnlyDictionary<string, TFunction> CreateFunctionsByName<TFunction>(TFunction[] functions)
        where TFunction : class, ISqlFunction
    {
        var dict = new Dictionary<string, TFunction>(functions.Length, StringComparer.OrdinalIgnoreCase);
        foreach (var function in functions)
            dict.Add(function.Name, function);
        return dict;
    }

    private static IReadOnlyDictionary<Aggregator, IAggregateFunction> CreateAggregateFunctionsByLegacy(
        IAggregateFunction[] functions)
    {
        var dict = new Dictionary<Aggregator, IAggregateFunction>(functions.Length);
        foreach (var function in functions)
        {
            if (function.LegacyAggregator is { } aggregator)
                dict.Add(aggregator, function);
        }

        return dict;
    }

    private static object? EvaluateRound(IReadOnlyList<object?> args)
    {
        double value = RequireDouble(args[0], "round");
        if (args.Count == 1)
            return Math.Round(value);

        int digits = checked((int)RequireDouble(args[1], "round"));
        return Math.Round(value, digits);
    }

    private static object? EvaluateLog(IReadOnlyList<object?> args)
    {
        double value = RequireDouble(args[0], "log");
        if (args.Count == 1)
            return Math.Log(value);

        double newBase = RequireDouble(args[1], "log");
        return Math.Log(value, newBase);
    }

    private static object? EvaluateCoalesce(IReadOnlyList<object?> args)
    {
        foreach (var arg in args)
        {
            if (arg is not null)
                return arg;
        }

        return null;
    }

    private static object? EvaluateCosineDistance(IReadOnlyList<object?> args)
    {
        var left = RequireVector(args[0], "cosine_distance");
        var right = RequireVector(args[1], "cosine_distance");
        EnsureSameVectorDimension(left, right, "cosine_distance");
        double leftNormSquared = 0;
        double rightNormSquared = 0;
        double dot = 0;
        for (int i = 0; i < left.Length; i++)
        {
            dot += left[i] * right[i];
            leftNormSquared += left[i] * left[i];
            rightNormSquared += right[i] * right[i];
        }

        if (leftNormSquared == 0 || rightNormSquared == 0)
            throw new InvalidOperationException("函数 cosine_distance 不接受零向量参数。");

        double cosineSimilarity = dot / (Math.Sqrt(leftNormSquared) * Math.Sqrt(rightNormSquared));
        return 1.0 - cosineSimilarity;
    }

    private static object? EvaluateL2Distance(IReadOnlyList<object?> args)
    {
        var left = RequireVector(args[0], "l2_distance");
        var right = RequireVector(args[1], "l2_distance");
        EnsureSameVectorDimension(left, right, "l2_distance");
        double sumSquared = 0;
        for (int i = 0; i < left.Length; i++)
        {
            double delta = left[i] - right[i];
            sumSquared += delta * delta;
        }

        return Math.Sqrt(sumSquared);
    }

    private static object? EvaluateInnerProduct(IReadOnlyList<object?> args)
    {
        var left = RequireVector(args[0], "inner_product");
        var right = RequireVector(args[1], "inner_product");
        EnsureSameVectorDimension(left, right, "inner_product");
        double dot = 0;
        for (int i = 0; i < left.Length; i++)
            dot += left[i] * right[i];
        return dot;
    }

    private static object? EvaluateVectorNorm(IReadOnlyList<object?> args)
    {
        var vector = RequireVector(args[0], "vector_norm");
        double sumSquared = 0;
        for (int i = 0; i < vector.Length; i++)
            sumSquared += vector[i] * vector[i];
        return Math.Sqrt(sumSquared);
    }

    private static object? EvaluateGeoDistance(IReadOnlyList<object?> args)
    {
        var left = RequireGeoPoint(args[0], "geo_distance");
        var right = RequireGeoPoint(args[1], "geo_distance");
        return HaversineMeters(left, right);
    }

    private static object? EvaluateGeoBearing(IReadOnlyList<object?> args)
    {
        var left = RequireGeoPoint(args[0], "geo_bearing");
        var right = RequireGeoPoint(args[1], "geo_bearing");
        double lat1 = DegreesToRadians(left.Lat);
        double lat2 = DegreesToRadians(right.Lat);
        double deltaLon = DegreesToRadians(right.Lon - left.Lon);
        double y = Math.Sin(deltaLon) * Math.Cos(lat2);
        double x = Math.Cos(lat1) * Math.Sin(lat2)
            - Math.Sin(lat1) * Math.Cos(lat2) * Math.Cos(deltaLon);
        double degrees = RadiansToDegrees(Math.Atan2(y, x));
        return (degrees + 360d) % 360d;
    }

    private static object? EvaluateGeoWithin(IReadOnlyList<object?> args)
    {
        var point = RequireGeoPoint(args[0], "geo_within");
        var center = GeoPoint.Create(
            RequireDouble(args[1], "geo_within"),
            RequireDouble(args[2], "geo_within"));
        double radiusMeters = RequireDouble(args[3], "geo_within");
        if (radiusMeters < 0 || double.IsNaN(radiusMeters))
            throw new InvalidOperationException("函数 geo_within 的 radius_m 必须 >= 0。");
        return HaversineMeters(point, center) <= radiusMeters;
    }

    private static object? EvaluateGeoBbox(IReadOnlyList<object?> args)
    {
        var point = RequireGeoPoint(args[0], "geo_bbox");
        double latMin = RequireDouble(args[1], "geo_bbox");
        double lonMin = RequireDouble(args[2], "geo_bbox");
        double latMax = RequireDouble(args[3], "geo_bbox");
        double lonMax = RequireDouble(args[4], "geo_bbox");
        if (latMin > latMax || lonMin > lonMax)
            throw new InvalidOperationException("函数 geo_bbox 要求 lat_min <= lat_max 且 lon_min <= lon_max。");
        _ = GeoPoint.Create(latMin, lonMin);
        _ = GeoPoint.Create(latMax, lonMax);
        return point.Lat >= latMin && point.Lat <= latMax && point.Lon >= lonMin && point.Lon <= lonMax;
    }

    private static object? EvaluateGeoSpeed(IReadOnlyList<object?> args)
    {
        var left = RequireGeoPoint(args[0], "geo_speed");
        var right = RequireGeoPoint(args[1], "geo_speed");
        double elapsedMs = RequireDouble(args[2], "geo_speed");
        if (elapsedMs <= 0 || double.IsNaN(elapsedMs))
            throw new InvalidOperationException("函数 geo_speed 的 elapsed_ms 必须 > 0。");
        return HaversineMeters(left, right) / (elapsedMs / 1000d);
    }

    private static object? EvaluateGeoTransform(IReadOnlyList<object?> args)
    {
        var point = RequireGeoPoint(args[0], "geo_transform");
        var from = GeoCoordinateTransforms.ParseCoordinateSystem(RequireString(args[1], "geo_transform"), "geo_transform", "from_system");
        var to = GeoCoordinateTransforms.ParseCoordinateSystem(RequireString(args[2], "geo_transform"), "geo_transform", "to_system");
        return GeoCoordinateTransforms.Transform(point, from, to);
    }

    private static double HaversineMeters(GeoPoint left, GeoPoint right)
    {
        const double EarthRadiusMeters = 6_371_008.8d;
        double lat1 = DegreesToRadians(left.Lat);
        double lat2 = DegreesToRadians(right.Lat);
        double deltaLat = DegreesToRadians(right.Lat - left.Lat);
        double deltaLon = DegreesToRadians(right.Lon - left.Lon);
        double sinLat = Math.Sin(deltaLat / 2d);
        double sinLon = Math.Sin(deltaLon / 2d);
        double a = sinLat * sinLat + Math.Cos(lat1) * Math.Cos(lat2) * sinLon * sinLon;
        double c = 2d * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1d - a));
        return EarthRadiusMeters * c;
    }

    private static double DegreesToRadians(double degrees) => degrees * (Math.PI / 180d);

    private static double RadiansToDegrees(double radians) => radians * (180d / Math.PI);

    private static double RequireDouble(object? value, string functionName)
    {
        return value switch
        {
            byte b => b,
            sbyte sb => sb,
            short s => s,
            ushort us => us,
            int i => i,
            uint ui => ui,
            long l => l,
            ulong ul => ul,
            float f => f,
            double d => d,
            decimal m => (double)m,
            null => throw new InvalidOperationException($"函数 {functionName} 不接受 NULL 参数。"),
            _ => throw new InvalidOperationException($"函数 {functionName} 需要数值参数。"),
        };
    }

    private static ReadOnlySpan<float> RequireVector(object? value, string functionName)
    {
        return value switch
        {
            float[] vector when vector.Length > 0 => vector,
            float[] => throw new InvalidOperationException($"函数 {functionName} 不接受空向量。"),
            null => throw new InvalidOperationException($"函数 {functionName} 不接受 NULL 参数。"),
            _ => throw new InvalidOperationException($"函数 {functionName} 需要 VECTOR 参数。"),
        };
    }

    private static GeoPoint RequireGeoPoint(object? value, string functionName)
    {
        return value switch
        {
            GeoPoint point => point,
            null => throw new InvalidOperationException($"函数 {functionName} 不接受 NULL 参数。"),
            _ => throw new InvalidOperationException($"函数 {functionName} 需要 GEOPOINT 参数。"),
        };
    }

    private static string RequireString(object? value, string functionName)
    {
        return value switch
        {
            string text when !string.IsNullOrWhiteSpace(text) => text,
            null => throw new InvalidOperationException($"函数 {functionName} 不接受 NULL 参数。"),
            _ => throw new InvalidOperationException($"函数 {functionName} 需要字符串参数。"),
        };
    }

    private static void EnsureSameVectorDimension(
        ReadOnlySpan<float> left, ReadOnlySpan<float> right, string functionName)
    {
        if (left.Length != right.Length)
        {
            throw new InvalidOperationException(
                $"函数 {functionName} 的两个向量参数维度必须一致：left={left.Length}, right={right.Length}。");
        }
    }

    private sealed class BuiltInAggregateFunction : IAggregateFunction
    {
        private readonly bool _allowsStarArgument;

        public BuiltInAggregateFunction(string name, Aggregator legacyAggregator, bool allowsStarArgument = false)
        {
            Name = name;
            LegacyAggregator = legacyAggregator;
            _allowsStarArgument = allowsStarArgument;
        }

        public string Name { get; }

        public Aggregator? LegacyAggregator { get; }

        public string? ResolveFieldName(FunctionCallExpression call, MeasurementSchema schema)
        {
            ArgumentNullException.ThrowIfNull(call);
            ArgumentNullException.ThrowIfNull(schema);

            if (call.IsStar)
            {
                if (!_allowsStarArgument)
                    throw new InvalidOperationException(
                        $"仅 count(*) 允许 '*' 作为参数，{call.Name}(*) 非法。");
                return null;
            }

            if (LegacyAggregator == Aggregator.Count
                && call.Arguments.Count == 1
                && call.Arguments[0] is LiteralExpression { Kind: SqlLiteralKind.Integer, IntegerValue: 1 })
            {
                return null;
            }

            if (call.Arguments.Count != 1 || call.Arguments[0] is not IdentifierExpression id)
                throw new InvalidOperationException(
                    $"{call.Name}(...) 必须接收一个列名作为参数；count 额外支持 count(*) 与 count(1)。");

            var col = schema.TryGetColumn(id.Name)
                ?? throw new InvalidOperationException(
                    $"聚合函数 {call.Name}({id.Name}) 引用了未知列。");
            if (col.Role != MeasurementColumnRole.Field)
                throw new InvalidOperationException(
                    $"聚合函数 {call.Name}({id.Name}) 只能作用于 FIELD 列。");
            if (LegacyAggregator != Aggregator.Count && col.DataType is FieldType.String or FieldType.Vector or FieldType.GeoPoint)
                throw new InvalidOperationException(
                    $"聚合函数 {call.Name} 仅支持数值字段，'{id.Name}' 的类型为 {col.DataType}。");
            return col.Name;
        }
    }

    private sealed class BuiltInScalarFunction : IScalarFunction
    {
        private readonly Func<IReadOnlyList<object?>, object?> _evaluator;

        public BuiltInScalarFunction(string name, int minArgumentCount, int maxArgumentCount,
            Func<IReadOnlyList<object?>, object?> evaluator)
        {
            Name = name;
            MinArgumentCount = minArgumentCount;
            MaxArgumentCount = maxArgumentCount;
            _evaluator = evaluator;
        }

        public string Name { get; }

        public int MinArgumentCount { get; }

        public int MaxArgumentCount { get; }

        public object? Evaluate(IReadOnlyList<object?> args)
        {
            ArgumentNullException.ThrowIfNull(args);
            if (args.Count < MinArgumentCount || args.Count > MaxArgumentCount)
            {
                string expected = MinArgumentCount == MaxArgumentCount
                    ? MinArgumentCount.ToString()
                    : $"{MinArgumentCount}~{MaxArgumentCount}";
                throw new InvalidOperationException(
                    $"函数 {Name} 需要 {expected} 个参数，实际为 {args.Count}。");
            }

            return _evaluator(args);
        }
    }
}
