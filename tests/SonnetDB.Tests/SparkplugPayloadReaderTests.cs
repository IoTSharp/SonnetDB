using SonnetDB.Model;
using SonnetDB.Mqtt;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>
/// M30 #263 Sparkplug B protobuf、topic 和 alias 解析测试。
/// </summary>
public sealed class SparkplugPayloadReaderTests
{
    [Theory]
    [InlineData("spBv1.0/factory/NBIRTH/edge-1", "NBirth", null)]
    [InlineData("spBv1.0/factory/NDATA/edge-1", "NData", null)]
    [InlineData("spBv1.0/factory/DBIRTH/edge-1/device-7", "DBirth", "device-7")]
    [InlineData("spBv1.0/factory/DDATA/edge-1/device-7", "DData", "device-7")]
    [InlineData("spBv1.0/factory/NDEATH/edge-1", "NDeath", null)]
    [InlineData("spBv1.0/factory/DDEATH/edge-1/device-7", "DDeath", "device-7")]
    [InlineData("spBv1.0/factory/NCMD/edge-1", "NCommand", null)]
    [InlineData("spBv1.0/factory/DCMD/edge-1/device-7", "DCommand", "device-7")]
    public void TryParse_ValidNamespace_ReturnsTypedRoute(
        string topic,
        string messageType,
        string? deviceId)
    {
        Assert.True(SparkplugTopicParser.TryParse(topic, out var route, out string error), error);
        Assert.Equal(messageType, route.MessageType.ToString());
        Assert.Equal("factory", route.GroupId);
        Assert.Equal("edge-1", route.EdgeNodeId);
        Assert.Equal(deviceId, route.DeviceId);
    }

    [Theory]
    [InlineData("spBv1.0/factory/DBIRTH/edge-1")]
    [InlineData("spBv1.0/factory/NBIRTH/edge-1/device-7")]
    [InlineData("spBv1.0/factory/UNKNOWN/edge-1")]
    [InlineData("SPBV1.0/factory/NBIRTH/edge-1")]
    public void TryParse_InvalidOrFutureNamespace_ReturnsFalse(string topic)
    {
        Assert.False(SparkplugTopicParser.TryParse(topic, out _, out string error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void TryRead_BirthThenAliasData_ResolvesScalarMetricsAndPreservesSlash()
    {
        var aliases = new SparkplugAliasStore();
        var birthRoute = Parse("spBv1.0/factory/NBIRTH/edge-1");
        var birth = new SparkplugPayloadReader(
            SparkplugTestPayloads.PayloadWithSequence(
                1000,
                0,
                SparkplugTestPayloads.UInt64("bdSeq", null, 5),
                SparkplugTestPayloads.Int32("Line/Speed", 1, 42),
                SparkplugTestPayloads.String("State", 2, "running"),
                SparkplugTestPayloads.Bytes("Blob", 3, [1, 2, 3]),
                SparkplugTestPayloads.Null("Missing", 4)),
            birthRoute,
            aliases);
        birth.CommitBirthAliases();

        var birthPoints = Drain(birth);

        Assert.Equal(3, birthPoints.Count);
        Assert.Equal((byte)0, birth.Sequence);
        Assert.Equal((ulong)5, birth.BirthDeathSequence);
        Assert.Equal(2, birth.SkippedMetrics);
        Assert.Equal(1, birth.UnsupportedMetrics);
        Assert.Equal(1, aliases.RegisteredEntityCount);
        Assert.Equal(42, birthPoints[1].Fields["Line/Speed"].AsLong());
        Assert.Equal("running", birthPoints[2].Fields["State"].AsString());

        var dataRoute = Parse("spBv1.0/factory/NDATA/edge-1");
        var data = new SparkplugPayloadReader(
            SparkplugTestPayloads.Payload(
                2000,
                SparkplugTestPayloads.Float(null, 1, 12.5f, timestamp: 2100),
                SparkplugTestPayloads.Boolean(null, 2, true)),
            dataRoute,
            aliases);
        var dataPoints = Drain(data);

        Assert.Equal(2, dataPoints.Count);
        Assert.Equal(12.5d, dataPoints[0].Fields["Line/Speed"].AsDouble());
        Assert.Equal(2100, dataPoints[0].Timestamp);
        Assert.True(dataPoints[1].Fields["State"].AsBool());
        Assert.Equal(2000, dataPoints[1].Timestamp);
    }

    [Fact]
    public void TryRead_AliasOnlyDataWithoutBirth_SkipsOrphanMetric()
    {
        var reader = new SparkplugPayloadReader(
            SparkplugTestPayloads.Payload(1000, SparkplugTestPayloads.Double(null, 99, 1.5)),
            Parse("spBv1.0/factory/DDATA/edge-1/device-7"),
            new SparkplugAliasStore());

        Assert.Empty(Drain(reader));
        Assert.Equal(1, reader.OrphanMetrics);
        Assert.Equal(1, reader.SkippedMetrics);
    }

    [Fact]
    public void TryRead_DeviceMetric_UsesDeviceMeasurementAndIdentityTags()
    {
        var reader = new SparkplugPayloadReader(
            SparkplugTestPayloads.Payload(1000, SparkplugTestPayloads.Int32("rpm", 1, 1500)),
            Parse("spBv1.0/factory/DBIRTH/edge-1/device-7"),
            new SparkplugAliasStore());

        Point point = Assert.Single(Drain(reader));
        Assert.Equal("device-7", point.Measurement);
        Assert.Equal("factory", point.Tags["group_id"]);
        Assert.Equal("edge-1", point.Tags["edge_node_id"]);
        Assert.Equal("device-7", point.Tags["device_id"]);
    }

    [Fact]
    public void Constructor_TruncatedProtobuf_ThrowsBulkIngestException()
    {
        Assert.Throws<SonnetDB.Ingest.BulkIngestException>(() =>
            new SparkplugPayloadReader(
                new byte[] { 0x12, 0x05, 0x08 },
                Parse("spBv1.0/factory/NBIRTH/edge-1"),
                new SparkplugAliasStore()));
    }

    private static SparkplugTopicRoute Parse(string topic)
    {
        Assert.True(SparkplugTopicParser.TryParse(topic, out var route, out string error), error);
        return route;
    }

    private static List<Point> Drain(SparkplugPayloadReader reader)
    {
        var points = new List<Point>();
        while (reader.TryRead(out var point))
            points.Add(point);
        return points;
    }
}
