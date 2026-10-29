using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>验证空左 JOIN 避免读取右表，同时保留绑定错误、首行及外连接语义。</summary>
public sealed class RelationalEmptyJoinTests : IDisposable
{
    private readonly string _root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "sndb-empty-join-" + Guid.NewGuid().ToString("N")));

    /// <summary>创建单个测试独占的数据库目录。</summary>
    public RelationalEmptyJoinTests() => Directory.CreateDirectory(_root);

    /// <summary>核对绝对路径与目录归属后回收测试数据库。</summary>
    public void Dispose()
    {
        string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!_root.StartsWith(temp, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(_root).StartsWith("sndb-empty-join-", StringComparison.Ordinal)
            || (File.GetAttributes(_root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("测试目录归属不符。");
        Directory.Delete(_root, recursive: true);
    }

    /// <summary>过滤后的空左侧在哈希、嵌套和 CROSS 算子中都不取得右表读取快照。</summary>
    [Theory]
    [InlineData("JOIN right_rows r ON l.JoinKey = r.JoinKey", false)]
    [InlineData("LEFT JOIN right_rows r ON l.JoinKey = r.JoinKey", false)]
    [InlineData("JOIN right_rows r ON l.JoinKey < r.JoinKey", false)]
    [InlineData("CROSS JOIN right_rows r", false)]
    [InlineData("JOIN right_rows r ON l.JoinKey = r.JoinKey", true)]
    public void Join_FilteredEmptyLeft_DoesNotScanRight(string join, bool preserveOrder)
    {
        using var db = CreateDatabase();
        int rightSnapshots = 0;
        int rightDecoded = 0;
        db.Tables.Open("right_rows").ReadSnapshotAcquiredTestHook = () => rightSnapshots++;
        db.Tables.Open("right_rows").RowDecodedTestHook = _ => rightDecoded++;
        // LEFT/CROSS 的顶层 WHERE 不保证下推，使用派生表明确构造空左输入，避免测试其它规划能力。
        bool derivedLeft = join.StartsWith("LEFT", StringComparison.Ordinal) || join.StartsWith("CROSS", StringComparison.Ordinal);
        string leftSource = derivedLeft ? "(SELECT Id, JoinKey FROM left_rows WHERE BindAt >= 10000)" : "left_rows";
        string filter = derivedLeft ? string.Empty : " WHERE l.BindAt >= 10000";
        var statement = Assert.IsType<SelectStatement>(SqlParser.Parse(
            "SELECT l.Id, r.Id FROM " + leftSource + " l " + join + filter + " ORDER BY l.Id"));
        var metrics = new RelationalSelectExecutionMetrics();

        var actual = preserveOrder
            ? RelationalSelectExecutor.ExecutePreservingJoinOrder(db, statement, metrics)
            : RelationalSelectExecutor.Execute(db, statement, metrics);

        Assert.Empty(actual.Rows);
        Assert.Equal(0, rightSnapshots);
        Assert.Equal(0, rightDecoded);
    }

    /// <summary>物理空左表即使估算选左 build，也不能先扫描右侧 probe。</summary>
    [Fact]
    public void HashJoin_PhysicallyEmptyLeft_DoesNotReadRight()
    {
        using var db = CreateDatabase();
        SqlExecutor.Execute(db, "DELETE FROM left_rows WHERE Id >= 0");
        int snapshots = 0;
        db.Tables.Open("right_rows").ReadSnapshotAcquiredTestHook = () => snapshots++;
        var actual = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT l.Id, r.Id FROM left_rows l JOIN right_rows r ON l.JoinKey = r.JoinKey"));
        Assert.Empty(actual.Rows);
        Assert.Equal(0, snapshots);
    }

    /// <summary>连接非空时首行只读取一次，重复连接键和 NULL 不匹配行为保持一致。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Join_NonEmptyInputs_PreservesFirstRowDuplicatesAndNulls(bool preserveOrder)
    {
        using var db = CreateDatabase();
        var statement = Assert.IsType<SelectStatement>(SqlParser.Parse(
            "SELECT l.Id, r.Id FROM left_rows l JOIN right_rows r ON l.JoinKey = r.JoinKey ORDER BY l.Id, r.Id"));
        var actual = preserveOrder
            ? RelationalSelectExecutor.ExecutePreservingJoinOrder(db, statement)
            : RelationalSelectExecutor.Execute(db, statement);

        Assert.Equal(3, actual.Rows.Count);
        Assert.Equal(new object?[] { 1L, 11L }, actual.Rows[0]);
        Assert.Equal(new object?[] { 1L, 12L }, actual.Rows[1]);
        Assert.Equal(new object?[] { 2L, 13L }, actual.Rows[2]);
    }

    /// <summary>LEFT 的空右侧必须输出全部左未匹配行，且规划明确使用右 build。</summary>
    [Fact]
    public void HashJoin_LeftJoinWithEmptyRight_PreservesLeftAndUsesRightBuild()
    {
        using var db = CreateDatabase();
        SqlExecutor.Execute(db, "DELETE FROM right_rows WHERE Id >= 0");
        var metrics = new RelationalSelectExecutionMetrics();
        var statement = Assert.IsType<SelectStatement>(SqlParser.Parse(
            "SELECT l.Id, r.Id FROM left_rows l LEFT JOIN right_rows r ON l.JoinKey = r.JoinKey ORDER BY l.Id"));

        var actual = RelationalSelectExecutor.Execute(db, statement, metrics);

        Assert.Equal("right", metrics.LastHashJoinBuildSide);
        Assert.Equal(3, actual.Rows.Count);
        Assert.All(actual.Rows, static row => Assert.Null(row[1]));
        Assert.Equal(3, metrics.LastHashJoinActualProbeRows);
        Assert.Equal(0, metrics.LastHashJoinActualBuildRows);
    }

    /// <summary>RIGHT/FULL 不能采用空左短路，必须保留所有右行。</summary>
    [Theory]
    [InlineData("RIGHT")]
    [InlineData("FULL")]
    public void OuterJoin_EmptyLeft_PreservesRight(string kind)
    {
        using var db = CreateDatabase();
        SqlExecutor.Execute(db, "DELETE FROM left_rows WHERE Id >= 0");
        var actual = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT l.Id, r.Id FROM left_rows l " + kind + " JOIN right_rows r ON l.JoinKey = r.JoinKey ORDER BY r.Id"));

        Assert.Equal(4, actual.Rows.Count);
        Assert.All(actual.Rows, static row => Assert.Null(row[0]));
    }

    /// <summary>空结果优化仍必须在读取数据前报告来源、ON、投影和排序的名称绑定错误。</summary>
    [Theory]
    [InlineData("SELECT l.Id FROM left_rows l JOIN missing_table r ON l.JoinKey = r.JoinKey WHERE l.BindAt >= 10000")]
    [InlineData("SELECT l.Id FROM left_rows l JOIN right_rows r ON l.Missing = r.JoinKey WHERE l.BindAt >= 10000")]
    [InlineData("SELECT r.Missing FROM left_rows l JOIN right_rows r ON l.JoinKey = r.JoinKey WHERE l.BindAt >= 10000")]
    [InlineData("SELECT l.Id FROM left_rows l JOIN right_rows r ON l.JoinKey = r.JoinKey WHERE l.BindAt >= 10000 ORDER BY r.Missing")]
    public void Join_EmptyInputWithInvalidNames_StillRejectsQuery(string query)
    {
        using var db = CreateDatabase();
        Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db, query));
    }

    /// <summary>创建无连接键索引的哈希样本以及可快速判定无左候选的时间索引。</summary>
    private Tsdb CreateDatabase()
    {
        var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, "CREATE TABLE left_rows (Id INT NOT NULL, JoinKey INT NULL, BindAt DATETIME NOT NULL, PRIMARY KEY (Id))");
        SqlExecutor.Execute(db, "CREATE TABLE right_rows (Id INT NOT NULL, JoinKey INT NULL, PRIMARY KEY (Id))");
        SqlExecutor.Execute(db, "CREATE INDEX ix_left_bind ON left_rows (BindAt)");
        SqlExecutor.Execute(db, "INSERT INTO left_rows (Id, JoinKey, BindAt) VALUES (1, 10, 0), (2, 20, 1), (3, NULL, 2)");
        SqlExecutor.Execute(db, "INSERT INTO right_rows (Id, JoinKey) VALUES (11, 10), (12, 10), (13, 20), (14, NULL)");
        return db;
    }
}
