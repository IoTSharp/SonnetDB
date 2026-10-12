using System.Reflection;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace SonnetDB.EntityFrameworkCore.Query.Internal;

/// <summary>
/// 将常用 .NET 字符串谓词翻译为 SonnetDB LIKE 表达式。
/// </summary>
public sealed class SonnetDbStringMethodTranslator : IMethodCallTranslator
{
    private static readonly MethodInfo _startsWith = typeof(string).GetRuntimeMethod(nameof(string.StartsWith), [typeof(string)])!;
    private static readonly MethodInfo _endsWith = typeof(string).GetRuntimeMethod(nameof(string.EndsWith), [typeof(string)])!;
    private static readonly MethodInfo _contains = typeof(string).GetRuntimeMethod(nameof(string.Contains), [typeof(string)])!;
    private static readonly MethodInfo _toLower = typeof(string).GetRuntimeMethod(nameof(string.ToLower), Type.EmptyTypes)!;
    private static readonly MethodInfo _toUpper = typeof(string).GetRuntimeMethod(nameof(string.ToUpper), Type.EmptyTypes)!;

    private readonly ISqlExpressionFactory _sqlExpressionFactory;

    /// <summary>
    /// 创建 SonnetDB 字符串方法翻译器。
    /// </summary>
    /// <param name="sqlExpressionFactory">SQL 表达式工厂。</param>
    public SonnetDbStringMethodTranslator(ISqlExpressionFactory sqlExpressionFactory)
    {
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
        {
            return null;
        }

        if (arguments.Count == 0)
        {
            if (method == _toLower)
            {
                return _sqlExpressionFactory.Function(
                    "LOWER",
                    [instance],
                    nullable: true,
                    argumentsPropagateNullability: [true],
                    method.ReturnType,
                    instance.TypeMapping);
            }

            if (method == _toUpper)
            {
                return _sqlExpressionFactory.Function(
                    "UPPER",
                    [instance],
                    nullable: true,
                    argumentsPropagateNullability: [true],
                    method.ReturnType,
                    instance.TypeMapping);
            }

            return null;
        }

        if (arguments.Count != 1)
        {
            return null;
        }

        var pattern = method switch
        {
            var current when current == _startsWith => BuildPattern(arguments[0], suffix: "%"),
            var current when current == _endsWith => BuildPattern(arguments[0], prefix: "%"),
            var current when current == _contains => BuildPattern(arguments[0], prefix: "%", suffix: "%"),
            _ => null
        };

        if (pattern is null)
        {
            return null;
        }

        // SonnetDB CONCAT 将 NULL 视为空字符串，不能让 NULL 参数变成匹配所有字符串的模式。
        return _sqlExpressionFactory.AndAlso(
            _sqlExpressionFactory.IsNotNull(arguments[0]),
            _sqlExpressionFactory.Like(instance, pattern, null));
    }

    private SqlExpression BuildPattern(
        SqlExpression pattern,
        string? prefix = null,
        string? suffix = null)
    {
        if (pattern is SqlConstantExpression { Value: string constant })
        {
            return _sqlExpressionFactory.Constant(
                string.Concat(prefix, EscapeLikePattern(constant), suffix));
        }

        // 必须先转义原始反斜杠，再转义通配符，避免二次转义新插入的反斜杠。
        var result = ReplacePattern(
            ReplacePattern(ReplacePattern(pattern, "\\", "\\\\"), "%", "\\%"), "_", "\\_");
        if (prefix is not null)
        {
            result = _sqlExpressionFactory.Add(_sqlExpressionFactory.Constant(prefix), result);
        }

        if (suffix is not null)
        {
            result = _sqlExpressionFactory.Add(result, _sqlExpressionFactory.Constant(suffix));
        }

        return result;
    }

    private SqlExpression ReplacePattern(SqlExpression pattern, string search, string replacement)
        => _sqlExpressionFactory.Function(
            "REPLACE",
            [pattern, _sqlExpressionFactory.Constant(search), _sqlExpressionFactory.Constant(replacement)],
            nullable: true,
            argumentsPropagateNullability: [true, false, false],
            typeof(string),
            pattern.TypeMapping);

    private static string EscapeLikePattern(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
    }
}
