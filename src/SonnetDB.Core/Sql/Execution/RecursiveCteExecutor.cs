using System.Text;
using SonnetDB.Engine;
using SonnetDB.Sql.Ast;

namespace SonnetDB.Sql.Execution;

/// <summary>在单次查询内按层求值一个有界递归 CTE。</summary>
internal static class RecursiveCteExecutor
{
    internal const int MaxDepth = 64;
    internal const int MaxRows = 100_000;
    internal const int MaxCandidateRowsPerRound = 100_000;
    internal const long MaxBytes = 32L * 1024 * 1024;

    internal static SelectExecutionResult Execute(Tsdb tsdb, SelectStatement statement)
    {
        (CommonTableExpression definition, SelectStatement anchor, SelectStatement member, bool distinct,
            IReadOnlyList<CommonTableExpression> preceding, IReadOnlyList<CommonTableExpression> ordinary) =
            Validate(statement);
        SqlExecutor.ThrowIfCancellationRequested();

        anchor = ExpandOrdinaryCtes(anchor, preceding);
        member = ExpandOrdinaryCtes(member, preceding);
        SelectExecutionResult anchorResult = ProbeBranch(tsdb, anchor, MaxCandidateRowsPerRound);
        string[] columns = ResolveColumns(definition, anchorResult.Columns);
        if (member.Projections.Count != columns.Length)
            throw new InvalidOperationException(
                $"递归 CTE 分支列数不一致：期望 {columns.Length} 列，实际 {member.Projections.Count} 列。");
        var rows = new List<IReadOnlyList<object?>>();
        var seen = distinct
            ? new HashSet<IReadOnlyList<object?>>(SqlExecutor.DistinctRowComparer.Instance)
            : null;
        var types = new Type?[columns.Length];
        long bytes = 0;
        using var memory = SqlQueryResources.Current?.CreateReservation();
        IReadOnlyList<IReadOnlyList<object?>> frontier = AddRows(anchorResult.Rows);

        for (int depth = 0; frontier.Count > 0; depth++)
        {
            SqlExecutor.ThrowIfCancellationRequested();
            if (depth >= MaxDepth)
                throw new InvalidOperationException($"递归 CTE 超过最大层数 {MaxDepth}。");

            using var scope = RecursiveCteScope.Enter(
                definition.Name,
                new SelectExecutionResult(columns, frontier));
            int candidateLimit = distinct
                ? MaxCandidateRowsPerRound
                : Math.Min(MaxCandidateRowsPerRound, MaxRows - rows.Count);
            SelectExecutionResult next = ProbeBranch(tsdb, member, candidateLimit);
            if (next.Columns.Count != columns.Length)
                throw new InvalidOperationException(
                    $"递归 CTE 分支列数不一致：期望 {columns.Length} 列，实际 {next.Columns.Count} 列。");
            frontier = AddRows(next.Rows);
        }

        using var finalScope = RecursiveCteScope.Enter(
            definition.Name,
            new SelectExecutionResult(columns, rows));
        return SqlExecutor.ExecuteSelect(tsdb, statement with
        {
            IsRecursive = false,
            CommonTableExpressions = ordinary,
        });

        IReadOnlyList<IReadOnlyList<object?>> AddRows(IReadOnlyList<IReadOnlyList<object?>> source)
        {
            var added = new List<IReadOnlyList<object?>>(source.Count);
            foreach (IReadOnlyList<object?> row in source)
            {
                SqlExecutor.ThrowIfCancellationRequested();
                if (row.Count != columns.Length)
                    throw new InvalidOperationException("递归 CTE 行宽与声明列数不一致。");
                for (int index = 0; index < row.Count; index++)
                {
                    if (row[index] is not { } value)
                        continue;
                    Type actual = NormalizeType(value.GetType());
                    if (types[index] is null)
                        types[index] = actual;
                    else if (types[index] != actual)
                        throw new InvalidOperationException($"递归 CTE 第 {index + 1} 列的分支类型不一致。");
                }

                object?[] copy = row.ToArray();
                if (seen is not null && !seen.Add(copy))
                    continue;
                if (rows.Count >= MaxRows)
                    throw new InvalidOperationException($"递归 CTE 超过最大结果行数 {MaxRows}。");
                long rowBytes = EstimateBytes(copy);
                if (rowBytes > MaxBytes - bytes)
                    throw new InvalidOperationException($"递归 CTE 超过最大结果字节数 {MaxBytes}。");
                if (memory is not null && !memory.TryReserve(rowBytes))
                    throw new InvalidOperationException("递归 CTE 超过当前查询或数据库的内存预算。");
                bytes += rowBytes;
                rows.Add(copy);
                added.Add(copy);
            }
            return added;
        }
    }

    private static SelectExecutionResult ProbeBranch(Tsdb tsdb, SelectStatement branch, int candidateLimit)
    {
        SqlExecutor.ThrowIfCancellationRequested();
        SelectExecutionResult result = SqlExecutor.ExecuteSelect(tsdb, branch with
        {
            Pagination = new PaginationSpec(0, candidateLimit + 1),
        });
        if (result.Rows.Count > candidateLimit)
        {
            throw new InvalidOperationException(
                $"递归 CTE 单轮候选行数超过上限 {candidateLimit}。");
        }
        return result;
    }

    private static SelectStatement ExpandOrdinaryCtes(
        SelectStatement branch,
        IReadOnlyList<CommonTableExpression> preceding)
        => preceding.Count == 0
            ? branch
            : CommonTableExpressionExpander.Expand(branch with { CommonTableExpressions = preceding });

    internal static SqlExplainExecutionResult Explain(string? databaseName, SelectStatement statement)
    {
        var (definition, _, _, _, _, _) = Validate(statement);
        return new SqlExplainExecutionResult(
            Database: databaseName,
            StatementType: "select_recursive_cte",
            Measurement: definition.Name,
            MatchedSeriesCount: 0,
            EstimatedSegmentCount: 0,
            EstimatedBlockCount: 0,
            EstimatedScannedRows: 0,
            EstimatedMemTableRows: 0,
            EstimatedSegmentRows: 0,
            HasTimeFilter: false,
            TagFilterCount: 0,
            AccessPath: "recursive_cte",
            IndexName: null)
        {
            PlanNode = "recursive_cte_worktable",
            MemoryBehavior = $"分层阻塞物化；最大层数 {MaxDepth}，最大行数 {MaxRows}，单轮候选行数 {MaxCandidateRowsPerRound}，最大字节数 {MaxBytes}。",
        };
    }

    private static (CommonTableExpression Definition, SelectStatement Anchor, SelectStatement Member,
        bool Distinct, IReadOnlyList<CommonTableExpression> Preceding,
        IReadOnlyList<CommonTableExpression> Ordinary)
        Validate(SelectStatement statement)
    {
        var definitions = statement.CommonTableExpressions;
        if (definitions.Count == 0)
            throw new NotSupportedException("WITH RECURSIVE 要求一个递归 CTE 定义。");
        if (definitions.Select(static cte => cte.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            != definitions.Count)
            throw new InvalidOperationException("CTE 名称不能重复。");

        var recursive = definitions
            .Select((cte, index) => (Definition: cte, Index: index))
            .Where(item => References(item.Definition.Name, item.Definition.Query) != 0
                || item.Definition.Query.SetOperationList.Any(operation =>
                    References(item.Definition.Name, operation.Query) != 0))
            .ToArray();
        if (recursive.Length != 1)
            throw new NotSupportedException("WITH RECURSIVE 当前要求恰好一个自引用的递归 CTE 定义。");
        (CommonTableExpression definition, int recursiveIndex) = recursive[0];
        CommonTableExpression[] preceding = definitions.Take(recursiveIndex).ToArray();
        CommonTableExpression[] ordinary = definitions.Where((_, index) => index != recursiveIndex).ToArray();
        if (preceding.Any(cte => References(definition.Name, cte.Query) != 0
            || cte.Query.SetOperationList.Any(operation =>
                References(definition.Name, operation.Query) != 0)))
            throw new NotSupportedException("递归 CTE 之前的普通 CTE 不得前向引用递归定义。");
        IReadOnlyList<SqlSetOperation> operations = definition.Query.SetOperationList;
        if (operations.Count != 1 || operations[0].Kind is not (SqlSetOperationKind.UnionAll or SqlSetOperationKind.Union))
            throw new NotSupportedException("递归 CTE 当前要求一个 anchor UNION [ALL] recursive_member。");

        SelectStatement anchor = definition.Query with
        {
            Unions = null,
            SetOperations = Array.Empty<SqlSetOperation>(),
            OrderBy = null,
            OrderByItems = null,
            Pagination = null,
        };
        SelectStatement member = operations[0].Query;
        if (definitions.Skip(recursiveIndex + 1).Any(cte =>
            References(cte.Name, anchor) != 0 || References(cte.Name, member) != 0))
            throw new NotSupportedException("递归 CTE 的 anchor 或 recursive_member 不得前向引用后续普通 CTE。");
        if (References(definition.Name, anchor) != 0)
            throw new NotSupportedException("递归 CTE 的 anchor 不得引用自身。");
        if (anchor.FromSubquery is not null
            || anchor.JoinClauses.Any(static join => join.Subquery is not null)
            || RelationalSelectExecutor.ContainsSubquery(anchor))
            throw new NotSupportedException("递归 CTE 的 anchor 当前不支持子查询。");
        if (anchor.Distinct || anchor.GroupBy.Count != 0 || anchor.Having is not null
            || RelationalSelectExecutor.ContainsAggregateForExplain(anchor))
            throw new NotSupportedException("递归 CTE 的 anchor 当前不支持 DISTINCT 或聚合分组。");
        if (References(definition.Name, member) != 1
            || member.SetOperationList.Count != 0
            || member.FromSubquery is not null
            || member.JoinClauses.Any(static join => join.Subquery is not null)
            || RelationalSelectExecutor.ContainsSubquery(member)
            || member.GroupBy.Count != 0
            || member.Having is not null
            || member.Distinct
            || RelationalSelectExecutor.ContainsAggregateForExplain(member)
            || member.Projections.Any(static item => item.Expression is StarExpression)
            || member.OrderByList.Count != 0
            || member.Pagination is not null)
        {
            throw new NotSupportedException("递归 CTE 的 recursive_member 仅支持一次直接自引用及普通 SELECT/JOIN/WHERE 投影。");
        }
        if (definition.Query.OrderByList.Count != 0 || definition.Query.Pagination is not null)
            throw new NotSupportedException("递归 CTE 定义内不支持 ORDER BY 或分页；请在最终 SELECT 中指定。");
        return (definition, anchor, member, operations[0].Kind == SqlSetOperationKind.Union,
            preceding, ordinary);
    }

    private static int References(string name, SelectStatement query)
    {
        int count = string.Equals(query.Measurement, name, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        count += query.JoinClauses.Count(join => string.Equals(join.TableName, name, StringComparison.OrdinalIgnoreCase));
        if (query.FromSubquery is not null)
            count += References(name, query.FromSubquery);
        foreach (JoinClause join in query.JoinClauses)
        {
            if (join.Subquery is not null)
                count += References(name, join.Subquery);
        }
        return count;
    }

    private static string[] ResolveColumns(CommonTableExpression definition, IReadOnlyList<string> actual)
    {
        if (definition.ColumnNames is not { } names)
            return actual.ToArray();
        if (names.Count != actual.Count)
            throw new InvalidOperationException(
                $"递归 CTE 输出列数不一致：声明 {names.Count} 列，anchor 返回 {actual.Count} 列。");
        if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count)
            throw new InvalidOperationException("递归 CTE 输出列名不能重复。");
        return names.ToArray();
    }

    private static Type NormalizeType(Type type)
        => type == typeof(byte) || type == typeof(short) || type == typeof(int) || type == typeof(long)
            ? typeof(long)
            : type;

    private static long EstimateBytes(IReadOnlyList<object?> row)
    {
        long bytes = 24 + row.Count * 8L;
        foreach (object? value in row)
        {
            bytes += value switch
            {
                string text => Encoding.UTF8.GetByteCount(text),
                byte[] data => data.Length,
                null => 0,
                _ => 16,
            };
        }
        return bytes;
    }
}

/// <summary>隔离同一查询中递归 CTE 的当前层或完整结果。</summary>
internal sealed class RecursiveCteScope : IDisposable
{
    private static readonly AsyncLocal<RecursiveCteScope?> Current = new();
    private readonly RecursiveCteScope? _previous;
    private readonly string _name;
    private readonly SelectExecutionResult _result;

    private RecursiveCteScope(string name, SelectExecutionResult result)
    {
        _previous = Current.Value;
        _name = name;
        _result = result;
        Current.Value = this;
    }

    internal static RecursiveCteScope Enter(string name, SelectExecutionResult result) => new(name, result);

    internal static SelectExecutionResult? Find(string name)
    {
        for (RecursiveCteScope? scope = Current.Value; scope is not null; scope = scope._previous)
        {
            if (string.Equals(scope._name, name, StringComparison.OrdinalIgnoreCase))
                return scope._result;
        }
        return null;
    }

    public void Dispose() => Current.Value = _previous;
}
