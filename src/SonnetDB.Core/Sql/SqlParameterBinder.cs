using System.Globalization;
using SonnetDB.Model;
using SonnetDB.Sql.Ast;

namespace SonnetDB.Sql;

/// <summary>
/// 把带参数占位符（<see cref="ParameterExpression"/>）的 AST 用实际参数值重写为可执行 AST（#213）。
/// <para>
/// 解析产出的 AST 与参数值无关（故可被 <see cref="SqlParser"/> 的解析缓存跨不同参数值复用）；
/// 执行前对该不可变 AST 做一次纯函数式重写：<see cref="ParameterExpression"/> → 对应字面量节点，
/// 其余节点原样透传（引用不变则不复制）。值绑定而非字符串拼接，从根上杜绝 SQL 注入。
/// </para>
/// <para>
/// CLR 值 → 字面量的类型映射与旧 ADO <c>ParameterBinder.FormatLiteral</c> 语义一致：
/// <c>DateTime</c>/<c>DateTimeOffset</c> → Unix 毫秒整数；<c>byte[]</c> → Base64 字符串字面量（BLOB 列自行解码）；
/// <c>GeoPoint</c> → <see cref="GeoPointLiteralExpression"/>；数值/布尔/字符串/null 各归对应字面量。
/// </para>
/// </summary>
public static class SqlParameterBinder
{
    /// <summary>
    /// 用 <paramref name="parameters"/> 绑定 <paramref name="statement"/> 中的全部参数占位符。
    /// 无占位符或 <paramref name="parameters"/> 为空时原样返回。
    /// </summary>
    /// <param name="statement">可能含 <see cref="ParameterExpression"/> 的语句 AST。</param>
    /// <param name="parameters">参数值集合。</param>
    /// <returns>已绑定的语句 AST（若无变化则为原实例）。</returns>
    /// <exception cref="InvalidOperationException">占位符缺少对应参数值时抛出。</exception>
    public static SqlStatement Bind(SqlStatement statement, SqlParameters? parameters)
    {
        ArgumentNullException.ThrowIfNull(statement);
        if (parameters is null)
            return statement;

        return statement switch
        {
            SelectStatement select => BindSelect(select, parameters),
            InsertStatement insert => BindInsert(insert, parameters),
            UpdateStatement update => BindUpdate(update, parameters),
            DeleteStatement delete => delete with { Where = BindExpr(delete.Where, parameters) },
            _ => statement, // DDL / 控制面语句不含参数占位符
        };
    }

    private static SelectStatement BindSelect(SelectStatement select, SqlParameters p)
    {
        var projections = BindProjections(select.Projections, p);
        var where = select.Where is null ? null : BindExpr(select.Where, p);
        var groupBy = BindExprList(select.GroupBy, p);
        var having = select.Having is null ? null : BindExpr(select.Having, p);
        var orderByItems = BindOrderBy(select.OrderByList, p);
        var joins = BindJoins(select.JoinClauses, p);
        var fromSubquery = select.FromSubquery is null ? null : BindSelect(select.FromSubquery, p);

        return select with
        {
            Projections = projections,
            Where = where,
            GroupBy = groupBy,
            Having = having,
            OrderBy = null,
            OrderByItems = orderByItems,
            Join = null,
            Joins = joins,
            FromSubquery = fromSubquery,
        };
    }

    private static InsertStatement BindInsert(InsertStatement insert, SqlParameters p)
    {
        var rows = new List<IReadOnlyList<SqlExpression>>(insert.Rows.Count);
        bool changed = false;
        foreach (var row in insert.Rows)
        {
            var boundRow = BindExprList(row, p);
            if (!ReferenceEquals(boundRow, row))
                changed = true;
            rows.Add(boundRow);
        }
        return changed ? insert with { Rows = rows } : insert;
    }

    private static UpdateStatement BindUpdate(UpdateStatement update, SqlParameters p)
    {
        var assignments = new List<UpdateAssignment>(update.Assignments.Count);
        bool changed = false;
        foreach (var a in update.Assignments)
        {
            var boundValue = BindExpr(a.Value, p);
            if (!ReferenceEquals(boundValue, a.Value))
            {
                changed = true;
                assignments.Add(a with { Value = boundValue });
            }
            else
            {
                assignments.Add(a);
            }
        }

        var where = BindExpr(update.Where, p);
        if (!ReferenceEquals(where, update.Where))
            changed = true;

        return changed ? update with { Assignments = assignments, Where = where } : update;
    }

    private static IReadOnlyList<SelectItem> BindProjections(IReadOnlyList<SelectItem> projections, SqlParameters p)
    {
        SelectItem[]? copy = null;
        for (int i = 0; i < projections.Count; i++)
        {
            var bound = BindExpr(projections[i].Expression, p);
            if (!ReferenceEquals(bound, projections[i].Expression))
            {
                copy ??= projections.ToArray();
                copy[i] = projections[i] with { Expression = bound };
            }
        }
        return copy ?? projections;
    }

    private static IReadOnlyList<OrderBySpec> BindOrderBy(IReadOnlyList<OrderBySpec> items, SqlParameters p)
    {
        OrderBySpec[]? copy = null;
        for (int i = 0; i < items.Count; i++)
        {
            var bound = BindExpr(items[i].Expression, p);
            if (!ReferenceEquals(bound, items[i].Expression))
            {
                copy ??= items.ToArray();
                copy[i] = items[i] with { Expression = bound };
            }
        }
        return copy ?? items;
    }

    private static IReadOnlyList<JoinClause> BindJoins(IReadOnlyList<JoinClause> joins, SqlParameters p)
    {
        JoinClause[]? copy = null;
        for (int i = 0; i < joins.Count; i++)
        {
            var on = BindExpr(joins[i].On, p);
            if (!ReferenceEquals(on, joins[i].On))
            {
                copy ??= joins.ToArray();
                copy[i] = joins[i] with { On = on };
            }
        }
        return copy ?? joins;
    }

    private static IReadOnlyList<SqlExpression> BindExprList(IReadOnlyList<SqlExpression> list, SqlParameters p)
    {
        SqlExpression[]? copy = null;
        for (int i = 0; i < list.Count; i++)
        {
            var bound = BindExpr(list[i], p);
            if (!ReferenceEquals(bound, list[i]))
            {
                copy ??= list.ToArray();
                copy[i] = bound;
            }
        }
        return copy ?? list;
    }

    /// <summary>递归重写表达式树：<see cref="ParameterExpression"/> 替换为字面量，其余节点按引用透传。</summary>
    private static SqlExpression BindExpr(SqlExpression expr, SqlParameters p)
    {
        switch (expr)
        {
            case ParameterExpression param:
                if (!p.TryResolve(param.Ordinal, param.Name, out var value))
                {
                    string label = param.Name is null ? $"位置参数 #{param.Ordinal + 1}" : $"@{param.Name}";
                    throw new InvalidOperationException($"未提供参数 {label} 的值。");
                }
                return ToLiteral(value);

            case BinaryExpression b:
                var left = BindExpr(b.Left, p);
                var right = BindExpr(b.Right, p);
                return ReferenceEquals(left, b.Left) && ReferenceEquals(right, b.Right)
                    ? b : b with { Left = left, Right = right };

            case UnaryExpression u:
                var operand = BindExpr(u.Operand, p);
                return ReferenceEquals(operand, u.Operand) ? u : u with { Operand = operand };

            case IsNullExpression n:
                var nOperand = BindExpr(n.Operand, p);
                return ReferenceEquals(nOperand, n.Operand) ? n : n with { Operand = nOperand };

            case InExpression inExpr:
                var inValue = BindExpr(inExpr.Value, p);
                var inValues = BindExprList(inExpr.Values, p);
                var inSubquery = inExpr.Subquery is null ? null : BindSelect(inExpr.Subquery, p);
                return ReferenceEquals(inValue, inExpr.Value)
                    && ReferenceEquals(inValues, inExpr.Values)
                    && ReferenceEquals(inSubquery, inExpr.Subquery)
                    ? inExpr
                    : inExpr with { Value = inValue, Values = inValues, Subquery = inSubquery };

            case FunctionCallExpression f:
                var args = BindExprList(f.Arguments, p);
                return ReferenceEquals(args, f.Arguments) ? f : f with { Arguments = args };

            case NamedArgumentExpression na:
                var naValue = BindExpr(na.Value, p);
                return ReferenceEquals(naValue, na.Value) ? na : na with { Value = naValue };

            case CaseExpression c:
                return BindCase(c, p);

            case SubqueryExpression sub:
                var subSelect = BindSelect(sub.Select, p);
                return ReferenceEquals(subSelect, sub.Select) ? sub : sub with { Select = subSelect };

            case ExistsExpression ex:
                var exSelect = BindSelect(ex.Select, p);
                return ReferenceEquals(exSelect, ex.Select) ? ex : ex with { Select = exSelect };

            default:
                // 字面量 / 标识符 / duration / vector / geo / star：无参数占位符，原样透传。
                return expr;
        }
    }

    private static SqlExpression BindCase(CaseExpression c, SqlParameters p)
    {
        CaseWhenClause[]? whens = null;
        for (int i = 0; i < c.WhenClauses.Count; i++)
        {
            var cond = BindExpr(c.WhenClauses[i].Condition, p);
            var result = BindExpr(c.WhenClauses[i].Result, p);
            if (!ReferenceEquals(cond, c.WhenClauses[i].Condition) || !ReferenceEquals(result, c.WhenClauses[i].Result))
            {
                whens ??= c.WhenClauses.ToArray();
                whens[i] = c.WhenClauses[i] with { Condition = cond, Result = result };
            }
        }

        var elseExpr = c.Else is null ? null : BindExpr(c.Else, p);
        bool elseChanged = !ReferenceEquals(elseExpr, c.Else);

        if (whens is null && !elseChanged)
            return c;

        return c with { WhenClauses = whens ?? c.WhenClauses, Else = elseExpr };
    }

    /// <summary>
    /// 把 CLR 参数值映射为对应的字面量表达式节点；类型决策与旧 <c>ParameterBinder.FormatLiteral</c> 一致。
    /// </summary>
    internal static SqlExpression ToLiteral(object? value)
    {
        switch (value)
        {
            case null:
            case DBNull:
                return LiteralExpression.Null();
            case bool b:
                return LiteralExpression.Bool(b);
            case string s:
                return LiteralExpression.String(s);
            case byte by:
                return LiteralExpression.Integer(by);
            case sbyte sb:
                return LiteralExpression.Integer(sb);
            case short sh:
                return LiteralExpression.Integer(sh);
            case ushort us:
                return LiteralExpression.Integer(us);
            case int i:
                return LiteralExpression.Integer(i);
            case uint ui:
                return LiteralExpression.Integer(ui);
            case long l:
                return LiteralExpression.Integer(l);
            case ulong ul:
                return LiteralExpression.Integer(checked((long)ul));
            case float f:
                return LiteralExpression.Float(f);
            case double d:
                return LiteralExpression.Float(d);
            case decimal m:
                return LiteralExpression.Float((double)m);
            case DateTime dt:
                // Unspecified 视为 UTC，与关系表 DateTime 列绑定语义一致。
                var utc = dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt.ToUniversalTime();
                return LiteralExpression.Integer(new DateTimeOffset(utc).ToUnixTimeMilliseconds());
            case DateTimeOffset dto:
                return LiteralExpression.Integer(dto.ToUnixTimeMilliseconds());
            case byte[] bytes:
                // BLOB：以 Base64 字符串字面量承载，BLOB 列执行时自行 Convert.FromBase64String 解码。
                return LiteralExpression.String(Convert.ToBase64String(bytes));
            case Guid g:
                return LiteralExpression.String(g.ToString("D", CultureInfo.InvariantCulture));
            case GeoPoint geo:
                return new GeoPointLiteralExpression(geo.Lat, geo.Lon);
            default:
                throw new NotSupportedException(
                    $"SQL 参数不支持 CLR 类型 {value.GetType().FullName}。");
        }
    }
}
