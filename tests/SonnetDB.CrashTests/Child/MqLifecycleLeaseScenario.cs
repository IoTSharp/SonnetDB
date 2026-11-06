using SonnetMQ;

internal static class MqLifecycleLeaseScenario
{
    internal static void Run(string root, string readyFile)
    {
        using var store = SonnetMqStore.Open(new SonnetMqOptions
        {
            Path = Path.Combine(root, ".system", "mq"),
            RetentionInterval = TimeSpan.Zero,
        });
        File.WriteAllText(readyFile, "mq-instance-lease-acquired");
        Thread.Sleep(TimeSpan.FromSeconds(30));
        throw new TimeoutException("Parent did not terminate the owned MQ lease child within 30 seconds.");
    }
}
