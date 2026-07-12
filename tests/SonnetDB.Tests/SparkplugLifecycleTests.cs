using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SonnetDB.Configuration;
using SonnetDB.Mqtt;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>
/// M30 #264 Sparkplug 生命周期、序列和 alias 恢复测试。
/// </summary>
public sealed class SparkplugLifecycleTests
{
    [Fact]
    public void Process_SequenceGap_RequestsRebirthOnceUntilNextBirth()
    {
        var store = new SparkplugLifecycleStore();
        SparkplugTopicRoute birth = Parse("spBv1.0/factory/NBIRTH/edge-1");
        SparkplugTopicRoute data = Parse("spBv1.0/factory/NDATA/edge-1");

        Assert.True(store.Process(birth, 0, 7).Accepted);
        Assert.True(store.Process(data, 1, null).Accepted);

        SparkplugLifecycleDecision gap = store.Process(data, 3, null);
        Assert.False(gap.Accepted);
        Assert.True(gap.RequiresRebirth);
        Assert.False(store.Process(data, 4, null).RequiresRebirth);

        Assert.True(store.Process(birth, 0, 8).Accepted);
        SparkplugNodeState state = store.GetState("factory", "edge-1");
        Assert.True(state.Online);
        Assert.Equal((ulong)8, state.BirthDeathSequence);
        Assert.False(state.RebirthRequested);
    }

    [Fact]
    public void Process_SequenceWrapFrom255ToZero_IsAccepted()
    {
        var store = new SparkplugLifecycleStore();
        SparkplugTopicRoute birth = Parse("spBv1.0/factory/NBIRTH/edge-1");
        SparkplugTopicRoute data = Parse("spBv1.0/factory/NDATA/edge-1");

        Assert.True(store.Process(birth, 0, 1).Accepted);
        for (int sequence = 1; sequence <= byte.MaxValue; sequence++)
            Assert.True(store.Process(data, (byte)sequence, null).Accepted);

        Assert.True(store.Process(data, 0, null).Accepted);
    }

    [Fact]
    public void Process_MissingRequiredBirthFields_RequestsRebirth()
    {
        var store = new SparkplugLifecycleStore();
        SparkplugTopicRoute birth = Parse("spBv1.0/factory/NBIRTH/edge-1");

        Assert.True(store.Process(birth, 0, null).RequiresRebirth);
        Assert.False(store.Process(birth, null, 1).Accepted);
    }

    [Fact]
    public void Process_DeathAndMissingDeviceBirth_TrackOfflineState()
    {
        var store = new SparkplugLifecycleStore();
        Assert.True(store.Process(Parse("spBv1.0/factory/NBIRTH/edge-1"), 0, 3).Accepted);

        SparkplugLifecycleDecision missingDevice = store.Process(
            Parse("spBv1.0/factory/DDATA/edge-1/device-1"),
            1,
            null);
        Assert.True(missingDevice.RequiresRebirth);

        Assert.True(store.Process(Parse("spBv1.0/factory/NBIRTH/edge-1"), 0, 4).Accepted);
        Assert.True(store.Process(Parse("spBv1.0/factory/DBIRTH/edge-1/device-1"), 1, null).Accepted);
        Assert.Equal(1, store.GetState("factory", "edge-1").OnlineDeviceCount);
        Assert.True(store.Process(Parse("spBv1.0/factory/DDEATH/edge-1/device-1"), null, null).Accepted);
        Assert.Equal(0, store.GetState("factory", "edge-1").OnlineDeviceCount);
        Assert.True(store.Process(Parse("spBv1.0/factory/NDEATH/edge-1"), null, 4).Accepted);
        Assert.False(store.GetState("factory", "edge-1").Online);
    }

    [Fact]
    public void PersistentAliasStore_AfterRestart_ResolvesBirthAlias()
    {
        string root = Path.Combine(Path.GetTempPath(), "sonnetdb-sparkplug-alias-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = Options.Create(new ServerOptions { DataRoot = root });
            SparkplugTopicRoute route = Parse("spBv1.0/factory/DBIRTH/edge-1/device-1");
            var first = new SparkplugAliasStore(options, NullLogger<SparkplugAliasStore>.Instance);
            first.ReplaceBirthAliases(route, [(7, "temperature")]);

            var restored = new SparkplugAliasStore(options, NullLogger<SparkplugAliasStore>.Instance);
            Assert.True(restored.TryResolveAlias(route, 7, out string name));
            Assert.Equal("temperature", name);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void EncodeRebirth_ProducesBooleanControlMetricAndSequence()
    {
        var reader = new SparkplugPayloadReader(
            SparkplugCommandEncoder.EncodeRebirth(9),
            Parse("spBv1.0/factory/NCMD/edge-1"),
            new SparkplugAliasStore());

        Assert.Equal((byte)9, reader.Sequence);
        Assert.True(reader.TryRead(out SonnetDB.Model.Point point));
        Assert.True(point.Fields["Node Control/Rebirth"].AsBool());
    }

    private static SparkplugTopicRoute Parse(string topic)
    {
        Assert.True(SparkplugTopicParser.TryParse(topic, out SparkplugTopicRoute route, out string error), error);
        return route;
    }
}
