using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.SqlExpressions;

namespace SonnetDB.EntityFrameworkCore.Query.Internal;

/// <summary>
/// 将 <see cref="Regex.IsMatch(string, string)"/> 翻译为 SonnetDB <c>regexp_like</c>。
/// </summary>
public sealed class SonnetDbRegexMethodTranslator : IMethodCallTranslator
{
    private static readonly MethodInfo IsMatchMethod = typeof(Regex).GetRuntimeMethod(
        nameof(Regex.IsMatch), [typeof(string), typeof(string)])!;

    private static readonly MethodInfo IsMatchWithOptionsMethod = typeof(Regex).GetRuntimeMethod(
        nameof(Regex.IsMatch), [typeof(string), typeof(string), typeof(RegexOptions)])!;

    private readonly ISqlExpressionFactory _sqlExpressionFactory;

    /// <summary>创建正则方法翻译器。</summary>
    /// <param name="sqlExpressionFactory">EF Core SQL 表达式工厂。</param>
    public SonnetDbRegexMethodTranslator(ISqlExpressionFactory sqlExpressionFactory)
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
        if (instance is not null || arguments.Count is < 2 or > 3)
            return null;
        if (method != IsMatchMethod && method != IsMatchWithOptionsMethod)
            return null;

        if (method == IsMatchMethod)
        {
            return _sqlExpressionFactory.Function(
                "REGEXP_LIKE",
                [arguments[0], arguments[1]],
                nullable: true,
                argumentsPropagateNullability: [true, true],
                typeof(bool));
        }

        if (arguments[2] is not SqlConstantExpression { Value: RegexOptions options })
            return null;

        string? flags = TranslateOptions(options);
        if (flags is null)
            return null;

        return _sqlExpressionFactory.Function(
            "REGEXP_LIKE",
            [arguments[0], arguments[1], _sqlExpressionFactory.Constant(flags)],
            nullable: true,
            argumentsPropagateNullability: [true, true, false],
            typeof(bool));
    }

    private static string? TranslateOptions(RegexOptions options)
    {
        const RegexOptions supported = RegexOptions.IgnoreCase
            | RegexOptions.Multiline
            | RegexOptions.Singleline
            | RegexOptions.IgnorePatternWhitespace
            | RegexOptions.CultureInvariant;
        if ((options & ~supported) != 0)
            return null;

        var flags = new StringBuilder(4);
        if (options.HasFlag(RegexOptions.IgnoreCase))
            flags.Append('i');
        if (options.HasFlag(RegexOptions.Multiline))
            flags.Append('m');
        if (options.HasFlag(RegexOptions.Singleline))
            flags.Append('s');
        if (options.HasFlag(RegexOptions.IgnorePatternWhitespace))
            flags.Append('x');
        return flags.ToString();
    }
}
