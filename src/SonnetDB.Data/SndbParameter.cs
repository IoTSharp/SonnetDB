using System.Data;
using System.Data.Common;
using System.Numerics;
using SonnetDB.Exceptions;

namespace SonnetDB.Data;

/// <summary>
/// SonnetDB ADO.NET 参数。支持基础标量类型（<see cref="string"/> / <see cref="bool"/> /
/// 整数族 / <see cref="float"/> / <see cref="double"/> / <see cref="decimal"/> /
/// <see cref="DateTime"/> / <see cref="DateTimeOffset"/> / <see cref="TimeOnly"/> / <see cref="TimeSpan"/>）、<c>byte[]</c>、<c>GeoPoint</c> 和
/// <c>float[]</c> / <c>Memory&lt;float&gt;</c> / <c>ReadOnlyMemory&lt;float&gt;</c> 向量。
/// </summary>
public sealed class SndbParameter : DbParameter
{
    private object? _value;
    /// <summary>构造一个空参数。</summary>
    public SndbParameter() { }

    /// <summary>构造命名参数并赋值。</summary>
    public SndbParameter(string parameterName, object? value)
    {
        ParameterName = parameterName;
        Value = value;
    }

    /// <inheritdoc />
    public override DbType DbType { get; set; } = DbType.Object;

    /// <inheritdoc />
    public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;

    /// <inheritdoc />
    public override bool IsNullable { get; set; } = true;

    /// <inheritdoc />
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string ParameterName { get; set; } = string.Empty;

    /// <inheritdoc />
    public override int Size { get; set; }

    /// <inheritdoc />
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string SourceColumn { get; set; } = string.Empty;

    /// <inheritdoc />
    public override bool SourceColumnNullMapping { get; set; }

    /// <inheritdoc />
    public override object? Value
    {
        get => _value;
        set
        {
            if (value is BigInteger)
            {
                throw new SndbParameterTypeException(
                    SndbParameterTypeException.BigIntegerUnsupportedCode,
                    "BigInteger 参数不受支持；请在应用层检查有符号 Int64 范围后转换为 long，或转换为 string 并存入 STRING 列。");
            }
            _value = value;
        }
    }

    /// <inheritdoc />
    public override void ResetDbType() => DbType = DbType.Object;
}
