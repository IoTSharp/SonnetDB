using System.Diagnostics;

namespace SonnetDB.Sql.Execution;

/// <summary>收集单条时序 INSERT 的阶段耗时，仅在请求启用诊断时分配。</summary>
internal sealed class SqlMeasurementInsertTiming
{
    internal bool IsMeasurement;
    internal long ParseTicks;
    internal long ParameterBindTicks;
    internal long NameBindTicks;
    internal long ExecutionTicks;
    internal long EngineTicks;
    internal long SchemaLockTicks;
    internal long WriteLockTicks;
    internal long SchemaTicks;
    internal long WalAppendTicks;
    internal long WalFlushToOsTicks;
    internal long WalFsyncWaitTicks;
    internal long BackpressureTicks;
    internal long ReceiptTicks;
    internal long QueryDiagnosticsTicks;

    internal static long Start(SqlMeasurementInsertTiming? timing)
        => timing is null ? 0 : Stopwatch.GetTimestamp();

    internal static void AddElapsed(ref long totalTicks, long start)
        => totalTicks += Stopwatch.GetTimestamp() - start;
}
