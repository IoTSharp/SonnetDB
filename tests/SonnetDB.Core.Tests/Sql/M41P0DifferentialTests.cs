using System.Text;
using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>以固定随机数据对拍 M41 #369～#371 快速路径和全扫描参考路径。</summary>
public sealed class M41P0DifferentialTests : IDisposable
{
    private const int RowCount = 512;
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"sndb-m41-p0-differential-{Guid.NewGuid():N}");

    /// <summary>随机键集合的主键 IN semijoin 应与未索引镜像列的关系扫描结果完全一致。</summary>
    [Fact]
    public void InSemijoin_RandomizedKeys_MatchesRelationalScanReference()
    {
        using var db = CreateDatabase();
        var fast = Select(db, """
            SELECT id FROM m41_diff_rows
            WHERE id IN (SELECT target_id FROM m41_diff_keys) AND status = 'ready'
            ORDER BY id
            """);
        var reference = Select(db, """
            SELECT id FROM m41_diff_rows
            WHERE id_ref IN (SELECT target_id FROM m41_diff_keys) AND status = 'ready'
            ORDER BY id
            """);

        Assert.Equal(ReadIds(reference), ReadIds(fast));
    }

    /// <summary>随机分布上的两个索引 OR 分支应与未索引镜像列的全扫描结果完全一致。</summary>
    [Fact]
    public void IndexUnion_RandomizedBranches_MatchesFullScanReference()
    {
        using var db = CreateDatabase();
        var fast = Select(db, """
            SELECT id FROM m41_diff_rows
            WHERE branch_a = 7 OR branch_b >= 850
            ORDER BY id
            """);
        var reference = Select(db, """
            SELECT id FROM m41_diff_rows
            WHERE branch_a_ref = 7 OR branch_b_ref >= 850
            ORDER BY id
            """);

        Assert.Equal(ReadIds(reference), ReadIds(fast));
    }

    /// <summary>随机有符号排序键的反向索引窗口应与未索引列的有界 Top-N 参考结果一致。</summary>
    [Fact]
    public void DescendingTopN_RandomizedSignedKeys_MatchesHeapReference()
    {
        using var db = CreateDatabase();
        var fast = Select(db, """
            SELECT id FROM m41_diff_rows
            ORDER BY sort_key DESC LIMIT 37 OFFSET 11
            """);
        var reference = Select(db, """
            SELECT id FROM m41_diff_rows
            ORDER BY sort_key_ref DESC LIMIT 37 OFFSET 11
            """);

        Assert.Equal(ReadIds(reference), ReadIds(fast));
    }

    /// <summary>删除测试使用的临时数据库目录。</summary>
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

    /// <summary>创建固定种子数据、索引列及其未索引镜像列。</summary>
    private Tsdb CreateDatabase()
    {
        var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db, """
            CREATE TABLE m41_diff_rows (
                id INT,
                id_ref INT,
                branch_a INT,
                branch_a_ref INT,
                branch_b INT,
                branch_b_ref INT,
                sort_key INT NOT NULL,
                sort_key_ref INT NOT NULL,
                status STRING,
                PRIMARY KEY (id)
            )
            """);
        SqlExecutor.Execute(db, "CREATE INDEX ix_m41_diff_a ON m41_diff_rows (branch_a)");
        SqlExecutor.Execute(db, "CREATE INDEX ix_m41_diff_b ON m41_diff_rows (branch_b)");
        SqlExecutor.Execute(db, "CREATE INDEX ix_m41_diff_sort ON m41_diff_rows (sort_key)");
        SqlExecutor.Execute(db, "CREATE TABLE m41_diff_keys (seq INT, target_id INT NULL, PRIMARY KEY (seq))");

        var random = new Random(369_370_371);
        var rows = new StringBuilder("INSERT INTO m41_diff_rows (id, id_ref, branch_a, branch_a_ref, branch_b, branch_b_ref, sort_key, sort_key_ref, status) VALUES ");
        for (int id = 1; id <= RowCount; id++)
        {
            if (id != 1)
                rows.Append(',');
            int branchA = random.Next(0, 32);
            int branchB = random.Next(0, 1024);
            long sortKey = (random.NextInt64(-1_000_000, 1_000_001) * 1024) + id;
            rows.Append('(')
                .Append(id).Append(',')
                .Append(id).Append(',')
                .Append(branchA).Append(',')
                .Append(branchA).Append(',')
                .Append(branchB).Append(',')
                .Append(branchB).Append(',')
                .Append(sortKey).Append(',')
                .Append(sortKey).Append(',')
                .Append((id & 1) == 0 ? "'ready'" : "'blocked'")
                .Append(')');
        }
        SqlExecutor.Execute(db, rows.ToString());

        var keys = new StringBuilder("INSERT INTO m41_diff_keys (seq, target_id) VALUES ");
        for (int seq = 1; seq <= 96; seq++)
        {
            if (seq != 1)
                keys.Append(',');
            keys.Append('(')
                .Append(seq)
                .Append(',')
                .Append(seq % 17 == 0 ? "NULL" : random.Next(1, RowCount + 65))
                .Append(')');
        }
        SqlExecutor.Execute(db, keys.ToString());
        return db;
    }

    /// <summary>执行并断言 SELECT 结果类型。</summary>
    private static SelectExecutionResult Select(Tsdb db, string sql)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, sql));

    /// <summary>读取单列 id 结果。</summary>
    private static long[] ReadIds(SelectExecutionResult result)
        => result.Rows.Select(static row => (long)row[0]!).ToArray();
}
