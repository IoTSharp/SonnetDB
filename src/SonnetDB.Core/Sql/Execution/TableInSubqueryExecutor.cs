using System.Globalization;
using SonnetDB.Engine;
using SonnetDB.Sql.Ast;
using SonnetDB.Tables;

namespace SonnetDB.Sql.Execution;

/// <summary>
/// Materializes non-correlated <c>IN (SELECT ...)</c> predicates before the single-table
/// UPDATE/DELETE evaluator walks table rows.
/// </summary>
internal static class TableInSubqueryExecutor
{
    public static bool ContainsInSubquery(SqlExpression expression)
        => expression switch
        {
            InExpression { Subquery: not null } => true,
            BinaryExpression binary => ContainsInSubquery(binary.Left) || ContainsInSubquery(binary.Right),
            UnaryExpression unary => ContainsInSubquery(unary.Operand),
            IsNullExpression isNull => ContainsInSubquery(isNull.Operand),
            InExpression inExpression => ContainsInSubquery(inExpression.Value)
                || inExpression.Values.Any(ContainsInSubquery),
            FunctionCallExpression function => function.Arguments.Any(ContainsInSubquery),
            NamedArgumentExpression named => ContainsInSubquery(named.Value),
            CaseExpression caseExpression => caseExpression.WhenClauses.Any(clause =>
                    ContainsInSubquery(clause.Condition) || ContainsInSubquery(clause.Result))
                || (caseExpression.Else is not null && ContainsInSubquery(caseExpression.Else)),
            _ => false,
        };

    public static SqlExpression Materialize(Tsdb tsdb, SqlExpression expression, TableSchema outerSchema)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(outerSchema);

        var cache = new Dictionary<SelectStatement, IReadOnlyList<SqlExpression>>(
            ReferenceEqualityComparer.Instance);
        return Rewrite(tsdb, expression, outerSchema, cache);
    }

    private static SqlExpression Rewrite(
        Tsdb tsdb,
        SqlExpression expression,
        TableSchema outerSchema,
        Dictionary<SelectStatement, IReadOnlyList<SqlExpression>> cache)
    {
        switch (expression)
        {
            case BinaryExpression binary:
                var left = Rewrite(tsdb, binary.Left, outerSchema, cache);
                var right = Rewrite(tsdb, binary.Right, outerSchema, cache);
                return ReferenceEquals(left, binary.Left) && ReferenceEquals(right, binary.Right)
                    ? binary
                    : binary with { Left = left, Right = right };

            case UnaryExpression unary:
                var operand = Rewrite(tsdb, unary.Operand, outerSchema, cache);
                return ReferenceEquals(operand, unary.Operand) ? unary : unary with { Operand = operand };

            case IsNullExpression isNull:
                var nullOperand = Rewrite(tsdb, isNull.Operand, outerSchema, cache);
                return ReferenceEquals(nullOperand, isNull.Operand)
                    ? isNull
                    : isNull with { Operand = nullOperand };

            case InExpression inExpression:
                return RewriteIn(tsdb, inExpression, outerSchema, cache);

            case FunctionCallExpression function:
                var arguments = RewriteList(tsdb, function.Arguments, outerSchema, cache);
                return ReferenceEquals(arguments, function.Arguments)
                    ? function
                    : function with { Arguments = arguments };

            case NamedArgumentExpression named:
                var value = Rewrite(tsdb, named.Value, outerSchema, cache);
                return ReferenceEquals(value, named.Value) ? named : named with { Value = value };

            case CaseExpression caseExpression:
                return RewriteCase(tsdb, caseExpression, outerSchema, cache);

            default:
                return expression;
        }
    }

    private static InExpression RewriteIn(
        Tsdb tsdb,
        InExpression expression,
        TableSchema outerSchema,
        Dictionary<SelectStatement, IReadOnlyList<SqlExpression>> cache)
    {
        var value = Rewrite(tsdb, expression.Value, outerSchema, cache);
        if (expression.Subquery is null)
        {
            var values = RewriteList(tsdb, expression.Values, outerSchema, cache);
            return ReferenceEquals(value, expression.Value) && ReferenceEquals(values, expression.Values)
                ? expression
                : expression with { Value = value, Values = values };
        }

        if (!cache.TryGetValue(expression.Subquery, out var materialized))
        {
            EnsureSupportedSources(tsdb, expression.Subquery);
            EnsureSingleColumnProjection(tsdb, expression.Subquery);
            EnsureNonCorrelated(tsdb, expression.Subquery, outerSchema);
            var result = SqlExecutor.ExecuteSelect(tsdb, expression.Subquery);
            if (result.Columns.Count != 1)
                throw new InvalidOperationException("DELETE/UPDATE 的 IN 子查询必须只返回一列。");

            materialized = result.Rows
                .Select(static row => (SqlExpression)new MaterializedSubqueryValueExpression(row[0]))
                .ToArray();
            cache.Add(expression.Subquery, materialized);
        }

        return expression with
        {
            Value = value,
            Values = materialized,
            Subquery = null,
        };
    }

    private static void EnsureSupportedSources(Tsdb tsdb, SelectStatement statement)
    {
        const string message =
            "DELETE/UPDATE 的 IN 子查询当前只支持普通关系表、measurement 及其普通 JOIN/派生表/UNION。";
        if (statement.TableValuedFunction is not null
            || statement.Measurement.StartsWith("information_schema.", StringComparison.OrdinalIgnoreCase)
            || DocumentVectorSearchExecutor.IsVectorSearch(statement)
            || HybridSearchExecutor.IsHybridSearch(statement)
            || tsdb.Documents.Catalog.TryGet(statement.Measurement) is not null)
        {
            throw new NotSupportedException(message);
        }

        if (statement.FromSubquery is not null)
        {
            EnsureSupportedSources(tsdb, statement.FromSubquery);
        }
        else if (!string.IsNullOrEmpty(statement.Measurement)
            && tsdb.Tables.Catalog.TryGet(statement.Measurement) is null
            && tsdb.Measurements.TryGet(statement.Measurement) is null)
        {
            throw new NotSupportedException(message);
        }

        foreach (var join in statement.JoinClauses)
        {
            if (join.Subquery is not null)
                EnsureSupportedSources(tsdb, join.Subquery);
            else if (tsdb.Tables.Catalog.TryGet(join.TableName) is null)
                throw new NotSupportedException(message);
        }

        foreach (var union in statement.UnionStatements)
            EnsureSupportedSources(tsdb, union);
    }

    private static void EnsureSingleColumnProjection(Tsdb tsdb, SelectStatement statement)
    {
        if (statement.TableValuedFunction is not null)
            throw new NotSupportedException("DELETE/UPDATE 的 IN 子查询暂不支持 FROM 表值函数。");
        if (statement.Projections.Count != 1)
            throw new InvalidOperationException("DELETE/UPDATE 的 IN 子查询必须只返回一列。");

        foreach (var union in statement.UnionStatements)
            EnsureSingleColumnProjection(tsdb, union);

        if (statement.Projections[0].Expression is not StarExpression)
            return;

        bool isKnownSingleColumnTable = statement.FromSubquery is null
            && statement.JoinClauses.Count == 0
            && statement.UnionStatements.Count == 0
            && tsdb.Tables.Catalog.TryGet(statement.Measurement)?.Columns.Count == 1;
        if (!isKnownSingleColumnTable)
            throw new InvalidOperationException("DELETE/UPDATE 的 IN 子查询必须只返回一列，SELECT * 无法展开为单列。");
    }

    private static IReadOnlyList<SqlExpression> RewriteList(
        Tsdb tsdb,
        IReadOnlyList<SqlExpression> expressions,
        TableSchema outerSchema,
        Dictionary<SelectStatement, IReadOnlyList<SqlExpression>> cache)
    {
        SqlExpression[]? copy = null;
        for (int i = 0; i < expressions.Count; i++)
        {
            var rewritten = Rewrite(tsdb, expressions[i], outerSchema, cache);
            if (!ReferenceEquals(rewritten, expressions[i]))
            {
                copy ??= expressions.ToArray();
                copy[i] = rewritten;
            }
        }

        return copy ?? expressions;
    }

    private static CaseExpression RewriteCase(
        Tsdb tsdb,
        CaseExpression expression,
        TableSchema outerSchema,
        Dictionary<SelectStatement, IReadOnlyList<SqlExpression>> cache)
    {
        CaseWhenClause[]? clauses = null;
        for (int i = 0; i < expression.WhenClauses.Count; i++)
        {
            var condition = Rewrite(tsdb, expression.WhenClauses[i].Condition, outerSchema, cache);
            var result = Rewrite(tsdb, expression.WhenClauses[i].Result, outerSchema, cache);
            if (!ReferenceEquals(condition, expression.WhenClauses[i].Condition)
                || !ReferenceEquals(result, expression.WhenClauses[i].Result))
            {
                clauses ??= expression.WhenClauses.ToArray();
                clauses[i] = expression.WhenClauses[i] with { Condition = condition, Result = result };
            }
        }

        var elseExpression = expression.Else is null
            ? null
            : Rewrite(tsdb, expression.Else, outerSchema, cache);
        return clauses is null && ReferenceEquals(elseExpression, expression.Else)
            ? expression
            : expression with { WhenClauses = clauses ?? expression.WhenClauses, Else = elseExpression };
    }

    private static void EnsureNonCorrelated(Tsdb tsdb, SelectStatement statement, TableSchema outerSchema)
    {
        var outerSource = new StaticSource(
            outerSchema.Name,
            outerSchema.Columns.Select(static column => column.Name).ToHashSet(StringComparer.OrdinalIgnoreCase));
        ValidateSelect(tsdb, statement, new StaticScope([outerSource], Parent: null, IsDeleteOuter: true));
    }

    private static void ValidateSelect(Tsdb tsdb, SelectStatement statement, StaticScope parent)
    {
        if (statement.FromSubquery is not null)
            ValidateSelect(tsdb, statement.FromSubquery, parent);
        foreach (var join in statement.JoinClauses)
            if (join.Subquery is not null)
                ValidateSelect(tsdb, join.Subquery, parent);

        var scope = new StaticScope(BuildSources(tsdb, statement), parent, IsDeleteOuter: false);
        foreach (var projection in statement.Projections)
            ValidateExpression(tsdb, projection.Expression, scope);
        if (statement.Where is not null)
            ValidateExpression(tsdb, statement.Where, scope);
        foreach (var group in statement.GroupBy)
            ValidateExpression(tsdb, group, scope);
        if (statement.Having is not null)
            ValidateExpression(tsdb, statement.Having, scope);
        var orderScope = new StaticScope(
            [new StaticSource(string.Empty, GetOutputColumnNames(tsdb, statement))],
            scope,
            IsDeleteOuter: false);
        foreach (var order in statement.OrderByList)
            ValidateExpression(tsdb, order.Expression, orderScope);
        foreach (var join in statement.JoinClauses)
            ValidateExpression(tsdb, join.On, scope);
        foreach (var union in statement.UnionStatements)
            ValidateSelect(tsdb, union, parent);
    }

    private static IReadOnlyList<StaticSource> BuildSources(Tsdb tsdb, SelectStatement statement)
    {
        var sources = new List<StaticSource>();
        string baseQualifier = statement.TableAlias ?? statement.Measurement;
        bool usesMeasurementJoin = statement.FromSubquery is null
            && tsdb.Measurements.TryGet(statement.Measurement) is not null;
        if (statement.FromSubquery is not null)
        {
            sources.Add(new StaticSource(baseQualifier, GetOutputColumnNames(tsdb, statement.FromSubquery)));
        }
        else if (!string.IsNullOrEmpty(statement.Measurement)
            && tsdb.Tables.Catalog.TryGet(statement.Measurement) is { } schema)
        {
            sources.Add(new StaticSource(
                baseQualifier,
                schema.Columns.Select(static column => column.Name).ToHashSet(StringComparer.OrdinalIgnoreCase)));
        }
        else if (!string.IsNullOrEmpty(statement.Measurement)
            && tsdb.Measurements.TryGet(statement.Measurement) is { } measurementSchema)
        {
            var columns = measurementSchema.Columns
                .Select(static column => column.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            columns.Add("time");
            sources.Add(new StaticSource(baseQualifier, columns));
            if (!string.Equals(baseQualifier, measurementSchema.Name, StringComparison.OrdinalIgnoreCase))
                sources.Add(new StaticSource(measurementSchema.Name, columns));
        }

        foreach (var join in statement.JoinClauses)
        {
            if (join.Subquery is not null)
            {
                sources.Add(new StaticSource(join.Alias, GetOutputColumnNames(tsdb, join.Subquery)));
            }
            else if (tsdb.Tables.Catalog.TryGet(join.TableName) is { } joinSchema)
            {
                var columns = joinSchema.Columns
                    .Select(static column => column.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                sources.Add(new StaticSource(
                    join.Alias,
                    columns));
                if (usesMeasurementJoin
                    && !string.Equals(join.Alias, joinSchema.Name, StringComparison.OrdinalIgnoreCase))
                    sources.Add(new StaticSource(joinSchema.Name, columns));
            }
        }

        return sources;
    }

    private static HashSet<string> GetOutputColumnNames(Tsdb tsdb, SelectStatement statement)
    {
        if (statement.FromSubquery is null
            && statement.JoinClauses.Count == 0
            && tsdb.Tables.Catalog.TryGet(statement.Measurement) is { } tableSchema
            && !RelationalSelectExecutor.NeedsRelationalPath(statement))
        {
            return TableSqlExecutor.ResolveProjectionColumnNames(statement, tableSchema)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sources = BuildSources(tsdb, statement);
        foreach (var projection in statement.Projections)
        {
            if (!string.IsNullOrEmpty(projection.Alias))
            {
                names.Add(projection.Alias);
                continue;
            }

            switch (projection.Expression)
            {
                case StarExpression:
                    foreach (var source in sources)
                        names.UnionWith(source.Columns);
                    break;
                case IdentifierExpression identifier:
                    names.Add(identifier.Name);
                    break;
                case FunctionCallExpression function:
                    names.Add(FormatRelationalFunctionColumnName(function));
                    break;
                case LiteralExpression literal:
                    names.Add(FormatRelationalLiteralColumnName(literal));
                    break;
                default:
                    names.Add(projection.Expression.GetType().Name);
                    break;
            }
        }

        return names;
    }

    private static string FormatRelationalFunctionColumnName(FunctionCallExpression function)
    {
        if (function.IsStar)
            return $"{function.Name.ToLowerInvariant()}(*)";
        if (function.Arguments.Count == 1 && function.Arguments[0] is IdentifierExpression identifier)
            return $"{function.Name.ToLowerInvariant()}({identifier.Name})";
        return function.Name.ToLowerInvariant();
    }

    private static string FormatRelationalLiteralColumnName(LiteralExpression literal)
        => literal.Kind switch
        {
            SqlLiteralKind.Null => "NULL",
            SqlLiteralKind.Boolean => literal.BooleanValue ? "TRUE" : "FALSE",
            SqlLiteralKind.Integer => literal.IntegerValue.ToString(CultureInfo.InvariantCulture),
            SqlLiteralKind.Float => literal.FloatValue.ToString(CultureInfo.InvariantCulture),
            SqlLiteralKind.String => literal.StringValue ?? string.Empty,
            _ => literal.Kind.ToString(),
        };

    private static void ValidateExpression(Tsdb tsdb, SqlExpression expression, StaticScope scope)
    {
        switch (expression)
        {
            case IdentifierExpression identifier:
                if (ResolvesToDeleteOuter(scope, identifier))
                {
                    string name = identifier.Qualifier is null
                        ? identifier.Name
                        : $"{identifier.Qualifier}.{identifier.Name}";
                    throw new InvalidOperationException(
                        $"DELETE/UPDATE 的 IN 子查询不支持相关子查询引用 '{name}'。");
                }
                return;

            case BinaryExpression binary:
                ValidateExpression(tsdb, binary.Left, scope);
                ValidateExpression(tsdb, binary.Right, scope);
                return;

            case UnaryExpression unary:
                ValidateExpression(tsdb, unary.Operand, scope);
                return;

            case IsNullExpression isNull:
                ValidateExpression(tsdb, isNull.Operand, scope);
                return;

            case InExpression inExpression:
                ValidateExpression(tsdb, inExpression.Value, scope);
                foreach (var item in inExpression.Values)
                    ValidateExpression(tsdb, item, scope);
                if (inExpression.Subquery is not null)
                    ValidateSelect(tsdb, inExpression.Subquery, scope);
                return;

            case FunctionCallExpression function:
                foreach (var argument in function.Arguments)
                    ValidateExpression(tsdb, argument, scope);
                return;

            case NamedArgumentExpression named:
                ValidateExpression(tsdb, named.Value, scope);
                return;

            case CaseExpression caseExpression:
                foreach (var clause in caseExpression.WhenClauses)
                {
                    ValidateExpression(tsdb, clause.Condition, scope);
                    ValidateExpression(tsdb, clause.Result, scope);
                }
                if (caseExpression.Else is not null)
                    ValidateExpression(tsdb, caseExpression.Else, scope);
                return;

            case SubqueryExpression subquery:
                ValidateSelect(tsdb, subquery.Select, scope);
                return;

            case ExistsExpression exists:
                ValidateSelect(tsdb, exists.Select, scope);
                return;
        }
    }

    private static bool ResolvesToDeleteOuter(StaticScope scope, IdentifierExpression identifier)
    {
        for (StaticScope? current = scope; current is not null; current = current.Parent)
        {
            int matches = 0;
            foreach (var source in current.Sources)
            {
                if (identifier.Qualifier is not null
                    && !string.Equals(identifier.Qualifier, source.Qualifier, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (source.Columns.Contains(identifier.Name))
                    matches++;
            }

            if (matches != 0)
                return current.IsDeleteOuter;
        }

        return false;
    }

    private sealed record StaticSource(string Qualifier, HashSet<string> Columns);

    private sealed record StaticScope(
        IReadOnlyList<StaticSource> Sources,
        StaticScope? Parent,
        bool IsDeleteOuter);
}

/// <summary>Runtime value produced by a materialized single-column subquery.</summary>
internal sealed record MaterializedSubqueryValueExpression(object? Value) : SqlExpression;
