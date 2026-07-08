using System.Text.Json;

namespace SonnetDB.Caching.EasyCaching;

internal static class SonnetDbEasyCachingCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static byte[] Serialize<T>(T value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);

    public static T? Deserialize<T>(byte[] payload) =>
        JsonSerializer.Deserialize<T>(payload, JsonOptions);

    public static object? Deserialize(byte[] payload, Type type) =>
        JsonSerializer.Deserialize(payload, type, JsonOptions);
}
