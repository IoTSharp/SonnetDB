using SonnetDB.Engine;
using SonnetDB.Sql.Ast;
using SonnetDB.Tables;
using SonnetDB.Views;

namespace SonnetDB.Sql.Execution;

/// <summary>在扫描、索引查找和连接之前绑定原生时刻的类型合同，不执行表达式或用户函数。</summary>
internal static class SqlDateTimeOffsetContract
{
    private readonly record struct TypeInfo(
        TableColumnType? Type,
        bool NullOnly = false,
        long? ParameterUtcTicks = null,
        bool IsExplicitParameterType = false,
        bool IsParameterValue = false)
    {
        internal bool IsNative => Type is TableColumnType.DateTimeOffset or TableColumnType.DateTime2;
    }

    private sealed record Column(string Name, string Qualifier, TypeInfo Type, bool QualifiedOnly = false);
    private sealed record Scope(IReadOnlyList<Column> Columns, Scope? Parent = null, bool DeclaresSources = false);

    internal static void Validate(Tsdb database, SqlStatement statement)
    {
        switch (statement)
        {
            case CreateTableAsStatement createAs:
                _ = Select(database, createAs.Query, null, 0);
                break;
            case SelectStatement select:
                _ = Select(database, select, null, 0);
                break;
            case UpdateStatement update:
                {
                    var columns = Source(database, update.TableName, update.TableAlias ?? update.TableName, null, 0).ToList();
                    foreach (var join in update.FromClauses)
                        columns.AddRange(JoinSource(database, join, null, 0));
                    var scope = new Scope(columns);
                    foreach (var join in update.FromClauses)
                        _ = Expression(database, join.On, scope, 0);
                    _ = Expression(database, update.Where, scope, 0);
                    foreach (var assignment in update.Assignments)
                        ValidateAssignment(columns.FirstOrDefault(column => column.Name == assignment.ColumnName)?.Type ?? default,
                            Expression(database, assignment.Value, scope, 0), assignment.Value);
                    break;
                }
            case DeleteStatement delete:
                _ = Expression(database, delete.Where,
                    new Scope(Source(database, delete.Measurement, delete.Measurement, null, 0)), 0);
                break;
            case InsertStatement insert:
                {
                    var columns = Source(database, insert.Measurement, insert.Measurement, null, 0);
                    var scope = new Scope(columns.Concat(columns.Select(column => column with
                    { Qualifier = "excluded", QualifiedOnly = true })).ToArray());
                    if (insert.Query is not null)
                    {
                        var output = Select(database, insert.Query, null, 0);
                        for (int index = 0; index < insert.Columns.Count && index < output.Count; index++)
                            ValidateAssignment(columns.FirstOrDefault(column => column.Name == insert.Columns[index])?.Type ?? default,
                                output[index].Type, null);
                    }
                    foreach (var row in insert.Rows)
                        for (int index = 0; index < row.Count; index++)
                            ValidateAssignment(index < insert.Columns.Count
                                    ? columns.FirstOrDefault(column => column.Name == insert.Columns[index])?.Type ?? default : default,
                                Expression(database, row[index], scope, 0), row[index]);
                    if (insert.OnConflict is { } conflict)
                    {
                        if (conflict.UpdateWhere is not null)
                            _ = Expression(database, conflict.UpdateWhere, scope, 0);
                        foreach (var assignment in conflict.UpdateAssignments)
                            ValidateAssignment(columns.FirstOrDefault(column => column.Name == assignment.ColumnName)?.Type ?? default,
                                Expression(database, assignment.Value, scope, 0), assignment.Value);
                    }
                    break;
                }
            case CreateTableStatement create:
                {
                    var scope = new Scope(create.Columns.Select(column => new Column(column.Name, create.Name,
                        CastType(column.DataType))).ToArray());
                    foreach (var check in create.CheckConstraintClauses)
                        _ = Expression(database, check.Expression, scope, 0);
                    foreach (var column in create.Columns)
                        if (column.DefaultExpression is not null)
                            ValidateAssignment(CastType(column.DataType),
                                Expression(database, column.DefaultExpression, scope, 0), column.DefaultExpression);
                    break;
                }
            case AlterTableAddCheckConstraintStatement check:
                _ = Expression(database, check.Expression,
                    new Scope(Source(database, check.TableName, check.TableName, null, 0)), 0);
                break;
            case CreateViewStatement view:
                _ = Select(database, view.Query, null, 0);
                break;
            case CreateMaterializedViewStatement view:
                _ = Select(database, view.Query, null, 0);
                break;
        }
    }

    internal static void ValidateTableExpression(SqlExpression expression, TableSchema schema)
        => _ = Expression(null, expression, new Scope(schema.Columns.Select(column =>
            new Column(column.Name, schema.Name, new TypeInfo(column.DataType))).ToArray()), 0);

    internal static IReadOnlyList<SelectColumnInfo> BindRecursiveColumns(Tsdb database, string name,
        IReadOnlyList<string> names, SelectStatement anchor, SelectStatement member)
    {
        var columns = Select(database, anchor, null, 0).Select((column, index) =>
            column with { Name = names[index], Qualifier = name }).ToList();
        var scope = new Scope(columns, DeclaresSources: true);
        MergeColumns(columns, Select(database, member, scope, 0));
        return columns.Select(column => new SelectColumnInfo(column.Type.Type)).ToArray();
    }

    private static IReadOnlyList<Column> Select(Tsdb database, SelectStatement query, Scope? outer, int depth)
    {
        CheckDepth(depth);
        if (query.IsRecursive)
        {
            // 递归源用 anchor 声明占位；每个分支仍在扫描前检查，不能从迭代行抽样推断。
            var declarations = new List<Column>();
            foreach (var cte in query.CommonTableExpressions)
            {
                var anchor = cte.Query with { SetOperations = [], Unions = null };
                var output = Select(database, anchor, outer, depth + 1).ToList();
                int first = declarations.Count;
                AddDeclarations();
                var recursiveScope = new Scope(declarations, outer, DeclaresSources: true);
                foreach (var branch in cte.Query.SetOperationList)
                    MergeColumns(output, Select(database, branch.Query, recursiveScope, depth + 1));
                declarations.RemoveRange(first, declarations.Count - first);
                AddDeclarations();
                foreach (var branch in cte.Query.SetOperationList)
                    MergeColumns(output, Select(database, branch.Query, recursiveScope, depth + 1));

                void AddDeclarations() => declarations.AddRange(output.Select((column, index) => column with
                {
                    Qualifier = cte.Name,
                    Name = cte.ColumnNames is { } names && index < names.Count ? names[index] : column.Name,
                }));
            }
            return Select(database, query with { IsRecursive = false, CommonTableExpressions = [] },
                new Scope(declarations, outer, DeclaresSources: true), depth + 1);
        }
        query = CommonTableExpressionExpander.Expand(query);
        if (database.Views.Catalog.Count != 0)
            query = ViewExpander.Expand(database.Views.Catalog, query);
        var columns = query.FromSubquery is not null
            ? Select(database, query.FromSubquery, null, depth + 1).Select(column => column with
            { Qualifier = query.TableAlias ?? query.Measurement }).ToList()
            : Source(database, query.Measurement, query.TableAlias ?? query.Measurement, outer, depth + 1).ToList();
        foreach (var join in query.JoinClauses)
            columns.AddRange(JoinSource(database, join, outer, depth + 1));
        var scope = new Scope(columns, outer);
        foreach (var join in query.JoinClauses)
            _ = Expression(database, join.On, scope, depth + 1);
        if (query.Where is not null)
            _ = Expression(database, query.Where, scope, depth + 1);
        foreach (var group in query.GroupBy)
            _ = Expression(database, group, scope, depth + 1);
        if (query.Having is not null)
            _ = Expression(database, query.Having, scope, depth + 1);
        foreach (var order in query.OrderByList)
            _ = Expression(database, order.Expression, scope, depth + 1);
        var result = new List<Column>();
        foreach (var item in query.Projections)
        {
            if (item.Expression is StarExpression)
                result.AddRange(columns);
            else if (item.Expression is IdentifierExpression { Name: "*", Qualifier: { } qualifier })
                result.AddRange(columns.Where(column => column.Qualifier == qualifier));
            else
                result.Add(new Column(item.Alias ?? (item.Expression as IdentifierExpression)?.Name ?? $"column{result.Count}",
                    string.Empty, Expression(database, item.Expression, scope, depth + 1)));
        }
        foreach (var operation in query.SetOperationList)
            MergeColumns(result, Select(database, operation.Query, outer, depth + 1));
        if (query.CteOutputColumnNames is { } names)
            for (int index = 0; index < result.Count && index < names.Count; index++)
                result[index] = result[index] with { Name = names[index] };
        return result;
    }

    private static void MergeColumns(IList<Column> left, IReadOnlyList<Column> right)
    {
        for (int index = 0; index < left.Count && index < right.Count; index++)
            left[index] = left[index] with { Type = Merge(left[index].Type, right[index].Type) };
    }

    private static IReadOnlyList<Column> JoinSource(Tsdb database, JoinClause join, Scope? outer, int depth)
    {
        if (join.ValuesRows is { } rows)
        {
            var result = join.ColumnNames.Select(name => new Column(name, join.Alias, new TypeInfo(null, true))).ToArray();
            foreach (var row in rows)
                for (int index = 0; index < row.Count && index < result.Length; index++)
                {
                    TypeInfo type = Expression(database, row[index], new Scope([]), depth + 1);
                    result[index] = result[index] with { Type = Merge(result[index].Type, type) };
                }
            return result;
        }
        IReadOnlyList<Column> output = join.Subquery is { } subquery
            ? Select(database, subquery, outer, depth + 1)
            : Source(database, join.TableName, join.Alias, outer, depth + 1);
        return output.Select((column, index) => column with
        {
            Qualifier = join.Alias,
            Name = index < join.ColumnNames.Count ? join.ColumnNames[index] : column.Name,
        }).ToArray();
    }

    private static IReadOnlyList<Column> Source(Tsdb database, string name, string qualifier, Scope? outer, int depth)
    {
        CheckDepth(depth);
        if (RecursiveCteScope.Find(name) is { } recursive)
            return recursive.Columns.Select((column, index) => new Column(column, qualifier,
                new TypeInfo(recursive.ColumnInfo?[index].DataType))).ToArray();
        for (Scope? scope = outer; scope is not null; scope = scope.Parent)
        {
            if (!scope.DeclaresSources)
                continue;
            var found = scope.Columns.Where(column => column.Qualifier == name).ToArray();
            if (found.Length != 0)
                return found.Select(column => column with { Qualifier = qualifier }).ToArray();
        }
        if (SonnetDB.Routines.TriggerTransitionTables.FindSchema(name) is { } transition)
            return transition.Columns.Select(column => new Column(column.Name, qualifier, new TypeInfo(column.DataType))).ToArray();
        if (database.Tables.Catalog.TryGet(name) is { } schema)
            return schema.Columns.Select(column => new Column(column.Name, qualifier, new TypeInfo(column.DataType))).ToArray();
        if (database.MaterializedViews.Catalog.TryGet(name) is not null)
        {
            var snapshot = database.MaterializedViews.ReadSnapshotDeclaration(name);
            return snapshot.Columns.Select((column, index) => new Column(column, qualifier,
                new TypeInfo(snapshot.ColumnInfo?[index].DataType ?? snapshot.ColumnSchema?[index].DataType))).ToArray();
        }
        return [];
    }

    private static TypeInfo Expression(Tsdb? database, SqlExpression expression, Scope scope, int depth)
    {
        CheckDepth(depth);
        switch (expression)
        {
            case IdentifierExpression identifier:
                return Resolve(identifier, scope)?.Type ?? default;
            case LiteralExpression { Kind: SqlLiteralKind.Null }:
                return new TypeInfo(null, true);
            case CastExpression cast:
                TypeInfo operandType = Expression(database, cast.Operand, scope, depth + 1);
                if (cast.IsParameterValue && cast.Operand is LiteralExpression { Kind: SqlLiteralKind.Null })
                    return new TypeInfo(null, NullOnly: true);
                if (cast.IsParameterValue && cast.TargetType == SqlDataType.DateTime2
                    && cast.Operand is LiteralExpression { Kind: SqlLiteralKind.Integer } ticks)
                    return new TypeInfo(TableColumnType.DateTime2, ParameterUtcTicks: ticks.IntegerValue,
                        IsExplicitParameterType: cast.IsExplicitParameterType,
                        IsParameterValue: true);
                if (cast.IsParameterValue && cast.TargetType == SqlDataType.DateTimeOffset
                    && cast.Operand is LiteralExpression { Kind: SqlLiteralKind.Integer } offsetTicks)
                    return new TypeInfo(TableColumnType.DateTimeOffset, ParameterUtcTicks: offsetTicks.IntegerValue,
                        IsExplicitParameterType: cast.IsExplicitParameterType,
                        IsParameterValue: true);
                TypeInfo castType = CastType(cast.TargetType);
                if (cast.IsParameterValue && castType.Type == operandType.Type)
                    return operandType with { IsParameterValue = true };
                if (operandType.IsExplicitParameterType && castType.Type == operandType.Type)
                    return operandType;
                return castType;
            case BinaryExpression binary:
                {
                    var left = Expression(database, binary.Left, scope, depth + 1);
                    var right = Expression(database, binary.Right, scope, depth + 1);
                    if (binary.Operator is SqlBinaryOperator.Equal or SqlBinaryOperator.NotEqual
                        or SqlBinaryOperator.LessThan or SqlBinaryOperator.LessThanOrEqual
                        or SqlBinaryOperator.GreaterThan or SqlBinaryOperator.GreaterThanOrEqual)
                        RequireCompatible(left, right, allowLegacyParameter: true);
                    else if (left.IsNative || right.IsNative)
                    {
                        if (binary.Operator == SqlBinaryOperator.Subtract && left.IsNative && right.IsNative && left.Type == right.Type)
                            return new TypeInfo(TableColumnType.Duration);
                        throw Unsupported();
                    }
                    return Infer(expression, scope);
                }
            case InExpression membership:
                {
                    var value = Expression(database, membership.Value, scope, depth + 1);
                    foreach (var item in membership.Values)
                        RequireCompatible(value, Expression(database, item, scope, depth + 1), allowLegacyParameter: true);
                    if (membership.Subquery is { } subquery && database is not null)
                    {
                        var output = Select(database, subquery, scope, depth + 1);
                        if (output.Count != 0)
                            RequireCompatible(value, output[0].Type);
                    }
                    return new TypeInfo(TableColumnType.Boolean);
                }
            case CaseExpression conditional:
                {
                    TypeInfo result = new(null, true);
                    foreach (var clause in conditional.WhenClauses)
                    {
                        _ = Expression(database, clause.Condition, scope, depth + 1);
                        result = Merge(result, Expression(database, clause.Result, scope, depth + 1));
                    }
                    return conditional.Else is null ? result
                        : Merge(result, Expression(database, conditional.Else, scope, depth + 1));
                }
            case FunctionCallExpression function:
                {
                    var arguments = function.Arguments.Select(argument => Expression(database, argument, scope, depth + 1)).ToArray();
                    if (function.Over is { } window)
                    {
                        foreach (var partition in window.PartitionBy)
                            _ = Expression(database, partition, scope, depth + 1);
                        foreach (var order in window.OrderBy)
                            _ = Expression(database, order.Expression, scope, depth + 1);
                    }
                    if (function.Name.Equals("coalesce", StringComparison.OrdinalIgnoreCase))
                        return arguments.Aggregate(new TypeInfo(null, true), Merge);
                    if (function.Name.Equals("nullif", StringComparison.OrdinalIgnoreCase) && arguments.Length == 2)
                        RequireCompatible(arguments[0], arguments[1], allowLegacyParameter: true);
                    if (function.Name.Equals("date_diff", StringComparison.OrdinalIgnoreCase)
                        || function.Name.Equals("datediff", StringComparison.OrdinalIgnoreCase))
                    {
                        if (arguments.Length == 3)
                        {
                            if (arguments[0].Type == TableColumnType.String)
                                RequireCompatible(arguments[1], arguments[2], allowLegacyParameter: true);
                            else if (arguments[2].Type == TableColumnType.String)
                                RequireCompatible(arguments[0], arguments[1], allowLegacyParameter: true);
                        }
                    }
                    return Infer(expression, scope);
                }
            case SubqueryExpression subquery when database is not null:
                {
                    var output = Select(database, subquery.Select, scope, depth + 1);
                    return output.Count == 0 ? default : output[0].Type;
                }
            case ExistsExpression exists when database is not null:
                _ = Select(database, exists.Select, scope, depth + 1);
                return new TypeInfo(TableColumnType.Boolean);
            case UnaryExpression unary:
                _ = Expression(database, unary.Operand, scope, depth + 1);
                return Infer(expression, scope);
            case IsNullExpression isNull:
                _ = Expression(database, isNull.Operand, scope, depth + 1);
                return new TypeInfo(TableColumnType.Boolean);
            case NamedArgumentExpression named:
                return Expression(database, named.Value, scope, depth + 1);
            default:
                return Infer(expression, scope);
        }
    }

    private static TypeInfo CastType(SqlDataType type)
        => new(RelationalSelectExecutor.InferColumnInfo(new CastExpression(LiteralExpression.Null(), type), []).DataType);

    private static TypeInfo Infer(SqlExpression expression, Scope scope)
    {
        var columns = new List<TableColumn>();
        var rewritten = SqlIdentifierExpressionBinder.Rewrite(expression, identifier =>
        {
            Column? column = Resolve(identifier, scope);
            if (column?.Type.Type is not { } type)
                return identifier;
            string name = "__native_type_" + columns.Count;
            columns.Add(new TableColumn(name, type, false, true, columns.Count));
            return identifier with { Name = name, Qualifier = null, IsNameBound = true };
        });
        return new TypeInfo(RelationalSelectExecutor.InferColumnInfo(rewritten, columns).DataType);
    }

    private static Column? Resolve(IdentifierExpression identifier, Scope scope)
    {
        for (Scope? current = scope; current is not null; current = current.Parent)
        {
            var matches = current.Columns.Where(column =>
                string.Equals(column.Name, identifier.Name, identifier.IsNameBound || identifier.IsQuoted
                    ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)
                && (identifier.Qualifier is null ? !column.QualifiedOnly
                    : string.Equals(column.Qualifier, identifier.Qualifier, identifier.IsNameBound || identifier.QualifierIsQuoted
                        ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))).ToArray();
            if (matches.Length != 0)
                return matches.Length == 1 ? matches[0] : null;
        }
        return null;
    }

    private static TypeInfo Merge(TypeInfo left, TypeInfo right)
    {
        RequireCompatible(left, right);
        // CASE/COALESCE 的多分支结果不能冒用其中一个参数的毫秒对齐证明。
        return left.NullOnly ? right : right.NullOnly ? left : left.Type == right.Type ? new TypeInfo(left.Type) : default;
    }

    private static void ValidateAssignment(TypeInfo target, TypeInfo source, SqlExpression? expression)
    {
        if (expression is DefaultValueExpression || source.NullOnly)
            return;
        if (target.Type == TableColumnType.DateTime2 && source.Type != TableColumnType.DateTime2)
            throw new InvalidOperationException("DATETIME2 写入需要原生 DateTime 参数或显式 CAST(... AS DATETIME2)，不能隐式解释 Unix 毫秒、字符串或其它类型。");
        if (target.Type == TableColumnType.DateTime && source.Type == TableColumnType.DateTime2)
            RequireCompatible(target, source, allowLegacyParameter: true);
        if (target.Type == TableColumnType.DateTime && source.Type == TableColumnType.DateTimeOffset)
        {
            if (source.IsParameterValue && !source.IsExplicitParameterType)
                RequireCompatible(target, source, allowLegacyParameter: true);
        }
    }

    private static void RequireCompatible(TypeInfo left, TypeInfo right, bool allowLegacyParameter = false)
    {
        if (allowLegacyParameter && (left.Type == TableColumnType.DateTime && right.ParameterUtcTicks is { } rightTicks
                && rightTicks % TimeSpan.TicksPerMillisecond == 0
            || right.Type == TableColumnType.DateTime && left.ParameterUtcTicks is { } leftTicks
                && leftTicks % TimeSpan.TicksPerMillisecond == 0))
            return;
        if (left.IsNative && right.IsNative && left.Type != right.Type)
            throw Unsupported();
        if (left.IsNative && !right.IsNative && !right.NullOnly
            || right.IsNative && !left.IsNative && !left.NullOnly)
            throw Unsupported();
    }

    private static InvalidOperationException Unsupported()
        => new("DATETIMEOFFSET/DATETIME2 比较或合并要求相同的原生类型；DATETIME、Unix 整数或未知类型必须显式 CAST。");

    private static void CheckDepth(int depth)
    {
        SqlExecutor.ThrowIfCancellationRequested();
        if (depth > 128)
            throw new InvalidOperationException("原生时间类型绑定超过 128 层上限。");
    }
}
