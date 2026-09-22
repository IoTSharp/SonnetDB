using System.Text;
using SonnetDB.Cdc;

namespace SonnetDB.Core.Tests.Cdc;

public sealed class CdcEventCodecTests
{
    [Fact]
    public void EncodeDecode_RoundTrip_IsDeterministic()
    {
        CdcEvent expected = CreateEvent();

        byte[] encoded = CdcEventCodec.Encode(expected);
        CdcEvent actual = CdcEventCodec.Decode(encoded);

        Assert.Equal(expected, actual);
        Assert.Equal(encoded, CdcEventCodec.Encode(actual));
        Assert.Equal(
            "{\"eventId\":\"evt-1\",\"source\":\"db-a\",\"entity\":\"documents\",\"key\":\"doc-7\",\"sequence\":42,\"occurredAtUtc\":\"2026-09-22T08:30:00+00:00\",\"metadata\":{\"contractVersion\":1,\"schema\":\"documents\",\"schemaVersion\":1,\"operation\":\"update\",\"checkpoint\":{\"partition\":2,\"offset\":99}},\"before\":{\"value\":1},\"after\":{\"value\":2}}",
            Encoding.UTF8.GetString(encoded));
    }

    [Fact]
    public void Decode_UnknownContractVersion_ThrowsExplicitVersionError()
    {
        byte[] encoded = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(CdcEventCodec.Encode(CreateEvent()))
                .Replace("\"contractVersion\":1", "\"contractVersion\":2", StringComparison.Ordinal));

        CdcUnsupportedVersionException exception = Assert.Throws<CdcUnsupportedVersionException>(
            () => CdcEventCodec.Decode(encoded));

        Assert.Equal(2, exception.Version);
    }

    [Fact]
    public void Decode_UnknownSchemaVersion_ThrowsExplicitSchemaError()
    {
        byte[] encoded = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(CdcEventCodec.Encode(CreateEvent()))
                .Replace("\"schemaVersion\":1", "\"schemaVersion\":2", StringComparison.Ordinal));

        CdcUnsupportedSchemaVersionException exception = Assert.Throws<CdcUnsupportedSchemaVersionException>(
            () => CdcEventCodec.Decode(encoded));

        Assert.Equal("documents", exception.Schema);
        Assert.Equal(2, exception.Version);
    }

    [Fact]
    public void Decode_UnknownOrDuplicateProperties_ThrowsFormatError()
    {
        string json = Encoding.UTF8.GetString(CdcEventCodec.Encode(CreateEvent()));

        CdcFormatException unknown = Assert.Throws<CdcFormatException>(
            () => CdcEventCodec.Decode(Encoding.UTF8.GetBytes(json.Replace("{\"eventId\"", "{\"future\":true,\"eventId\"", StringComparison.Ordinal))));
        Assert.Contains("未知字段", unknown.Message, StringComparison.Ordinal);

        CdcFormatException duplicate = Assert.Throws<CdcFormatException>(
            () => CdcEventCodec.Decode(Encoding.UTF8.GetBytes(json.Replace("{\"eventId\"", "{\"eventId\":\"duplicate\",\"eventId\"", StringComparison.Ordinal))));
        Assert.Contains("重复", duplicate.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Encode_InvalidUtf8BudgetAndPayload_Throws()
    {
        string oversizedIdentifier = new('x', CdcEventCodec.MaxStringBytes + 1);
        Assert.Throws<ArgumentException>(() => CdcEventCodec.Encode(CreateEvent(Source: oversizedIdentifier)));
        Assert.Throws<ArgumentException>(() => CdcEventCodec.Encode(CreateEvent(Source: "bad\uD800")));
        Assert.Throws<ArgumentException>(() => CdcEventCodec.Encode(CreateEvent(AfterJson: "{\"value\":\"" + new string('x', CdcEventCodec.MaxPayloadBytes) + "\"}")));
        Assert.Throws<ArgumentException>(() => CdcEventCodec.Encode(CreateEvent(AfterJson: "not-json")));
    }

    [Fact]
    public void Encode_DeepPayload_ThrowsFormatBoundary()
    {
        var builder = new StringBuilder();
        for (int depth = 0; depth < 33; depth++)
            builder.Append('[');
        builder.Append('0');
        for (int depth = 0; depth < 33; depth++)
            builder.Append(']');

        Assert.Throws<ArgumentException>(() => CdcEventCodec.Encode(CreateEvent(AfterJson: builder.ToString())));
    }

    [Fact]
    public void Decode_InvalidUtf8AndOversizedInput_ThrowsFormatError()
    {
        Assert.Throws<CdcFormatException>(() => CdcEventCodec.Decode([0xFF, 0xFE, 0xFD]));
        Assert.Throws<CdcFormatException>(() => CdcEventCodec.Decode(new byte[CdcEventCodec.MaxEventBytes + 1]));
        Assert.Throws<CdcFormatException>(() => CdcEventCodec.Decode(Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(CdcEventCodec.Encode(CreateEvent()))
                .Replace("\"source\":\"db-a\"", "\"source\":\"bad\\uD800\"", StringComparison.Ordinal))));
    }

    [Fact]
    public async Task StreamRoundTrip_AndCancellation_AreBounded()
    {
        CdcEvent expected = CreateEvent();
        await using var stream = new MemoryStream();
        await CdcEventCodec.WriteAsync(stream, expected);
        stream.Position = 0;
        CdcEvent actual = await CdcEventCodec.ReadAsync(stream);
        Assert.Equal(expected, actual);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => CdcEventCodec.WriteAsync(new MemoryStream(), expected, cancellation.Token).AsTask());
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => CdcEventCodec.ReadAsync(new MemoryStream(), cancellation.Token).AsTask());
    }

    [Fact]
    public void Encode_OperationPayloadShape_IsValidated()
    {
        Assert.Throws<ArgumentException>(() => CdcEventCodec.Encode(CreateEvent(
            Operation: CdcOperation.Insert,
            BeforeJson: "{\"value\":0}")));
        Assert.Throws<ArgumentException>(() => CdcEventCodec.Encode(CreateEvent(
            Operation: CdcOperation.Delete,
            AfterJson: "{\"value\":0}")));
    }

    private static CdcEvent CreateEvent(
        string Source = "db-a",
        CdcOperation Operation = CdcOperation.Update,
        string? BeforeJson = "{\"value\":1}",
        string? AfterJson = "{\"value\":2}")
        => new(
            "evt-1",
            Source,
            "documents",
            "doc-7",
            42,
            new DateTimeOffset(2026, 9, 22, 8, 30, 0, TimeSpan.Zero),
            new CdcEventMetadata(1, "documents", 1, Operation, new CdcCheckpoint(2, 99)),
            BeforeJson,
            AfterJson);
}
