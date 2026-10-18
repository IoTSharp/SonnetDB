using System.Buffers.Binary;
using System.Text;

namespace SonnetDB.Tables;

/// <summary>关系表成对身份的稳定长度编码与存储路径映射。</summary>
internal static class TableObjectNameCodec
{
    private const byte IdentityVersion = 1;
    private const int HeaderSize = 9;
    private const int MaximumPartBytes = 1_048_576;
    internal const string QualifiedStorageDirectory = "schemas-v1";
    private static readonly UTF8Encoding _strictUtf8 = new(false, true);

    /// <summary>把 schema 与表名编码为无分隔符歧义的版本化二进制身份。</summary>
    internal static byte[] EncodeIdentity(TableObjectName objectName)
    {
        byte[] schema = _strictUtf8.GetBytes(objectName.SchemaName);
        byte[] name = _strictUtf8.GetBytes(objectName.Name);
        ValidatePartLength(schema.Length, nameof(objectName.SchemaName));
        ValidatePartLength(name.Length, nameof(objectName.Name));
        byte[] result = new byte[checked(HeaderSize + schema.Length + name.Length)];
        result[0] = IdentityVersion;
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(1, 4), schema.Length);
        schema.CopyTo(result.AsSpan(5));
        int nameLengthOffset = 5 + schema.Length;
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(nameLengthOffset, 4), name.Length);
        name.CopyTo(result.AsSpan(nameLengthOffset + 4));
        return result;
    }

    /// <summary>解码版本化成对身份。</summary>
    internal static TableObjectName DecodeIdentity(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length < HeaderSize || encoded[0] != IdentityVersion)
            throw new InvalidDataException("关系表对象身份版本或长度无效。");
        int schemaLength = BinaryPrimitives.ReadInt32LittleEndian(encoded.Slice(1, 4));
        ValidateDecodedPartLength(schemaLength, encoded.Length - HeaderSize);
        int nameLengthOffset = checked(5 + schemaLength);
        if (nameLengthOffset + 4 > encoded.Length)
            throw new InvalidDataException("关系表对象身份 schema 长度越界。");
        int nameLength = BinaryPrimitives.ReadInt32LittleEndian(encoded.Slice(nameLengthOffset, 4));
        ValidateDecodedPartLength(nameLength, encoded.Length - nameLengthOffset - 4);
        if (nameLengthOffset + 4 + nameLength != encoded.Length)
            throw new InvalidDataException("关系表对象身份包含尾随字节或截断内容。");
        try
        {
            string schema = _strictUtf8.GetString(encoded.Slice(5, schemaLength));
            string name = _strictUtf8.GetString(encoded.Slice(nameLengthOffset + 4, nameLength));
            return new TableObjectName(schema, name);
        }
        catch (DecoderFallbackException error)
        {
            throw new InvalidDataException("关系表对象身份不是有效 UTF-8。", error);
        }
    }

    /// <summary>
    /// 返回 rowstore 相对路径段。main 保持旧单段十六进制路径；非 main 进入独立保留目录。
    /// </summary>
    internal static string[] EncodeStoragePath(TableObjectName objectName)
        => objectName.IsDefaultSchema
            ? [EncodeHex(_strictUtf8.GetBytes(objectName.Name))]
            : [QualifiedStorageDirectory, EncodeHex(EncodeIdentity(objectName))];

    /// <summary>
    /// 从 rowstore 相对路径段恢复身份。旧单段名称全部属于 main，名称内的点不会被拆分。
    /// </summary>
    internal static TableObjectName DecodeStoragePath(IReadOnlyList<string> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Count == 1)
        {
            try
            {
                return new TableObjectName(_strictUtf8.GetString(Convert.FromHexString(segments[0])));
            }
            catch (Exception error) when (error is FormatException or DecoderFallbackException)
            {
                throw new InvalidDataException("旧关系表 rowstore 路径不是有效名称编码。", error);
            }
        }
        if (segments.Count == 2 && string.Equals(segments[0], QualifiedStorageDirectory, StringComparison.Ordinal))
        {
            try
            {
                return DecodeIdentity(Convert.FromHexString(segments[1]));
            }
            catch (FormatException error)
            {
                throw new InvalidDataException("限定关系表 rowstore 路径不是有效十六进制编码。", error);
            }
        }
        throw new InvalidDataException("关系表 rowstore 路径段数量或命名空间无效。");
    }

    private static string EncodeHex(ReadOnlySpan<byte> value) => Convert.ToHexString(value).ToLowerInvariant();

    private static void ValidatePartLength(int length, string parameterName)
    {
        if (length is <= 0 or > MaximumPartBytes)
            throw new ArgumentOutOfRangeException(parameterName, "关系表对象名称的 UTF-8 长度必须在 1 到 1048576 字节之间。");
    }

    private static void ValidateDecodedPartLength(int length, int remaining)
    {
        if (length is <= 0 or > MaximumPartBytes || length > remaining)
            throw new InvalidDataException("关系表对象身份中的名称长度无效。");
    }
}
