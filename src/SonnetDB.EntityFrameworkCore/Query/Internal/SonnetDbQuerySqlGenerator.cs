using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;
using System.Linq.Expressions;

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
