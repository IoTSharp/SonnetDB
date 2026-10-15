using SonnetDB.Catalog;
using SonnetDB.Engine;
using SonnetDB.Model;
using SonnetDB.Query;
using SonnetDB.Sql.Ast;
using SonnetDB.Storage.Format;

namespace SonnetDB.Sql.Execution;

/// <summary>有界 measurement 数值或 VECTOR FIELD 替换；目标读取与原子提交在同一写锁内完成。</summary>
internal static class MeasurementVectorUpdateExecutor
{
    internal static RowsAffectedExecutionResult Execute(Tsdb tsdb, UpdateStatement statement, MeasurementSchema schema)
    {
        statement = (UpdateStatement)MeasurementSqlNameBinder.Bind(tsdb, statement);
        if (statement.Assignments.Count != 1)
            throw new NotSupportedException("measurement UPDATE 当前只支持一个数值或 VECTOR FIELD 赋值。");
        if (statement.FromClauses.Count != 0)
            throw new NotSupportedException("measurement VECTOR UPDATE 不支持 JOIN/FROM。");
        if (statement.ReturningColumns.Count != 0)
            throw new NotSupportedException("measurement VECTOR UPDATE 暂不支持 RETURNING。");

        var assignment = statement.Assignments[0];
        var column = schema.TryGetColumn(assignment.ColumnName)
            ?? throw new InvalidOperationException($"measurement UPDATE 引用了未知列 '{assignment.ColumnName}'。");
        if (column.Role != MeasurementColumnRole.Field
            || column.DataType is not (FieldType.Vector or FieldType.Int64 or FieldType.Float64))
            throw new NotSupportedException("measurement UPDATE 当前只支持已声明的数值或 VECTOR FIELD 列。");
        if (column.DataType != FieldType.Vector && statement.WhereClauseIsOmitted)
            throw new NotSupportedException("measurement 数值 UPDATE 必须提供 TAG 和 time 条件中的至少一种 WHERE 条件。");

        FieldValue value = column.DataType == FieldType.Vector
            ? BindValue(assignment.Value, column)
            : BindNumericValue(assignment.Value, column);
        return tsdb.ExecuteMeasurementWrite(() => ExecuteLocked(tsdb, statement, schema, column, value));
    }

    private static RowsAffectedExecutionResult ExecuteLocked(
        Tsdb tsdb,
        UpdateStatement statement,
        MeasurementSchema schema,
        MeasurementColumn column,
        FieldValue value)
    {
        if (!ReferenceEquals(tsdb.Measurements.TryGet(schema.Name), schema))
            throw new InvalidOperationException("measurement schema 已变化，请重试 FIELD UPDATE。");
        if (tsdb.VectorReplacements.Count >= MeasurementVectorReplacementStore.MaxReplacements)
            tsdb.PruneStaleVectorReplacements();
        var where = WhereClauseDecomposer.Decompose(statement.Where, schema);
        if (where.GeoFilters.Count != 0)
            throw new NotSupportedException("measurement FIELD UPDATE 暂不支持 GEO 谓词。");
        if (where.Residual is not null)
            throw new NotSupportedException("measurement FIELD UPDATE 当前只支持 TAG 和 time 条件。");

        var targets = new List<(ulong SeriesId, long Timestamp)>();
        foreach (SeriesEntry series in tsdb.Catalog.Find(schema.Name, where.TagFilter))
        {
            SqlExecutor.ThrowIfCancellationRequested();
            var timestamps = new HashSet<long>();
            foreach (var point in tsdb.Query.Execute(new PointQuery(series.Id, column.Name, where.TimeRange)))
            {
                SqlExecutor.ThrowIfCancellationRequested();
                if (!timestamps.Add(point.Timestamp))
                    continue;
                targets.Add((series.Id, point.Timestamp));
                if (targets.Count > MeasurementVectorReplacementStore.MaxRowsPerUpdate)
                {
                    throw new InvalidOperationException(
                        $"measurement FIELD UPDATE 单次最多修改 {MeasurementVectorReplacementStore.MaxRowsPerUpdate} 行。");
                }
            }

            foreach (var otherColumn in schema.FieldColumns)
            {
                if (string.Equals(otherColumn.Name, column.Name, StringComparison.Ordinal))
                    continue;
                foreach (var point in tsdb.Query.Execute(new PointQuery(series.Id, otherColumn.Name, where.TimeRange)))
                {
                    SqlExecutor.ThrowIfCancellationRequested();
                    if (!timestamps.Contains(point.Timestamp))
                    {
                        throw new NotSupportedException(
                            column.DataType == FieldType.Vector
                                ? "measurement VECTOR UPDATE 不支持目标行缺少原 VECTOR FIELD 值。"
                                : "measurement 数值 UPDATE 不支持目标行缺少原数值 FIELD 值。");
                    }
                }
            }
        }

        if (targets.Count == 0)
            return new RowsAffectedExecutionResult(schema.Name, 0, "update");

        // 一次 KV batch 写入完整目标集合；若不同 series 命中，按同一持久批次提交。
        SqlExecutor.ThrowIfCancellationRequested();
        tsdb.VectorReplacements.ReplaceMany(targets, column.Name, value);
        return new RowsAffectedExecutionResult(schema.Name, targets.Count, "update");
    }

    private static FieldValue BindNumericValue(SqlExpression expression, MeasurementColumn column)
    {
        if (expression is UnaryExpression { Operator: SqlUnaryOperator.Negate, Operand: LiteralExpression operand })
        {
            expression = operand.Kind switch
            {
                SqlLiteralKind.Integer => LiteralExpression.Integer(checked(-operand.IntegerValue)),
                SqlLiteralKind.Float => LiteralExpression.Float(-operand.FloatValue),
                SqlLiteralKind.Decimal => LiteralExpression.Decimal(-operand.DecimalValue),
                _ => expression,
            };
        }
        if (expression is LiteralExpression { Kind: SqlLiteralKind.Null })
            throw new InvalidOperationException($"数值 FIELD '{column.Name}' 不允许为 NULL。");

        // 参数已经绑定为字面量；不执行依赖当前行的表达式，避免部分计算后才发现非法值。
        if (expression is not LiteralExpression literal)
            throw new InvalidOperationException($"数值 FIELD '{column.Name}' 需要数值参数或字面量。");
        if (column.DataType == FieldType.Int64)
        {
            if (literal.Kind != SqlLiteralKind.Integer)
                throw new InvalidOperationException($"数值 FIELD '{column.Name}' 类型不匹配：INT 需要 Int64 整数。");
            return FieldValue.FromLong(literal.IntegerValue);
        }

        double number = literal.Kind switch
        {
            SqlLiteralKind.Integer => literal.IntegerValue,
            SqlLiteralKind.Float => literal.FloatValue,
            SqlLiteralKind.Decimal => (double)literal.DecimalValue,
            _ => throw new InvalidOperationException($"数值 FIELD '{column.Name}' 类型不匹配：FLOAT 需要数值。"),
        };
        if (!double.IsFinite(number))
            throw new InvalidOperationException($"数值 FIELD '{column.Name}' 不允许非有限数值。");
        return FieldValue.FromDouble(number);
    }

    private static FieldValue BindValue(SqlExpression expression, MeasurementColumn column)
    {
        if (expression is LiteralExpression { Kind: SqlLiteralKind.Null })
            throw new InvalidOperationException($"VECTOR FIELD '{column.Name}' 不允许为 NULL。");
        if (expression is not VectorLiteralExpression vector)
            throw new InvalidOperationException($"VECTOR FIELD '{column.Name}' 需要向量参数或字面量。");
        int expected = column.VectorDimension
            ?? throw new InvalidDataException($"VECTOR FIELD '{column.Name}' 缺少声明维度。");
        if (vector.Components.Count != expected)
            throw new InvalidOperationException(
                $"VECTOR FIELD '{column.Name}' 维度不匹配：声明 {expected}，实际 {vector.Components.Count}。");
        var values = new float[expected];
        for (int i = 0; i < expected; i++)
        {
            double component = vector.Components[i];
            values[i] = (float)component;
            if (!double.IsFinite(component) || !float.IsFinite(values[i]))
                throw new InvalidOperationException($"VECTOR FIELD '{column.Name}' 包含非有限数值。");
        }
        return FieldValue.FromVector(values);
    }
}
