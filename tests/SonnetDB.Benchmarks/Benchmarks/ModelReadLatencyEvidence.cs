using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SonnetDB.Benchmarks.Benchmarks;

/// <summary>KV、Document 与 Object Storage 原生读取的请求级尾延迟证据 runner。</summary>
internal static class ModelReadLatencyEvidenceRunner
{
    private const int QuickWarmupCount = 32;
    private const int QuickSampleCount = 256;
    private const int FullWarmupCount = 200;
    private const int FullSampleCount = 5_000;

    /// <summary>顺序运行六种原生读取请求并生成可重复的 JSON 与 Markdown artifact。</summary>
    public static async Task<ModelReadLatencyEvidenceReport> RunAsync(
        string outputDirectory,
        bool quick,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        int warmupCount = quick ? QuickWarmupCount : FullWarmupCount;
        int sampleCount = quick ? QuickSampleCount : FullSampleCount;
        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
        var operations = new List<ModelReadOperationEvidence>(6);

        var kv = new KvModelBenchmark();
        try
        {
            kv.Setup();
            operations.Add(MeasureSync(
                "kv",
                "point_read",
                warmupCount,
                sampleCount,
                static (fixture, ordinal) => fixture.ReadPointOnce(),
                kv,
                cancellationToken));
            operations.Add(MeasureSync(
                "kv",
                "get_many_256",
                warmupCount,
                sampleCount,
                static (fixture, ordinal) => fixture.ReadBatchOnce(),
                kv,
                cancellationToken));
        }
        finally
        {
            kv.Cleanup();
        }

        var document = new DocumentModelBenchmark();
        try
        {
            document.Setup();
            operations.Add(MeasureSync(
                "document",
                "id_read",
                warmupCount,
                sampleCount,
                static (fixture, ordinal) => fixture.ReadByIdOnce(),
                document,
                cancellationToken));
            operations.Add(MeasureSync(
                "document",
                "indexed_json_path_query",
                warmupCount,
                sampleCount,
                static (fixture, ordinal) => fixture.ReadIndexedQueryOnce(ordinal),
                document,
                cancellationToken));
        }
        finally
        {
            document.Cleanup();
        }

        var objects = new ObjectStorageModelBenchmark();
        try
        {
            await objects.Setup().WaitAsync(cancellationToken).ConfigureAwait(false);
            operations.Add(await MeasureAsync(
                "object_storage",
                "full_read_64k",
                warmupCount,
                sampleCount,
                static (fixture, ordinal, token) => fixture.ReadFullObjectOnceAsync(ordinal, token),
                objects,
                cancellationToken).ConfigureAwait(false));
            operations.Add(await MeasureAsync(
                "object_storage",
                "range_read_4k",
                warmupCount,
                sampleCount,
                static (fixture, ordinal, token) => fixture.ReadRangeOnceAsync(ordinal, token),
                objects,
                cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            objects.Cleanup();
        }

        cancellationToken.ThrowIfCancellationRequested();
        var report = new ModelReadLatencyEvidenceReport(
            "sonnetdb-model-read-latency-v1",
            ResolveBuildIdentity(),
            quick ? "local_smoke" : "local_full",
            startedUtc,
            DateTimeOffset.UtcNow,
            new ModelReadLatencyEnvironment(
                RuntimeInformation.FrameworkDescription,
                RuntimeInformation.OSDescription,
                RuntimeInformation.ProcessArchitecture.ToString(),
                Environment.ProcessorCount,
                GCSettingsDescription()),
            warmupCount,
            sampleCount,
            "nearest_rank",
            operations,
            "PASS",
            [
                "每个计时样本恰好包含一次原生 API 请求；不把 BenchmarkDotNet 的迭代均值当作请求尾延迟。",
                "本报告是单线程、热本地嵌入式读取 smoke，不覆盖服务端排队、并发上限、冷缓存或物理 I/O 尾延迟。",
                "固定 x64/ARM64、Native AOT、现场同语料和生产发布门禁必须使用独立环境报告。",
            ]);

        string fullOutputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(fullOutputDirectory);
        string json = JsonSerializer.Serialize(
            report,
            ModelReadLatencyEvidenceJsonContext.Default.ModelReadLatencyEvidenceReport);
        await File.WriteAllTextAsync(
            Path.Combine(fullOutputDirectory, "model-read-latency.json"),
            json,
            Encoding.UTF8,
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(fullOutputDirectory, "model-read-latency.md"),
            BuildMarkdown(report),
            Encoding.UTF8,
            cancellationToken).ConfigureAwait(false);
        return report;
    }

    /// <summary>采集同步请求的逐次耗时与当前线程分配，样本数和预热次数均为硬上限。</summary>
    private static ModelReadOperationEvidence MeasureSync<TFixture>(
        string domain,
        string operation,
        int warmupCount,
        int sampleCount,
        Func<TFixture, int, long> action,
        TFixture fixture,
        CancellationToken cancellationToken)
    {
        for (int index = 0; index < warmupCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = action(fixture, index);
        }

        var samples = new ModelReadLatencySample[sampleCount];
        long checksum = 0;
        long totalTicks = 0;
        for (int index = 0; index < samples.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            long value = action(fixture, index);
            long elapsedTicks = Stopwatch.GetTimestamp() - started;
            long allocatedBytes = Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
            checksum = unchecked(checksum + value);
            totalTicks = checked(totalTicks + elapsedTicks);
            samples[index] = new ModelReadLatencySample(ToMilliseconds(elapsedTicks), allocatedBytes);
        }

        return Summarize(domain, operation, checksum, totalTicks, samples);
    }

    /// <summary>采集异步请求的逐次耗时；发生线程切换时不伪造当前线程分配值。</summary>
    private static async Task<ModelReadOperationEvidence> MeasureAsync<TFixture>(
        string domain,
        string operation,
        int warmupCount,
        int sampleCount,
        Func<TFixture, int, CancellationToken, ValueTask<long>> action,
        TFixture fixture,
        CancellationToken cancellationToken)
    {
        for (int index = 0; index < warmupCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = await action(fixture, index, cancellationToken).ConfigureAwait(false);
        }

        var samples = new ModelReadLatencySample[sampleCount];
        long checksum = 0;
        long totalTicks = 0;
        for (int index = 0; index < samples.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int threadBefore = Environment.CurrentManagedThreadId;
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            long value = await action(fixture, index, cancellationToken).ConfigureAwait(false);
            long elapsedTicks = Stopwatch.GetTimestamp() - started;
            long allocatedBytes = Environment.CurrentManagedThreadId == threadBefore
                ? Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore)
                : -1;
            checksum = unchecked(checksum + value);
            totalTicks = checked(totalTicks + elapsedTicks);
            samples[index] = new ModelReadLatencySample(ToMilliseconds(elapsedTicks), allocatedBytes);
        }

        return Summarize(domain, operation, checksum, totalTicks, samples);
    }

    /// <summary>按 nearest-rank 聚合原始请求样本，同时保留原始数组供离线复算。</summary>
    private static ModelReadOperationEvidence Summarize(
        string domain,
        string operation,
        long checksum,
        long totalTicks,
        IReadOnlyList<ModelReadLatencySample> samples)
    {
        double[] elapsed = samples.Select(static sample => sample.ElapsedMilliseconds).Order().ToArray();
        long[] allocated = samples
            .Where(static sample => sample.AllocatedBytes >= 0)
            .Select(static sample => sample.AllocatedBytes)
            .Order()
            .ToArray();
        double totalSeconds = totalTicks / (double)Stopwatch.Frequency;
        return new ModelReadOperationEvidence(
            domain,
            operation,
            samples.Count,
            checksum,
            NearestRank(elapsed, 0.50),
            NearestRank(elapsed, 0.95),
            NearestRank(elapsed, 0.99),
            elapsed[^1],
            totalSeconds <= 0 ? 0 : samples.Count / totalSeconds,
            NearestRank(allocated, 0.50),
            NearestRank(allocated, 0.95),
            NearestRank(allocated, 0.99),
            samples.Count - allocated.Length,
            samples);
    }

    /// <summary>计算已排序 double 样本的 nearest-rank 分位数。</summary>
    internal static double NearestRank(IReadOnlyList<double> sorted, double percentile)
    {
        ArgumentNullException.ThrowIfNull(sorted);
        if (sorted.Count == 0)
            throw new ArgumentException("分位数样本不能为空。", nameof(sorted));
        if (percentile is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(percentile));
        int index = Math.Clamp((int)Math.Ceiling(percentile * sorted.Count) - 1, 0, sorted.Count - 1);
        return sorted[index];
    }

    /// <summary>计算已排序 long 样本的 nearest-rank 分位数；无可用样本时返回 -1。</summary>
    internal static long NearestRank(IReadOnlyList<long> sorted, double percentile)
    {
        ArgumentNullException.ThrowIfNull(sorted);
        if (sorted.Count == 0)
            return -1;
        if (percentile is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(percentile));
        int index = Math.Clamp((int)Math.Ceiling(percentile * sorted.Count) - 1, 0, sorted.Count - 1);
        return sorted[index];
    }

    /// <summary>将 Stopwatch tick 转为毫秒，不经过 DateTime 墙钟。</summary>
    private static double ToMilliseconds(long ticks) => ticks * 1_000d / Stopwatch.Frequency;

    /// <summary>从程序集信息记录构建身份；dirty 状态由外层系统性能报告单独冻结。</summary>
    private static string ResolveBuildIdentity()
        => typeof(ModelReadLatencyEvidenceRunner).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            ?? "unknown";

    /// <summary>记录当前 GC 运行模式，避免把不同 GC 配置的样本直接混用。</summary>
    private static string GCSettingsDescription()
        => $"server={System.Runtime.GCSettings.IsServerGC};latency={System.Runtime.GCSettings.LatencyMode}";

    /// <summary>构造便于审阅的 Markdown 摘要，原始请求样本保留在 JSON。</summary>
    private static string BuildMarkdown(ModelReadLatencyEvidenceReport report)
    {
        var text = new StringBuilder();
        text.AppendLine("# SonnetDB Model Read Request Latency");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"- Schema: `{report.Schema}`");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Build: `{report.BuildIdentity}`");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Mode: `{report.Mode}`");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Status: `{report.Status}`");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Samples per operation: `{report.SampleCount:N0}`");
        text.AppendLine();
        text.AppendLine("| Domain | Request | P50 ms | P95 ms | P99 ms | Max ms | req/s | Alloc P95 | Unknown alloc |");
        text.AppendLine("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (ModelReadOperationEvidence operation in report.Operations)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"| {operation.Domain} | {operation.Operation} | {operation.P50Milliseconds:F6} | "
                + $"{operation.P95Milliseconds:F6} | {operation.P99Milliseconds:F6} | "
                + $"{operation.MaxMilliseconds:F6} | {operation.RequestsPerSecond:F2} | "
                + $"{operation.AllocatedBytesP95:N0} | {operation.UnknownAllocationSamples:N0} |");
        }
        text.AppendLine();
        text.AppendLine("## Boundaries");
        text.AppendLine();
        foreach (string limitation in report.Limitations)
            text.AppendLine(CultureInfo.InvariantCulture, $"- {limitation}");
        return text.ToString();
    }
}

/// <summary>一次原生读取请求的原始耗时与分配样本。</summary>
internal sealed record ModelReadLatencySample(double ElapsedMilliseconds, long AllocatedBytes);

/// <summary>一种原生读取请求的 nearest-rank 汇总及可复算原始样本。</summary>
internal sealed record ModelReadOperationEvidence(
    string Domain,
    string Operation,
    int SampleCount,
    long Checksum,
    double P50Milliseconds,
    double P95Milliseconds,
    double P99Milliseconds,
    double MaxMilliseconds,
    double RequestsPerSecond,
    long AllocatedBytesP50,
    long AllocatedBytesP95,
    long AllocatedBytesP99,
    int UnknownAllocationSamples,
    IReadOnlyList<ModelReadLatencySample> Samples);

/// <summary>请求级尾延迟运行环境。</summary>
internal sealed record ModelReadLatencyEnvironment(
    string Framework,
    string OperatingSystem,
    string Architecture,
    int ProcessorCount,
    string GcMode);

/// <summary>KV、Document 与 Object Storage 原生读取的请求级证据报告。</summary>
internal sealed record ModelReadLatencyEvidenceReport(
    string Schema,
    string BuildIdentity,
    string Mode,
    DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc,
    ModelReadLatencyEnvironment Environment,
    int WarmupCount,
    int SampleCount,
    string PercentileMethod,
    IReadOnlyList<ModelReadOperationEvidence> Operations,
    string Status,
    IReadOnlyList<string> Limitations);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(ModelReadLatencyEvidenceReport))]
internal sealed partial class ModelReadLatencyEvidenceJsonContext : JsonSerializerContext;
