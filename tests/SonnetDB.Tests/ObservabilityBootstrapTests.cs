using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Json;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>
/// M17 #90：Server OpenTelemetry 引导测试——验证 MeterProvider / TracerProvider 注册进 DI，
/// 且 `AddMeter("SonnetDB.Core")` 订阅使 Core 的 BCL Meter 指标真正流入 OTel SDK
/// （经 InMemory exporter 断言到具体 metric 名与值）。
/// </summary>
public sealed class ObservabilityBootstrapTests : IAsyncLifetime
{
    private WebApplication? _app;
    private string? _dataRoot;
    private readonly List<Metric> _exportedMetrics = new();

    public async Task InitializeAsync()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "sonnetdb-otel-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);

        var options = new ServerOptions
        {
            DataRoot = _dataRoot,
            AutoLoadExistingDatabases = false,
            AllowAnonymousProbes = true,
            Tokens = new Dictionary<string, string> { ["t"] = ServerRoles.Admin },
        };

        _app = TestServerHost.Build(options, services =>
            services.ConfigureOpenTelemetryMeterProvider(b => b.AddInMemoryExporter(_exportedMetrics)));
        await _app.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        if (_dataRoot is not null && Directory.Exists(_dataRoot))
        {
            try { Directory.Delete(_dataRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void MeterAndTracerProviders_AreRegistered()
    {
        Assert.NotNull(_app!.Services.GetService<MeterProvider>());
        Assert.NotNull(_app.Services.GetService<TracerProvider>());
    }

    [Fact]
    public void MeterProvider_CollectsSonnetDbCoreMetrics()
    {
        // 触发一次引擎写入，使 SonnetDB.Core Meter 产生测量值。
        var registry = _app!.Services.GetRequiredService<Hosting.TsdbRegistry>();
        Assert.True(registry.TryCreate("otel_probe", out var db));
        db.Write(Model.Point.Create(
            "metric",
            1_700_000_000_000L,
            new Dictionary<string, string> { ["host"] = "h1" },
            new Dictionary<string, Model.FieldValue> { ["value"] = Model.FieldValue.FromDouble(1) }));

        var meterProvider = _app.Services.GetRequiredService<MeterProvider>();
        Assert.True(meterProvider.ForceFlush(10_000));

        lock (_exportedMetrics)
        {
            Assert.Contains(_exportedMetrics, m => m.Name == "sonnetdb.write.points");
            Assert.Contains(_exportedMetrics, m => m.Name == "sonnetdb.write.duration");
        }
    }
}

/// <summary>
/// M17 #91：`Observability:Prometheus:Enabled=true` 时 <c>/metrics</c> 由 OpenTelemetry
/// Prometheus 拉取端点接管，暴露 <c>sonnetdb.*</c> 指标（prom 命名下划线化）。
/// </summary>
public sealed class PrometheusEndpointTests : IAsyncLifetime
{
    private WebApplication? _app;
    private string? _baseUrl;
    private string? _dataRoot;

    public async Task InitializeAsync()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "sonnetdb-prom-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);

        var options = new ServerOptions
        {
            DataRoot = _dataRoot,
            AutoLoadExistingDatabases = false,
            AllowAnonymousProbes = true,
            Tokens = new Dictionary<string, string> { ["t"] = ServerRoles.Admin },
            Observability = new ObservabilityOptions
            {
                Prometheus = new PrometheusOptions { Enabled = true },
            },
        };

        _app = TestServerHost.Build(options);
        await _app.StartAsync();

        var addresses = _app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel 未暴露监听地址。");
        _baseUrl = addresses.Addresses.First();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        if (_dataRoot is not null && Directory.Exists(_dataRoot))
        {
            try { Directory.Delete(_dataRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Metrics_ServedByOpenTelemetryExporter_ExposesSonnetDbMeters()
    {
        // 产生一次写入，保证 sonnetdb.write.points 有数据点可导出。
        var registry = _app!.Services.GetRequiredService<Hosting.TsdbRegistry>();
        Assert.True(registry.TryCreate("prom_probe", out var db));
        db.Write(Model.Point.Create(
            "metric",
            1_700_000_000_000L,
            new Dictionary<string, string> { ["host"] = "h1" },
            new Dictionary<string, Model.FieldValue> { ["value"] = Model.FieldValue.FromDouble(1) }));

        using var client = new HttpClient { BaseAddress = new Uri(_baseUrl!) };
        var resp = await client.GetAsync("/metrics");

        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        // OTel prom exporter 把 sonnetdb.write.points 规范为下划线 + 单位/类型后缀。
        Assert.Contains("sonnetdb_write_points", body);
        // 旧最小指标集端点已被接管（不再出现 uptime 文本渲染器的指标名）。
        Assert.DoesNotContain("sonnetdb_uptime_seconds", body);
    }

    [Fact]
    public async Task Metrics_MeasurementInsertStages_RecordOnlySuccessfulMeasurementReceipts()
    {
        var registry = _app!.Services.GetRequiredService<Hosting.TsdbRegistry>();
        Assert.True(registry.TryCreate("insert_stage_probe", out _));
        using var client = new HttpClient { BaseAddress = new Uri(_baseUrl!), Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "t");
        using var response = await client.PostAsync("/v1/db/insert_stage_probe/sql/batch",
            JsonContent.Create(new SqlBatchRequest([
                new("CREATE TABLE unrelated_table (id INT, PRIMARY KEY (id))"),
                new("INSERT INTO unrelated_table (id) VALUES (1)"),
                new("INSERT INTO private_measurement (TIME, private_field) VALUES (1000, @value)",
                    new Dictionary<string, JsonElementValue> { ["value"] = new(ScalarKind.String, StringValue: "private_payload") }),
                new("INSERT INTO private_measurement (TIME, private_field) VALUES (2000, NULL)"),
                new("INSERT INTO private_measurement (TIME, private_field) VALUES (3000, 'unwritten_suffix')"),
            ]), ServerJsonContext.Default.SqlBatchRequest));
        response.EnsureSuccessStatusCode();
        string[] lines = (await response.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length);
        using var insertEnd = JsonDocument.Parse(lines[2]);
        Assert.Equal(1, insertEnd.RootElement.GetProperty("recordsAffected").GetInt32());
        Assert.Contains("sql_error", lines[3]);

        string metrics = await client.GetStringAsync("/metrics");
        const string metric = "sonnetdb_sql_measurement_insert_stage_duration";
        string[] counters = metrics.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith(metric, StringComparison.Ordinal) && line.Contains("_count{", StringComparison.Ordinal))
            .ToArray();
        string[] stages = ["parse", "bind_parameters", "bind_names", "execution_prepare", "schema_lock_wait", "write_lock_wait",
            "schema", "wal_append", "wal_flush_to_os", "engine_other", "ndjson_receipt", "query_diagnostics"];
        Assert.Equal(stages.Length, counters.Length);
        foreach (string stage in stages)
        {
            string counter = Assert.Single(counters, line => line.Contains($"stage=\"{stage}\"", StringComparison.Ordinal));
            Assert.EndsWith("} 1", counter.TrimEnd());
        }
        Assert.DoesNotContain("private_measurement", metrics, StringComparison.Ordinal);
        Assert.DoesNotContain("private_field", metrics, StringComparison.Ordinal);
        Assert.DoesNotContain("private_payload", metrics, StringComparison.Ordinal);
    }
}
