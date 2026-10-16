using SonnetDB.Engine;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

public sealed partial class SqlMaintenanceContractTests
{
    /// <summary>方向必须改变磁盘索引遍历顺序，包含列须从持久索引项读取。</summary>
    [Theory]
    [InlineData("", false)]
    [InlineData("ASC", false)]
    [InlineData("DESC", true)]
    public void IndexDirection_Reopen_ChangesPhysicalOrder(string direction, bool descending)
    {
        using (var db = Open())
        {
            Execute(db, "CREATE TABLE Device(Id INT,Rank INT,Payload STRING,PRIMARY KEY(Id))");
            Execute(db, $"CREATE INDEX IxOrder ON Device(Rank {direction}) INCLUDE(Payload)");
            Execute(db, "INSERT INTO Device(Id,Rank,Payload) VALUES(3,20,'twenty'),(1,30,'thirty'),(2,10,'ten')");
        }

        using var reopened = Open();
        TableStore store = reopened.Tables.Open("Device");
        int decoded = 0;
        store.RowDecodedTestHook = _ => decoded++;
        TableRow[] rows = store.EnumerateCoveredIndex(Assert.Single(store.Schema.Indexes), []).ToArray();
        Assert.Equal(descending ? new object?[] { 30L, 20L, 10L } : [10L, 20L, 30L], rows.Select(row => row.Values[1]));
        Assert.Equal(descending ? new object?[] { "thirty", "twenty", "ten" } : ["ten", "twenty", "thirty"], rows.Select(row => row.Values[2]));
        Assert.Equal(0, decoded);
        Assert.Equal(3, rows.Select(row => Convert.ToHexString(row.PrimaryKey.Span)).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>声明方向与请求方向的四种组合都须保留跨零范围和有界 Top-N 的结果。</summary>
    [Theory]
    [InlineData("ASC", "ASC")]
    [InlineData("ASC", "DESC")]
    [InlineData("DESC", "ASC")]
    [InlineData("DESC", "DESC")]
    public void IndexDirection_SignedRangeAndTopN_MatchesReference(string declaredDirection, string requestedDirection)
    {
        using var db = Open();
        Execute(db, "CREATE TABLE Device(Id INT,Rank INT NOT NULL,ReferenceRank INT NOT NULL,Payload STRING,PRIMARY KEY(Id))");
        Execute(db, $"CREATE INDEX IxOrder ON Device(Rank {declaredDirection}) INCLUDE(Payload)");
        long[] ranks = [long.MinValue, -100, -3, -1, 0, 1, 3, 100, long.MaxValue];
        TableStore store = db.Tables.Open("Device");
        store.InsertMany(ranks.Select((rank, i) => (IReadOnlyList<object?>)new object?[] { (long)i + 1, rank, rank, $"row-{i}" }).ToArray());
        var reference = Query(db, $"SELECT Id,ReferenceRank FROM Device WHERE ReferenceRank>=-100 AND ReferenceRank<=100 ORDER BY ReferenceRank {requestedDirection} LIMIT 3 OFFSET 1");
        var limits = new List<int>();
        store.RangeScanLimitTestHook = limits.Add;
        var actual = Query(db, $"SELECT Id,Rank FROM Device WHERE Rank>=-100 AND Rank<=100 ORDER BY Rank {requestedDirection} LIMIT 3 OFFSET 1");
        Assert.Equal(reference.Rows.Select(row => row[0]), actual.Rows.Select(row => row[0]));
        Assert.Equal(reference.Rows.Select(row => row[1]), actual.Rows.Select(row => row[1]));
        Assert.NotEmpty(limits);
        Assert.All(limits, limit => Assert.InRange(limit, 4, 5));
        store.RangeScanLimitTestHook = null;

        var expected = requestedDirection == "DESC" ? ranks.Reverse() : ranks;
        Assert.Equal(expected, Query(db, $"SELECT Rank FROM Device ORDER BY Rank {requestedDirection}").Rows.Select(row => (long)row[0]!));
    }

    /// <summary>包含列的事务提交、回滚及键迁移不能留下旧载荷或错误的主行身份。</summary>
    [Fact]
    public void IncludedIndex_TransactionsAndPrimaryKeyRemoval_PreservePayloadAndIdentity()
    {
        using (var db = Open())
        {
            Execute(db, "CREATE TABLE Device(Id INT,Code STRING,Payload STRING,CONSTRAINT PkDevice PRIMARY KEY(Id))");
            Execute(db, "CREATE INDEX IxCovered ON Device(Code DESC) INCLUDE(Payload)");
            Execute(db, "INSERT INTO Device(Id,Code,Payload) VALUES(1,'a','original')");
            SqlExecutor.ExecuteScript(db, "BEGIN; UPDATE Device SET Payload='rolled-back' WHERE Id=1; ROLLBACK;");
            Assert.Equal("original", Query(db, "SELECT Payload FROM Device WHERE Code='a'").Rows.Single()[0]);
            SqlExecutor.ExecuteScript(db, "BEGIN; UPDATE Device SET Payload='committed' WHERE Id=1; INSERT INTO Device(Id,Code,Payload) VALUES(2,'b','second'); COMMIT;");
            Execute(db, "ALTER TABLE Device DROP CONSTRAINT PkDevice");
            Execute(db, "INSERT INTO Device(Id,Code,Payload) VALUES(1,'a','duplicate-id')");
        }

        using var reopened = Open();
        int decoded = 0;
        TableStore store = reopened.Tables.Open("Device");
        store.RowDecodedTestHook = _ => decoded++;
        Assert.Equal(new object?[] { "committed", "duplicate-id" }, Query(reopened, "SELECT Payload FROM Device WHERE Code='a' ORDER BY Payload").Rows.Select(row => row[0]));
        Assert.Equal("second", Query(reopened, "SELECT Payload FROM Device WHERE Code='b'").Rows.Single()[0]);
        Assert.Equal(0, decoded);
        Assert.Equal(3, store.Scan().Select(row => Convert.ToHexString(row.PrimaryKey.Span)).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>在线索引断点必须保存方向和包含列，并把暂停期间的变更补入续建结果。</summary>
    [Fact]
    public void IncludedIndex_OnlineCancellationAndReopen_ResumesExactDefinition()
    {
        const string createSql = "CREATE INDEX IxOnline ON Device(Rank DESC) INCLUDE(Payload) ONLINE";
        using (var db = Open())
        {
            Execute(db, "CREATE TABLE Device(Id INT,Rank INT,Payload STRING,PRIMARY KEY(Id))");
            string values = string.Join(',', Enumerable.Range(1, 65).Select(i => $"({i},{i},'row-{i}')"));
            Execute(db, $"INSERT INTO Device(Id,Rank,Payload) VALUES{values}");
            using var cancellationDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationDeadline.Token);
            int completedPages = 0;
            db.Tables.OnlineIndexPageCompletedTestHook = _ =>
            {
                completedPages++;
                cancellation.Cancel();
            };
            for (int attempt = 0; attempt < 32 && completedPages == 0; attempt++)
            {
                try
                {
                    var yielded = Assert.IsType<RowsAffectedExecutionResult>(ExecuteWithCancellation(db, createSql, cancellation.Token));
                    Assert.Equal("create_index_online_pending", yielded.Operation);
                }
                catch (OperationCanceledException) when (completedPages == 1 && cancellation.IsCancellationRequested)
                {
                }
            }
            Assert.Equal(1, completedPages);
            Assert.True(cancellation.IsCancellationRequested);
            Assert.False(cancellationDeadline.IsCancellationRequested);
            db.Tables.OnlineIndexPageCompletedTestHook = null;
            Assert.Empty(db.Tables.Catalog.TryGet("Device")!.Indexes);
        }

        using var reopened = Open();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Assert.Empty(reopened.Tables.Catalog.TryGet("Device")!.Indexes);
        ExecuteWithCancellation(reopened, "SELECT Id FROM Device LIMIT 1", deadline.Token);
        Assert.Throws<InvalidOperationException>(() => ExecuteWithCancellation(reopened,
            "CREATE INDEX IxOnline ON Device(Rank ASC) INCLUDE(Payload) ONLINE", deadline.Token));
        Assert.Throws<InvalidOperationException>(() => ExecuteWithCancellation(reopened,
            "CREATE INDEX IxOnline ON Device(Rank DESC) INCLUDE(Id) ONLINE", deadline.Token));
        ExecuteWithCancellation(reopened, "UPDATE Device SET Payload='updated-scanned' WHERE Id=1", deadline.Token);
        ExecuteWithCancellation(reopened, "UPDATE Device SET Payload='updated-unscanned' WHERE Id=65", deadline.Token);
        ExecuteWithCancellation(reopened, "DELETE FROM Device WHERE Id=2", deadline.Token);
        ExecuteWithCancellation(reopened, "INSERT INTO Device(Id,Rank,Payload) VALUES(66,66,'inserted')", deadline.Token);

        bool completed = false;
        for (int attempt = 0; attempt < 32 && !completed; attempt++)
        {
            var result = Assert.IsType<RowsAffectedExecutionResult>(ExecuteWithCancellation(reopened, createSql, deadline.Token));
            completed = result.Operation == "create_index_online_complete";
            Assert.True(completed || result.Operation == "create_index_online_pending");
        }
        Assert.True(completed, "包含列在线索引没有在取消预算及有界重试内完成。");
        TableStore store = reopened.Tables.Open("Device");
        int decoded = 0;
        store.RowDecodedTestHook = _ => decoded++;
        var rows = store.EnumerateCoveredIndex(Assert.Single(store.Schema.Indexes), [], cancellationToken: deadline.Token).ToArray();
        Assert.Equal(Enumerable.Range(1, 66).Where(value => value != 2).Reverse().Select(value => (object?)(long)value), rows.Select(row => row.Values[1]));
        Assert.Equal("inserted", rows[0].Values[2]);
        Assert.Equal("updated-unscanned", rows[1].Values[2]);
        Assert.Equal("updated-scanned", rows[^1].Values[2]);
        Assert.Equal(0, decoded);
    }

    /// <summary>成功创建支持新选项的索引后再测试错误输入，避免把未支持语法当作正确拒绝。</summary>
    [Theory]
    [InlineData("CREATE INDEX IxInvalid ON Device(Code DESC) INCLUDE(Missing)")]
    [InlineData("CREATE INDEX IxInvalid ON Device(Code DESC) INCLUDE(Code)")]
    [InlineData("CREATE INDEX IxInvalid ON Device(Code DESC) INCLUDE(Payload,payload)")]
    [InlineData("CREATE INDEX IxInvalid ON Device(Code DESC,code ASC) INCLUDE(Payload)")]
    public void IncludedIndex_InvalidColumns_RejectsWithoutPublishing(string invalidSql)
    {
        using var db = Open();
        Execute(db, "CREATE TABLE Device(Id INT,Code STRING,Payload STRING,PRIMARY KEY(Id))");
        Execute(db, "CREATE INDEX IxControl ON Device(Code DESC) INCLUDE(Payload)");
        Execute(db, "INSERT INTO Device(Id,Code,Payload) VALUES(1,'a','value')");
        Assert.ThrowsAny<Exception>(() => Execute(db, invalidSql));
        Assert.Equal("IxControl", Assert.Single(db.Tables.Catalog.TryGet("Device")!.Indexes).Name);
        Assert.Equal("value", Query(db, "SELECT Payload FROM Device WHERE Code='a'").Rows.Single()[0]);
    }

    private static object? ExecuteWithCancellation(Tsdb db, string sql, CancellationToken cancellationToken)
        => SqlExecutor.Execute(db, null, sql, null, null, new SqlExecutionOptions { CancellationToken = cancellationToken });
}
