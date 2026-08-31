using Microsoft.Extensions.Configuration;
using SonnetDB.Configuration;
using SonnetDB.Engine;
using SonnetDB.Hosting;
using Xunit;

namespace SonnetDB.Tests;

public sealed class ServerOptionsTests
{
    [Fact]
    public void Defaults_UseProductionObservabilitySettings()
    {
        var options = new ServerOptions();

        var slowQuery = options.Observability.SlowQueryLog;
        Assert.True(slowQuery.Enabled);
        Assert.Equal(10_000, slowQuery.ThresholdMs);
        Assert.Equal(30_000, slowQuery.WarningThresholdMs);
        Assert.Equal(60_000, slowQuery.CriticalThresholdMs);
        Assert.Equal(256, slowQuery.Capacity);
        Assert.Equal(1024, slowQuery.AggregateCapacity);
        Assert.False(options.Observability.DiagnosticDump.Enabled);
        Assert.Equal(4, options.SqlHttpAdmission.PermitLimit);
        Assert.Equal(8, options.SqlHttpAdmission.QueueLimit);
        Assert.Equal(SqlMemoryOptions.Default.QueryLimitBytes, options.SqlExecution.QueryLimitBytes);
        Assert.Equal(SqlMemoryOptions.Default.GlobalLimitBytes, options.SqlExecution.GlobalLimitBytes);
        Assert.Equal(SqlMemoryOptions.Default.MaxParallelWorkers, options.SqlExecution.MaxParallelWorkers);
        Assert.Equal(SqlMemoryOptions.Default.ParallelismMinRows, options.SqlExecution.ParallelismMinRows);
        Assert.Equal(
            SqlMemoryOptions.Default.ParallelWorkerMemoryBytes,
            options.SqlExecution.ParallelWorkerMemoryBytes);
        Assert.Equal(4, options.RelationalTableWarmupConcurrency);
        Assert.Equal(256L * 1024 * 1024, options.Kv.IndexRebuildMaxWalBytes);
        Assert.Equal(100_000, options.Kv.IndexRebuildMaxOverlayEntries);
        Assert.False(options.SemanticSearch.Enabled);
        Assert.Equal("auto", options.SemanticSearch.Backend);
        Assert.Equal(768, options.SemanticSearch.Dimensions);
        Assert.Equal("input_ids", options.SemanticSearch.TextInputName);
        Assert.Equal("pooler_output", options.SemanticSearch.TextOutputName);
        Assert.Equal("pixel_values", options.SemanticSearch.VisionInputName);
        Assert.Equal("pooler_output", options.SemanticSearch.VisionOutputName);
        Assert.False(options.Modbus.Enabled);
        Assert.Equal(250, options.Modbus.DiscoveryIntervalMilliseconds);
        Assert.Equal(1_000, options.Modbus.ReconnectBaseDelayMilliseconds);
        Assert.Equal(30_000, options.Modbus.MaxReconnectDelayMilliseconds);
    }

    [Fact]
    public void Bind_WithModbusRuntimeValues_AppliesAndBoundsConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SonnetDBServer:Modbus:Enabled"] = "true",
                ["SonnetDBServer:Modbus:DiscoveryIntervalMilliseconds"] = "1",
                ["SonnetDBServer:Modbus:RetryBaseDelayMilliseconds"] = "50",
                ["SonnetDBServer:Modbus:MaxRetryDelayMilliseconds"] = "10",
                ["SonnetDBServer:Modbus:ReconnectBaseDelayMilliseconds"] = "100",
                ["SonnetDBServer:Modbus:MaxReconnectDelayMilliseconds"] = "10",
            })
            .Build();

        ModbusRuntimeOptions options = ServerOptionsBinder.Bind(configuration).Modbus;

        Assert.True(options.Enabled);
        Assert.Equal(10, options.DiscoveryIntervalMilliseconds);
        Assert.Equal(50, options.RetryBaseDelayMilliseconds);
        Assert.Equal(50, options.MaxRetryDelayMilliseconds);
        Assert.Equal(100, options.ReconnectBaseDelayMilliseconds);
        Assert.Equal(100, options.MaxReconnectDelayMilliseconds);
    }

    [Fact]
    public void Bind_WithSemanticSearchValues_AppliesAndBoundsConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SonnetDBServer:SemanticSearch:Enabled"] = "true",
                ["SonnetDBServer:SemanticSearch:Backend"] = "usearch",
                ["SonnetDBServer:SemanticSearch:Dimensions"] = "768",
                ["SonnetDBServer:SemanticSearch:DefaultTopK"] = "5000",
                ["SonnetDBServer:SemanticSearch:MaxTopK"] = "12",
            })
            .Build();

        var options = ServerOptionsBinder.Bind(configuration).SemanticSearch;

        Assert.True(options.Enabled);
        Assert.Equal("usearch", options.Backend);
        Assert.Equal(768, options.Dimensions);
        Assert.Equal(12, options.MaxTopK);
        Assert.Equal(12, options.DefaultTopK);
    }

    [Fact]
    public void Bind_WithDiagnosticDumpEnabled_AppliesExplicitOptIn()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SonnetDBServer:Observability:DiagnosticDump:Enabled"] = "true",
            })
            .Build();

        var options = ServerOptionsBinder.Bind(configuration);

        Assert.True(options.Observability.DiagnosticDump.Enabled);
    }

    [Fact]
    public void Bind_WithLegacySlowQueryKeys_AppliesCompatibilityValues()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SonnetDBServer:SlowQueryEnabled"] = "false",
                ["SonnetDBServer:SlowQueryThresholdMs"] = "25",
                ["SonnetDBServer:SlowQueryWarningThresholdMs"] = "50",
                ["SonnetDBServer:SlowQueryCriticalThresholdMs"] = "75",
            })
            .Build();

        var options = ServerOptionsBinder.Bind(configuration).Observability.SlowQueryLog;

        Assert.False(options.Enabled);
        Assert.Equal(25, options.ThresholdMs);
        Assert.Equal(50, options.WarningThresholdMs);
        Assert.Equal(75, options.CriticalThresholdMs);
    }

    [Fact]
    public void Bind_WithNestedAndLegacySlowQueryKeys_PrefersNestedValues()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SonnetDBServer:SlowQueryThresholdMs"] = "25",
                ["SonnetDBServer:Observability:SlowQueryLog:ThresholdMs"] = "125",
            })
            .Build();

        var options = ServerOptionsBinder.Bind(configuration).Observability.SlowQueryLog;

        Assert.Equal(125, options.ThresholdMs);
    }

    [Fact]
    public void Bind_WithSlowQueryCapacities_AppliesAndBoundsConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SonnetDBServer:Observability:SlowQueryLog:Capacity"] = "1",
                ["SonnetDBServer:Observability:SlowQueryLog:AggregateCapacity"] = "20000",
            })
            .Build();

        var options = ServerOptionsBinder.Bind(configuration).Observability.SlowQueryLog;

        Assert.Equal(16, options.Capacity);
        Assert.Equal(16_384, options.AggregateCapacity);
    }

    [Fact]
    public void Bind_WithSqlHttpAdmissionValues_AppliesAndBoundsConfiguration()
    {
        var configured = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SonnetDBServer:SqlHttpAdmission:PermitLimit"] = "2",
                ["SonnetDBServer:SqlHttpAdmission:QueueLimit"] = "12",
            })
            .Build();

        var configuredOptions = ServerOptionsBinder.Bind(configured).SqlHttpAdmission;

        Assert.Equal(2, configuredOptions.PermitLimit);
        Assert.Equal(12, configuredOptions.QueueLimit);

        var outOfRange = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SonnetDBServer:SqlHttpAdmission:PermitLimit"] = "0",
                ["SonnetDBServer:SqlHttpAdmission:QueueLimit"] = "5000",
            })
            .Build();

        var boundedOptions = ServerOptionsBinder.Bind(outOfRange).SqlHttpAdmission;

        Assert.Equal(1, boundedOptions.PermitLimit);
        Assert.Equal(4096, boundedOptions.QueueLimit);
    }

    /// <summary>验证每数据库 SQL 内部资源配置可绑定，并按处理器数量限制内部 worker。</summary>
    [Fact]
    public void Bind_WithSqlExecutionValues_AppliesConfiguration()
    {
        int configuredWorkers = Math.Min(2, Math.Clamp(Environment.ProcessorCount, 1, 64));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SonnetDBServer:SqlExecution:QueryLimitBytes"] = "8388608",
                ["SonnetDBServer:SqlExecution:GlobalLimitBytes"] = "67108864",
                ["SonnetDBServer:SqlExecution:MaxParallelWorkers"] = "2",
                ["SonnetDBServer:SqlExecution:ParallelismMinRows"] = "512",
                ["SonnetDBServer:SqlExecution:ParallelWorkerMemoryBytes"] = "131072",
            })
            .Build();

        SqlExecutionResourceOptions options = ServerOptionsBinder.Bind(configuration).SqlExecution;

        Assert.Equal(8L * 1024 * 1024, options.QueryLimitBytes);
        Assert.Equal(64L * 1024 * 1024, options.GlobalLimitBytes);
        Assert.Equal(configuredWorkers, options.MaxParallelWorkers);
        Assert.Equal(512, options.ParallelismMinRows);
        Assert.Equal(128L * 1024, options.ParallelWorkerMemoryBytes);
    }

    /// <summary>验证查询、全局和 worker 预算的交叉约束不会允许总预留超过全局预算。</summary>
    [Fact]
    public void Bind_WithSqlExecutionExtremes_EnforcesCoupledBounds()
    {
        int workerLimit = Math.Clamp(Environment.ProcessorCount, 1, 64);
        var coupled = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SonnetDBServer:SqlExecution:QueryLimitBytes"] = "8388608",
                ["SonnetDBServer:SqlExecution:GlobalLimitBytes"] = "1",
                ["SonnetDBServer:SqlExecution:MaxParallelWorkers"] = int.MaxValue.ToString(),
                ["SonnetDBServer:SqlExecution:ParallelismMinRows"] = "0",
                ["SonnetDBServer:SqlExecution:ParallelWorkerMemoryBytes"] = long.MaxValue.ToString(),
            })
            .Build();

        SqlExecutionResourceOptions coupledOptions = ServerOptionsBinder.Bind(coupled).SqlExecution;

        Assert.Equal(8L * 1024 * 1024, coupledOptions.QueryLimitBytes);
        Assert.Equal(coupledOptions.QueryLimitBytes, coupledOptions.GlobalLimitBytes);
        Assert.Equal(workerLimit, coupledOptions.MaxParallelWorkers);
        Assert.Equal(1, coupledOptions.ParallelismMinRows);
        Assert.Equal(
            Math.Min(64L * 1024 * 1024, coupledOptions.GlobalLimitBytes / workerLimit),
            coupledOptions.ParallelWorkerMemoryBytes);
        Assert.True(
            coupledOptions.ParallelWorkerMemoryBytes * coupledOptions.MaxParallelWorkers
            <= coupledOptions.GlobalLimitBytes);

        var maximums = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SonnetDBServer:SqlExecution:QueryLimitBytes"] = long.MaxValue.ToString(),
                ["SonnetDBServer:SqlExecution:GlobalLimitBytes"] = long.MaxValue.ToString(),
                ["SonnetDBServer:SqlExecution:ParallelismMinRows"] = long.MaxValue.ToString(),
            })
            .Build();
        SqlExecutionResourceOptions maximumOptions = ServerOptionsBinder.Bind(maximums).SqlExecution;

        Assert.Equal(16L * 1024 * 1024 * 1024, maximumOptions.QueryLimitBytes);
        Assert.Equal(64L * 1024 * 1024 * 1024, maximumOptions.GlobalLimitBytes);
        Assert.Equal(1_000_000_000, maximumOptions.ParallelismMinRows);
    }

    /// <summary>验证 worker 总预留同时受单查询预算约束，避免配置有效但运行时始终回退串行。</summary>
    [Fact]
    public void Bind_WithWorkerMemoryAbovePerQueryShare_ClampsToQueryBudget()
    {
        int workerLimit = Math.Clamp(Environment.ProcessorCount, 1, 64);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SonnetDBServer:SqlExecution:QueryLimitBytes"] = (1024L * 1024).ToString(),
                ["SonnetDBServer:SqlExecution:GlobalLimitBytes"] = (4L * 1024 * 1024 * 1024).ToString(),
                ["SonnetDBServer:SqlExecution:MaxParallelWorkers"] = int.MaxValue.ToString(),
                ["SonnetDBServer:SqlExecution:ParallelWorkerMemoryBytes"] = long.MaxValue.ToString(),
            })
            .Build();

        SqlExecutionResourceOptions options = ServerOptionsBinder.Bind(configuration).SqlExecution;
        long expectedWorkerBytes = Math.Min(
            64L * 1024 * 1024,
            Math.Min(
                options.QueryLimitBytes / options.MaxParallelWorkers,
                options.GlobalLimitBytes / options.MaxParallelWorkers));

        Assert.Equal(workerLimit, options.MaxParallelWorkers);
        Assert.Equal(expectedWorkerBytes, options.ParallelWorkerMemoryBytes);
        Assert.True(
            options.ParallelWorkerMemoryBytes * options.MaxParallelWorkers
            <= options.QueryLimitBytes);
        Assert.True(
            options.ParallelWorkerMemoryBytes * options.MaxParallelWorkers
            <= options.GlobalLimitBytes);
    }

    /// <summary>验证服务注册层把五个可绑定字段完整复制到 Core 的不可变资源选项。</summary>
    [Fact]
    public void ServiceRegistration_MapsSqlExecutionOptionsToCore()
    {
        var server = new SqlExecutionResourceOptions
        {
            QueryLimitBytes = 11,
            GlobalLimitBytes = 22,
            MaxParallelWorkers = 3,
            ParallelismMinRows = 44,
            ParallelWorkerMemoryBytes = 55,
        };

        SqlMemoryOptions core = SonnetDbServiceRegistration.CreateSqlMemoryOptions(server);

        Assert.Equal(server.QueryLimitBytes, core.QueryLimitBytes);
        Assert.Equal(server.GlobalLimitBytes, core.GlobalLimitBytes);
        Assert.Equal(server.MaxParallelWorkers, core.MaxParallelWorkers);
        Assert.Equal(server.ParallelismMinRows, core.ParallelismMinRows);
        Assert.Equal(server.ParallelWorkerMemoryBytes, core.ParallelWorkerMemoryBytes);
    }

    /// <summary>验证索引重建预算可由部署配置覆盖，并始终限制在明确的安全范围内。</summary>
    [Fact]
    public void Bind_WithKvIndexRebuildBudget_AppliesAndBoundsConfiguration()
    {
        var configured = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SonnetDBServer:Kv:IndexRebuildMaxWalBytes"] = "3221225472",
                ["SonnetDBServer:Kv:IndexRebuildMaxOverlayEntries"] = "4000000",
            })
            .Build();

        var configuredOptions = ServerOptionsBinder.Bind(configured).Kv;

        Assert.Equal(3L * 1024 * 1024 * 1024, configuredOptions.IndexRebuildMaxWalBytes);
        Assert.Equal(4_000_000, configuredOptions.IndexRebuildMaxOverlayEntries);

        var outOfRange = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SonnetDBServer:Kv:IndexRebuildMaxWalBytes"] = long.MaxValue.ToString(),
                ["SonnetDBServer:Kv:IndexRebuildMaxOverlayEntries"] = "0",
            })
            .Build();

        var boundedOptions = ServerOptionsBinder.Bind(outOfRange).Kv;

        Assert.Equal(64L * 1024 * 1024 * 1024, boundedOptions.IndexRebuildMaxWalBytes);
        Assert.Equal(1, boundedOptions.IndexRebuildMaxOverlayEntries);
    }

    /// <summary>验证关系表启动预热并发可配置，并限制在明确的资源边界内。</summary>
    [Fact]
    public void Bind_WithRelationalTableWarmupConcurrency_AppliesAndBoundsConfiguration()
    {
        var configured = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SonnetDBServer:RelationalTableWarmupConcurrency"] = "8",
            })
            .Build();
        Assert.Equal(8, ServerOptionsBinder.Bind(configured).RelationalTableWarmupConcurrency);

        var tooLow = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SonnetDBServer:RelationalTableWarmupConcurrency"] = "0",
            })
            .Build();
        var tooHigh = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SonnetDBServer:RelationalTableWarmupConcurrency"] = "64",
            })
            .Build();

        Assert.Equal(1, ServerOptionsBinder.Bind(tooLow).RelationalTableWarmupConcurrency);
        Assert.Equal(16, ServerOptionsBinder.Bind(tooHigh).RelationalTableWarmupConcurrency);
    }
}
