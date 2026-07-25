using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace SonnetDB.EntityFrameworkCore.Query.Internal;

/// <summary>
/// SonnetDB 基础查询 SQL 生成器。
/// </summary>
public sealed class SonnetDbQuerySqlGenerator : QuerySqlGenerator
{
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
