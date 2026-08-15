using System.Diagnostics;
using System.Diagnostics.Metrics;
using SonnetDB.Contracts;

namespace SonnetDB.Diagnostics;

/// <summary>
/// SQL 执行的低基数进程指标。指纹与索引名只保存在有界诊断聚合，不作为 metric label。
/// </summary>
internal static class SqlQueryDiagnostics
{
    private static readonly Meter Meter = new("SonnetDB.Server", "1.0.0");
    private static readonly Counter<long> Queries = Meter.CreateCounter<long>(
        "sonnetdb.sql.query.count", unit: "{query}", description: "Completed SQL statements.");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>(
        "sonnetdb.sql.query.duration", unit: "ms", description: "End-to-end SQL statement duration.");
    private static readonly Histogram<double> QueueWait = Meter.CreateHistogram<double>(
        "sonnetdb.sql.queue.wait.duration", unit: "ms", description: "SQL admission queue wait duration.");
    private static readonly Histogram<long> CandidateRows = Meter.CreateHistogram<long>(
        "sonnetdb.sql.candidate.rows", unit: "{row}", description: "Candidate rows produced by the selected access path.");
    private static readonly Histogram<long> ExaminedRows = Meter.CreateHistogram<long>(
        "sonnetdb.sql.examined.rows", unit: "{row}", description: "Candidate rows examined by residual predicates.");
    private static readonly Histogram<long> ReturnedRows = Meter.CreateHistogram<long>(
        "sonnetdb.sql.returned.rows", unit: "{row}", description: "Rows returned by a SQL statement.");
    private static readonly Histogram<long> AllocatedBytes = Meter.CreateHistogram<long>(
        "sonnetdb.sql.allocated.bytes", unit: "By", description: "Managed bytes allocated on the synchronous SQL execution thread.");
    private static readonly Histogram<double> LockWait = Meter.CreateHistogram<double>(
        "sonnetdb.sql.lock.wait.duration", unit: "ms", description: "Critical table and KV lock wait attributed to a SQL statement.");
    private static readonly Histogram<long> LogicalReads = Meter.CreateHistogram<long>(
        "sonnetdb.sql.logical.reads", unit: "{read}", description: "Logical row reads attributed to a SQL statement.");
    private static readonly Histogram<long> LogicalWrites = Meter.CreateHistogram<long>(
        "sonnetdb.sql.logical.writes", unit: "{write}", description: "Logical row writes attributed to a SQL statement.");
    private static readonly Histogram<long> PhysicalReads = Meter.CreateHistogram<long>(
        "sonnetdb.sql.physical.reads", unit: "{read}", description: "Physical segment reads attributed to a SQL statement.");
    private static readonly Histogram<long> PhysicalReadBytes = Meter.CreateHistogram<long>(
        "sonnetdb.sql.physical.read.bytes", unit: "By", description: "Physical segment payload bytes attributed to a SQL statement.");
    private static readonly Histogram<long> PhysicalWrites = Meter.CreateHistogram<long>(
        "sonnetdb.sql.physical.writes", unit: "{write}", description: "Physical WAL record writes attributed to a SQL statement.");
    private static readonly Histogram<long> PhysicalWriteBytes = Meter.CreateHistogram<long>(
        "sonnetdb.sql.physical.write.bytes", unit: "By", description: "Physical WAL record bytes attributed to a SQL statement.");
    private static readonly Histogram<double> ExecutionDuration = Meter.CreateHistogram<double>(
        "sonnetdb.sql.execution.duration", unit: "ms", description: "Core SQL execution duration without HTTP response encoding.");
    private static readonly Histogram<double> WalFsyncDuration = Meter.CreateHistogram<double>(
        "sonnetdb.sql.wal.fsync.duration", unit: "ms", description: "WAL fsync duration attributed to a SQL statement.");
    private static readonly Histogram<long> WalFsyncCount = Meter.CreateHistogram<long>(
        "sonnetdb.sql.wal.fsync.count", unit: "{fsync}", description: "WAL fsync count attributed to a SQL statement.");
    private static readonly Histogram<long> Gen0Collections = Meter.CreateHistogram<long>(
        "sonnetdb.sql.gc.gen0.collections", unit: "{collection}", description: "Gen0 collections observed during a SQL statement.");
    private static readonly Histogram<long> Gen1Collections = Meter.CreateHistogram<long>(
        "sonnetdb.sql.gc.gen1.collections", unit: "{collection}", description: "Gen1 collections observed during a SQL statement.");
    private static readonly Histogram<long> Gen2Collections = Meter.CreateHistogram<long>(
        "sonnetdb.sql.gc.gen2.collections", unit: "{collection}", description: "Gen2 collections observed during a SQL statement.");

    /// <summary>记录一条完成语句，标签仅使用封闭的 outcome/access-path/fallback 集合。</summary>
    internal static void Record(SlowQueryDiagnosticEntry entry)
    {
        var tags = new TagList
        {
            { "outcome", entry.Failed ? "error" : "ok" },
            { "access.path", entry.AccessPath ?? "unknown" },
            { "fallback.reason", entry.FallbackReason ?? "none" },
        };
        Queries.Add(1, tags);
        Duration.Record(entry.ElapsedMs, tags);
        QueueWait.Record(entry.QueueWaitMs, tags);
        CandidateRows.Record(entry.CandidateRows, tags);
        ExaminedRows.Record(entry.ExaminedRows, tags);
        ReturnedRows.Record(entry.RowCount, tags);
        if (entry.AllocatedBytes >= 0)
            AllocatedBytes.Record(entry.AllocatedBytes, tags);
        LockWait.Record(entry.TableLockWaitMs + entry.KvLockWaitMs, tags);
        LogicalReads.Record(entry.LogicalReads, tags);
        LogicalWrites.Record(entry.LogicalWrites, tags);
        PhysicalReads.Record(entry.PhysicalReads, tags);
        PhysicalReadBytes.Record(entry.PhysicalReadBytes, tags);
        PhysicalWrites.Record(entry.PhysicalWrites, tags);
        PhysicalWriteBytes.Record(entry.PhysicalWriteBytes, tags);
        ExecutionDuration.Record(entry.ExecutionElapsedMs, tags);
        WalFsyncDuration.Record(entry.WalFsyncMs, tags);
        WalFsyncCount.Record(entry.WalFsyncCount, tags);
        Gen0Collections.Record(entry.Gen0Collections, tags);
        Gen1Collections.Record(entry.Gen1Collections, tags);
        Gen2Collections.Record(entry.Gen2Collections, tags);
    }
}
