using Microsoft.Extensions.Configuration;
using SonnetDB.Configuration;
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
        Assert.False(options.Observability.DiagnosticDump.Enabled);
        Assert.Equal(4, options.SqlHttpAdmission.PermitLimit);
        Assert.Equal(8, options.SqlHttpAdmission.QueueLimit);
        Assert.Equal(256L * 1024 * 1024, options.Kv.IndexRebuildMaxWalBytes);
        Assert.Equal(100_000, options.Kv.IndexRebuildMaxOverlayEntries);
        Assert.False(options.SemanticSearch.Enabled);
        Assert.Equal("auto", options.SemanticSearch.Backend);
        Assert.Equal(768, options.SemanticSearch.Dimensions);
        Assert.Equal("input_ids", options.SemanticSearch.TextInputName);
        Assert.Equal("pooler_output", options.SemanticSearch.TextOutputName);
        Assert.Equal("pixel_values", options.SemanticSearch.VisionInputName);
        Assert.Equal("pooler_output", options.SemanticSearch.VisionOutputName);
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
}
