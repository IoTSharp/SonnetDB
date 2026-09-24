using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SonnetDB.Engine;
using SonnetDB.Kv;
using SonnetDB.ObjectStorage;

namespace SonnetDB.Benchmarks.Benchmarks;

/// <summary>M36 #323 对象分页本地预检与现场容量报告的边界状态。</summary>
public static class ObjectM36ValidationStatus
{
    /// <summary>本地受控工作负载已完成。</summary>
    public const string Pass = "PASS";

    /// <summary>尚未在固定目标硬件执行。</summary>
    public const string NotReady = "NOT_READY";

    /// <summary>尚未执行容量测量。</summary>
    public const string NotRun = "NOT_RUN";

    /// <summary>现场验证后置，不能作为发布通过。</summary>
    public const string Deferred = "DEFERRED";
}

/// <summary>M36 #323 对象高变更率分页的本地前置 runner。</summary>
public static class ObjectM36ValidationEvidenceRunner
{
    private const string BucketName = "m36-object-validation";
    private const int QuickFixtureObjectCount = 256;
    private const int FullFixtureObjectCount = 8_192;
    private const int QuickSampleCount = 64;
    private const int FullSampleCount = 1_024;
    private const int PageSize = 64;
    private static readonly byte[] Payload = CreatePayload();

    /// <summary>运行本机预检并写出可复核的 JSON 与 Markdown 报告。</summary>
    /// <param name="outputDirectory">报告输出目录。</param>
    /// <param name="quick">是否运行缩规模预检；两种模式均不构成固定硬件容量证据。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>本机预检报告。</returns>
    public static Task<ObjectM36ValidationReport> RunAsync(
        string outputDirectory,
        bool quick,
        CancellationToken cancellationToken = default)
        => RunAsync(
            outputDirectory,
            quick ? QuickFixtureObjectCount : FullFixtureObjectCount,
            quick ? QuickSampleCount : FullSampleCount,
            quick ? "local_precheck_quick" : "local_precheck_full",
            cancellationToken);

    /// <summary>以受限确定性规模运行报告合同测试；不构成容量或现场验收。</summary>
    internal static Task<ObjectM36ValidationReport> RunContractAsync(
        string outputDirectory,
        int fixtureObjectCount,
        int sampleCount,
        CancellationToken cancellationToken = default)
        => RunAsync(outputDirectory, fixtureObjectCount, sampleCount, "contract_smoke", cancellationToken);

    /// <summary>验证 JSON 报告是否保留本机预检与固定硬件验收之间的硬边界。</summary>
    /// <param name="reportPath">报告 JSON 路径。</param>
    /// <returns>验证结果与全部失败原因。</returns>
    public static ObjectM36ValidationVerification Verify(string reportPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportPath);
        var failures = new List<string>();
        ObjectM36ValidationReport? report;
        try
        {
            report = JsonSerializer.Deserialize(
                File.ReadAllText(reportPath, Encoding.UTF8),
                ObjectM36ValidationEvidenceJsonContext.Default.ObjectM36ValidationReport);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return new ObjectM36ValidationVerification(false, [exception.Message]);
        }

        if (report is null)
            return new ObjectM36ValidationVerification(false, ["报告不能为空。"]);

        if (!string.Equals(report.Schema, "m36-object-validation-v1", StringComparison.Ordinal))
            failures.Add("schema 必须为 m36-object-validation-v1。");
        if (!string.Equals(report.Issue, "#323", StringComparison.Ordinal))
            failures.Add("issue 必须为 #323。");
        if (!string.Equals(report.LocalPrecheck, ObjectM36ValidationStatus.Pass, StringComparison.Ordinal))
            failures.Add("本地预检未通过。");
        if (!string.Equals(report.FixedHardware, ObjectM36ValidationStatus.NotReady, StringComparison.Ordinal))
            failures.Add("本机报告不得声明 fixedHardware 通过。");
        if (!string.Equals(report.Capacity, ObjectM36ValidationStatus.NotRun, StringComparison.Ordinal))
            failures.Add("本机报告不得声明 capacity 已运行或通过。");
        if (!string.Equals(report.ReleaseDecision, ObjectM36ValidationStatus.Deferred, StringComparison.Ordinal))
            failures.Add("本机报告的 releaseDecision 必须为 DEFERRED。");
        if (!string.Equals(report.TransferValidation, ObjectM36ValidationStatus.Deferred, StringComparison.Ordinal))
            failures.Add("传输恢复与校验和验证必须保持 DEFERRED。");
        if (report.Pagination.FixtureObjectCount <= 0 || report.Pagination.SampleCount <= 0)
            failures.Add("分页 fixture 与样本数必须为正数。");
        if (report.Pagination.Samples.Count != report.Pagination.SampleCount)
            failures.Add("分页原始样本数与汇总样本数不一致。");
        if (report.Pagination.Samples.Any(static sample => sample.ElapsedMilliseconds < 0 || sample.ReturnedObjects <= 0))
            failures.Add("分页样本含有无效耗时或空结果。");
        if (report.Pagination.Mutations < report.Pagination.SampleCount)
            failures.Add("每个分页样本至少应有一次交错变更。");
        return new ObjectM36ValidationVerification(failures.Count == 0, failures);
    }

    private static async Task<ObjectM36ValidationReport> RunAsync(
        string outputDirectory,
        int fixtureObjectCount,
        int sampleCount,
        string mode,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        if (fixtureObjectCount < PageSize)
            throw new ArgumentOutOfRangeException(nameof(fixtureObjectCount), "对象 fixture 至少必须包含一个完整分页。");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleCount);

        string databaseRoot = Path.Combine(Path.GetTempPath(), "sndb-m36-object-validation-" + Guid.NewGuid().ToString("N"));
        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
        ObjectM36PaginationEvidence pagination;
        try
        {
            using var database = Tsdb.Open(CreateOptions(databaseRoot));
            var store = new SndbObjectStore(database);
            store.CreateBucket(BucketName, "M36 #323 local validation fixture");
            await SeedAsync(store, fixtureObjectCount, cancellationToken).ConfigureAwait(false);
            pagination = await MeasureInterleavedPaginationAsync(store, fixtureObjectCount, sampleCount, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            TryDeleteFixtureDirectory(databaseRoot);
        }

        var report = new ObjectM36ValidationReport(
            "m36-object-validation-v1",
            "#323",
            mode,
            startedUtc,
            DateTimeOffset.UtcNow,
            new ObjectM36ValidationEnvironment(
                RuntimeInformation.FrameworkDescription,
                RuntimeInformation.OSDescription,
                RuntimeInformation.ProcessArchitecture.ToString(),
                Environment.ProcessorCount,
                System.Runtime.GCSettings.IsServerGC),
            pagination,
            ObjectM36ValidationStatus.Pass,
            ObjectM36ValidationStatus.NotReady,
            ObjectM36ValidationStatus.NotRun,
            ObjectM36ValidationStatus.Deferred,
            ObjectM36ValidationStatus.Deferred,
            [
                "本 runner 只执行单进程嵌入式交错写入、删除与 ListObjects 分页预检；不测 Server、SDK、CLI 或网络传输。",
                "无论模式、环境变量或本机硬件配置如何，本 runner 均固定输出 fixedHardware=NOT_READY、capacity=NOT_RUN 和 releaseDecision=DEFERRED。",
                "正式 #323 容量证据必须在受保护的固定目标硬件上，以冻结版本、明确的高变更率/对象容量合同和原始 artifact 独立采集并审阅。",
                "文件传输的 multipart/resume/checksum/并发恢复属于 #322 及 #323 文件流现场旅程，本预检不把对象元数据分页替代为传输容量证据。",
            ]);

        WriteReport(outputDirectory, report);
        return report;
    }

    private static async Task SeedAsync(SndbObjectStore store, int fixtureObjectCount, CancellationToken cancellationToken)
    {
        for (int index = 0; index < fixtureObjectCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await PutAsync(store, KeyFor(index), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<ObjectM36PaginationEvidence> MeasureInterleavedPaginationAsync(
        SndbObjectStore store,
        int fixtureObjectCount,
        int sampleCount,
        CancellationToken cancellationToken)
    {
        var samples = new ObjectM36PageSample[sampleCount];
        int writes = 0;
        int deletes = 0;
        long checksum = 0;
        long started = Stopwatch.GetTimestamp();
        for (int index = 0; index < samples.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string key = KeyFor(index % fixtureObjectCount);
            await PutAsync(store, key, cancellationToken).ConfigureAwait(false);
            writes++;
            if (index % 8 == 0)
            {
                _ = store.DeleteObject(BucketName, key);
                deletes++;
                await PutAsync(store, key, cancellationToken).ConfigureAwait(false);
                writes++;
            }

            long allocationBefore = GC.GetAllocatedBytesForCurrentThread();
            long pageStarted = Stopwatch.GetTimestamp();
            SndbObjectListResult page = store.ListObjects(BucketName, "objects/", PageSize, null, null, cancellationToken);
            double elapsedMilliseconds = ToMilliseconds(Stopwatch.GetTimestamp() - pageStarted);
            long allocatedBytes = Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - allocationBefore);
            ValidatePage(page, fixtureObjectCount);
            checksum = unchecked(checksum + page.Objects.Sum(static item => item.SizeBytes));
            samples[index] = new ObjectM36PageSample(elapsedMilliseconds, allocatedBytes, page.Objects.Count);
        }

        double elapsedSeconds = (Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency;
        double[] elapsed = samples.Select(static sample => sample.ElapsedMilliseconds).Order().ToArray();
        long[] allocated = samples.Select(static sample => sample.AllocatedBytes).Order().ToArray();
        return new ObjectM36PaginationEvidence(
            fixtureObjectCount,
            PageSize,
            sampleCount,
            writes + deletes,
            writes,
            deletes,
            checksum,
            NearestRank(elapsed, 0.50),
            NearestRank(elapsed, 0.95),
            NearestRank(elapsed, 0.99),
            NearestRank(allocated, 0.95),
            elapsedSeconds <= 0 ? 0 : (writes + deletes) / elapsedSeconds,
            samples);
    }

    private static async Task PutAsync(SndbObjectStore store, string key, CancellationToken cancellationToken)
    {
        await using var content = new MemoryStream(Payload, writable: false);
        _ = await store.PutObjectAsync(
            BucketName,
            key,
            content,
            contentType: "application/octet-stream",
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static void ValidatePage(SndbObjectListResult page, int fixtureObjectCount)
    {
        if (page.Objects.Count != Math.Min(PageSize, fixtureObjectCount))
            throw new InvalidDataException("高变更率分页未返回预期对象数。");
        if (page.Objects.Zip(page.Objects.Skip(1), static (left, right) => StringComparer.Ordinal.Compare(left.Key, right.Key) < 0)
            .Any(static ordered => !ordered))
        {
            throw new InvalidDataException("高变更率分页未保持 ordinal key 顺序。");
        }
    }

    private static TsdbOptions CreateOptions(string rootDirectory)
        => new()
        {
            RootDirectory = rootDirectory,
            Kv = KvOptions.Default with
            {
                AutoCheckpointEnabled = false,
                SyncWalOnEveryWrite = false,
                ExpirerEnabled = false,
                CleanupEnabled = false,
            },
        };

    private static string KeyFor(int index) => $"objects/{index:D8}.bin";

    private static byte[] CreatePayload()
    {
        var payload = new byte[4 * 1024];
        new Random(323).NextBytes(payload);
        return payload;
    }

    private static double ToMilliseconds(long ticks) => ticks * 1_000d / Stopwatch.Frequency;

    private static double NearestRank(IReadOnlyList<double> sorted, double percentile)
    {
        int index = Math.Clamp((int)Math.Ceiling(percentile * sorted.Count) - 1, 0, sorted.Count - 1);
        return sorted[index];
    }

    private static long NearestRank(IReadOnlyList<long> sorted, double percentile)
    {
        int index = Math.Clamp((int)Math.Ceiling(percentile * sorted.Count) - 1, 0, sorted.Count - 1);
        return sorted[index];
    }

    private static void WriteReport(string outputDirectory, ObjectM36ValidationReport report)
    {
        string fullOutputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(fullOutputDirectory);
        File.WriteAllText(
            Path.Combine(fullOutputDirectory, "m36-object-validation.json"),
            JsonSerializer.Serialize(report, ObjectM36ValidationEvidenceJsonContext.Default.ObjectM36ValidationReport),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.WriteAllText(
            Path.Combine(fullOutputDirectory, "m36-object-validation.md"),
            BuildMarkdown(report),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string BuildMarkdown(ObjectM36ValidationReport report)
    {
        var text = new StringBuilder();
        text.AppendLine("# M36 #323 Object Validation Precheck");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"- Schema: `{report.Schema}`");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Mode: `{report.Mode}`");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Local precheck: `{report.LocalPrecheck}`");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Fixed hardware: `{report.FixedHardware}`");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Capacity: `{report.Capacity}`");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Transfer validation: `{report.TransferValidation}`");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Release decision: `{report.ReleaseDecision}`");
        text.AppendLine();
        text.AppendLine("| Fixture objects | Samples | Mutations | Writes | Deletes | Page P50 ms | Page P95 ms | Page P99 ms | Alloc P95 bytes |");
        text.AppendLine("| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        text.AppendLine(CultureInfo.InvariantCulture,
            $"| {report.Pagination.FixtureObjectCount:N0} | {report.Pagination.SampleCount:N0} | {report.Pagination.Mutations:N0} | "
            + $"{report.Pagination.Writes:N0} | {report.Pagination.Deletes:N0} | {report.Pagination.PageP50Milliseconds:F6} | "
            + $"{report.Pagination.PageP95Milliseconds:F6} | {report.Pagination.PageP99Milliseconds:F6} | {report.Pagination.AllocatedBytesP95:N0} |");
        text.AppendLine();
        text.AppendLine("## Boundaries");
        text.AppendLine();
        foreach (string limitation in report.Limitations)
            text.AppendLine(CultureInfo.InvariantCulture, $"- {limitation}");
        return text.ToString();
    }

    private static void TryDeleteFixtureDirectory(string databaseRoot)
    {
        if (!Directory.Exists(databaseRoot))
            return;

        string temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        string fixtureRoot = Path.GetFullPath(databaseRoot);
        string relative = Path.GetRelativePath(temporaryRoot, fixtureRoot);
        if (relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            throw new InvalidOperationException("拒绝删除系统临时目录之外的对象验证 fixture。");
        }

        Directory.Delete(fixtureRoot, recursive: true);
    }
}

/// <summary>M36 #323 对象预检报告。</summary>
public sealed record ObjectM36ValidationReport(
    string Schema,
    string Issue,
    string Mode,
    DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc,
    ObjectM36ValidationEnvironment Environment,
    ObjectM36PaginationEvidence Pagination,
    string LocalPrecheck,
    string FixedHardware,
    string Capacity,
    string TransferValidation,
    string ReleaseDecision,
    IReadOnlyList<string> Limitations);

/// <summary>M36 #323 运行环境摘要，不记录机器名或数据内容。</summary>
public sealed record ObjectM36ValidationEnvironment(
    string Framework,
    string OperatingSystem,
    string Architecture,
    int ProcessorCount,
    bool ServerGc);

/// <summary>高变更率分页的原始样本与可复算汇总。</summary>
public sealed record ObjectM36PaginationEvidence(
    int FixtureObjectCount,
    int PageSize,
    int SampleCount,
    int Mutations,
    int Writes,
    int Deletes,
    long Checksum,
    double PageP50Milliseconds,
    double PageP95Milliseconds,
    double PageP99Milliseconds,
    long AllocatedBytesP95,
    double MutationsPerSecond,
    IReadOnlyList<ObjectM36PageSample> Samples);

/// <summary>一次分页请求的原始时间、分配和返回数量。</summary>
public sealed record ObjectM36PageSample(double ElapsedMilliseconds, long AllocatedBytes, int ReturnedObjects);

/// <summary>对象预检报告 verifier 的结果。</summary>
public sealed record ObjectM36ValidationVerification(bool IsValid, IReadOnlyList<string> Failures);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(ObjectM36ValidationReport))]
internal sealed partial class ObjectM36ValidationEvidenceJsonContext : JsonSerializerContext;
