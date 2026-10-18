using System.Data;
using SonnetDB.Protocol;
using SonnetDB.Sql;

namespace SonnetDB.Data.Internal;

internal sealed class ProcedureCallSnapshot
{
    private readonly FrozenParameter[] _parameters;
    private int _published;

    private ProcedureCallSnapshot(string name, FrozenParameter[] parameters)
    {
        Name = name;
        _parameters = parameters;
        Arguments = Array.AsReadOnly(parameters.Select(static parameter => new SqlProcedureFrameParameter(
                SqlExecuteFrameCodec.FromClr(parameter.Name, parameter.Direction is ParameterDirection.Output or ParameterDirection.ReturnValue ? null : parameter.Value),
                parameter.Direction switch
                {
                    ParameterDirection.Input => SqlProcedureFrameDirection.Input,
                    ParameterDirection.Output => SqlProcedureFrameDirection.Output,
                    ParameterDirection.InputOutput => SqlProcedureFrameDirection.InputOutput,
                    _ => SqlProcedureFrameDirection.ReturnValue,
                }, (int)parameter.DbType, parameter.Size, parameter.Precision, parameter.Scale)).ToArray());
    }

    public string Name { get; }
    public IReadOnlyList<SqlProcedureFrameParameter> Arguments { get; }

    public static ProcedureCallSnapshot ForLegacyCall() => new(string.Empty, []);

    public static ProcedureCallSnapshot Create(string name, SndbParameterCollection parameters)
    {
        var tokens = SqlLexer.Tokenize(name);
        if (tokens.Count != 2 || tokens[0].Kind != TokenKind.IdentifierLiteral)
            throw new ArgumentException("CommandType.StoredProcedure 的 CommandText 必须是一个过程名称。", nameof(name));
        string spelling = tokens[0].IsQuotedIdentifier
            ? "\"" + tokens[0].Text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : tokens[0].Text;
        if (!string.Equals(name.Trim(), spelling, StringComparison.Ordinal))
            throw new ArgumentException("过程名称不允许附带注释或 SQL 文本。", nameof(name));
        if (parameters.Count > SqlExecuteFrameCodec.MaxParameterCount)
            throw new ArgumentException("过程参数数量超过上限 256。", nameof(parameters));
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var frozen = new List<FrozenParameter>(parameters.Count);
        bool hasReturn = false;
        foreach (var parameter in parameters.Items)
        {
            bool returns = parameter.Direction == ParameterDirection.ReturnValue;
            string parameterName = SndbParameterCollection.NormalizeName(parameter.ParameterName);
            if (returns)
            {
                if (hasReturn) throw new ArgumentException("过程只能有一个 ReturnValue 参数。");
                hasReturn = true;
                if (parameterName.Length == 0) parameterName = "__sndb_return_value";
                if (!names.Add(parameterName)) throw new ArgumentException("返回值参数与实参名称冲突。");
            }
            else if (string.IsNullOrWhiteSpace(parameterName) || !names.Add(parameterName))
                throw new ArgumentException("过程命名参数缺少名称或重复。");
            ValidateType(parameter.DbType);
            if (parameter.Size < 0 || parameter.Precision != 0 || parameter.Scale != 0)
                throw new NotSupportedException("首版过程标量不支持负 Size 或非零 Precision/Scale。");
            object? value = parameter.Value is DBNull ? null : parameter.Value;
            if (parameter.Direction is ParameterDirection.Input or ParameterDirection.InputOutput)
            {
                if (value is not (null or string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal))
                    throw new NotSupportedException("过程参数当前只支持数值、布尔和字符串标量。");
                if (value is string text && parameter.Size > 0 && text.Length > parameter.Size)
                    throw new ArgumentException("过程字符串输入超过声明的 Size。");
            }
            frozen.Add(new FrozenParameter(parameter, parameterName, parameter.Direction,
                parameter.DbType, parameter.Size, parameter.Precision, parameter.Scale, value));
        }
        return new ProcedureCallSnapshot(tokens[0].Text, frozen.ToArray());
    }

    public void Publish(IReadOnlyDictionary<string, object?> outputs, long returnValue)
    {
        if (Volatile.Read(ref _published) != 0) return;
        var pending = new List<(SndbParameter Target, object Value)>();
        int outputCount = 0;
        foreach (var parameter in _parameters)
        {
            if (parameter.Direction == ParameterDirection.Input) continue;
            object? value;
            if (parameter.Direction == ParameterDirection.ReturnValue) value = returnValue;
            else
            {
                outputCount++;
                if (!outputs.TryGetValue(parameter.Name, out value))
                    throw new InvalidDataException($"过程响应缺少输出参数 '{parameter.Name}'。");
            }
            pending.Add((parameter.Target, ConvertOutput(value, parameter)));
        }
        if (outputs.Count != outputCount) throw new InvalidDataException("过程响应包含未绑定的输出参数。");
        if (Interlocked.Exchange(ref _published, 1) != 0) return;
        foreach (var value in pending) value.Target.Value = value.Value;
    }

    private static object ConvertOutput(object? value, FrozenParameter parameter)
    {
        var declaration = new SqlProcedureFrameParameter(SqlExecuteFrameCodec.FromClr(parameter.Name, null),
            parameter.Direction == ParameterDirection.ReturnValue ? SqlProcedureFrameDirection.ReturnValue : SqlProcedureFrameDirection.Output,
            (int)parameter.DbType, parameter.Size, parameter.Precision, parameter.Scale);
        return SqlProcedureFrameCodec.ConvertDeclaredValue(declaration, value) ?? DBNull.Value;
    }

    public void ValidateCompletion(IReadOnlyDictionary<string, object?> outputs, long returnValue)
    {
        int count = 0;
        foreach (var parameter in _parameters)
        {
            if (parameter.Direction == ParameterDirection.Input) continue;
            object? value;
            if (parameter.Direction == ParameterDirection.ReturnValue) value = returnValue;
            else
            {
                count++;
                if (!outputs.TryGetValue(parameter.Name, out value)) throw new InvalidDataException("过程响应缺少输出参数。");
            }
            _ = ConvertOutput(value, parameter);
        }
        if (count != outputs.Count) throw new InvalidDataException("过程响应输出数量不匹配。");
    }

    private static void ValidateType(DbType type)
    {
        if (type is not (DbType.Object or DbType.String or DbType.StringFixedLength or DbType.AnsiString
            or DbType.AnsiStringFixedLength or DbType.Boolean or DbType.Byte or DbType.SByte or DbType.Int16
            or DbType.UInt16 or DbType.Int32 or DbType.UInt32 or DbType.Int64 or DbType.UInt64 or DbType.Single or DbType.Double))
            throw new NotSupportedException($"过程声明尚不支持 DbType.{type}。");
    }

    private sealed record FrozenParameter(SndbParameter Target, string Name, ParameterDirection Direction,
        DbType DbType, int Size, byte Precision, byte Scale, object? Value);
}
