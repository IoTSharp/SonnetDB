using System.Reflection;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace SonnetDB.EntityFrameworkCore.Query.Internal;

/// <summary>
/// 将 <see cref="DateTime"/> 和 <see cref="DateTimeOffset"/> 日期运算翻译为 SonnetDB 日期函数。
/// </summary>
public sealed class SonnetDbDateTimeMethodTranslator : IMethodCallTranslator
{
    private static readonly IReadOnlyDictionary<MethodInfo, string> DateAddMethods = CreateDateAddMethods();
    private static readonly MethodInfo DateTimeOffsetToUnixTimeMilliseconds = typeof(DateTimeOffset)
        .GetRuntimeMethod(nameof(DateTimeOffset.ToUnixTimeMilliseconds), Type.EmptyTypes)!;
    private static readonly MethodInfo DateTimeOffsetToUnixTimeSeconds = typeof(DateTimeOffset)
        .GetRuntimeMethod(nameof(DateTimeOffset.ToUnixTimeSeconds), Type.EmptyTypes)!;

    private readonly ISqlExpressionFactory _sqlExpressionFactory;

    /// <summary>
    /// 创建 SonnetDB 日期时间方法翻译器。
    /// </summary>
    /// <param name="sqlExpressionFactory">EF Core SQL 表达式工厂。</param>
    public SonnetDbDateTimeMethodTranslator(ISqlExpressionFactory sqlExpressionFactory)
    {
        ArgumentNullException.ThrowIfNull(sqlExpressionFactory);
        _sqlExpressionFactory = sqlExpressionFactory;
    }

    /// <inheritdoc />
    public SqlExpression? Translate(
        SqlExpression? instance,
        MethodInfo method,
        IReadOnlyList<SqlExpression> arguments,
        IDiagnosticsLogger<Microsoft.EntityFrameworkCore.DbLoggerCategory.Query> logger)
    {
        if (instance is null)
            return null;

        if (DateAddMethods.TryGetValue(method, out var datePart) && arguments.Count == 1)
        {
            string functionName = method.DeclaringType == typeof(DateTimeOffset)
                ? "DATE_ADD_DATETIME_OFFSET"
                : "DATE_ADD_DATETIME";
            return _sqlExpressionFactory.Function(
                functionName,
                [instance, arguments[0], _sqlExpressionFactory.Constant(datePart)],
                nullable: true,
                argumentsPropagateNullability: [true, true, false],
                method.ReturnType);
        }

        if (arguments.Count == 0 && method == DateTimeOffsetToUnixTimeMilliseconds)
            return UnixTimeFunction("TO_UNIX_MILLISECONDS", instance, method.ReturnType);

        if (arguments.Count == 0 && method == DateTimeOffsetToUnixTimeSeconds)
            return UnixTimeFunction("TO_UNIX_SECONDS", instance, method.ReturnType);

        return null;
    }

    private SqlExpression UnixTimeFunction(string name, SqlExpression instance, Type returnType)
        => _sqlExpressionFactory.Function(
            name,
            [instance],
            nullable: true,
            argumentsPropagateNullability: [true],
            returnType);

    private static IReadOnlyDictionary<MethodInfo, string> CreateDateAddMethods()
    {
        var methods = new Dictionary<MethodInfo, string>();
        AddDateAddMethods(methods, typeof(DateTime));
        AddDateAddMethods(methods, typeof(DateTimeOffset));
        return methods;
    }

    private static void AddDateAddMethods(IDictionary<MethodInfo, string> methods, Type declaringType)
    {
        Add(methods, declaringType, nameof(DateTime.AddYears), typeof(int), "year");
        Add(methods, declaringType, nameof(DateTime.AddMonths), typeof(int), "month");
        Add(methods, declaringType, nameof(DateTime.AddDays), typeof(double), "day");
        Add(methods, declaringType, nameof(DateTime.AddHours), typeof(double), "hour");
        Add(methods, declaringType, nameof(DateTime.AddMinutes), typeof(double), "minute");
        Add(methods, declaringType, nameof(DateTime.AddSeconds), typeof(double), "second");
        Add(methods, declaringType, nameof(DateTime.AddMilliseconds), typeof(double), "millisecond");
        Add(methods, declaringType, nameof(DateTime.AddMicroseconds), typeof(double), "microsecond");
        Add(methods, declaringType, nameof(DateTime.AddTicks), typeof(long), "tick");
    }

    private static void Add(
        IDictionary<MethodInfo, string> methods,
        Type declaringType,
        string methodName,
        Type argumentType,
        string datePart)
    {
        if (declaringType.GetRuntimeMethod(methodName, [argumentType]) is { } method)
            methods.Add(method, datePart);
    }
}
