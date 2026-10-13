using System.Data.Common;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using Microsoft.EntityFrameworkCore.Storage;

namespace SonnetDB.EntityFrameworkCore.Storage.Internal;

/// <summary>
/// 为 ToJson 聚合提供整容器、固定标量查询及 OwnsOne 标量属性更新映射。
/// </summary>
public sealed class SonnetDbJsonTypeMapping : JsonTypeMapping
{
    internal const string UnsupportedOperationMessage =
        "SonnetDB ToJson supports whole-container operations, supported fixed scalar queries and scalar updates through OwnsOne containers. This JSON operation is not supported.";

    private static readonly MethodInfo GetStringMethod =
        typeof(DbDataReader).GetMethod(nameof(DbDataReader.GetString), [typeof(int)])!;

    private static readonly MethodInfo GetUtf8BytesMethod =
        typeof(Encoding).GetMethod(nameof(Encoding.GetBytes), [typeof(string)])!;

    private static readonly ConstructorInfo MemoryStreamConstructor =
        typeof(MemoryStream).GetConstructor([typeof(byte[])])!;

    /// <summary>
    /// 创建支持 JSON 整容器读写的映射。
    /// </summary>
    public SonnetDbJsonTypeMapping()
        : base("JSON", typeof(JsonTypePlaceholder), System.Data.DbType.String)
    {
    }

    private SonnetDbJsonTypeMapping(RelationalTypeMappingParameters parameters)
        : base(parameters)
    {
    }

    /// <inheritdoc />
    protected override RelationalTypeMapping Clone(RelationalTypeMappingParameters parameters)
        => new SonnetDbJsonTypeMapping(parameters);

    /// <summary>
    /// 获取用于读取完整 JSON 文本的数据读取方法。
    /// </summary>
    /// <returns>关系数据读取器的字符串读取方法。</returns>
    public override MethodInfo GetDataReaderMethod()
        => GetStringMethod;

    /// <summary>
    /// 将完整 JSON 文本转换为 EF JSON 物化器使用的 UTF-8 数据流。
    /// </summary>
    /// <param name="expression">读取 JSON 字符串的表达式。</param>
    /// <returns>创建 JSON 数据流的表达式。</returns>
    public override Expression CustomizeDataReaderExpression(Expression expression)
        => Expression.New(
            MemoryStreamConstructor,
            Expression.Call(Expression.Constant(Encoding.UTF8), GetUtf8BytesMethod, expression));

    /// <inheritdoc />
    protected override void ConfigureParameter(DbParameter parameter)
    {
        if (parameter.Value is not null and not DBNull and not string)
        {
            throw new NotSupportedException(UnsupportedOperationMessage);
        }

        base.ConfigureParameter(parameter);
    }

    /// <inheritdoc />
    protected override string GenerateNonNullSqlLiteral(object value)
        => value is string json
            ? "'" + json.Replace("'", "''", StringComparison.Ordinal) + "'"
            : throw new NotSupportedException(UnsupportedOperationMessage);
}
