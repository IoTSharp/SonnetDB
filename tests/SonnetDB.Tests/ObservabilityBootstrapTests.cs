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
