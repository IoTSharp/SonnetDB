using SonnetDB.Engine;
using SonnetDB.Hosting;
using SonnetDB.Kv;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>验证注册表为新建和自动加载数据库统一应用 SQL 内部资源配置。</summary>
public sealed class TsdbRegistrySqlExecutionOptionsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sndb-registry-sql-resources-{Guid.NewGuid():N}");

    /// <summary>验证新建数据库使用注册表传入的单查询预算，而不是 Core 默认预算。</summary>
    [Fact]
    public void TryCreate_UsesConfiguredSqlMemoryOptions()
    {
        using var registry = new TsdbRegistry(
            _root,
            broadcaster: null,
            KvOptions.Default,
            CreateForcedSpillOptions());

        Assert.True(registry.TryCreate("created", out Tsdb database));
        SeedSortRows(database);

        AssertConfiguredBudgetForcesSortSpill(database);
    }

    /// <summary>验证启动扫描已有目录时同样使用注册表传入的单查询预算。</summary>
    [Fact]
    public void LoadExisting_UsesConfiguredSqlMemoryOptions()
    {
        string databaseRoot = Path.Combine(_root, "loaded");
        using (Tsdb database = Tsdb.Open(new TsdbOptions { RootDirectory = databaseRoot }))
            SeedSortRows(database);

        using var registry = new TsdbRegistry(
            _root,
            broadcaster: null,
            KvOptions.Default,
            CreateForcedSpillOptions());
        registry.LoadExisting();

        Assert.True(registry.TryGet("loaded", out Tsdb loaded));
        AssertConfiguredBudgetForcesSortSpill(loaded);
    }

    /// <summary>验证原有三参数构造入口继续使用 Core 默认 SQL 资源配置。</summary>
    [Fact]
    public void LegacyConstructor_RemainsCompatible()
    {
        using var registry = new TsdbRegistry(_root, broadcaster: null, KvOptions.Default);

        Assert.True(registry.TryCreate("legacy", out _));
    }

    /// <summary>创建足以强制排序 spill 的数据库级资源配置。</summary>
    /// <returns>单查询预算为 96 字节的 Core SQL 资源选项。</returns>
    private static SqlMemoryOptions CreateForcedSpillOptions()
        => new()
        {
            QueryLimitBytes = 96,
            GlobalLimitBytes = 1024 * 1024,
            MaxParallelWorkers = 1,
            ParallelismMinRows = 1,
            ParallelWorkerMemoryBytes = 64,
        };

    /// <summary>创建测试关系表并写入固定排序语料。</summary>
    /// <param name="database">目标数据库。</param>
    private static void SeedSortRows(Tsdb database)
    {
        SqlExecutor.Execute(database, "CREATE TABLE resource_items (id INT, value INT, PRIMARY KEY (id))");
        database.Tables.Open("resource_items").InsertMany(
            Enumerable.Range(0, 40)
                .Select(static value => (IReadOnlyList<object?>)new object?[] { (long)value, (long)(40 - value) })
                .ToArray());
    }

    /// <summary>执行无单次覆盖预算的排序，并确认数据库默认预算驱动了 spill。</summary>
    /// <param name="database">已写入固定排序语料的数据库。</param>
    private static void AssertConfiguredBudgetForcesSortSpill(Tsdb database)
    {
        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            database,
            "EXPLAIN ANALYZE SELECT id, value FROM resource_items ORDER BY value DESC, id"));
        var values = result.Rows.ToDictionary(
            static row => (string)row[0]!,
            static row => row[1],
            StringComparer.Ordinal);

        Assert.True(Convert.ToInt64(values["actual_spill_count"]) > 0);
        Assert.True(Convert.ToInt64(values["actual_spill_bytes"]) > 0);
        Assert.InRange(Convert.ToInt64(values["actual_peak_memory_bytes"]), 0, 96);
    }

    /// <summary>删除每个测试独占的临时数据库目录。</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
