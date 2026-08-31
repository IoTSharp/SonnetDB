using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace SonnetDB.Kv;

/// <summary>
/// 在 KV 权威原始字节与 UTF-8 字符串或 source-generated JSON 类型之间转换。
/// </summary>
public static class KvValueCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    /// <summary>
    /// 把字符串编码为不带 BOM 的 UTF-8 字节。
    /// </summary>
    /// <param name="value">要编码的字符串。</param>
    /// <returns>独立的 UTF-8 字节数组。</returns>
    public static byte[] EncodeUtf8(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return StrictUtf8.GetBytes(value);
    }

    /// <summary>
    /// 严格解码 UTF-8 字节；非法字节序列会抛出 <see cref="DecoderFallbackException"/>。
    /// </summary>
    /// <param name="value">要解码的 UTF-8 字节。</param>
    /// <returns>解码后的字符串。</returns>
    public static string DecodeUtf8(ReadOnlySpan<byte> value) => StrictUtf8.GetString(value);

    /// <summary>
    /// 使用调用方提供的 source-generated JSON 元数据编码值。
    /// </summary>
    /// <typeparam name="T">值类型。</typeparam>
    /// <param name="value">要编码的值。</param>
    /// <param name="jsonTypeInfo">source-generated JSON 类型元数据。</param>
    /// <returns>独立的 UTF-8 JSON 字节数组。</returns>
    public static byte[] EncodeJson<T>(T value, JsonTypeInfo<T> jsonTypeInfo)
    {
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);
        return JsonSerializer.SerializeToUtf8Bytes(value, jsonTypeInfo);
    }

    /// <summary>
    /// 使用调用方提供的 source-generated JSON 元数据解码值。
    /// </summary>
    /// <typeparam name="T">值类型。</typeparam>
    /// <param name="value">UTF-8 JSON 字节。</param>
    /// <param name="jsonTypeInfo">source-generated JSON 类型元数据。</param>
    /// <returns>解码后的值；JSON <c>null</c> 按 <typeparamref name="T"/> 的可空语义返回。</returns>
    public static T? DecodeJson<T>(ReadOnlySpan<byte> value, JsonTypeInfo<T> jsonTypeInfo)
    {
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);
        return JsonSerializer.Deserialize(value, jsonTypeInfo);
    }
}
