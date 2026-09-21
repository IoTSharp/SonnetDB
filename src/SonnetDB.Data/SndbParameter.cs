using System.Data;
using System.Data.Common;

namespace SonnetDB.Data;

/// <summary>
/// SonnetDB ADO.NET 参数。支持基础标量类型（<see cref="string"/> / <see cref="bool"/> /
/// 整数族 / <see cref="float"/> / <see cref="double"/> / <see cref="decimal"/> /
/// <see cref="DateTime"/> / <see cref="DateTimeOffset"/> / <see cref="TimeOnly"/> / <see cref="TimeSpan"/>）、<c>byte[]</c>、<c>GeoPoint</c> 和
/// <c>float[]</c> / <c>Memory&lt;float&gt;</c> / <c>ReadOnlyMemory&lt;float&gt;</c> 向量。
/// </summary>
public sealed class SndbParameter : DbParameter
{
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
    public override object? Value { get; set; }

    /// <inheritdoc />
    public override void ResetDbType() => DbType = DbType.Object;
}
