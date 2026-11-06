using System.Buffers.Binary;
using SonnetDB.Storage.Format;
using SonnetDB.Storage.Segments;
using Xunit;

namespace SonnetDB.Core.Tests.Storage.Segments;

/// <summary>无命中时间范围也必须验证完整 Int64 RLE 值载荷。</summary>
public sealed class Int64RleEmptyRangeTests
{
    /// <summary>raw/DoD 时间戳的空命中不能隐藏非法 run 或尾字节。</summary>
    /// <param name="deltaTimestamp">是否使用 DoD 时间戳。</param>
    /// <param name="trailingBytes">是否注入合法 run 后的尾字节，否则注入零长度 run。</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void DecodeRange_NoMatchingTimestamp_RejectsMalformedRlePayload(bool deltaTimestamp, bool trailingBytes)
    {
        byte[] timestamps = CreateTimestamps(deltaTimestamp);
        var descriptor = CreateDescriptor(deltaTimestamp);
        byte[] payload = new byte[trailingBytes ? 10 : 9];
        BinaryPrimitives.WriteInt64LittleEndian(payload, long.MinValue);
        payload[8] = trailingBytes ? (byte)2 : (byte)0;

        // [5,5] 在块的[0,10]内但没有点；不能靠全范围分支意外完成验证。
        Assert.Throws<InvalidDataException>(() => BlockDecoder.DecodeRange(descriptor, timestamps, payload, 5, 5));
    }

    /// <summary>有效 RLE 载荷仍允许无命中范围返回空结果。</summary>
    /// <param name="deltaTimestamp">是否使用 DoD 时间戳。</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DecodeRange_NoMatchingTimestampAndValidPayload_ReturnsEmpty(bool deltaTimestamp)
    {
        byte[] payload = new byte[9];
        BinaryPrimitives.WriteInt64LittleEndian(payload, long.MaxValue);
        payload[8] = 2;
        Assert.Empty(BlockDecoder.DecodeRange(CreateDescriptor(deltaTimestamp), CreateTimestamps(deltaTimestamp), payload, 5, 5));
    }

    private static BlockDescriptor CreateDescriptor(bool deltaTimestamp) => new()
    {
        Count = 2,
        MinTimestamp = 0,
        MaxTimestamp = 10,
        FieldType = FieldType.Int64,
        TimestampEncoding = deltaTimestamp ? BlockEncoding.DeltaTimestamp : BlockEncoding.None,
        ValueEncoding = BlockEncoding.Int64Rle,
    };

    private static byte[] CreateTimestamps(bool deltaTimestamp)
    {
        if (deltaTimestamp)
        {
            var result = new byte[TimestampCodec.MeasureDeltaOfDelta([0L, 10L])];
            TimestampCodec.WriteDeltaOfDelta([0L, 10L], result);
            return result;
        }
        var raw = new byte[16];
        BinaryPrimitives.WriteInt64LittleEndian(raw.AsSpan(8), 10);
        return raw;
    }
}
