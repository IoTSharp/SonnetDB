using System.Threading;

/// <summary>为 evidence target 的运行时环境隔离回归提供受控启动失败。</summary>
public static class StartupHook
{
    /// <summary>仅在测试显式请求时让 target 启动失败。</summary>
    public static void Initialize()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("SONNETDB_M40_TEST_FAIL_LAUNCHER_STARTUP"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        Thread.Sleep(TimeSpan.FromSeconds(2));
        throw new InvalidOperationException("Intentional evidence target startup-hook failure.");
    }
}
