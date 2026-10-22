using SonnetDB.Engine;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Tables;

/// <summary>验证原子替换读取全部真实唯一键冲突，不把单目标 UPSERT 当作替换。</summary>
public sealed class TableReplaceConflictTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-replace-conflicts-" + Guid.NewGuid().ToString("N"));

    /// <summary>同一候选可以同时替换主键和多个唯一索引命中的不同旧行。</summary>
    [Fact]
    public void FindReplacementConflicts_WithDifferentUniqueOwners_ReturnsEveryOwner()
    {
        using var database = Open();
        CreateItems(database);
        SqlExecutor.Execute(database, "INSERT INTO items(id,email,phone) VALUES (1, 'a', 'p1'), (2, 'b', 'p2'), (3, 'c', 'p3')");

        var conflicts = database.Tables.Open("items").FindReplacementConflicts([1L, "b", "p3"]);

        Assert.Equal([1L, 2L, 3L], conflicts.Select(static row => Assert.IsType<long>(row.Values[0])).Order());
    }

    /// <summary>同一旧行同时命中主键与多个唯一键时只删除一次。</summary>
    [Fact]
    public void FindReplacementConflicts_WithRepeatedOwner_ReturnsOneRow()
    {
        using var database = Open();
        CreateItems(database);
        SqlExecutor.Execute(database, "INSERT INTO items(id,email,phone) VALUES (1, 'a', 'p1')");

        var conflict = Assert.Single(database.Tables.Open("items").FindReplacementConflicts([1L, "a", "p1"]));

        Assert.Equal(1L, conflict.Values[0]);
    }

    /// <summary>可空唯一键保持 NULL 不冲突语义，主键冲突仍生效。</summary>
    [Fact]
    public void FindReplacementConflicts_WithNullUniqueKeys_DoesNotMatchNullOwners()
    {
        using var database = Open();
        CreateItems(database);
        SqlExecutor.Execute(database, "INSERT INTO items(id,email,phone) VALUES (1, NULL, NULL), (2, NULL, NULL)");
        var store = database.Tables.Open("items");

        Assert.Empty(store.FindReplacementConflicts([3L, null, null]));
        Assert.Equal(1L, Assert.Single(store.FindReplacementConflicts([1L, null, null])).Values[0]);
    }

    /// <summary>联合唯一键必须匹配完整编码，不能只按首列误删其它行。</summary>
    [Fact]
    public void FindReplacementConflicts_WithCompositeUniqueKey_MatchesAllColumns()
    {
        using var database = Open();
        SqlExecutor.Execute(database, "CREATE TABLE items (id INT, tenant INT, name STRING, PRIMARY KEY(id))");
        SqlExecutor.Execute(database, "CREATE UNIQUE INDEX by_name ON items (tenant, name)");
        SqlExecutor.Execute(database, "INSERT INTO items(id,tenant,name) VALUES (1, 10, 'same'), (2, 20, 'same')");
        var store = database.Tables.Open("items");

        Assert.Empty(store.FindReplacementConflicts([3L, 30L, "same"]));
        Assert.Equal(2L, Assert.Single(store.FindReplacementConflicts([3L, 20L, "same"])).Values[0]);
    }

    /// <summary>无显式主键的关系表仍通过真实唯一索引定位物理行身份。</summary>
    [Fact]
    public void FindReplacementConflicts_WithoutPrimaryKey_UsesUniqueIndexOwner()
    {
        using var database = Open();
        SqlExecutor.Execute(database, "CREATE TABLE items (name STRING, value INT)");
        SqlExecutor.Execute(database, "CREATE UNIQUE INDEX by_name ON items (name)");
        SqlExecutor.Execute(database, "INSERT INTO items(name,value) VALUES ('same', 1)");

        var conflict = Assert.Single(database.Tables.Open("items").FindReplacementConflicts(["same", 2L]));

        Assert.Equal("same", conflict.Values[0]);
        Assert.Equal(1L, conflict.Values[1]);
        Assert.False(conflict.PrimaryKey.IsEmpty);
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });

    private static void CreateItems(Tsdb database)
    {
        SqlExecutor.Execute(database, "CREATE TABLE items (id INT, email STRING, phone STRING, PRIMARY KEY(id))");
        SqlExecutor.Execute(database, "CREATE UNIQUE INDEX by_email ON items (email)");
        SqlExecutor.Execute(database, "CREATE UNIQUE INDEX by_phone ON items (phone)");
    }

    /// <summary>释放测试独占数据库目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
