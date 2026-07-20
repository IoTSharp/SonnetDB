using System.Reflection;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace SonnetDB.EntityFrameworkCore.Query.Internal;

/// <summary>
/// 将 <see cref="DateTime"/> 和 <see cref="DateTimeOffset"/> 成员访问翻译为 SonnetDB 日期函数。
/// </summary>
public sealed class SonnetDbDateTimeMemberTranslator : IMemberTranslator
{
    private static readonly HashSet<string> DateParts =
    [
        nameof(DateTime.Year),
        nameof(DateTime.Month),
        nameof(DateTime.Day),
        nameof(DateTime.DayOfYear),
        nameof(DateTime.DayOfWeek),
        nameof(DateTime.Hour),
        nameof(DateTime.Minute),
        nameof(DateTime.Second),
        nameof(DateTime.Millisecond),
        nameof(DateTime.Microsecond),
        nameof(DateTime.Nanosecond),
    ];

    private readonly ISqlExpressionFactory _sqlExpressionFactory;

    /// <summary>
    /// 创建 SonnetDB 日期时间成员翻译器。
    /// </summary>
    /// <param name="sqlExpressionFactory">EF Core SQL 表达式工厂。</param>
    public SonnetDbDateTimeMemberTranslator(ISqlExpressionFactory sqlExpressionFactory)
    {
        ArgumentNullException.ThrowIfNull(sqlExpressionFactory);
        _sqlExpressionFactory = sqlExpressionFactory;
    }

    /// <inheritdoc />
    public SqlExpression? Translate(
        SqlExpression? instance,
        MemberInfo member,
        Type returnType,
        IDiagnosticsLogger<Microsoft.EntityFrameworkCore.DbLoggerCategory.Query> logger)
    {
        var declaringType = member.DeclaringType;
        if (declaringType != typeof(DateTime) && declaringType != typeof(DateTimeOffset))
            return null;

        if (instance is null)
            return TranslateStaticMember(declaringType, member.Name, returnType);

        if (member.Name == nameof(DateTime.Date))
            return Function("DATE_ONLY", [instance], [true], returnType);

        if (DateParts.Contains(member.Name))
        {
            return Function(
                "DATE_PART",
                [_sqlExpressionFactory.Constant(ToDatePart(member.Name)), instance],
                [false, true],
                returnType);
        }

        if (declaringType == typeof(DateTimeOffset))
        {
            string? functionName = member.Name switch
            {
                nameof(DateTimeOffset.DateTime) => "TO_DATETIME",
                nameof(DateTimeOffset.UtcDateTime) => "TO_UTC_DATETIME",
                nameof(DateTimeOffset.LocalDateTime) => "TO_LOCAL_DATETIME",
                _ => null,
            };

            if (functionName is not null)
                return Function(functionName, [instance], [true], returnType);
        }

        return null;
    }

    private SqlExpression? TranslateStaticMember(Type declaringType, string memberName, Type returnType)
    {
        if (memberName == nameof(DateTime.Now))
        {
            string functionName = declaringType == typeof(DateTimeOffset)
                ? "CURRENT_DATETIME_OFFSET"
                : "CURRENT_DATETIME";
            return Function(functionName, [], [], returnType, nullable: false);
        }

        if (memberName == nameof(DateTime.UtcNow))
        {
            string functionName = declaringType == typeof(DateTimeOffset)
                ? "CURRENT_UTC_DATETIME_OFFSET"
                : "CURRENT_UTC_DATETIME";
            return Function(functionName, [], [], returnType, nullable: false);
        }

        if (declaringType == typeof(DateTime) && memberName == nameof(DateTime.Today))
        {
            var now = Function("CURRENT_DATETIME", [], [], typeof(DateTime), nullable: false);
            return Function("DATE_ONLY", [now], [false], returnType, nullable: false);
        }

        return null;
    }

    private SqlExpression Function(
        string name,
        IReadOnlyList<SqlExpression> arguments,
        IReadOnlyList<bool> argumentsPropagateNullability,
        Type returnType,
        bool nullable = true)
        => _sqlExpressionFactory.Function(
            name,
            arguments,
            nullable,
            argumentsPropagateNullability,
            returnType);

    private static string ToDatePart(string memberName)
        => memberName switch
        {
            nameof(DateTime.DayOfYear) => "day_of_year",
            nameof(DateTime.DayOfWeek) => "day_of_week",
            _ => memberName.ToLowerInvariant(),
        };
}
