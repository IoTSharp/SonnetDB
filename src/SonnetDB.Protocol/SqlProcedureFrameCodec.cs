using System.Buffers;
using System.Data;
using System.Globalization;
using SonnetDB.IO;

namespace SonnetDB.Protocol;

/// <summary>过程参数的协议方向，独立于 ADO.NET 与 Core。</summary>
public enum SqlProcedureFrameDirection : byte
{
    /// <summary>IN。</summary>
    Input,
    /// <summary>OUT。</summary>
    Output,
    /// <summary>INOUT。</summary>
    InputOutput,
    /// <summary>RETURN 状态的接收声明，不是过程声明的实参。</summary>
    ReturnValue,
}

/// <summary>一个过程参数的类型、输入值与执行前冻结的 ADO 声明。</summary>
/// <param name="Value">复用精确的 SQL Frame 参数值。</param>
/// <param name="Direction">参数方向。</param>
/// <param name="DbType">System.Data.DbType 的整数值。</param>
/// <param name="Size">字符串输出容量；0 表示未指定。</param>
/// <param name="Precision">声明精度。</param>
/// <param name="Scale">声明小数位。</param>
public sealed record SqlProcedureFrameParameter(SqlExecuteFrameParameter Value,
    SqlProcedureFrameDirection Direction, int DbType, int Size, byte Precision, byte Scale);

/// <summary>解码后的过程请求；SessionId 只表示已有调用方事务，不是逻辑连接会话。</summary>
/// <param name="Db">数据库名。</param>
/// <param name="Name">过程目录名称。</param>
/// <param name="SessionId">已有事务标识；空串表示自动提交。</param>
/// <param name="Parameters">冻结的命名参数。</param>
public sealed record SqlProcedureFrameRequest(string Db, string Name, string SessionId,
    IReadOnlyList<SqlProcedureFrameParameter> Parameters);

/// <summary>已完成过程的结果帧、累计影响行数、返回值与输出快照。</summary>
/// <param name="ResultFrames">每个结果的原生 typed meta/rows/end 帧。</param>
/// <param name="RecordsAffected">累计影响行数。</param>
/// <param name="ReturnValue">过程返回值。</param>
/// <param name="Outputs">命名输出值。</param>
public sealed record SqlProcedureFrameResponse(IReadOnlyList<byte[]> ResultFrames,
    int RecordsAffected, long ReturnValue, IReadOnlyList<SqlExecuteFrameParameter> Outputs);

/// <summary>Procedure=3 的有界二进制请求及成功完成信封；不包含反射 JSON。</summary>
public static class SqlProcedureFrameCodec
{
    /// <summary>过程 payload 版本。</summary>
    public const byte Version = 1;
    /// <summary>请求字节上限。</summary>
    public const int MaxRequestBytes = 4 * 1024 * 1024;
    /// <summary>结果与输出合计字节上限。</summary>
    public const int MaxResponseBytes = 16 * 1024 * 1024;
    /// <summary>结果集数量上限，与默认例程语句预算一致。</summary>
    public const int MaxResults = 64;
    /// <summary>REST Base64 JSON 的独立编码体积上限，包含字段名与空白预算。</summary>
    public const int MaxJsonRequestBytes = ((MaxRequestBytes + 2) / 3 * 4) + 4096;
    /// <summary>REST JSON 响应编码体积上限。</summary>
    public const int MaxJsonResponseBytes = ((MaxResponseBytes + 2) / 3 * 4) + 4096;

    /// <summary>编码独立过程请求 payload。</summary>
    public static byte[] EncodeRequestPayload(string db, string name, string sessionId,
        IReadOnlyList<SqlProcedureFrameParameter> parameters)
    {
        if (parameters.Count > SqlExecuteFrameCodec.MaxParameterCount)
            throw new ArgumentException("过程参数超过 256 个。");
        if (sessionId.Length > 128) throw new ArgumentException("事务标识超过上限。");
        byte[] values = EncodeValues(db, name, parameters.Select(static parameter => parameter.Value).ToArray());
        int length = checked(1 + 4 + values.Length + SpanWriter.MeasureVarString(sessionId)
            + 4 + parameters.Count * 11);
        if (length > MaxRequestBytes) throw new ArgumentException("过程请求超过字节预算。");
        byte[] bytes = new byte[length];
        var writer = new SpanWriter(bytes);
        writer.WriteByte(Version);
        WriteBlob(ref writer, values);
        writer.WriteVarString(sessionId);
        writer.WriteInt32(parameters.Count);
        foreach (var parameter in parameters)
        {
            Validate(parameter);
            writer.WriteByte((byte)parameter.Direction);
            writer.WriteInt32(parameter.DbType);
            writer.WriteInt32(parameter.Size);
            writer.WriteByte(parameter.Precision);
            writer.WriteByte(parameter.Scale);
        }
        return bytes;
    }

    /// <summary>解码并严格验证过程请求 payload。</summary>
    public static SqlProcedureFrameRequest DecodeRequest(ReadOnlySpan<byte> payload)
    {
        try
        {
            if (payload.Length > MaxRequestBytes) throw new FrameFormatException("过程请求超过字节预算。");
            var reader = new SpanReader(payload);
            if (reader.ReadByte() != Version) throw new FrameFormatException("不支持的过程协议版本。");
            var values = SqlExecuteFrameCodec.DecodeRequest(ReadBlob(ref reader, MaxRequestBytes));
            string session = reader.ReadVarString();
            if (session.Length > 128) throw new FrameFormatException("事务标识超过上限。");
            int count = reader.ReadInt32();
            if (count != values.Parameters.Count) throw new FrameFormatException("过程参数元数据数量不匹配。");
            var parameters = new SqlProcedureFrameParameter[count];
            for (int index = 0; index < count; index++)
            {
                var parameter = new SqlProcedureFrameParameter(values.Parameters[index],
                    (SqlProcedureFrameDirection)reader.ReadByte(), reader.ReadInt32(), reader.ReadInt32(),
                    reader.ReadByte(), reader.ReadByte());
                Validate(parameter);
                parameters[index] = parameter;
            }
            if (reader.Remaining != 0) throw new FrameFormatException("过程请求包含多余字节。");
            return new SqlProcedureFrameRequest(values.Db, values.Sql, session, Array.AsReadOnly(parameters));
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or OverflowException)
        {
            throw new FrameFormatException("过程请求无效：" + exception.Message);
        }
    }

    /// <summary>编码完整成功响应；没有完整信封时客户端不得发布输出。</summary>
    public static byte[] EncodeResponsePayload(SqlProcedureFrameResponse response)
    {
        if (response.ResultFrames.Count > MaxResults || response.RecordsAffected < -1)
            throw new ArgumentException("过程结果数量或影响行数无效。");
        byte[] outputs = EncodeValues(string.Empty, "outputs", response.Outputs);
        long length = 1 + 1 + 4 + 8 + 4 + 4L + outputs.Length;
        foreach (byte[] result in response.ResultFrames) length += 4L + result.Length;
        if (length > MaxResponseBytes) throw new ArgumentException("过程响应超过字节预算。");
        byte[] bytes = new byte[checked((int)length)];
        var writer = new SpanWriter(bytes);
        writer.WriteByte(Version);
        writer.WriteByte(1); // complete，非 commit 声明；外层事务可能仍处于活动状态。
        writer.WriteInt32(response.RecordsAffected);
        writer.WriteInt64(response.ReturnValue);
        writer.WriteInt32(response.ResultFrames.Count);
        foreach (byte[] result in response.ResultFrames) WriteBlob(ref writer, result);
        WriteBlob(ref writer, outputs);
        return bytes;
    }

    /// <summary>解码完整响应；未知版本、缺失完成标记及尾部字节均拒绝。</summary>
    public static SqlProcedureFrameResponse DecodeResponse(ReadOnlySpan<byte> payload)
    {
        try
        {
            if (payload.Length > MaxResponseBytes) throw new FrameFormatException("过程响应超过字节预算。");
            var reader = new SpanReader(payload);
            if (reader.ReadByte() != Version || reader.ReadByte() != 1)
                throw new FrameFormatException("过程响应没有受支持的完整成功终态；提交状态未知。");
            int affected = reader.ReadInt32();
            if (affected < -1) throw new FrameFormatException("过程影响行数无效。");
            long returned = reader.ReadInt64();
            int count = reader.ReadInt32();
            if (count is < 0 or > MaxResults) throw new FrameFormatException("过程结果数量无效。");
            var results = new byte[count][];
            for (int index = 0; index < count; index++) results[index] = ReadBlob(ref reader, MaxResponseBytes).ToArray();
            var outputs = SqlExecuteFrameCodec.DecodeRequest(ReadBlob(ref reader, MaxRequestBytes));
            if (reader.Remaining != 0 || outputs.Db.Length != 0 || outputs.Sql != "outputs")
                throw new FrameFormatException("过程完成信封无效。");
            return new SqlProcedureFrameResponse(Array.AsReadOnly(results), affected, returned, outputs.Parameters);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or OverflowException)
        {
            throw new FrameFormatException("过程响应无效，提交状态未知：" + exception.Message);
        }
    }

    /// <summary>为过程 payload 增加 Frame 信封。</summary>
    public static byte[] WrapFrame(ReadOnlySpan<byte> payload, bool response, uint streamId = 1)
    {
        int maximum = response ? MaxResponseBytes : MaxRequestBytes;
        if (payload.Length > maximum) throw new ArgumentException("过程帧超过字节预算。");
        byte[] bytes = new byte[FrameHeader.Size + payload.Length];
        var header = new FrameHeader((uint)payload.Length, FrameHeader.CurrentVersion,
            (byte)FrameService.Sql, (byte)SqlFrameOp.Procedure, response ? (byte)FrameFlags.Response : (byte)FrameFlags.None, streamId);
        header.Write(bytes);
        payload.CopyTo(bytes.AsSpan(FrameHeader.Size));
        return bytes;
    }

    private static byte[] EncodeValues(string db, string name, IReadOnlyList<SqlExecuteFrameParameter> values)
    {
        var buffer = new ArrayBufferWriter<byte>();
        SqlExecuteFrameCodec.EncodeRequest(buffer, 1, db, name, values);
        return buffer.WrittenSpan[FrameHeader.Size..].ToArray();
    }

    private static void Validate(SqlProcedureFrameParameter parameter)
    {
        if (!Enum.IsDefined(parameter.Direction) || parameter.Size < 0
            || !Enum.IsDefined((System.Data.DbType)parameter.DbType))
            throw new FrameFormatException("过程参数方向、类型或大小无效。");
        if (parameter.Precision != 0 || parameter.Scale != 0)
            throw new FrameFormatException("首版过程标量不支持非零 Precision/Scale。");
        if ((DbType)parameter.DbType is not (DbType.Object or DbType.String or DbType.StringFixedLength or DbType.AnsiString
            or DbType.AnsiStringFixedLength or DbType.Boolean or DbType.Byte or DbType.SByte or DbType.Int16
            or DbType.UInt16 or DbType.Int32 or DbType.UInt32 or DbType.Int64 or DbType.UInt64 or DbType.Single or DbType.Double))
            throw new FrameFormatException("过程参数 DbType 尚不受支持。");
        if (parameter.Value.Name.Length == 0) throw new FrameFormatException("过程参数必须具名。");
        if (parameter.Value.Kind is SqlFrameParameterKind.Blob or >= SqlFrameParameterKind.Timestamp
            && parameter.Value.Kind != SqlFrameParameterKind.Decimal)
            throw new FrameFormatException("过程参数当前只支持数值、布尔和字符串标量。");
    }

    /// <summary>按冻结的声明检查一个输入或输出值；错误标量类别及容量溢出均拒绝。</summary>
    public static object? ConvertDeclaredValue(SqlProcedureFrameParameter parameter, object? value)
    {
        Validate(parameter);
        if (value is null) return null;
        var type = (DbType)parameter.DbType;
        if (type is DbType.String or DbType.StringFixedLength or DbType.AnsiString or DbType.AnsiStringFixedLength)
        {
            if (value is not string text || parameter.Size > 0 && text.Length > parameter.Size)
                throw new InvalidOperationException("过程字符串值与 DbType/Size 不匹配。");
            return text;
        }
        if (type == DbType.Boolean && value is not bool)
            throw new InvalidOperationException("过程布尔值与 DbType 不匹配。");
        if (type is not (DbType.Object or DbType.Boolean)
            && value is not (byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal))
            throw new InvalidOperationException("过程数值与 DbType 不匹配。");
        object result = type switch
        {
            DbType.Object or DbType.Boolean => value,
            DbType.Byte => Convert.ToByte(value, CultureInfo.InvariantCulture),
            DbType.SByte => Convert.ToSByte(value, CultureInfo.InvariantCulture),
            DbType.Int16 => Convert.ToInt16(value, CultureInfo.InvariantCulture),
            DbType.UInt16 => Convert.ToUInt16(value, CultureInfo.InvariantCulture),
            DbType.Int32 => Convert.ToInt32(value, CultureInfo.InvariantCulture),
            DbType.UInt32 => Convert.ToUInt32(value, CultureInfo.InvariantCulture),
            DbType.Int64 => Convert.ToInt64(value, CultureInfo.InvariantCulture),
            DbType.UInt64 => Convert.ToUInt64(value, CultureInfo.InvariantCulture),
            DbType.Single => Convert.ToSingle(value, CultureInfo.InvariantCulture),
            DbType.Double => Convert.ToDouble(value, CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException("过程参数声明类型不受支持。"),
        };
        if (result is float single && !float.IsFinite(single) || result is double number && !double.IsFinite(number))
            throw new InvalidOperationException("过程浮点值超出有限范围。");
        return result;
    }

    private static void WriteBlob(ref SpanWriter writer, ReadOnlySpan<byte> bytes)
    {
        writer.WriteInt32(bytes.Length);
        writer.WriteBytes(bytes);
    }

    private static ReadOnlySpan<byte> ReadBlob(ref SpanReader reader, int maximum)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > maximum || count > reader.Remaining)
            throw new FrameFormatException("过程二进制块长度无效。");
        return reader.ReadBytes(count);
    }
}
