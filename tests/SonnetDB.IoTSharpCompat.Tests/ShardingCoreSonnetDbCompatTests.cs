namespace SonnetDB.IoTSharpCompat.Tests;

using IoTSharp.Contracts;
using IoTSharp.Data;
using IoTSharp.Data.Shardings;
using IoTSharp.Data.Shardings.Routes;
using IoTSharp.Data.SonnetDB;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ShardingCore;
using ShardingCore.TableExists.Abstractions;
using SonnetDB.Data;
using Xunit;
using DataType = IoTSharp.Contracts.DataType;

public sealed class ShardingCoreSonnetDbCompatTests : IDisposable
{
    private readonly string _root;

    public ShardingCoreSonnetDbCompatTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sndb-sharding-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore test cleanup failures */ }
    }

    [Fact]
    public async Task ShardingCore_WithSonnetDb_CanCreateShardInsertAndQuery()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new AppSettings
        {
            DataBase = DataBaseType.SonnetDB,
            TelemetryStorage = TelemetryStorage.Sharding,
            ShardingByDateMode = ShardingByDateMode.PerDay,
            ShardingBeginTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ConnectionStrings = new Dictionary<string, string>
            {
                ["TelemetryStorage"] = $"Data Source={_root}"
            }
        }));

        var sharding = services.AddShardingDbContext<ShardingDbContext>();
        sharding.UseRouteConfig(options =>
        {
            options.AddShardingTableRoute<TelemetryDataDayRoute>();
        });
        sharding.UseConfig(options =>
        {
            options.ThrowIfQueryRouteNotMatch = false;
            options.UseShellDbContextConfigure(builder => builder.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking));
            options.AddDefaultDataSource("ds0", $"Data Source={_root}");
            options.UseSonnetDBToSharding();
        });
        sharding.AddShardingCore();
        services.AddSingleton<ITableEnsureManager, SonnetDbTableEnsureManager>();

        await using var provider = services.BuildServiceProvider(validateScopes: true);
        provider.UseAutoTryCompensateTable();

        var tableName = "TelemetryData_20260102";
        await AssertTelemetryIndexesAsync(tableName);

        var deviceId = Guid.NewGuid();
        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ShardingDbContext>();
            context.Set<TelemetryData>().Add(new TelemetryData
            {
                DeviceId = deviceId,
                KeyName = "temperature",
                DateTime = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                DataSide = DataSide.AnySide,
                Type = DataType.Double,
                Value_Double = 23.5
            });

            await context.SaveChangesAsync();
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ShardingDbContext>();
            var loaded = await context.Set<TelemetryData>()
                .Where(item => item.DeviceId == deviceId && item.KeyName == "temperature")
                .SingleAsync();

            Assert.Equal(23.5, loaded.Value_Double);
            Assert.Equal(DataType.Double, loaded.Type);
        }

        await AssertDeviceIdQueryUsesSecondaryIndexAsync(tableName, deviceId);
    }

    private async Task AssertTelemetryIndexesAsync(string tableName)
    {
        await using var connection = new SndbConnection($"Data Source={_root}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SHOW INDEXES ON \"{tableName}\"";

        var indexes = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            indexes.Add(reader.GetString(0), reader.GetString(2));
        }

        Assert.Contains(indexes, item => item.Value == "DeviceId");
        Assert.Contains(indexes, item => item.Value == "DeviceId,KeyName");
        Assert.Contains(indexes, item => item.Value == "KeyName");
    }

    private async Task AssertDeviceIdQueryUsesSecondaryIndexAsync(string tableName, Guid deviceId)
    {
        await using var connection = new SndbConnection($"Data Source={_root}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"EXPLAIN SELECT \"KeyName\" FROM \"{tableName}\" WHERE \"DeviceId\" = '{deviceId}'";

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0), reader.GetValue(1));
        }

        Assert.Equal("secondary_index", values["access_path"]);
    }
}
