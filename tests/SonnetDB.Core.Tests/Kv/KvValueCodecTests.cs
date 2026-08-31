using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SonnetDB.Kv;
using Xunit;

namespace SonnetDB.Core.Tests.Kv;

public sealed class KvValueCodecTests
{
    [Fact]
    public void EncodeDecodeUtf8_WithUnicode_RoundTripsWithoutBom()
    {
        const string value = "设备-A/温度";

        byte[] encoded = KvValueCodec.EncodeUtf8(value);

        Assert.False(encoded.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.Equal(value, KvValueCodec.DecodeUtf8(encoded));
    }

    [Fact]
    public void DecodeUtf8_WithInvalidSequence_ThrowsDecoderFallbackException()
    {
        Assert.Throws<DecoderFallbackException>(() => KvValueCodec.DecodeUtf8([0xc3, 0x28]));
    }

    [Fact]
    public void EncodeUtf8_WithUnpairedSurrogate_ThrowsEncoderFallbackException()
    {
        Assert.Throws<EncoderFallbackException>(() => KvValueCodec.EncodeUtf8("\ud800"));
    }

    [Fact]
    public void EncodeDecodeJson_WithSourceGeneratedTypeInfo_RoundTrips()
    {
        var value = new KvCodecPayload("sensor-1", 3, ["north", "critical"]);

        byte[] encoded = KvValueCodec.EncodeJson(
            value,
            KvValueCodecJsonContext.Default.KvCodecPayload);
        KvCodecPayload? decoded = KvValueCodec.DecodeJson(
            encoded,
            KvValueCodecJsonContext.Default.KvCodecPayload);

        Assert.NotNull(decoded);
        Assert.Equal(value.Name, decoded.Name);
        Assert.Equal(value.Count, decoded.Count);
        Assert.Equal(value.Tags, decoded.Tags);
        Assert.Equal(
            "{\"name\":\"sensor-1\",\"count\":3,\"tags\":[\"north\",\"critical\"]}",
            Encoding.UTF8.GetString(encoded));
    }

    [Fact]
    public void DecodeJson_WithMalformedPayload_ThrowsJsonException()
    {
        Assert.Throws<JsonException>(() => KvValueCodec.DecodeJson(
            "{"u8,
            KvValueCodecJsonContext.Default.KvCodecPayload));
    }
}

internal sealed record KvCodecPayload(string Name, int Count, string[] Tags);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(KvCodecPayload))]
internal sealed partial class KvValueCodecJsonContext : JsonSerializerContext;
