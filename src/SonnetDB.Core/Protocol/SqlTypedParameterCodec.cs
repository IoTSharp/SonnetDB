using SonnetDB.Sql;
using SonnetDB.Sql.Ast;

namespace SonnetDB.Protocol;

/// <summary>把既有 Execute 精确参数合同转换为 Core 参数，保留参数来源与类型化 NULL。</summary>
public static class SqlTypedParameterCodec
{
    /// <summary>按编码次序和名称登记参数，不把精确时间格式化成 SQL 字符串。</summary>
    /// <param name="parameters">已由有界协议解码的参数。</param>
    /// <returns>独立参数集合。</returns>
    public static SqlParameters ToSqlParameters(IReadOnlyList<SqlExecuteFrameParameter> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var result = new SqlParameters();
        foreach (var parameter in parameters)
        {
            SqlDataType? type = parameter.Kind switch
            {
                SqlFrameParameterKind.Timestamp => SqlDataType.DateTime,
                SqlFrameParameterKind.DateTime => SqlDataType.DateTime2,
                SqlFrameParameterKind.DateTimeOffset => SqlDataType.DateTimeOffset,
                SqlFrameParameterKind.Duration => SqlDataType.Duration,
                SqlFrameParameterKind.Time => SqlDataType.Time,
                _ => null,
            };
            if (type is { } declared)
            {
                result.AddPositional(parameter.Value, declared);
                if (parameter.Name.Length != 0) result.AddNamed(parameter.Name, parameter.Value, declared);
            }
            else
            {
                result.AddPositional(parameter.Value);
                if (parameter.Name.Length != 0) result.AddNamed(parameter.Name, parameter.Value);
            }
        }
        return result;
    }
}
