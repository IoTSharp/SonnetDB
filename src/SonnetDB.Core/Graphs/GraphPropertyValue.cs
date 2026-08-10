using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SonnetDB.Graphs;

/// <summary>
/// 图属性的类型化标量值。二进制输入和输出均复制，实例不会借用调用方缓冲区。
/// </summary>
public readonly struct GraphPropertyValue : IEquatable<GraphPropertyValue>
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private readonly long _numeric;
    private readonly object? _reference;

    private GraphPropertyValue(GraphPropertyKind kind, long numeric, object? reference)
    {
        Kind = kind;
        _numeric = numeric;
        _reference = reference;
    }

    /// <summary>属性值的类型标签。</summary>
    public GraphPropertyKind Kind { get; }

    /// <summary>空属性值。</summary>
    public static GraphPropertyValue Null => default;

    /// <summary>创建 64 位有符号整数属性值。</summary>
    /// <param name="value">整数值。</param>
    /// <returns>类型化属性值。</returns>
    public static GraphPropertyValue FromInt64(long value)
        => new(GraphPropertyKind.Int64, value, null);

    /// <summary>创建 64 位浮点属性值，并保留原始 IEEE 754 位模式。</summary>
    /// <param name="value">浮点值。</param>
    /// <returns>类型化属性值。</returns>
    public static GraphPropertyValue FromFloat64(double value)
        => new(GraphPropertyKind.Float64, BitConverter.DoubleToInt64Bits(value), null);

    /// <summary>创建布尔属性值。</summary>
    /// <param name="value">布尔值。</param>
    /// <returns>类型化属性值。</returns>
    public static GraphPropertyValue FromBoolean(bool value)
        => new(GraphPropertyKind.Boolean, value ? 1 : 0, null);

    /// <summary>创建字符串属性值。</summary>
    /// <param name="value">非 null 且可编码为严格 UTF-8 的字符串。</param>
    /// <returns>类型化属性值。</returns>
    /// <exception cref="ArgumentException">字符串包含未配对 surrogate，不能编码为 UTF-8。</exception>
    public static GraphPropertyValue FromString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ValidateUnicode(value);
        return new GraphPropertyValue(GraphPropertyKind.String, 0, value);
    }

    /// <summary>创建 UTC 时间属性值；持久化精度固定为毫秒。</summary>
    /// <param name="value">任意 offset 的时间值，写入前归一化为 UTC。</param>
    /// <returns>类型化属性值。</returns>
    public static GraphPropertyValue FromDateTime(DateTimeOffset value)
        => new(GraphPropertyKind.DateTime, value.ToUnixTimeMilliseconds(), null);

    /// <summary>创建二进制属性值并复制输入。</summary>
    /// <param name="value">二进制内容。</param>
    /// <returns>类型化属性值。</returns>
    public static GraphPropertyValue FromBlob(ReadOnlySpan<byte> value)
        => new(GraphPropertyKind.Blob, 0, value.ToArray());

    /// <summary>创建 JSON 属性值并验证其语法，原始文本不会被规范化。</summary>
    /// <param name="value">可编码为严格 UTF-8 的合法 JSON 文本。</param>
    /// <returns>类型化属性值。</returns>
    /// <exception cref="ArgumentException">文本包含未配对 surrogate，不能编码为 UTF-8。</exception>
    /// <exception cref="JsonException">JSON 语法无效时抛出。</exception>
    public static GraphPropertyValue FromJson(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ValidateUnicode(value);
        using JsonDocument _ = JsonDocument.Parse(value);
        return new GraphPropertyValue(GraphPropertyKind.Json, 0, value);
    }

    /// <summary>读取 64 位有符号整数。</summary>
    /// <returns>整数值。</returns>
    public long AsInt64() => Kind == GraphPropertyKind.Int64
        ? _numeric
        : throw TypeMismatch(GraphPropertyKind.Int64);

    /// <summary>读取 64 位浮点数。</summary>
    /// <returns>浮点值。</returns>
    public double AsFloat64() => Kind == GraphPropertyKind.Float64
        ? BitConverter.Int64BitsToDouble(_numeric)
        : throw TypeMismatch(GraphPropertyKind.Float64);

    /// <summary>读取布尔值。</summary>
    /// <returns>布尔值。</returns>
    public bool AsBoolean() => Kind == GraphPropertyKind.Boolean
        ? _numeric != 0
        : throw TypeMismatch(GraphPropertyKind.Boolean);

    /// <summary>读取字符串。</summary>
    /// <returns>字符串值。</returns>
    public string AsString() => Kind == GraphPropertyKind.String
        ? (string)_reference!
        : throw TypeMismatch(GraphPropertyKind.String);

    /// <summary>读取 UTC 时间。</summary>
    /// <returns>精确到毫秒的 UTC 时间。</returns>
    public DateTimeOffset AsDateTime() => Kind == GraphPropertyKind.DateTime
        ? DateTimeOffset.FromUnixTimeMilliseconds(_numeric)
        : throw TypeMismatch(GraphPropertyKind.DateTime);

    /// <summary>读取二进制值的副本。</summary>
    /// <returns>由调用方独占的二进制副本。</returns>
    public byte[] AsBlob() => Kind == GraphPropertyKind.Blob
        ? ((byte[])_reference!).ToArray()
        : throw TypeMismatch(GraphPropertyKind.Blob);

    /// <summary>读取原始 JSON 文本。</summary>
    /// <returns>JSON 文本。</returns>
    public string AsJson() => Kind == GraphPropertyKind.Json
        ? (string)_reference!
        : throw TypeMismatch(GraphPropertyKind.Json);

    internal long NumericBits => _numeric;

    internal string ReferenceText => (string)_reference!;

    internal ReadOnlySpan<byte> BlobSpan => (byte[])_reference!;

    /// <inheritdoc />
    public bool Equals(GraphPropertyValue other)
    {
        if (Kind != other.Kind || _numeric != other._numeric)
            return false;

        return Kind switch
        {
            GraphPropertyKind.String or GraphPropertyKind.Json =>
                string.Equals((string?)_reference, (string?)other._reference, StringComparison.Ordinal),
            GraphPropertyKind.Blob => BlobSpan.SequenceEqual(other.BlobSpan),
            _ => true,
        };
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is GraphPropertyValue other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Kind);
        hash.Add(_numeric);
        if (Kind is GraphPropertyKind.String or GraphPropertyKind.Json)
            hash.Add((string?)_reference, StringComparer.Ordinal);
        else if (Kind == GraphPropertyKind.Blob)
        {
            foreach (byte value in BlobSpan)
                hash.Add(value);
        }

        return hash.ToHashCode();
    }

    /// <inheritdoc />
    public override string ToString() => Kind switch
    {
        GraphPropertyKind.Null => "null",
        GraphPropertyKind.Int64 => _numeric.ToString(CultureInfo.InvariantCulture),
        GraphPropertyKind.Float64 => BitConverter.Int64BitsToDouble(_numeric).ToString("R", CultureInfo.InvariantCulture),
        GraphPropertyKind.Boolean => _numeric == 0 ? "false" : "true",
        GraphPropertyKind.String => (string)_reference!,
        GraphPropertyKind.DateTime => AsDateTime().ToString("O", CultureInfo.InvariantCulture),
        GraphPropertyKind.Blob => Convert.ToHexString(BlobSpan),
        GraphPropertyKind.Json => (string)_reference!,
        _ => throw new InvalidOperationException($"未知图属性类型 {Kind}。"),
    };

    /// <summary>比较两个图属性值是否相等。</summary>
    public static bool operator ==(GraphPropertyValue left, GraphPropertyValue right) => left.Equals(right);

    /// <summary>比较两个图属性值是否不相等。</summary>
    public static bool operator !=(GraphPropertyValue left, GraphPropertyValue right) => !left.Equals(right);

    private InvalidOperationException TypeMismatch(GraphPropertyKind expected)
        => new($"图属性类型为 {Kind}，不能按 {expected} 读取。");

    private static void ValidateUnicode(string value)
    {
        try
        {
            _ = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException("图属性文本必须是可编码为严格 UTF-8 的有效 Unicode。", nameof(value), exception);
        }
    }
}
