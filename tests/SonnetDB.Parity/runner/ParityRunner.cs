using SonnetDB.Parity.Adapters;
using SonnetDB.Parity.Adapters.ClickHouse;
using SonnetDB.Parity.Adapters.Influx;
using SonnetDB.Parity.Adapters.Meili;
using SonnetDB.Parity.Adapters.Minio;
using SonnetDB.Parity.Adapters.Nats;
using SonnetDB.Parity.Adapters.Postgres;
using SonnetDB.Parity.Adapters.Qdrant;
using SonnetDB.Parity.Adapters.Redis;
using SonnetDB.Parity.Adapters.SonnetDb;
using SonnetDB.Parity.Adapters.VictoriaMetrics;
using SonnetDB.Parity.Runner.Reporting;
using SonnetDB.Parity.Scenarios;
using SonnetDB.Parity.Scenarios.Analytics;
using SonnetDB.Parity.Scenarios.FullText;
using SonnetDB.Parity.Scenarios.Kv;
using SonnetDB.Parity.Scenarios.Mq;
using SonnetDB.Parity.Scenarios.Object;
using SonnetDB.Parity.Scenarios.Relational;
using SonnetDB.Parity.Scenarios.Tsdb;
using SonnetDB.Parity.Scenarios.Vector;
using Xunit;

namespace SonnetDB.Parity.Runner;

/// <summary>
/// Parity 关系型套件驱动。单进程内实例化 SonnetDB（嵌入式）与 Postgres（竞品），
/// 跑同一批关系型场景并输出 JSON / Markdown 差异报告。
/// </summary>
public sealed class ParityRunner
{
    /// <summary>关系型场景套件：SonnetDB 自检通过或结构化 SKIP，Postgres 可达时参与 diff。</summary>
    [Fact]
    public async Task RelationalSuite_SonnetDbMatchesPostgres()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = cts.Token;

        var scenarios = CreateRelationalScenarios();
        var runId = "relational-" + Guid.NewGuid().ToString("N")[..8];
        var startedAt = DateTimeOffset.UtcNow;
        var reportDir = ResolveReportDirectory(runId);
        var ctx = new ScenarioContext { RunId = runId, ReportDirectory = reportDir, Cancellation = ct };

        var reports = new List<ScenarioReport>();
        var failures = new List<string>();

        await using var sonnet = new SonnetDbAdapter();
        var postgresAvailable = await PostgresAdapter.TryConnectAsync(ct);

        foreach (var scenario in scenarios)
        {
            var sonnetResult = await RunBackendAsync(scenario, sonnet, ctx);
            var backends = new List<BackendOutcome>
            {
                ToOutcome(sonnet.BackendName, sonnetResult),
            };

            bool? withinTolerance = null;
            IReadOnlyList<string> differences = [];

            if (!sonnetResult.Pass)
                failures.Add($"SonnetDB 场景 {scenario.Name} 自检未通过。");

            if (postgresAvailable)
            {
                await using var pg = new PostgresAdapter();
                await pg.OpenAsync(ct);
                var pgResult = await RunBackendAsync(scenario, pg, ctx);

                backends.Add(ToOutcome(pg.BackendName, pgResult));
                if (!pgResult.Pass)
                    failures.Add($"Postgres 场景 {scenario.Name} 自检未通过。");

                if (sonnetResult.GapReason is null && pgResult.GapReason is null && sonnetResult.Pass && pgResult.Pass)
                {
                    var diff = DiffResults(sonnetResult, pgResult);
                    withinTolerance = diff.WithinTolerance;
                    differences = diff.Differences;
                    if (!diff.WithinTolerance)
                        failures.Add($"SonnetDB 与 Postgres 场景 {scenario.Name} 结果超出容差：" + string.Join("; ", diff.Differences));
                }
            }
            else
            {
                backends.Add(new BackendOutcome(
                    "postgres",
                    "skipped",
                    "postgres unreachable (compose 未启动或 PARITY_PG_* 未配置)",
                    0,
                    new Dictionary<string, object?>()));
            }

            reports.Add(new ScenarioReport(scenario.Name, withinTolerance, differences, backends));
        }

        var report = new ParityReport(
            runId,
            startedAt,
            reports,
            BuildCapabilityGaps(scenarios, reports, ["sonnetdb", "postgres"]),
            ["sonnetdb", "postgres"]);

        await JsonReporter.WriteAsync(report, reportDir);
        await MarkdownReporter.WriteAsync(report, reportDir);

        Assert.Empty(failures);
    }

    /// <summary>TSDB 场景套件：SonnetDB 对齐 InfluxDB 与 VictoriaMetrics。</summary>
    [Fact]
    public async Task TsdbSuite_SonnetDbMatchesInfluxDbAndVictoriaMetrics()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = cts.Token;

        var scenarios = CreateTsdbScenarios();
        var runId = "tsdb-" + Guid.NewGuid().ToString("N")[..8];
        var startedAt = DateTimeOffset.UtcNow;
        var reportDir = ResolveReportDirectory(runId);
        var ctx = new ScenarioContext { RunId = runId, ReportDirectory = reportDir, Cancellation = ct };
        var backends = new[] { "sonnetdb", "influxdb", "victoriametrics" };

        var reports = new List<ScenarioReport>();
        var failures = new List<string>();

        await using var sonnet = new SonnetDbAdapter();
        var influxAvailable = await InfluxAdapter.TryConnectAsync(ct);
        var victoriaAvailable = await VictoriaMetricsAdapter.TryConnectAsync(ct);

        foreach (var scenario in scenarios)
        {
            var sonnetResult = await RunBackendAsync(scenario, sonnet, ctx);
            var outcomes = new List<BackendOutcome> { ToOutcome(sonnet.BackendName, sonnetResult) };
            var differences = new List<string>();
            bool? withinTolerance = null;

            if (!sonnetResult.Pass)
                failures.Add($"SonnetDB TSDB 场景 {scenario.Name} 自检未通过。");

            await RunTsdbCompetitorAsync(
                scenario,
                ctx,
                sonnetResult,
                "influxdb",
                influxAvailable,
                static () => new InfluxAdapter(),
                outcomes,
                differences,
                failures,
                result => withinTolerance = MergeTolerance(withinTolerance, result));

            await RunTsdbCompetitorAsync(
                scenario,
                ctx,
                sonnetResult,
                "victoriametrics",
                victoriaAvailable,
                static () => new VictoriaMetricsAdapter(),
                outcomes,
                differences,
                failures,
                result => withinTolerance = MergeTolerance(withinTolerance, result));

            reports.Add(new ScenarioReport(scenario.Name, withinTolerance, differences, outcomes));
        }

        var report = new ParityReport(
            runId,
            startedAt,
            reports,
            BuildCapabilityGaps(scenarios, reports, backends),
            backends);

        await JsonReporter.WriteAsync(report, reportDir);
        await MarkdownReporter.WriteAsync(report, reportDir);

        Assert.Empty(failures);
    }

    /// <summary>KV 场景套件：SonnetDB 对齐 Redis。</summary>
    [Fact]
    public async Task KvSuite_SonnetDbMatchesRedis()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = cts.Token;

        var scenarios = CreateKvScenarios();
        var runId = "kv-" + Guid.NewGuid().ToString("N")[..8];
        var startedAt = DateTimeOffset.UtcNow;
        var reportDir = ResolveReportDirectory(runId);
        var ctx = new ScenarioContext { RunId = runId, ReportDirectory = reportDir, Cancellation = ct };
        var backends = new[] { "sonnetdb", "redis" };

        var reports = new List<ScenarioReport>();
        var failures = new List<string>();

        await using var sonnet = new SonnetDbAdapter();
        var redisAvailable = await RedisAdapter.TryConnectAsync(ct);

        foreach (var scenario in scenarios)
        {
            var sonnetResult = await RunBackendAsync(scenario, sonnet, ctx);
            var outcomes = new List<BackendOutcome> { ToOutcome(sonnet.BackendName, sonnetResult) };
            var differences = new List<string>();
            bool? withinTolerance = null;

            if (!sonnetResult.Pass)
                failures.Add($"SonnetDB KV 场景 {scenario.Name} 自检未通过。");

            await RunCompetitorAsync(
                scenario,
                ctx,
                sonnetResult,
                "redis",
                redisAvailable,
                static () => new RedisAdapter(),
                outcomes,
                differences,
                failures,
                result => withinTolerance = result,
                scenario.Tolerance);

            reports.Add(new ScenarioReport(scenario.Name, withinTolerance, differences, outcomes));
        }

        var report = new ParityReport(
            runId,
            startedAt,
            reports,
            BuildCapabilityGaps(scenarios, reports, backends),
            backends);

        await JsonReporter.WriteAsync(report, reportDir);
        await MarkdownReporter.WriteAsync(report, reportDir);

        Assert.Empty(failures);
    }

    /// <summary>向量场景套件：SonnetDB 对齐 Qdrant。</summary>
    [Fact]
    public async Task VectorSuite_SonnetDbMatchesQdrant()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = cts.Token;

        var scenarios = CreateVectorScenarios();
        var runId = "vector-" + Guid.NewGuid().ToString("N")[..8];
        var startedAt = DateTimeOffset.UtcNow;
        var reportDir = ResolveReportDirectory(runId);
        var ctx = new ScenarioContext { RunId = runId, ReportDirectory = reportDir, Cancellation = ct };
        var backends = new[] { "sonnetdb", "qdrant" };

        var reports = new List<ScenarioReport>();
        var failures = new List<string>();

        await using var sonnet = new SonnetDbAdapter();
        var qdrantAvailable = await QdrantAdapter.TryConnectAsync(ct);

        foreach (var scenario in scenarios)
        {
            var sonnetResult = await RunBackendAsync(scenario, sonnet, ctx);
            var outcomes = new List<BackendOutcome> { ToOutcome(sonnet.BackendName, sonnetResult) };
            var differences = new List<string>();
            bool? withinTolerance = null;

            if (!sonnetResult.Pass)
                failures.Add($"SonnetDB 向量场景 {scenario.Name} 自检未通过。");

            await RunCompetitorAsync(
                scenario,
                ctx,
                sonnetResult,
                "qdrant",
                qdrantAvailable,
                static () => new QdrantAdapter(),
                outcomes,
                differences,
                failures,
                result => withinTolerance = result,
                scenario.Tolerance);

            reports.Add(new ScenarioReport(scenario.Name, withinTolerance, differences, outcomes));
        }

        var report = new ParityReport(
            runId,
            startedAt,
            reports,
            BuildCapabilityGaps(scenarios, reports, backends),
            backends);

        await JsonReporter.WriteAsync(report, reportDir);
        await MarkdownReporter.WriteAsync(report, reportDir);

        Assert.Empty(failures);
    }

    /// <summary>对象桶场景套件：SonnetDB 对齐 MinIO。</summary>
    [Fact]
    public async Task ObjectSuite_SonnetDbMatchesMinio()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = cts.Token;

        var scenarios = CreateObjectScenarios();
        var runId = "object-" + Guid.NewGuid().ToString("N")[..8];
        var startedAt = DateTimeOffset.UtcNow;
        var reportDir = ResolveReportDirectory(runId);
        var ctx = new ScenarioContext { RunId = runId, ReportDirectory = reportDir, Cancellation = ct };
        var backends = new[] { "sonnetdb", "minio" };

        var reports = new List<ScenarioReport>();
        var failures = new List<string>();

        await using var sonnet = new SonnetDbAdapter();
        var minioAvailable = await MinioAdapter.TryConnectAsync(ct);

        foreach (var scenario in scenarios)
        {
            var sonnetResult = await RunBackendAsync(scenario, sonnet, ctx);
            var outcomes = new List<BackendOutcome> { ToOutcome(sonnet.BackendName, sonnetResult) };
            var differences = new List<string>();
            bool? withinTolerance = null;

            if (!sonnetResult.Pass)
                failures.Add($"SonnetDB 对象桶场景 {scenario.Name} 自检未通过。");

            await RunCompetitorAsync(
                scenario,
                ctx,
                sonnetResult,
                "minio",
                minioAvailable,
                static () => new MinioAdapter(),
                outcomes,
                differences,
                failures,
                result => withinTolerance = result,
                scenario.Tolerance);

            reports.Add(new ScenarioReport(scenario.Name, withinTolerance, differences, outcomes));
        }

        var report = new ParityReport(
            runId,
            startedAt,
            reports,
            BuildCapabilityGaps(scenarios, reports, backends),
            backends);

        await JsonReporter.WriteAsync(report, reportDir);
        await MarkdownReporter.WriteAsync(report, reportDir);

        Assert.Empty(failures);
    }

    /// <summary>MQ 场景套件：SonnetMQ 对齐 NATS JetStream。</summary>
    [Fact]
    public async Task MqSuite_SonnetMqMatchesNatsJetStream()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = cts.Token;

        var scenarios = CreateMqScenarios();
        var runId = "mq-" + Guid.NewGuid().ToString("N")[..8];
        var startedAt = DateTimeOffset.UtcNow;
        var reportDir = ResolveReportDirectory(runId);
        var ctx = new ScenarioContext { RunId = runId, ReportDirectory = reportDir, Cancellation = ct };
        var backends = new[] { "sonnetdb", "nats" };

        var reports = new List<ScenarioReport>();
        var failures = new List<string>();

        await using var sonnet = new SonnetDbAdapter();
        var natsAvailable = await NatsAdapter.TryConnectAsync(ct);

        foreach (var scenario in scenarios)
        {
            var sonnetResult = await RunBackendAsync(scenario, sonnet, ctx);
            var outcomes = new List<BackendOutcome> { ToOutcome(sonnet.BackendName, sonnetResult) };
            var differences = new List<string>();
            bool? withinTolerance = null;

            if (!sonnetResult.Pass)
                failures.Add($"SonnetMQ 场景 {scenario.Name} 自检未通过。");

            await RunCompetitorAsync(
                scenario,
                ctx,
                sonnetResult,
                "nats",
                natsAvailable,
                static () => new NatsAdapter(),
                outcomes,
                differences,
                failures,
                result => withinTolerance = result,
                scenario.Tolerance);

            reports.Add(new ScenarioReport(scenario.Name, withinTolerance, differences, outcomes));
        }

        var report = new ParityReport(
            runId,
            startedAt,
            reports,
            BuildCapabilityGaps(scenarios, reports, backends),
            backends);

        await JsonReporter.WriteAsync(report, reportDir);
        await MarkdownReporter.WriteAsync(report, reportDir);

        Assert.Empty(failures);
    }

    /// <summary>全文场景套件：SonnetDB 对齐 Meilisearch。</summary>
    [Fact]
    public async Task FullTextSuite_SonnetDbMatchesMeilisearch()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = cts.Token;

        var scenarios = CreateFullTextScenarios();
        var runId = "fulltext-" + Guid.NewGuid().ToString("N")[..8];
        var startedAt = DateTimeOffset.UtcNow;
        var reportDir = ResolveReportDirectory(runId);
        var ctx = new ScenarioContext { RunId = runId, ReportDirectory = reportDir, Cancellation = ct };
        var backends = new[] { "sonnetdb", "meilisearch" };

        var reports = new List<ScenarioReport>();
        var failures = new List<string>();

        await using var sonnet = new SonnetDbAdapter();
        var meiliAvailable = await MeiliAdapter.TryConnectAsync(ct);

        foreach (var scenario in scenarios)
        {
            var sonnetResult = await RunBackendAsync(scenario, sonnet, ctx);
            var outcomes = new List<BackendOutcome> { ToOutcome(sonnet.BackendName, sonnetResult) };
            var differences = new List<string>();
            bool? withinTolerance = null;

            if (!sonnetResult.Pass)
                failures.Add($"SonnetDB 全文场景 {scenario.Name} 自检未通过。");

            await RunCompetitorAsync(
                scenario,
                ctx,
                sonnetResult,
                "meilisearch",
                meiliAvailable,
                static () => new MeiliAdapter(),
                outcomes,
                differences,
                failures,
                result => withinTolerance = result,
                scenario.Tolerance);

            reports.Add(new ScenarioReport(scenario.Name, withinTolerance, differences, outcomes));
        }

        var report = new ParityReport(
            runId,
            startedAt,
            reports,
            BuildCapabilityGaps(scenarios, reports, backends),
            backends);

        await JsonReporter.WriteAsync(report, reportDir);
        await MarkdownReporter.WriteAsync(report, reportDir);

        Assert.Empty(failures);
    }

    /// <summary>分析场景套件：SonnetDB 对齐 ClickHouse，性能数字 warning only，聚合精度按容差 gating。</summary>
    [Fact]
    public async Task AnalyticsSuite_SonnetDbMatchesClickHouse()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = cts.Token;

        var scenarios = CreateAnalyticsScenarios();
        var runId = "analytics-" + Guid.NewGuid().ToString("N")[..8];
        var startedAt = DateTimeOffset.UtcNow;
        var reportDir = ResolveReportDirectory(runId);
        var ctx = new ScenarioContext { RunId = runId, ReportDirectory = reportDir, Cancellation = ct };
        var backends = new[] { "sonnetdb", "clickhouse" };

        var reports = new List<ScenarioReport>();
        var failures = new List<string>();

        await using var sonnet = new SonnetDbAdapter();
        var clickHouseAvailable = await ClickHouseAdapter.TryConnectAsync(ct);

        foreach (var scenario in scenarios)
        {
            var sonnetResult = await RunBackendAsync(scenario, sonnet, ctx);
            var outcomes = new List<BackendOutcome> { ToOutcome(sonnet.BackendName, sonnetResult) };
            var differences = new List<string>();
            bool? withinTolerance = null;

            if (!sonnetResult.Pass)
                failures.Add($"SonnetDB 分析场景 {scenario.Name} 自检未通过。");

            await RunCompetitorAsync(
                scenario,
                ctx,
                sonnetResult,
                "clickhouse",
                clickHouseAvailable,
                static () => new ClickHouseAdapter(),
                outcomes,
                differences,
                failures,
                result => withinTolerance = result,
                scenario.Tolerance);

            reports.Add(new ScenarioReport(scenario.Name, withinTolerance, differences, outcomes));
        }

        var report = new ParityReport(
            runId,
            startedAt,
            reports,
            BuildCapabilityGaps(scenarios, reports, backends),
            backends);

        await JsonReporter.WriteAsync(report, reportDir);
        await MarkdownReporter.WriteAsync(report, reportDir);

        Assert.Empty(failures);
    }

    private static IReadOnlyList<IScenario> CreateRelationalScenarios() =>
    [
        new HelloWorldRelationalScenario(),
        new TpccLiteScenario(),
        new FkCascadeConstraintScenario(),
        new IsolationReadCommittedScenario(),
        new SubqueryCorrelatedScenario(),
        new GroupByHavingScenario(),
        new InformationSchemaIntrospectionScenario(),
        new UpdateReturningCountScenario(),
        new AlterTableEvolutionScenario(),
    ];

    private static IReadOnlyList<TsdbScenarioBase> CreateTsdbScenarios() =>
    [
        new IngestOneMillionPointsScenario(),
        new GroupByTimeWindowScenario(),
        new DerivativeAccuracyScenario(),
        new RateIrateConsistencyScenario(),
        new HoltWintersForecastRecallScenario(),
        new PercentileP95Scenario(),
        new DistinctCountHllScenario(),
    ];

    private static IReadOnlyList<KvScenarioBase> CreateKvScenarios() =>
    [
        new SetGetScanThroughputScenario(),
        new TtlAccuracyScenario(),
        new IncrConcurrency16ClientsScenario(),
        new CasOptimisticLockScenario(),
        new ScanCursor10MKeysScenario(),
    ];

    private static IReadOnlyList<VectorScenarioBase> CreateVectorScenarios() =>
    [
        new AnnRecallAt10Scenario(),
        new FilteredSearchScenario(),
        new UpsertDuringQueryScenario(),
    ];

    private static IReadOnlyList<ObjectScenarioBase> CreateObjectScenarios() =>
    [
        new PutGetObjectScenario(),
        new MultipartUploadScenario(),
        new RangeReadOffsetsScenario(),
        new ListObjectsV2PaginationScenario(),
        new CopyDeletePresignScenario(),
    ];

    private static IReadOnlyList<MqScenarioBase> CreateMqScenarios() =>
    [
        new PublishConsumeAckScenario(),
        new ConsumerGroupOffsetScenario(),
        new ReplayAfterRestartScenario(),
        new FanOut10P10CScenario(),
        new BackpressureUnboundedProducerScenario(),
    ];

    private static IReadOnlyList<FullTextScenarioBase> CreateFullTextScenarios() =>
    [
        new IndexOneMillionDocumentsScenario(),
        new Bm25RankingTop10OverlapScenario(),
        new CjkTokenizeCorrectnessScenario(),
        new FacetFilterQueryScenario(),
        new IncrementalUpdateDuringQueryScenario(),
        new TypoTolerantQueryScenario(),
    ];

    private static IReadOnlyList<AnalyticsScenarioBase> CreateAnalyticsScenarios() =>
    [
        new GroupByTimeOneBRowsWallclockScenario(),
        new WindowAvg7DayScenario(),
        new TopNPerDeviceScenario(),
        new ColumnarCompressionRatioScenario(),
        new PercentileAccuracyP50P95P99Scenario(),
    ];

    private static BackendOutcome ToOutcome(string backend, ScenarioResult result)
    {
        var rowCount = result.SqlResult?.Rows.Count ?? result.Rows.Count;
        return new BackendOutcome(
            backend,
            result.GapReason is not null ? "skipped" : result.Pass ? "pass" : "fail",
            result.GapReason,
            rowCount,
            result.Metrics);
    }

    private static async Task<ScenarioResult> RunBackendAsync(IScenario scenario, IDataPlane plane, ScenarioContext ctx)
    {
        try
        {
            return await scenario.RunAsync(plane, ctx);
        }
        catch (Exception ex)
        {
            var result = new ScenarioResult { Pass = false };
            result.Metrics["exception_type"] = ex.GetType().Name;
            result.Metrics["exception_message"] = ex.Message;
            return result;
        }
    }

    private static DiffResult DiffResults(ScenarioResult expected, ScenarioResult actual, DiffTolerance? tolerance = null)
    {
        if (expected.SqlResult is not null && actual.SqlResult is not null)
            return ResultDiffer.DiffSqlResults(expected.SqlResult, actual.SqlResult, tolerance ?? DiffTolerance.Strict);
        return ResultDiffer.DiffRows(expected.Rows, actual.Rows, tolerance ?? DiffTolerance.Strict);
    }

    private static async Task RunTsdbCompetitorAsync<TAdapter>(
        TsdbScenarioBase scenario,
        ScenarioContext ctx,
        ScenarioResult sonnetResult,
        string backendName,
        bool available,
        Func<TAdapter> createAdapter,
        List<BackendOutcome> outcomes,
        List<string> differences,
        List<string> failures,
        Action<bool> setWithinTolerance)
        where TAdapter : IDataPlane
    {
        if (!available)
        {
            outcomes.Add(new BackendOutcome(
                backendName,
                "skipped",
                $"{backendName} unreachable (compose full 未启动或 PARITY_* 未配置)",
                0,
                new Dictionary<string, object?>()));
            return;
        }

        await using var adapter = createAdapter();
        var result = await RunBackendAsync(scenario, adapter, ctx).ConfigureAwait(false);
        outcomes.Add(ToOutcome(adapter.BackendName, result));
        if (!result.Pass)
            failures.Add($"{backendName} TSDB 场景 {scenario.Name} 自检未通过。");

        if (sonnetResult.GapReason is null && result.GapReason is null && sonnetResult.Pass && result.Pass)
        {
            var diff = DiffResults(sonnetResult, result, scenario.Tolerance);
            setWithinTolerance(diff.WithinTolerance);
            if (!diff.WithinTolerance)
            {
                var prefixed = diff.Differences.Select(d => $"{backendName}: {d}").ToArray();
                differences.AddRange(prefixed);
                failures.Add($"SonnetDB 与 {backendName} 场景 {scenario.Name} 结果超出容差：" + string.Join("; ", prefixed));
            }
        }
    }

    private static async Task RunCompetitorAsync<TAdapter>(
        IScenario scenario,
        ScenarioContext ctx,
        ScenarioResult sonnetResult,
        string backendName,
        bool available,
        Func<TAdapter> createAdapter,
        List<BackendOutcome> outcomes,
        List<string> differences,
        List<string> failures,
        Action<bool> setWithinTolerance,
        DiffTolerance tolerance)
        where TAdapter : IDataPlane
    {
        if (!available)
        {
            outcomes.Add(new BackendOutcome(
                backendName,
                "skipped",
                $"{backendName} unreachable (compose full/light 未启动或 PARITY_* 未配置)",
                0,
                new Dictionary<string, object?>()));
            return;
        }

        await using var adapter = createAdapter();
        var result = await RunBackendAsync(scenario, adapter, ctx).ConfigureAwait(false);
        outcomes.Add(ToOutcome(adapter.BackendName, result));
        if (!result.Pass)
            failures.Add($"{backendName} 场景 {scenario.Name} 自检未通过。");

        if (sonnetResult.GapReason is null && result.GapReason is null && sonnetResult.Pass && result.Pass)
        {
            var diff = DiffResults(sonnetResult, result, tolerance);
            setWithinTolerance(diff.WithinTolerance);
            if (!diff.WithinTolerance)
            {
                var prefixed = diff.Differences.Select(d => $"{backendName}: {d}").ToArray();
                differences.AddRange(prefixed);
                failures.Add($"SonnetDB 与 {backendName} 场景 {scenario.Name} 结果超出容差：" + string.Join("; ", prefixed));
            }
        }
    }

    private static bool MergeTolerance(bool? current, bool next)
        => (current ?? true) && next;

    private static IReadOnlyList<CapabilityGap> BuildCapabilityGaps(
        IReadOnlyList<IScenario> scenarios,
        IReadOnlyList<ScenarioReport> reports,
        IReadOnlyList<string> backendNames)
    {
        var rows = new List<CapabilityGap>(scenarios.Count);
        foreach (var scenario in scenarios)
        {
            var report = reports.FirstOrDefault(r => string.Equals(r.Name, scenario.Name, StringComparison.Ordinal));
            if (report is null)
            {
                rows.Add(new CapabilityGap(scenario.Name, scenario.Required.ToString(), "missing", new Dictionary<string, string>(), "scenario did not run"));
                continue;
            }
            var sonnet = Find(report, "sonnetdb");
            var statuses = backendNames.ToDictionary(
                static b => b,
                b => Find(report, b)?.Status ?? "missing",
                StringComparer.OrdinalIgnoreCase);
            rows.Add(new CapabilityGap(
                scenario.Name,
                scenario.Required.ToString(),
                sonnet?.Status ?? "missing",
                statuses,
                sonnet?.GapReason));
        }

        return rows;
    }

    private static BackendOutcome? Find(ScenarioReport report, string backend)
        => report.Backends.FirstOrDefault(b => string.Equals(b.Backend, backend, StringComparison.OrdinalIgnoreCase));

    private static string ResolveReportDirectory(string runId)
    {
        var overrideDir = Environment.GetEnvironmentVariable("PARITY_REPORT_DIR");
        var baseDir = string.IsNullOrWhiteSpace(overrideDir)
            ? Path.Combine(AppContext.BaseDirectory, "parity-reports")
            : overrideDir;
        return Path.Combine(baseDir, runId);
    }
}
