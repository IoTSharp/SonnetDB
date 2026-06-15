using SonnetDB.Memory;
using Xunit;

namespace SonnetDB.Core.Tests.Memory;

/// <summary>
/// <see cref="MemTableFlushPolicy"/> 单元测试。
/// </summary>
public sealed class MemTableFlushPolicyTests
{
    [Fact]
    public void Default_HasExpectedValues()
    {
        var policy = MemTableFlushPolicy.Default;

        Assert.Equal(16 * 1024 * 1024L, policy.MaxBytes);
        Assert.Null(policy.HardCapBytes);
        Assert.Equal(64 * 1024 * 1024L, policy.ResolveHardCapBytes());
        Assert.Equal(1_000_000L, policy.MaxPoints);
        Assert.Equal(TimeSpan.FromMinutes(5), policy.MaxAge);
    }

    [Fact]
    public void InitProperties_CanBeCustomized()
    {
        var policy = new MemTableFlushPolicy
        {
            MaxBytes = 1024,
            HardCapBytes = 4096,
            MaxPoints = 100,
            MaxAge = TimeSpan.FromSeconds(30)
        };

        Assert.Equal(1024L, policy.MaxBytes);
        Assert.Equal(4096L, policy.HardCapBytes);
        Assert.Equal(4096L, policy.ResolveHardCapBytes());
        Assert.Equal(100L, policy.MaxPoints);
        Assert.Equal(TimeSpan.FromSeconds(30), policy.MaxAge);
    }

    [Fact]
    public void ResolveHardCapBytes_WhenUnset_UsesFourTimesMaxBytes()
    {
        var policy = new MemTableFlushPolicy
        {
            MaxBytes = 1234,
        };

        Assert.Equal(4936L, policy.ResolveHardCapBytes());
    }

    [Fact]
    public void MaxAge_Zero_ShouldFlushImmediately()
    {
        var table = new MemTable();
        var policy = new MemTableFlushPolicy
        {
            MaxBytes = long.MaxValue,
            MaxPoints = long.MaxValue,
            MaxAge = TimeSpan.Zero
        };

        // Even without any data, MaxAge=0 means already past the threshold
        Assert.True(table.ShouldFlush(policy));
    }

    [Fact]
    public void Default_IsSingleton()
    {
        // Ensure Default returns the same instance each time
        Assert.Same(MemTableFlushPolicy.Default, MemTableFlushPolicy.Default);
    }
}
