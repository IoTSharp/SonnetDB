using System.Data.Common;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Storage;

namespace SonnetDB.EntityFrameworkCore.Storage.Internal;

/// <summary>
/// 为 ToJson 聚合提供模型与迁移的 JSON 容器映射；聚合读写尚不支持。
/// </summary>
public sealed class SonnetDbJsonTypeMapping : JsonTypeMapping
{
    internal const string UnsupportedOperationMessage =
        "SonnetDB ToJson support is limited to model and migration generation. JSON aggregate queries and writes are not supported.";

    /// <summary>
    /// 创建仅支持模型与迁移生成的 JSON 容器映射。
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
    /// 拒绝获取 JSON 聚合的数据读取方法；当前切片仅支持模型与迁移生成。
    /// </summary>
    /// <returns>此方法不返回数据读取方法。</returns>
    /// <exception cref="NotSupportedException">尚未支持 JSON 聚合读取，调用时始终抛出异常。</exception>
    public override MethodInfo GetDataReaderMethod()
        => throw new NotSupportedException(UnsupportedOperationMessage);

    /// <inheritdoc />
    protected override void ConfigureParameter(DbParameter parameter)
        => throw new NotSupportedException(UnsupportedOperationMessage);

    /// <inheritdoc />
    protected override string GenerateNonNullSqlLiteral(object value)
        => throw new NotSupportedException(UnsupportedOperationMessage);
}
