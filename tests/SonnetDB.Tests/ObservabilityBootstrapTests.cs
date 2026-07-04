using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using SonnetDB.Configuration;
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
}
