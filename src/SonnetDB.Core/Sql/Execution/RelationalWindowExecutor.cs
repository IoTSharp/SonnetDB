using SonnetDB.Engine;
using SonnetDB.Sql.Ast;
using SonnetDB.Tables;

namespace SonnetDB.Sql.Execution;

internal static partial class RelationalSelectExecutor
{
    private sealed record WindowRow(int Index, object?[] Row, object?[] OrderValues);

    private static bool ContainsWindow(IReadOnlyList<SelectItem> items)
        => items.Any(static item => ContainsWindow(item.Expression));

    private static bool ContainsWindow(SqlExpression expression)
        => expression switch
        {
            FunctionCallExpression { Over: not null } => true,
            FunctionCallExpression function => function.Arguments.Any(ContainsWindow),
            UnaryExpression unary => ContainsWindow(unary.Operand),
            CastExpression cast => ContainsWindow(cast.Operand),
            BinaryExpression binary => ContainsWindow(binary.Left) || ContainsWindow(binary.Right),
            CaseExpression caseExpression => caseExpression.WhenClauses.Any(when =>
                    ContainsWindow(when.Condition) || ContainsWindow(when.Result))
                || caseExpression.Else is not null && ContainsWindow(caseExpression.Else),
            IsNullExpression isNull => ContainsWindow(isNull.Operand),
            InExpression inExpression => ContainsWindow(inExpression.Value)
                || inExpression.Values.Any(ContainsWindow),
            _ => false,
        };

    private static (IReadOnlyList<string> Columns, IEnumerable<IReadOnlyList<object?>> Rows,
        IReadOnlyList<SelectColumnInfo> ColumnInfo) ProjectWindowRows(
        Tsdb tsdb,
        SelectStatement statement,
        Relation relation,
        RelationalScope? outerScope,
        SubqueryMemo memo)
    {
        IReadOnlyList<Projection> projections = BuildRawProjections(statement.Projections, relation);
        object?[][] input = RetainBranchRows(relation.Rows).ToArray();
        var output = new object?[input.Length][];
        for (int row = 0; row < input.Length; row++)
            output[row] = new object?[projections.Count];

        var columnInfo = new SelectColumnInfo[projections.Count];
        for (int column = 0; column < projections.Count; column++)
        {
            SqlExpression expression = projections[column].Expression;
            if (expression is FunctionCallExpression { Over: not null } function)
            {
                object?[] values = EvaluateWindow(tsdb, function, relation with { Rows = input }, input,
                    outerScope, memo);
                for (int row = 0; row < input.Length; row++)
                    output[row][column] = values[row];
                columnInfo[column] = function.Name.Equals("row_number", StringComparison.OrdinalIgnoreCase)
                    ? new SelectColumnInfo(TableColumnType.Int64, false)
                    : InferSelectColumnInfo(function, relation.Columns);
            }
            else
            {
                if (ContainsWindow(expression))
                    throw new NotSupportedException("关系表窗口函数当前仅支持直接 SELECT 投影。");
                columnInfo[column] = InferSelectColumnInfo(expression, relation.Columns);
                for (int row = 0; row < input.Length; row++)
                {
                    SqlExecutor.ThrowIfCancellationRequested();
                    output[row][column] = EvaluateScalar(tsdb, expression, relation.Columns,
                        input[row], outerScope, memo);
                }
            }
        }

        return (projections.Select(static projection => projection.Name).ToArray(), output, columnInfo);
    }

    private static object?[] EvaluateWindow(
        Tsdb tsdb,
        FunctionCallExpression function,
        Relation relation,
        object?[][] input,
        RelationalScope? outerScope,
        SubqueryMemo memo)
    {
        WindowSpecification specification = function.Over!;
        string name = function.Name.ToLowerInvariant();
        bool rowNumber = name == "row_number";
        if (!rowNumber && !IsAggregateFunction(name))
            throw new NotSupportedException($"关系表窗口函数 '{function.Name}' 尚未支持。");
        if (rowNumber && (function.IsStar || function.IsDistinct || function.Arguments.Count != 0))
            throw new InvalidOperationException("row_number() 不接受参数或 DISTINCT。");
        if (rowNumber && specification.OrderBy.Count == 0)
            throw new InvalidOperationException("关系表 row_number() 必须指定窗口内 ORDER BY。");
        if (!rowNumber && (function.IsStar && name != "count"
            || !function.IsStar && function.Arguments.Count != 1))
            throw new InvalidOperationException($"窗口函数 {function.Name} 的参数数量无效。");
        if (function.IsDistinct && specification.OrderBy.Count != 0)
            throw new NotSupportedException("有序窗口聚合暂不支持 DISTINCT。");

        foreach (SqlExpression expression in specification.PartitionBy)
            ValidateWindowKey(expression);
        foreach (OrderBySpec order in specification.OrderBy)
            ValidateWindowKey(order.Expression);

        object?[] result = new object?[input.Length];
        var partitions = new Dictionary<GroupKey, List<WindowRow>>();
        for (int index = 0; index < input.Length; index++)
        {
            SqlExecutor.ThrowIfCancellationRequested();
            object?[] row = input[index];
            var key = new GroupKey(specification.PartitionBy.Select(expression =>
                EvaluateScalar(tsdb, expression, relation.Columns, row, outerScope, memo)).ToArray());
            if (!partitions.TryGetValue(key, out List<WindowRow>? partition))
            {
                partition = [];
                partitions.Add(key, partition);
            }
            partition.Add(new WindowRow(index, row, specification.OrderBy.Select(order =>
                EvaluateScalar(tsdb, order.Expression, relation.Columns, row, outerScope, memo)).ToArray()));
        }

        bool integral = !rowNumber && name is "sum" or "min" or "max" or "avg"
            && IsIntegralAggregateInput(tsdb, new AggregateSpec(function), relation, outerScope, memo);
        foreach (List<WindowRow> partition in partitions.Values)
        {
            SqlExecutor.ThrowIfCancellationRequested();
            if (specification.OrderBy.Count > 0)
                partition.Sort((left, right) => CompareWindowRows(left, right, specification.OrderBy));

            if (rowNumber)
            {
                for (int position = 0; position < partition.Count; position++)
                    result[partition[position].Index] = (long)position + 1;
            }
            else if (specification.OrderBy.Count == 0)
            {
                object? value = EvaluateAggregate(tsdb, new AggregateSpec(function), relation.Columns,
                    partition.Select(static item => item.Row).ToArray(), integral, outerScope, memo);
                foreach (WindowRow item in partition)
                    result[item.Index] = value;
            }
            else
            {
                // 默认 RANGE UNBOUNDED PRECEDING..CURRENT ROW：相同排序键共享前缀结果。
                var prefix = new List<object?[]>(partition.Count);
                for (int start = 0; start < partition.Count;)
                {
                    SqlExecutor.ThrowIfCancellationRequested();
                    int end = start + 1;
                    while (end < partition.Count && CompareWindowKeys(
                        partition[start], partition[end], specification.OrderBy) == 0)
                        end++;
                    for (int position = start; position < end; position++)
                        prefix.Add(partition[position].Row);
                    object? value = EvaluateAggregate(tsdb, new AggregateSpec(function), relation.Columns,
                        prefix, integral, outerScope, memo);
                    for (int position = start; position < end; position++)
                        result[partition[position].Index] = value;
                    start = end;
                }
            }
        }
        return result;

        void ValidateWindowKey(SqlExpression expression)
        {
            if (ContainsWindow(expression) || ContainsAggregate(expression)
                || !CanEvaluateAgainstRelation(expression, relation))
                throw new InvalidOperationException("窗口 PARTITION BY / ORDER BY 必须引用可解析的关系行标量表达式。");
        }
    }

    private static int CompareWindowRows(WindowRow left, WindowRow right,
        IReadOnlyList<OrderBySpec> orderBy)
    {
        int comparison = CompareWindowKeys(left, right, orderBy);
        return comparison != 0 ? comparison : left.Index.CompareTo(right.Index);
    }

    private static int CompareWindowKeys(WindowRow left, WindowRow right,
        IReadOnlyList<OrderBySpec> orderBy)
    {
        for (int index = 0; index < orderBy.Count; index++)
        {
            int comparison = ScalarComparer.Instance.Compare(left.OrderValues[index], right.OrderValues[index]);
            if (comparison != 0)
                return orderBy[index].Direction == SortDirection.Descending ? -comparison : comparison;
        }
        return 0;
    }
}
