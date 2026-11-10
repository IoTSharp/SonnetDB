using System.Diagnostics;
using SonnetDB.Diagnostics;

namespace SonnetDB.Kv.Lsm;

/// <summary>共享进程采样；后台 compaction 复用 KV 清理的查询、CPU 与 GC 内存阈值。</summary>
internal sealed class KvLsmCompactionThrottle(KvOptions options)
{
    private static readonly object SampleSync = new();
    private static long s_timestamp;
    private static TimeSpan s_processorTime;
    private static double s_cpuPercent;

    internal KvCleanupThrottleReason GetReason()
    {
        if (options.CleanupPauseWhenQueriesActive && QueryActivityTracker.ActiveQueries > 0)
            return KvCleanupThrottleReason.ActiveQueries;
        if (options.CleanupMaxMemoryLoadPercent > 0)
        {
            GCMemoryInfo memory = GC.GetGCMemoryInfo();
            if (memory.HighMemoryLoadThresholdBytes > 0
                && memory.MemoryLoadBytes * 100d / memory.HighMemoryLoadThresholdBytes >= options.CleanupMaxMemoryLoadPercent)
                return KvCleanupThrottleReason.MemoryPressure;
        }
        return options.CleanupMaxCpuPercent > 0 && SampleCpu() >= options.CleanupMaxCpuPercent
            ? KvCleanupThrottleReason.CpuPressure : KvCleanupThrottleReason.None;
    }

    private static double SampleCpu()
    {
        lock (SampleSync)
        {
            long now = Stopwatch.GetTimestamp();
            if (s_timestamp != 0 && Stopwatch.GetElapsedTime(s_timestamp, now) < TimeSpan.FromMilliseconds(100))
                return s_cpuPercent;
            using Process process = Process.GetCurrentProcess();
            TimeSpan cpu = process.TotalProcessorTime;
            if (s_timestamp != 0)
            {
                double elapsed = Stopwatch.GetElapsedTime(s_timestamp, now).TotalMilliseconds;
                s_cpuPercent = Math.Max(0, (cpu - s_processorTime).TotalMilliseconds / elapsed / Environment.ProcessorCount * 100d);
            }
            s_timestamp = now;
            s_processorTime = cpu;
            return s_cpuPercent;
        }
    }
}
