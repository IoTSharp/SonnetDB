using System.Diagnostics;
using SonnetDB.Contracts;
using SonnetDB.Copilot;
using SonnetDB.Hosting;

namespace SonnetDB.Diagnostics;

/// <summary>
/// 采集仅包含进程与数据库 metadata 的诊断快照。
/// </summary>
internal sealed class DiagnosticDumpService(
    TsdbRegistry registry,
    CopilotInFlightTracker copilotInFlightTracker)
{
    private readonly long _startedAtTimestamp = Stopwatch.GetTimestamp();

    /// <summary>
    /// 采集当前诊断快照，不读取用户数据点或 WAL 记录内容。
    /// </summary>
    /// <returns>进程、GC、ThreadPool、数据库与 Copilot 运行时快照。</returns>
    public DiagnosticDumpResponse Capture()
    {
        var gcInfo = GC.GetGCMemoryInfo();
        ThreadPool.GetAvailableThreads(out var availableWorkerThreads, out var availableCompletionPortThreads);
        ThreadPool.GetMinThreads(out var minWorkerThreads, out var minCompletionPortThreads);
        ThreadPool.GetMaxThreads(out var maxWorkerThreads, out var maxCompletionPortThreads);

        var databases = new List<DatabaseDiagnosticSnapshot>(registry.Count);
        foreach (var databaseName in registry.ListDatabases())
        {
            if (!registry.TryGet(databaseName, out var database))
                continue;

            try
            {
                var snapshot = database.GetRuntimeDiagnosticSnapshot();
                databases.Add(new DatabaseDiagnosticSnapshot(
                    databaseName,
                    snapshot.MemTableEstimatedBytes,
                    snapshot.MemTablePointCount,
                    snapshot.SegmentCount,
                    snapshot.PendingFlushTasks,
                    snapshot.PendingCompactionTasks,
                    snapshot.CheckpointLsn,
                    snapshot.WalFiles
                        .Select(file => new WalFileDiagnosticEntry(
                            file.FileName,
                            file.FileLength,
                            file.StartLsn,
                            file.LastLsn,
                            file.IsActive))
                        .ToList()));
            }
            catch (ObjectDisposedException)
            {
                // 与并发 DROP DATABASE 竞争时跳过已关闭实例。
            }
        }

        return new DiagnosticDumpResponse(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            new ProcessDiagnosticSnapshot(
                Environment.ProcessId,
                (long)Stopwatch.GetElapsedTime(_startedAtTimestamp).TotalMilliseconds,
                Environment.WorkingSet),
            new GcDiagnosticSnapshot(
                GC.GetTotalMemory(forceFullCollection: false),
                gcInfo.HeapSizeBytes,
                gcInfo.FragmentedBytes,
                gcInfo.TotalCommittedBytes,
                gcInfo.MemoryLoadBytes,
                gcInfo.TotalAvailableMemoryBytes,
                gcInfo.HighMemoryLoadThresholdBytes,
                gcInfo.PinnedObjectsCount,
                gcInfo.FinalizationPendingCount,
                GC.CollectionCount(0),
                GC.CollectionCount(1),
                GC.CollectionCount(2)),
            new ThreadPoolDiagnosticSnapshot(
                ThreadPool.ThreadCount,
                ThreadPool.PendingWorkItemCount,
                ThreadPool.CompletedWorkItemCount,
                availableWorkerThreads,
                availableCompletionPortThreads,
                minWorkerThreads,
                minCompletionPortThreads,
                maxWorkerThreads,
                maxCompletionPortThreads),
            new CopilotRuntimeDiagnosticSnapshot(copilotInFlightTracker.Count),
            databases);
    }
}
