using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace SonnetDB.EntityFrameworkCore.Query.Internal;

/// <summary>
/// SonnetDB 基础查询 SQL 生成器。
/// </summary>
public sealed class SonnetDbQuerySqlGenerator : QuerySqlGenerator
{
    private string? _unqualifiedDeleteAlias;

    /// <summary>
    /// 创建 SonnetDB 查询 SQL 生成器。
    /// </summary>
    /// <param name="dependencies">查询 SQL 生成器依赖。</param>
    public SonnetDbQuerySqlGenerator(QuerySqlGeneratorDependencies dependencies)
        : base(dependencies)
    {
    }

    /// <inheritdoc />
    protected override Expression VisitSqlConstant(SqlConstantExpression sqlConstantExpression)
    {
        if (sqlConstantExpression.Value is bool value)
        {
            Sql.Append(value ? "TRUE" : "FALSE");
            return sqlConstantExpression;
        }

        return base.VisitSqlConstant(sqlConstantExpression);
    }

    /// <summary>
    /// 将 C# 字符串加法生成为 concat，避免与 SonnetDB 的数值加法语义冲突。
    /// </summary>
    /// <param name="sqlBinaryExpression">待生成的 SQL 二元表达式。</param>
    /// <returns>已访问的 SQL 表达式。</returns>
    protected override Expression VisitSqlBinary(SqlBinaryExpression sqlBinaryExpression)
    {
        if (sqlBinaryExpression.OperatorType == ExpressionType.Add
            && sqlBinaryExpression.Type == typeof(string))
        {
            Sql.Append("concat(");
            Visit(sqlBinaryExpression.Left);
            Sql.Append(", ");
            Visit(sqlBinaryExpression.Right);
            Sql.Append(")");
            return sqlBinaryExpression;
        }

        return base.VisitSqlBinary(sqlBinaryExpression);
    }

    /// <summary>
    /// 生成 SonnetDB 支持的无目标别名 DELETE；谓词中的目标列同时去除 EF 内部表别名限定。
    /// </summary>
    /// <param name="deleteExpression">待生成的删除表达式。</param>
    /// <returns>已访问的删除表达式。</returns>
    protected override Expression VisitDelete(DeleteExpression deleteExpression)
    {
        var selectExpression = deleteExpression.SelectExpression;
        if (selectExpression is
            {
                Tables: [var table],
                GroupBy: [],
                Having: null,
                Projection: [],
                Orderings: [],
                Offset: null,
                Limit: null,
            }
            && table.Equals(deleteExpression.Table))
        {
            Sql.Append("DELETE FROM ")
                .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(
                    deleteExpression.Table.Name,
                    deleteExpression.Table.Schema));

            if (selectExpression.Predicate is not null)
            {
                Sql.AppendLine().Append("WHERE ");
                var previousAlias = _unqualifiedDeleteAlias;
                _unqualifiedDeleteAlias = deleteExpression.Table.Alias;
                try
                {
                    Visit(selectExpression.Predicate);
                }
                finally
                {
                    _unqualifiedDeleteAlias = previousAlias;
                }
            }

            return deleteExpression;
        }

        return base.VisitDelete(deleteExpression);
    }

    /// <summary>
    /// DELETE 谓词引用目标表时只输出列名，因为 SonnetDB DELETE 语法不声明目标别名。
    /// </summary>
    /// <param name="columnExpression">待生成的列表达式。</param>
    /// <returns>已访问的列表达式。</returns>
    protected override Expression VisitColumn(ColumnExpression columnExpression)
    {
        if (string.Equals(columnExpression.TableAlias, _unqualifiedDeleteAlias, StringComparison.Ordinal))
        {
            Sql.Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(columnExpression.Name));
            return columnExpression;
        }

        return base.VisitColumn(columnExpression);
    }

    /// <inheritdoc />
    protected override void GenerateLimitOffset(SelectExpression selectExpression)
    {
        if (selectExpression.Limit is not null)
        {
            Sql.AppendLine()
                .Append("LIMIT ");
            Visit(selectExpression.Limit);

            if (selectExpression.Offset is not null)
            {
                Sql.Append(" OFFSET ");
                Visit(selectExpression.Offset);
            }
        }
        else if (selectExpression.Offset is not null)
        {
            Sql.AppendLine()
                .Append("OFFSET ");
            Visit(selectExpression.Offset);
        }
    }
}
