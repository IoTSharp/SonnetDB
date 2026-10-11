using SonnetDB.Engine;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>验证三个切片共享的预检边界，注册函数和嵌套来源不能绕过准入。</summary>
public sealed class SqlTableFunctionMaterializationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("sonnetdb-tvf-contract-").FullName;
    private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(30));

    /// <summary>同名注册回调在触碰文件或候选之前拒绝。</summary>
    [Theory]
    [InlineData("json_each", "json_each('missing-file.json')")]
    [InlineData("json_table", "json_table('missing-file.json')")]
    [InlineData("knn", "knn(points, embedding, [1,0], 1)")]
    [InlineData("vector_search", "vector_search(source => vectors, vector_field => '$.embedding', vector => [1,0], k => 1)")]
    public void Execute_RegisteredBuiltinOverride_RejectsWithoutCallback(string name, string source)
    {
        using Tsdb database = Open();
        int calls = 0;
        database.Functions.RegisterTableValuedFunction(name, (_, _) =>
        {
            calls++;
            return new SelectExecutionResult(["value"], [[1L]]);
        });

        Assert.Throws<NotSupportedException>(() => Select(database, $"SELECT * FROM {source}"));
        Assert.Equal(0, calls);
        Assert.Equal(0, database.SqlMemoryBudget.ReservedBytes);
        Assert.False(SqlRowRetentionBudget.HasExecutionBudget);
    }

    /// <summary>派生来源、CTE 和集合运算不会被顶层直连许可放行。</summary>
    [Theory]
    [InlineData("SELECT * FROM (SELECT id FROM json_each('missing-file.json')) q")]
    [InlineData("WITH q AS (SELECT id FROM json_each('missing-file.json')) SELECT * FROM q")]
    [InlineData("SELECT id FROM json_each('missing-file.json') UNION ALL SELECT 'other'")]
    [InlineData("SELECT id FROM vector_search(source => vectors, vector_field => '$.embedding', vector => [1,0], k => 1) UNION ALL SELECT 'other'")]
    public void Execute_IndirectTableFunction_RejectsBeforeReading(string sql)
    {
        using Tsdb database = Open();

        Assert.Throws<NotSupportedException>(() => Select(database, sql));
        Assert.Equal(0, database.SqlMemoryBudget.ReservedBytes);
        Assert.False(SqlRowRetentionBudget.HasExecutionBudget);
    }

    private Tsdb Open()
    {
        Tsdb database = Tsdb.Open(new TsdbOptions { RootDirectory = Path.Combine(_root, "db"), AllowUserFunctions = true });
        SqlExecutor.Execute(database, "CREATE DOCUMENT COLLECTION vectors");
        SqlExecutor.Execute(database, "CREATE MEASUREMENT points (embedding FIELD VECTOR(2))");
        return database;
    }

    /// <summary>无效内置标量函数参数在触碰文件或候选前拒绝。</summary>
    [Theory]
    [InlineData("json_each('missing-file.json')")]
    [InlineData("knn(points, embedding, [1,0], 1)")]
    [InlineData("vector_search(source => vectors, vector_field => '$.embedding', vector => [1,0], k => 1)")]
    public void Execute_InvalidScalarArity_RejectsBeforeReading(string source)
    {
        using Tsdb database = Open();
        Assert.Throws<NotSupportedException>(() => Select(database, $"SELECT upper() FROM {source}"));
        Assert.Equal(0, database.SqlMemoryBudget.ReservedBytes);
    }

    private object? Select(Tsdb database, string sql)
        => SqlExecutor.Execute(database, databaseName: null, sql, parameters: null, controlPlane: null,
            new SqlExecutionOptions { MaxMaterializedRows = 10, CancellationToken = _deadline.Token });

    /// <summary>释放计时器并回收本测试唯一创建的目录。</summary>
    public void Dispose()
    {
        _deadline.Dispose();
        Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(_root), ignoreCase: true);
        Directory.Delete(_root, recursive: true);
    }
}
