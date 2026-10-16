using System.Buffers;
using System.Globalization;
using System.Text;
using SonnetDB.IO;

namespace SonnetDB.Protocol;

/// <summary>
/// SQL 写入 Frame 的参数类型。协议只传输精确的二进制值，不拼接 SQL 文本。
/// </summary>
public enum SqlFrameParameterKind : byte
{
    /// <summary>SQL NULL。</summary>
    Null = 0,

    /// <summary>有符号 Int64，小端序。</summary>
    Int64 = 1,

    /// <summary>IEEE 754 Float64，小端序。</summary>
    Float64 = 2,

    /// <summary>布尔值，0 或 1。</summary>
    Boolean = 3,

    /// <summary>UTF-8 字符串。</summary>
    String = 4,

    /// <summary>原始二进制值。</summary>
    Blob = 5,

    /// <summary>UTC Unix 毫秒时间戳。</summary>
    Timestamp = 6,

    /// <summary>四个 little-endian Int32，按 decimal.GetBits 顺序保留精度、小数位和符号。</summary>
    Decimal = 7,

    /// <summary>UTC ticks 的 DateTimeOffset，偏移固定为零。</summary>
    DateTimeOffset = 8,

    /// <summary>完整 TimeSpan ticks。</summary>
    Duration = 9,

    /// <summary>一天内的 TimeOnly ticks。</summary>
    Time = 10,

    /// <summary>有限的 Float32 向量。</summary>
    Vector = 11,

    /// <summary>保留完整 UTC ticks 的 DateTime。</summary>
    DateTime = 12,
}

/// <summary>一个命名或位置 SQL Frame 参数；空名称表示按位置绑定。</summary>
/// <param name="Name">参数名（不含 @/:）；空字符串表示位置参数。</param>
/// <param name="Kind">精确协议类型。</param>
/// <param name="Value">与 <paramref name="Kind"/> 对应的 CLR 值；null 保留 Kind 作为声明类型。</param>
public sealed record SqlExecuteFrameParameter(
    string Name,
    SqlFrameParameterKind Kind,
    object? Value);

/// <summary>SQL Execute 请求帧解码结果。</summary>
/// <param name="Db">数据库名。</param>
/// <param name="Sql">单条 SQL 文本。</param>
/// <param name="Parameters">按请求顺序排列的命名或位置参数。</param>
public sealed record SqlExecuteFrameRequest(
    string Db,
    string Sql,
    IReadOnlyList<SqlExecuteFrameParameter> Parameters);

/// <summary>SQL Execute 已提交响应。</summary>
/// <param name="RecordsAffected">受影响行数；DDL 为零。</param>
public readonly record struct SqlExecuteFrameResponse(long RecordsAffected);

/// <summary>
/// SQL Execute=2 的独立 Protocol 编解码。该类型不引用 Core，供 Server、.NET Client 和其它语言绑定复用。
/// </summary>
public static class SqlExecuteFrameCodec
{
    /// <summary>Execute 请求和响应的 payload 合同版本；独立于 Frame 信封版本。</summary>
    public const byte CurrentVersion = 1;

    /// <summary>本切片单个请求的累计编码 payload 上限。</summary>
    public const int MaxRequestPayloadBytes = 4 * 1024 * 1024;

    /// <summary>参数数量上限。</summary>
    public const int MaxParameterCount = 256;

    /// <summary>数据库名和参数名的 UTF-8 字节上限。</summary>
    public const int MaxNameBytes = 512;

    /// <summary>SQL 文本的 UTF-8 字节上限。</summary>
    public const int MaxSqlBytes = 1024 * 1024;

    /// <summary>单个 Blob 或字符串参数的字节上限。</summary>
    public const int MaxValueBytes = MaxRequestPayloadBytes;

    /// <summary>单个向量维度上限。</summary>
    public const int MaxVectorDimension = 1_000_000;

    /// <summary>已提交响应的终态标记。</summary>
    private const byte CommittedOutcome = 1;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>把常用 ADO/CLR 值转换为精确的 SQL Frame 参数。</summary>
    /// <param name="name">参数名；空字符串表示位置参数。</param>
    /// <param name="value">CLR 参数值。</param>
    /// <returns>协议参数。</returns>
    public static SqlExecuteFrameParameter FromClr(string name, object? value)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (value is DBNull)
            value = null;
        if (value is Enum enumValue)
            value = Convert.ChangeType(enumValue, enumValue.GetTypeCode(), CultureInfo.InvariantCulture);

        return value switch
        {
            null => new(name, SqlFrameParameterKind.Null, null),
            byte or sbyte or short or ushort or int or uint or long
                => new(name, SqlFrameParameterKind.Int64, Convert.ToInt64(value, CultureInfo.InvariantCulture)),
            ulong unsignedValue when unsignedValue <= long.MaxValue
                => new(name, SqlFrameParameterKind.Int64, (long)unsignedValue),
            ulong => throw new ArgumentOutOfRangeException(nameof(value), "UInt64 参数超出 Int64 协议范围。"),
            float or double => new(name, SqlFrameParameterKind.Float64,
                Convert.ToDouble(value, CultureInfo.InvariantCulture)),
            bool boolean => new(name, SqlFrameParameterKind.Boolean, boolean),
            string text => new(name, SqlFrameParameterKind.String, text),
            byte[] bytes => new(name, SqlFrameParameterKind.Blob, bytes),
            decimal number => new(name, SqlFrameParameterKind.Decimal, number),
            DateTimeOffset offset => new(name, SqlFrameParameterKind.DateTimeOffset, offset),
            DateTime dateTime => new(name, SqlFrameParameterKind.DateTime,
                dateTime.Kind == DateTimeKind.Unspecified ? System.DateTime.SpecifyKind(dateTime, DateTimeKind.Utc) : dateTime.ToUniversalTime()),
            TimeSpan duration => new(name, SqlFrameParameterKind.Duration, duration),
            TimeOnly time => new(name, SqlFrameParameterKind.Time, time),
            float[] vector => new(name, SqlFrameParameterKind.Vector, vector),
            Memory<float> vector => new(name, SqlFrameParameterKind.Vector, vector.ToArray()),
            ReadOnlyMemory<float> vector => new(name, SqlFrameParameterKind.Vector, vector.ToArray()),
            Guid guid => new(name, SqlFrameParameterKind.String, guid.ToString("D", CultureInfo.InvariantCulture)),
            _ => throw new NotSupportedException($"SQL Frame 参数不支持 CLR 类型 {value.GetType().FullName}。"),
        };
    }

    /// <summary>
    /// 编码 SQL Execute 请求。值类型由协议精确选择；不支持的 CLR 类型立即拒绝。
    /// </summary>
    /// <param name="writer">目标写入器。</param>
    /// <param name="streamId">关联编号。</param>
    /// <param name="db">数据库名。</param>
    /// <param name="sql">单条 SQL。</param>
    /// <param name="parameters">全部命名或全部位置参数，不允许混用。</param>
    public static void EncodeRequest(
        IBufferWriter<byte> writer,
        uint streamId,
        string db,
        string sql,
        IReadOnlyList<SqlExecuteFrameParameter>? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrEmpty(sql);
        if (EncodingByteCount(db) > MaxNameBytes || EncodingByteCount(sql) > MaxSqlBytes)
            throw new ArgumentException("数据库名或 SQL 超过编码字节上限。");
        int parameterCount = parameters?.Count ?? 0;
        if (parameterCount > MaxParameterCount)
            throw new ArgumentException($"参数数 {parameterCount} 超过上限 {MaxParameterCount}。", nameof(parameters));

        long payloadLength = sizeof(byte) + SpanWriter.MeasureVarString(db)
            + SpanWriter.MeasureVarString(sql)
            + SpanWriter.MeasureVarUInt32((uint)parameterCount);
        if (parameters is not null)
        {
            ValidateBindings(parameters);
            foreach (SqlExecuteFrameParameter parameter in parameters)
            {
                ValidateParameter(parameter);
                payloadLength += SpanWriter.MeasureVarString(parameter.Name)
                    + 2
                    + MeasureValue(parameter);
            }
        }

        if (payloadLength > MaxRequestPayloadBytes)
            throw new ArgumentException($"SQL Execute payload 长度 {payloadLength} 超过上限 {MaxRequestPayloadBytes}。");

        var header = new FrameHeader((uint)payloadLength, FrameHeader.CurrentVersion,
            (byte)FrameService.Sql, (byte)SqlFrameOp.Execute, (byte)FrameFlags.None, streamId);
        Span<byte> destination = writer.GetSpan(checked(FrameHeader.Size + (int)payloadLength));
        header.Write(destination);
        var payloadWriter = new SpanWriter(destination.Slice(FrameHeader.Size, checked((int)payloadLength)));
        payloadWriter.WriteByte(CurrentVersion);
        payloadWriter.WriteVarString(db);
        payloadWriter.WriteVarString(sql);
        payloadWriter.WriteVarUInt32((uint)parameterCount);
        if (parameters is not null)
        {
            foreach (SqlExecuteFrameParameter parameter in parameters)
            {
                payloadWriter.WriteVarString(parameter.Name);
                WriteValue(ref payloadWriter, parameter);
            }
        }

        writer.Advance(checked(FrameHeader.Size + (int)payloadLength));
    }

    /// <summary>从 Execute 请求 payload 解码数据库、SQL 和精确参数。</summary>
    /// <param name="payload">不含 Frame 头的请求字节。</param>
    /// <returns>拥有独立值的请求。</returns>
    public static SqlExecuteFrameRequest DecodeRequest(ReadOnlySpan<byte> payload)
    {
        try
        {
            if (payload.Length > MaxRequestPayloadBytes)
                throw new FrameFormatException("SQL Execute 请求超过累计编码字节上限。");
            var reader = new SpanReader(payload);
            if (reader.ReadByte() != CurrentVersion)
                throw new FrameFormatException("不支持的 SQL Execute payload 版本。");
            string db = ReadBoundedString(ref reader, "db", MaxNameBytes);
            string sql = ReadBoundedString(ref reader, "sql", MaxSqlBytes);
            if (sql.Length == 0)
                throw new FrameFormatException("sql 文本不可为空。");

            uint parameterCount = reader.ReadVarUInt32();
            if (parameterCount > MaxParameterCount)
                throw new FrameFormatException($"参数数 {parameterCount} 超过上限 {MaxParameterCount}。");

            var parameters = new SqlExecuteFrameParameter[parameterCount];
            for (int index = 0; index < parameters.Length; index++)
            {
                string name = ReadBoundedString(ref reader, "参数名", MaxNameBytes);
                SqlFrameParameterKind kind = (SqlFrameParameterKind)reader.ReadByte();
                if (kind > SqlFrameParameterKind.DateTime)
                    throw new FrameFormatException("未知 SQL Execute 参数类型。");
                bool hasValue = ReadBoolean(ref reader);
                if (kind == SqlFrameParameterKind.Null && hasValue)
                    throw new FrameFormatException("NULL 参数不允许包含值。");
                object? value = hasValue ? ReadValue(ref reader, kind) : null;
                parameters[index] = new SqlExecuteFrameParameter(name, kind, value);
            }
            ValidateBindings(parameters);

            if (reader.Remaining != 0)
                throw new FrameFormatException("execute 请求帧体尾部有多余字节。");
            return new SqlExecuteFrameRequest(db, sql, parameters);
        }
        catch (FrameFormatException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or OverflowException or ArgumentException)
        {
            throw new FrameFormatException($"execute 请求帧体无效：{ex.Message}");
        }
    }

    /// <summary>编码单个已提交响应帧。</summary>
    /// <param name="writer">目标写入器。</param>
    /// <param name="streamId">关联编号。</param>
    /// <param name="recordsAffected">非负影响行数。</param>
    public static void EncodeResponse(IBufferWriter<byte> writer, uint streamId, long recordsAffected)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentOutOfRangeException.ThrowIfNegative(recordsAffected);
        const int payloadLength = sizeof(long) + 2 * sizeof(byte);
        var header = new FrameHeader(payloadLength, FrameHeader.CurrentVersion,
            (byte)FrameService.Sql, (byte)SqlFrameOp.Execute,
            (byte)FrameFlags.Response, streamId);
        Span<byte> destination = writer.GetSpan(FrameHeader.Size + payloadLength);
        header.Write(destination);
        var payloadWriter = new SpanWriter(destination.Slice(FrameHeader.Size, payloadLength));
        payloadWriter.WriteByte(CurrentVersion);
        payloadWriter.WriteInt64(recordsAffected);
        payloadWriter.WriteByte(CommittedOutcome);
        writer.Advance(FrameHeader.Size + payloadLength);
    }

    /// <summary>解码并校验已提交响应 payload。</summary>
    /// <param name="payload">响应字节。</param>
    /// <returns>可信的完成终态。</returns>
    public static SqlExecuteFrameResponse DecodeResponse(ReadOnlySpan<byte> payload)
    {
        try
        {
            var reader = new SpanReader(payload);
            if (reader.ReadByte() != CurrentVersion)
                throw new FrameFormatException("不支持的 SQL Execute 响应版本。");
            long recordsAffected = reader.ReadInt64();
            byte outcome = reader.ReadByte();
            if (outcome != CommittedOutcome || recordsAffected < 0)
                throw new FrameFormatException($"execute 响应终态 {outcome} 不是 committed。");
            if (reader.Remaining != 0)
                throw new FrameFormatException("execute 响应帧体尾部有多余字节。");
            return new SqlExecuteFrameResponse(recordsAffected);
        }
        catch (FrameFormatException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or OverflowException)
        {
            throw new FrameFormatException($"execute 响应帧体无效：{ex.Message}");
        }
    }

    private static void ValidateParameter(SqlExecuteFrameParameter parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        if (parameter.Name is null)
            throw new ArgumentException("参数名不可为 null。", nameof(parameter));
        if (EncodingByteCount(parameter.Name) > MaxNameBytes)
            throw new ArgumentException("参数名超过字节上限。", nameof(parameter));
        if (parameter.Kind > SqlFrameParameterKind.DateTime)
            throw new ArgumentException("未知 SQL Frame 参数类型。", nameof(parameter));
        if (parameter.Value is null)
            return;

        switch (parameter.Kind)
        {
            case SqlFrameParameterKind.Null when parameter.Value is null:
                return;
            case SqlFrameParameterKind.Int64 when parameter.Value is long:
            case SqlFrameParameterKind.Float64 when parameter.Value is double value && double.IsFinite(value):
            case SqlFrameParameterKind.Boolean when parameter.Value is bool:
            case SqlFrameParameterKind.String when parameter.Value is string:
            case SqlFrameParameterKind.Blob when parameter.Value is byte[]:
            case SqlFrameParameterKind.Timestamp when parameter.Value is long:
            case SqlFrameParameterKind.Decimal when parameter.Value is decimal:
            case SqlFrameParameterKind.DateTimeOffset when parameter.Value is DateTimeOffset:
            case SqlFrameParameterKind.Duration when parameter.Value is TimeSpan:
            case SqlFrameParameterKind.Time when parameter.Value is TimeOnly:
            case SqlFrameParameterKind.Vector when parameter.Value is float[]:
            case SqlFrameParameterKind.DateTime when parameter.Value is DateTime dateTime && dateTime.Kind == DateTimeKind.Utc:
                break;
            default:
                throw new ArgumentException($"参数 {parameter.Name} 的值与协议类型 {parameter.Kind} 不匹配。", nameof(parameter));
        }

        if (parameter.Value is string text && EncodingByteCount(text) > MaxValueBytes)
            throw new ArgumentException("字符串参数超过字节上限。", nameof(parameter));
        if (parameter.Value is byte[] bytes && bytes.Length > MaxValueBytes)
            throw new ArgumentException("Blob 参数超过字节上限。", nameof(parameter));
        if (parameter.Value is float[] vector)
        {
            if (vector.Length == 0 || vector.Length > MaxVectorDimension)
                throw new ArgumentException("VECTOR 参数维度超出范围。", nameof(parameter));
            if (vector.Any(static value => !float.IsFinite(value)))
                throw new ArgumentException("VECTOR 参数只能包含有限浮点数。", nameof(parameter));
        }
    }

    private static long MeasureValue(SqlExecuteFrameParameter parameter)
        => parameter.Value is null ? 0 : parameter.Kind switch
        {
            SqlFrameParameterKind.Null => 0,
            SqlFrameParameterKind.Int64 => sizeof(long),
            SqlFrameParameterKind.Float64 => sizeof(double),
            SqlFrameParameterKind.Boolean => sizeof(byte),
            SqlFrameParameterKind.String => SpanWriter.MeasureVarString((string)parameter.Value!),
            SqlFrameParameterKind.Blob => SpanWriter.MeasureVarUInt32((uint)((byte[])parameter.Value!).Length) + ((byte[])parameter.Value!).Length,
            SqlFrameParameterKind.Timestamp => sizeof(long),
            SqlFrameParameterKind.Decimal => 4 * sizeof(int),
            SqlFrameParameterKind.DateTimeOffset => sizeof(long),
            SqlFrameParameterKind.Duration => sizeof(long),
            SqlFrameParameterKind.Time => sizeof(long),
            SqlFrameParameterKind.Vector => SpanWriter.MeasureVarUInt32((uint)((float[])parameter.Value!).Length) + sizeof(float) * ((float[])parameter.Value!).Length,
            SqlFrameParameterKind.DateTime => sizeof(long),
            _ => throw new ArgumentOutOfRangeException(nameof(parameter)),
        };

    private static void WriteValue(ref SpanWriter writer, SqlExecuteFrameParameter parameter)
    {
        writer.WriteByte((byte)parameter.Kind);
        writer.WriteByte(parameter.Value is null ? (byte)0 : (byte)1);
        if (parameter.Value is null)
            return;
        switch (parameter.Kind)
        {
            case SqlFrameParameterKind.Null:
                break;
            case SqlFrameParameterKind.Int64:
            case SqlFrameParameterKind.Timestamp:
                writer.WriteInt64((long)parameter.Value!);
                break;
            case SqlFrameParameterKind.Float64:
                writer.WriteDouble((double)parameter.Value!);
                break;
            case SqlFrameParameterKind.Boolean:
                writer.WriteByte((bool)parameter.Value! ? (byte)1 : (byte)0);
                break;
            case SqlFrameParameterKind.String:
                writer.WriteVarString((string)parameter.Value!);
                break;
            case SqlFrameParameterKind.Blob:
                WriteBytes(ref writer, (byte[])parameter.Value!);
                break;
            case SqlFrameParameterKind.Decimal:
                foreach (int bits in decimal.GetBits((decimal)parameter.Value!))
                    writer.WriteInt32(bits);
                break;
            case SqlFrameParameterKind.DateTimeOffset:
                writer.WriteInt64(((DateTimeOffset)parameter.Value!).UtcTicks);
                break;
            case SqlFrameParameterKind.Duration:
                writer.WriteInt64(((TimeSpan)parameter.Value!).Ticks);
                break;
            case SqlFrameParameterKind.Time:
                writer.WriteInt64(((TimeOnly)parameter.Value!).Ticks);
                break;
            case SqlFrameParameterKind.Vector:
                float[] vector = (float[])parameter.Value!;
                writer.WriteVarUInt32((uint)vector.Length);
                foreach (float value in vector)
                    writer.WriteSingle(value);
                break;
            case SqlFrameParameterKind.DateTime:
                DateTime dateTime = (DateTime)parameter.Value!;
                writer.WriteInt64(dateTime.Ticks);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(parameter));
        }
    }

    private static object? ReadValue(ref SpanReader reader, SqlFrameParameterKind kind)
        => kind switch
        {
            SqlFrameParameterKind.Null => null,
            SqlFrameParameterKind.Int64 => reader.ReadInt64(),
            SqlFrameParameterKind.Float64 => ReadFiniteDouble(ref reader),
            SqlFrameParameterKind.Boolean => ReadBoolean(ref reader),
            SqlFrameParameterKind.String => ReadBoundedString(ref reader, "字符串参数", MaxValueBytes),
            SqlFrameParameterKind.Blob => reader.ReadBytes(ReadBoundedLength(ref reader, "Blob 参数", MaxValueBytes)).ToArray(),
            SqlFrameParameterKind.Timestamp => reader.ReadInt64(),
            SqlFrameParameterKind.Decimal => ReadDecimal(ref reader),
            SqlFrameParameterKind.DateTimeOffset => ReadDateTimeOffset(ref reader),
            SqlFrameParameterKind.Duration => TimeSpan.FromTicks(reader.ReadInt64()),
            SqlFrameParameterKind.Time => ReadTimeOnly(ref reader),
            SqlFrameParameterKind.Vector => ReadVector(ref reader),
            SqlFrameParameterKind.DateTime => ReadDateTime(ref reader),
            _ => throw new FrameFormatException($"参数值类型标记 {(byte)kind} 非法。"),
        };

    private static string ReadBoundedString(ref SpanReader reader, string label, int maxBytes)
    {
        int length = ReadBoundedLength(ref reader, label, maxBytes);
        return StrictUtf8.GetString(reader.ReadBytes(length));
    }

    private static int ReadBoundedLength(ref SpanReader reader, string label, int maxBytes)
    {
        uint length = reader.ReadVarUInt32();
        if (length > maxBytes || length > (uint)reader.Remaining)
            throw new FrameFormatException($"{label}长度 {length} 非法。");
        return (int)length;
    }

    private static bool ReadBoolean(ref SpanReader reader)
    {
        byte value = reader.ReadByte();
        return value switch
        {
            0 => false,
            1 => true,
            _ => throw new FrameFormatException("布尔参数必须是 0 或 1。"),
        };
    }

    private static double ReadFiniteDouble(ref SpanReader reader)
    {
        double value = reader.ReadDouble();
        if (!double.IsFinite(value))
            throw new FrameFormatException("Float64 参数必须是有限数值。");
        return value;
    }

    private static decimal ReadDecimal(ref SpanReader reader)
    {
        Span<int> bits = stackalloc int[4];
        for (int index = 0; index < bits.Length; index++)
            bits[index] = reader.ReadInt32();
        return new decimal(bits);
    }

    private static DateTimeOffset ReadDateTimeOffset(ref SpanReader reader)
    {
        long ticks = reader.ReadInt64();
        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
            throw new FrameFormatException("DateTimeOffset 参数 ticks 超出范围。");
        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    private static TimeOnly ReadTimeOnly(ref SpanReader reader)
    {
        return new TimeOnly(reader.ReadInt64());
    }

    private static float[] ReadVector(ref SpanReader reader)
    {
        uint dimension = reader.ReadVarUInt32();
        if (dimension == 0 || dimension > MaxVectorDimension || 4L * dimension > reader.Remaining)
            throw new FrameFormatException("VECTOR 参数维度非法。");
        var values = new float[(int)dimension];
        for (int index = 0; index < values.Length; index++)
        {
            values[index] = reader.ReadSingle();
            if (!float.IsFinite(values[index]))
                throw new FrameFormatException("VECTOR 参数只能包含有限浮点数。");
        }
        return values;
    }

    private static DateTime ReadDateTime(ref SpanReader reader)
    {
        long ticks = reader.ReadInt64();
        return new DateTime(ticks, DateTimeKind.Utc);
    }

    private static void WriteBytes(ref SpanWriter writer, byte[] value)
    {
        writer.WriteVarUInt32((uint)value.Length);
        writer.WriteBytes(value);
    }

    private static int EncodingByteCount(string value)
        => StrictUtf8.GetByteCount(value);

    private static void ValidateBindings(IReadOnlyList<SqlExecuteFrameParameter> parameters)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool? named = null;
        foreach (SqlExecuteFrameParameter parameter in parameters)
        {
            ArgumentNullException.ThrowIfNull(parameter);
            ArgumentNullException.ThrowIfNull(parameter.Name);
            bool isNamed = parameter.Name.Length != 0;
            if (named.HasValue && named.Value != isNamed)
                throw new ArgumentException("SQL Execute 不允许混合命名与位置参数。");
            named = isNamed;
            if (isNamed && (!names.Add(parameter.Name) || parameter.Name[0] is '@' or ':'
                || string.IsNullOrWhiteSpace(parameter.Name)))
                throw new ArgumentException("SQL Execute 参数名必须唯一且不含 @/: 前缀。");
        }
    }
}
