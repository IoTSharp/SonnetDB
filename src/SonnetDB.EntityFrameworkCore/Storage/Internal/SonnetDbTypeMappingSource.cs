using System.Data;
using Microsoft.EntityFrameworkCore.Storage;

namespace SonnetDB.EntityFrameworkCore.Storage.Internal;

/// <summary>
/// SonnetDB SQL 类型与 CLR 类型的基础映射源。
/// </summary>
public sealed class SonnetDbTypeMappingSource : RelationalTypeMappingSource
{
    private static readonly LongTypeMapping Long = new("INT");
    private static readonly IntTypeMapping Int = new("INT");
    private static readonly ShortTypeMapping Short = new("INT");
    private static readonly ByteTypeMapping Byte = new("INT");
    private static readonly DoubleTypeMapping Double = new("FLOAT");
    private static readonly FloatTypeMapping Float = new("FLOAT");
    private static readonly BoolTypeMapping Bool = new("BOOL");
    private static readonly StringTypeMapping String = new("STRING", DbType.String);
    private static readonly ByteArrayTypeMapping Bytes = new("BLOB");
    private static readonly DateTimeTypeMapping DateTime = new("DATETIME");
    private static readonly DateTimeOffsetTypeMapping DateTimeOffset = new("DATETIME", DbType.DateTimeOffset);
    private static readonly GuidTypeMapping Guid = new("STRING", DbType.Guid);

    private static readonly Dictionary<string, RelationalTypeMapping> StoreTypeMappings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["INT"] = Long,
        ["INTEGER"] = Long,
        ["FLOAT"] = Double,
        ["DOUBLE"] = Double,
        ["BOOL"] = Bool,
        ["BOOLEAN"] = Bool,
        ["STRING"] = String,
        ["TEXT"] = String,
        ["BLOB"] = Bytes,
        ["JSON"] = String,
        ["DATETIME"] = DateTime
    };

    private static readonly Dictionary<Type, RelationalTypeMapping> ClrTypeMappings = new()
    {
        [typeof(long)] = Long,
        [typeof(int)] = Int,
        [typeof(short)] = Short,
        [typeof(byte)] = Byte,
        [typeof(double)] = Double,
        [typeof(float)] = Float,
        [typeof(bool)] = Bool,
        [typeof(string)] = String,
        [typeof(byte[])] = Bytes,
        [typeof(DateTime)] = DateTime,
        [typeof(DateTimeOffset)] = DateTimeOffset,
        [typeof(Guid)] = Guid
    };

    /// <summary>
    /// 创建 SonnetDB 类型映射源。
    /// </summary>
    /// <param name="dependencies">类型映射依赖。</param>
    /// <param name="relationalDependencies">关系型类型映射依赖。</param>
    public SonnetDbTypeMappingSource(
        TypeMappingSourceDependencies dependencies,
        RelationalTypeMappingSourceDependencies relationalDependencies)
        : base(dependencies, relationalDependencies)
    {
    }

    /// <inheritdoc />
    protected override RelationalTypeMapping? FindMapping(in RelationalTypeMappingInfo mappingInfo)
    {
        if (mappingInfo.ClrType is { } clrType
            && ClrTypeMappings.TryGetValue(Nullable.GetUnderlyingType(clrType) ?? clrType, out var clrTypeMapping))
        {
            return clrTypeMapping;
        }

        if (mappingInfo.StoreTypeName is { } storeType
            && StoreTypeMappings.TryGetValue(storeType, out var storeTypeMapping))
        {
            return storeTypeMapping;
        }

        return base.FindMapping(mappingInfo);
    }
}
