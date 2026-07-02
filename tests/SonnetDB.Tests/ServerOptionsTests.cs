using SonnetDB.Configuration;
using Xunit;

namespace SonnetDB.Tests;

public sealed class ServerOptionsTests
{
    [Fact]
    public void Defaults_UseProductionSlowQueryThresholds()
    {
        var options = new ServerOptions();

        Assert.True(options.SlowQueryEnabled);
        Assert.Equal(10_000, options.SlowQueryThresholdMs);
        Assert.Equal(30_000, options.SlowQueryWarningThresholdMs);
        Assert.Equal(60_000, options.SlowQueryCriticalThresholdMs);
    }
}
