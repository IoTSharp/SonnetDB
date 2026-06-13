using System.Text.Json;

namespace SonnetDB.Caching;

internal static class SonnetDbCacheCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static byte[] Serialize<T>(T value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);

    public static T? Deserialize<T>(byte[] payload) =>
        JsonSerializer.Deserialize<T>(payload, JsonOptions);

    public static byte[] SerializeDistributed(DistributedCacheEnvelope envelope) =>
        JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);

    public static DistributedCacheEnvelope? DeserializeDistributed(byte[] payload) =>
        JsonSerializer.Deserialize<DistributedCacheEnvelope>(payload, JsonOptions);
}

internal sealed record DistributedCacheEnvelope(
    byte[] Value,
    long? AbsoluteExpirationUtcTicks,
    long? SlidingExpirationTicks);
