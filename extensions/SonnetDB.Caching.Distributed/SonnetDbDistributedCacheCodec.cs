using System.Text.Json;

namespace SonnetDB.Caching.Distributed;

internal static class SonnetDbDistributedCacheCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static byte[] Serialize(SonnetDbDistributedCacheEnvelope envelope) =>
        JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);

    public static SonnetDbDistributedCacheEnvelope? Deserialize(byte[] payload) =>
        JsonSerializer.Deserialize<SonnetDbDistributedCacheEnvelope>(payload, JsonOptions);
}

internal sealed record SonnetDbDistributedCacheEnvelope(
    byte[] Value,
    long? AbsoluteExpirationUtcTicks,
    long? SlidingExpirationTicks);
