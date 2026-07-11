using Microsoft.Extensions.Configuration;
using SonnetDB.Configuration;
using SonnetDB.Hosting;
using Xunit;

namespace SonnetDB.Tests;

public sealed class ServerOptionsTests
{
    [Fact]
    public void Defaults_UseProductionSlowQueryThresholds()
    {
        var options = new ServerOptions();

        var slowQuery = options.Observability.SlowQueryLog;
        Assert.True(slowQuery.Enabled);
        Assert.Equal(10_000, slowQuery.ThresholdMs);
        Assert.Equal(30_000, slowQuery.WarningThresholdMs);
        Assert.Equal(60_000, slowQuery.CriticalThresholdMs);
        Assert.Equal(256, slowQuery.Capacity);
    }

    [Fact]
    public void Bind_WithLegacySlowQueryKeys_AppliesCompatibilityValues()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SonnetDBServer:SlowQueryEnabled"] = "false",
                ["SonnetDBServer:SlowQueryThresholdMs"] = "25",
                ["SonnetDBServer:SlowQueryWarningThresholdMs"] = "50",
                ["SonnetDBServer:SlowQueryCriticalThresholdMs"] = "75",
            })
            .Build();

        var options = ServerOptionsBinder.Bind(configuration).Observability.SlowQueryLog;

        Assert.False(options.Enabled);
        Assert.Equal(25, options.ThresholdMs);
        Assert.Equal(50, options.WarningThresholdMs);
        Assert.Equal(75, options.CriticalThresholdMs);
    }

    [Fact]
    public void Bind_WithNestedAndLegacySlowQueryKeys_PrefersNestedValues()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SonnetDBServer:SlowQueryThresholdMs"] = "25",
                ["SonnetDBServer:Observability:SlowQueryLog:ThresholdMs"] = "125",
            })
            .Build();

        var options = ServerOptionsBinder.Bind(configuration).Observability.SlowQueryLog;

        Assert.Equal(125, options.ThresholdMs);
    }
}
