using System.Diagnostics;
using SonnetMQ;
using Xunit;

namespace SonnetDB.CrashTests;

public sealed partial class CrashReliabilityTests
{
    /// <summary>实例 MQ 的目录租约在没有消息段时也排斥其它进程，并在持有进程终止后释放。</summary>
    [Fact]
    public void MqInstance_CrossProcessLifecycleLease_RejectsConcurrentOwnerAndReopensAfterTermination()
    {
        string root = NewScenarioRoot();
        string ready = Path.Combine(root, "mq-owner.ready");
        var options = new SonnetMqOptions { Path = Path.Combine(root, ".system", "mq"), RetentionInterval = TimeSpan.Zero };
        using Process process = StartChild("hold_mq_instance_lifecycle_lease", root, ready);
        try
        {
            WaitForReady(process, ready, TimeSpan.FromSeconds(10));
            Assert.Equal("mq-instance-lease-acquired", File.ReadAllText(ready));
            Assert.Throws<IOException>(() => SonnetMqStore.Open(options));
            Assert.False(process.HasExited);
        }
        finally { TerminateAndWait(process, TimeSpan.FromSeconds(10)); }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        for (int reopen = 0; reopen < 2; reopen++)
        {
            deadline.Token.ThrowIfCancellationRequested();
            using var store = SonnetMqStore.Open(options);
            Assert.Equal(reopen, store.Publish("events", [1]));
        }
    }
}
