using SonnetDB.Catalog;
using SonnetDB.Model;
using SonnetDB.Query;
using SonnetDB.Sql.Ast;

namespace SonnetDB.Sql.Execution;

/// <summary>
/// <c>WHERE</c> 子句的 v1 分解结果：
/// <list type="bullet">
///   <item><description>仅支持顶层由 <c>AND</c> 连接的合取式（不允许 <c>OR</c> / <c>NOT</c>）。</description></item>
///   <item><description>每个叶子谓词必须是
///     <c>tag_col = '字符串字面量'</c> 或 <c>time</c> 与时间表达式的比较（<c>= / != / &lt; / &lt;= / &gt; / &gt;=</c>）。
///     时间表达式支持 Unix 毫秒整数字面量、duration 字面量以及 <c>now()</c> 参与的算术表达式。</description></item>
///   <item><description>不支持 field 列上的过滤；time 右值仅支持受限的时间表达式。</description></item>
/// </list>
/// </summary>
/// <param name="TagFilter">tag 列等值过滤集合（已合并去重；同 tag 列重复声明且取值不一致时抛错）。</param>
/// <param name="TimeRange">从 <c>time</c> 比较推导出的闭区间时间窗。</param>
/// <param name="Residual">
/// 无法下推为 tag/time/geo 的剩余谓词（字段比较、顶层 OR、非等值 tag 比较等）合取；由扫描路径
/// 按数据点逐点三值求值过滤。为 null 表示无残差（全部谓词已下推）。
/// </param>
internal readonly record struct WhereClause(
    IReadOnlyDictionary<string, string> TagFilter,
    TimeRange TimeRange,
    IReadOnlyList<GeoPointWhereFilter> GeoFilters,
    SqlExpression? Residual = null);

internal readonly record struct GeoPointWhereFilter(
    string FieldName,
    GeoPointFilter QueryFilter,
    FunctionCallExpression Predicate);

internal static class WhereClauseDecomposer
{
    /// <summary>
    /// 把可选的 WHERE 表达式分解为 <see cref="WhereClause"/>。<paramref name="where"/> 为 null 时
    /// 返回空 tag 过滤 + 全时间窗。
    /// </summary>
    /// <param name="where">WHERE 表达式 AST，可为 null。</param>
    /// <param name="schema">用于校验 tag 列名是否存在 / 是否真的是 Tag 列。</param>
    /// <exception cref="InvalidOperationException">表达式不在 v1 支持的形态内时抛出。</exception>
    public static WhereClause Decompose(SqlExpression? where, MeasurementSchema schema)
        => Decompose(where, schema, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    internal static WhereClause Decompose(SqlExpression? where, MeasurementSchema schema, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(schema);

        var tagFilter = new Dictionary<string, string>(StringComparer.Ordinal);
        long fromInclusive = long.MinValue;
        long toInclusive = long.MaxValue;
        var residualLeaves = new List<SqlExpression>();

        if (where is not null)
        {
            foreach (var leaf in FlattenAnd(where))
                ApplyLeaf(leaf, schema, tagFilter, ref fromInclusive, ref toInclusive, nowMs, residualLeaves);
        }

        if (fromInclusive > toInclusive)
            throw new InvalidOperationException(
                $"WHERE 子句的时间窗为空：[from={fromInclusive}, to={toInclusive}]。");

        var geoFilters = new List<GeoPointWhereFilter>();
        if (where is not null)
        {
            foreach (var leaf in FlattenAnd(where))
                TryAddGeoFilter(leaf, schema, geoFilters);
        }

        var residual = CombineAnd(residualLeaves);
        return new WhereClause(tagFilter, new TimeRange(fromInclusive, toInclusive), geoFilters, residual);
    }

    /// <summary>把残差叶子按 AND 合并为单个表达式；空列表返回 null。</summary>
    private static SqlExpression? CombineAnd(IReadOnlyList<SqlExpression> leaves)
    {
        if (leaves.Count == 0)
            return null;
        var combined = leaves[0];
        for (int i = 1; i < leaves.Count; i++)
            combined = new BinaryExpression(SqlBinaryOperator.And, combined, leaves[i]);
        return combined;
    }

    private static IEnumerable<SqlExpression> FlattenAnd(SqlExpression expr)
    {
        if (expr is BinaryExpression { Operator: SqlBinaryOperator.And } andExpr)
        {
            foreach (var l in FlattenAnd(andExpr.Left)) yield return l;
            foreach (var r in FlattenAnd(andExpr.Right)) yield return r;
        }
        else
        {
            yield return expr;
        }
    }

    private static void ApplyLeaf(
        SqlExpression leaf,
        MeasurementSchema schema,
        Dictionary<string, string> tagFilter,
        ref long fromInclusive,
        ref long toInclusive,
        long nowMs,
        List<SqlExpression> residualLeaves)
    {
        if (leaf is not BinaryExpression bin || !IsComparisonOperator(bin.Operator))
        {
            // geo 谓词由 TryAddGeoFilter 第二遍处理，这里跳过不重复。
            if (TryIsSupportedGeoPredicate(leaf, schema))
                return;

            // 其余（顶层 OR、NOT、IS NULL、IN、函数布尔等）无法下推 → 残差逐点求值（#217）。
            residualLeaves.Add(leaf);
            return;
        }

        // 规范化：把字面量放到右侧。
        var (left, right, op) = NormalizeComparison(bin);

        // time vs literal：可下推为时间窗。
        if (left is IdentifierExpression { Name: var leftName } &&
            string.Equals(leftName, "time", StringComparison.OrdinalIgnoreCase) &&
            IsTimeComparableLiteral(right))
        {
            ApplyTimeComparison(op, right, ref fromInclusive, ref toInclusive, nowMs);
            return;
        }

        // tag_col = 'literal'：可下推为 tag 等值过滤（仅顶层 AND 的等值 tag 比较到达此处）。
        if (op == SqlBinaryOperator.Equal &&
            left is IdentifierExpression { Name: var tagName } &&
            right is LiteralExpression { Kind: SqlLiteralKind.String, StringValue: var tagVal } &&
            schema.TryGetColumn(tagName) is { Role: MeasurementColumnRole.Tag })
        {
            if (tagFilter.TryGetValue(tagName, out var existing))
            {
                if (!string.Equals(existing, tagVal, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"WHERE 中 tag '{tagName}' 被同时约束为 '{existing}' 和 '{tagVal}'，结果集为空。");
            }
            else
            {
                tagFilter[tagName] = tagVal!;
            }
            return;
        }

        // 其余比较（字段比较、非等值 tag 比较、tag 比非字符串等）→ 残差逐点求值。
        residualLeaves.Add(leaf);
    }

    /// <summary>time 右值是否可下推为时间窗（整数 / duration / now() 时间表达式）；否则退回残差。</summary>
    private static bool IsTimeComparableLiteral(SqlExpression right)
        => right is LiteralExpression { Kind: SqlLiteralKind.Integer }
            or DurationLiteralExpression
            or BinaryExpression
            or FunctionCallExpression
            or UnaryExpression;

    private static bool TryAddGeoFilter(
        SqlExpression leaf,
        MeasurementSchema schema,
        List<GeoPointWhereFilter> geoFilters)
    {
        if (!TryParseGeoPredicate(leaf, schema, out var fieldName, out var box, out var predicate))
            return false;

        var (hashMin, hashMax) = GeoHash32.RangeForBox(box);
        geoFilters.Add(new GeoPointWhereFilter(fieldName, new GeoPointFilter(hashMin, hashMax), predicate));
        return true;
    }

    private static bool TryIsSupportedGeoPredicate(SqlExpression leaf, MeasurementSchema schema)
        => TryParseGeoPredicate(leaf, schema, out _, out _, out _);

    private static bool TryParseGeoPredicate(
        SqlExpression leaf,
        MeasurementSchema schema,
        out string fieldName,
        out GeoBoundingBox box,
        out FunctionCallExpression predicate)
    {
        fieldName = string.Empty;
        box = default;
        predicate = null!;

        if (leaf is not FunctionCallExpression fn)
            return false;

        if (string.Equals(fn.Name, "geo_within", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fn.Name, "ST_DWithin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fn.Name, "ST_Within", StringComparison.OrdinalIgnoreCase))
        {
            if (fn.Arguments.Count != 4 || fn.Arguments[0] is not IdentifierExpression id)
                return false;

            var column = schema.TryGetColumn(id.Name)
                ?? throw new InvalidOperationException($"WHERE 中引用了未知列 '{id.Name}'。");
            if (column.Role != MeasurementColumnRole.Field || column.DataType != Storage.Format.FieldType.GeoPoint)
                throw new InvalidOperationException($"WHERE 中地理空间谓词要求 '{id.Name}' 是 GEOPOINT FIELD 列。");

            double lat = RequireNumericLiteral(fn.Arguments[1], fn.Name);
            double lon = RequireNumericLiteral(fn.Arguments[2], fn.Name);
            double radius = RequireNumericLiteral(fn.Arguments[3], fn.Name);
            if (radius < 0)
                throw new InvalidOperationException($"函数 {fn.Name} 的 radius_m 必须 >= 0。");

            fieldName = id.Name;
            box = GeoHash32.BoundingBoxForCircle(lat, lon, radius);
            predicate = fn;
            return true;
        }

        if (string.Equals(fn.Name, "geo_bbox", StringComparison.OrdinalIgnoreCase))
        {
            if (fn.Arguments.Count != 5 || fn.Arguments[0] is not IdentifierExpression id)
                return false;

            var column = schema.TryGetColumn(id.Name)
                ?? throw new InvalidOperationException($"WHERE 中引用了未知列 '{id.Name}'。");
            if (column.Role != MeasurementColumnRole.Field || column.DataType != Storage.Format.FieldType.GeoPoint)
                throw new InvalidOperationException($"WHERE 中地理空间谓词要求 '{id.Name}' 是 GEOPOINT FIELD 列。");

            double latMin = RequireNumericLiteral(fn.Arguments[1], fn.Name);
            double lonMin = RequireNumericLiteral(fn.Arguments[2], fn.Name);
            double latMax = RequireNumericLiteral(fn.Arguments[3], fn.Name);
            double lonMax = RequireNumericLiteral(fn.Arguments[4], fn.Name);
            if (latMin > latMax || lonMin > lonMax)
                throw new InvalidOperationException("函数 geo_bbox 要求 lat_min <= lat_max 且 lon_min <= lon_max。");

            fieldName = id.Name;
            box = new GeoBoundingBox(latMin, lonMin, latMax, lonMax);
            predicate = fn;
            return true;
        }

        return false;
    }

    private static double RequireNumericLiteral(SqlExpression expression, string functionName)
    {
        return expression switch
        {
            LiteralExpression { Kind: SqlLiteralKind.Integer, IntegerValue: var value } => value,
            LiteralExpression { Kind: SqlLiteralKind.Float, FloatValue: var value } => value,
            _ => throw new InvalidOperationException($"函数 {functionName} 的空间剪枝参数必须是数值字面量。"),
        };
    }

    private static void ApplyTimeComparison(
        SqlBinaryOperator op,
        SqlExpression right,
        ref long fromInclusive,
        ref long toInclusive,
        long nowMs)
    {
        var ts = TimeExpressionEvaluator.Evaluate(right, nowMs);

        switch (op)
        {
            case SqlBinaryOperator.Equal:
                if (ts > fromInclusive) fromInclusive = ts;
                if (ts < toInclusive) toInclusive = ts;
                break;
            case SqlBinaryOperator.GreaterThanOrEqual:
                if (ts > fromInclusive) fromInclusive = ts;
                break;
            case SqlBinaryOperator.GreaterThan:
                if (ts == long.MaxValue)
                    throw new InvalidOperationException("'time > long.MaxValue' 永远为假。");
                if (ts + 1 > fromInclusive) fromInclusive = ts + 1;
                break;
            case SqlBinaryOperator.LessThanOrEqual:
                if (ts < toInclusive) toInclusive = ts;
                break;
            case SqlBinaryOperator.LessThan:
                if (ts == long.MinValue)
                    throw new InvalidOperationException("'time < long.MinValue' 永远为假。");
                if (ts - 1 < toInclusive) toInclusive = ts - 1;
                break;
            case SqlBinaryOperator.NotEqual:
                throw new InvalidOperationException(
                    "WHERE 中暂不支持 'time != X'（v1）。");
            default:
                throw new InvalidOperationException(
                    $"不支持的 time 比较运算符 {op}。");
        }
    }

    private static (SqlExpression Left, SqlExpression Right, SqlBinaryOperator Op) NormalizeComparison(
        BinaryExpression bin)
    {
        // 若右侧为标识符、左侧为字面量/时间表达式，则交换并翻转运算符。
        if (bin.Left is not IdentifierExpression && bin.Right is IdentifierExpression)
            return (bin.Right, bin.Left, FlipComparison(bin.Operator));
        return (bin.Left, bin.Right, bin.Operator);
    }

    private static SqlBinaryOperator FlipComparison(SqlBinaryOperator op) => op switch
    {
        SqlBinaryOperator.Equal => SqlBinaryOperator.Equal,
        SqlBinaryOperator.NotEqual => SqlBinaryOperator.NotEqual,
        SqlBinaryOperator.LessThan => SqlBinaryOperator.GreaterThan,
        SqlBinaryOperator.LessThanOrEqual => SqlBinaryOperator.GreaterThanOrEqual,
        SqlBinaryOperator.GreaterThan => SqlBinaryOperator.LessThan,
        SqlBinaryOperator.GreaterThanOrEqual => SqlBinaryOperator.LessThanOrEqual,
        _ => op,
    };

    private static bool IsComparisonOperator(SqlBinaryOperator op) => op
        is SqlBinaryOperator.Equal
        or SqlBinaryOperator.NotEqual
        or SqlBinaryOperator.LessThan
        or SqlBinaryOperator.LessThanOrEqual
        or SqlBinaryOperator.GreaterThan
        or SqlBinaryOperator.GreaterThanOrEqual;
}
