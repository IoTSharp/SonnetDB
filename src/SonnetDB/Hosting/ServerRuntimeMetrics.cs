using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;
using SonnetDB.Engine;

namespace SonnetDB.Hosting;

internal sealed class ServerRuntimeMetrics
{
    internal const string MeterName = "SonnetDB.Server.Runtime";
    private const string FlushFaults = "sonnetdb.timeseries.flush.recovery.faults";
    private const string GcHeapSize = "sonnetdb.process.gc.heap.size";
    private const string GcCommitted = "sonnetdb.process.gc.committed";
    private const string WorkingSet = "sonnetdb.process.working.set";
    private const string FlushFaultsHelp = "First time-series flush recovery fault per opened database, grouped by exception category.";
    private const string GcHeapSizeHelp = "Managed heap size at the most recent garbage collection.";
    private const string GcCommittedHelp = "Committed garbage collector memory at the most recent garbage collection.";
    private const string WorkingSetHelp = "Current process working set.";

    private static readonly string[] Categories =
    [
        "out_of_memory", "io", "invalid_data", "access_denied", "disposed", "cancelled", "other",
    ];

    private readonly long[] _flushFaults = new long[Categories.Length];

    public ServerRuntimeMetrics(IMeterFactory meterFactory)
    {
        Meter = meterFactory.Create(MeterName);
        Meter meter = Meter;
        meter.CreateObservableCounter<long>(FlushFaults, ObserveFlushFaults, description: FlushFaultsHelp);
        meter.CreateObservableGauge(GcHeapSize, static () => GC.GetGCMemoryInfo().HeapSizeBytes, "By", GcHeapSizeHelp);
        meter.CreateObservableGauge(GcCommitted, static () => GC.GetGCMemoryInfo().TotalCommittedBytes, "By", GcCommittedHelp);
        meter.CreateObservableGauge(WorkingSet, static () => Environment.WorkingSet, "By", WorkingSetHelp);
    }

    internal Meter Meter { get; }

    internal void Observe(Tsdb database)
    {
        int reported = 0;
        database.DiagnosticEvent += (_, diagnostic) =>
        {
            if (diagnostic.Operation != "FlushPump.Flush" || diagnostic.Exception is not { } error
                || Interlocked.Exchange(ref reported, 1) != 0)
                return;

            Interlocked.Increment(ref _flushFaults[GetExceptionCategory(error)]);
        };
    }

    private IEnumerable<Measurement<long>> ObserveFlushFaults()
    {
        for (int i = 0; i < Categories.Length; i++)
            yield return new Measurement<long>(Interlocked.Read(ref _flushFaults[i]),
                new KeyValuePair<string, object?>("exception_category", Categories[i]));
    }

    internal void AppendPrometheus(StringBuilder builder)
    {
        const string counter = "sonnetdb_timeseries_flush_recovery_faults_total";
        builder.Append("# HELP ").Append(counter).Append(' ').AppendLine(FlushFaultsHelp);
        builder.Append("# TYPE ").Append(counter).AppendLine(" counter");
        for (int i = 0; i < Categories.Length; i++)
        {
            builder.Append(counter).Append("{exception_category=\"").Append(Categories[i]).Append("\"} ")
                .AppendLine(Interlocked.Read(ref _flushFaults[i]).ToString(CultureInfo.InvariantCulture));
        }

        GCMemoryInfo gc = GC.GetGCMemoryInfo();
        AppendGauge("sonnetdb_process_gc_heap_size_bytes", GcHeapSizeHelp, gc.HeapSizeBytes);
        AppendGauge("sonnetdb_process_gc_committed_bytes", GcCommittedHelp, gc.TotalCommittedBytes);
        AppendGauge("sonnetdb_process_working_set_bytes", WorkingSetHelp, Environment.WorkingSet);

        void AppendGauge(string name, string description, long value)
        {
            builder.Append("# HELP ").Append(name).Append(' ').AppendLine(description);
            builder.Append("# TYPE ").Append(name).AppendLine(" gauge");
            builder.Append(name).Append(' ').AppendLine(value.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static int GetExceptionCategory(Exception exception)
    {
        int category = Categories.Length - 1;
        for (int depth = 0; depth < 8; depth++)
        {
            category = exception switch
            {
                OutOfMemoryException => 0,
                InvalidDataException => 2,
                IOException => 1,
                UnauthorizedAccessException => 3,
                ObjectDisposedException => 4,
                OperationCanceledException => 5,
                _ => category,
            };
            if (exception.InnerException is not { } inner)
                break;
            exception = inner;
        }
        return category;
    }
}
