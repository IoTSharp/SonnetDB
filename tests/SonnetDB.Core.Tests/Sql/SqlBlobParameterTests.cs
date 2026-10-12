using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>验证 #271 的二进制参数类型、内容比较和索引访问合同。</summary>
public sealed class SqlBlobParameterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-blob-" + Guid.NewGuid().ToString("N"));

    /// <summary>明确的十六进制字面量保留 BLOB 类型，包括空值和高位字节。</summary>
    [Theory]
    [InlineData("X'00017F80FF'", "00017F80FF")]
    [InlineData("x'00ff'", "00FF")]
    [InlineData("X''", "")]
    public void Parse_BinaryLiteral_PreservesType(string sql, string expectedHex)
    {
        var statement = Assert.IsType<SelectStatement>(SqlParser.Parse($"SELECT {sql} FROM credentials"));
        var literal = Assert.IsType<LiteralExpression>(Assert.Single(statement.Projections).Expression);
        Assert.Equal(SqlLiteralKind.Blob, literal.Kind);
        Assert.Equal(expectedHex, Convert.ToHexString(literal.BlobValue.Span));
    }

    /// <summary>非法十六进制内容不能退回字符串或被部分解析。</summary>
    [Theory]
    [InlineData("X'0'")]
    [InlineData("X'GG'")]
    [InlineData("X'00 01'")]
    [InlineData("X'00;01'")]
    public void Parse_InvalidBinaryLiteral_Rejects(string sql)
        => Assert.Throws<SqlParseException>(() => SqlParser.Parse($"SELECT {sql} FROM credentials"));

    /// <summary>参数绑定复制数组，重复使用缓存 AST 时不污染其它参数值。</summary>
    [Fact]
    public void Bind_BinaryParameter_CopiesInputAndPreservesCachedPlaceholder()
    {
        const string sql = "SELECT id FROM credentials WHERE credential = @key";
        var parsed = Assert.IsType<SelectStatement>(SqlParser.Parse(sql));
        byte[] key = [0, 1, 127, 128, 255];
        var first = Assert.IsType<SelectStatement>(SqlParameterBinder.Bind(parsed, new SqlParameters().AddNamed("key", key)));
        key[0] = 9;
        var second = Assert.IsType<SelectStatement>(SqlParameterBinder.Bind(parsed, new SqlParameters().AddNamed("key", key)));
        var firstLiteral = Assert.IsType<LiteralExpression>(Assert.IsType<BinaryExpression>(first.Where).Right);
        var secondLiteral = Assert.IsType<LiteralExpression>(Assert.IsType<BinaryExpression>(second.Where).Right);
        Assert.Equal("00017F80FF", Convert.ToHexString(firstLiteral.BlobValue.Span));
        Assert.Equal("09017F80FF", Convert.ToHexString(secondLiteral.BlobValue.Span));
        Assert.IsType<ParameterExpression>(Assert.IsType<BinaryExpression>(parsed.Where).Right);
        Assert.Same(parsed, SqlParser.Parse(sql));
    }

    /// <summary>普通查询和相关 EXISTS 保留主键/二级索引访问，且不用字符串猜测二进制。</summary>
    [Fact]
    public void Execute_BinaryEquality_UsesPrimaryAndSecondaryIndexes()
    {
        using var db = Open();
        SqlExecutor.Execute(db, "CREATE TABLE owners (id INT, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO owners (id) VALUES (1), (2)");
        SqlExecutor.Execute(db, "CREATE TABLE credentials (credential BLOB, owner INT, payload BLOB NULL, PRIMARY KEY (credential))");
        SqlExecutor.Execute(db, "CREATE INDEX ix_credentials_payload ON credentials (payload)");
        Execute(db, "INSERT INTO credentials (credential, owner, payload) VALUES (@key, 1, @key)", new SqlParameters().AddNamed("key", new byte[] { 0, 1, 127, 128, 255 }));
        Execute(db, "INSERT INTO credentials (credential, owner, payload) VALUES (@key, 2, NULL)", new SqlParameters().AddNamed("key", new byte[] { 9, 8, 7 }));
        Execute(db, "INSERT INTO credentials (credential, owner, payload) VALUES (@key, 1, @key)", new SqlParameters().AddNamed("key", Array.Empty<byte>()));
        var store = db.Tables.Open("credentials");
        long scansBefore = store.FullScanCount;
        long lookupsBefore = store.PrimaryKeyLookupCount;
        var key = new byte[] { 0, 1, 127, 128, 255 };
        Assert.Equal(1L, Assert.Single(Query(db, "SELECT owner FROM credentials WHERE credential = @key", key).Rows)[0]);
        Assert.True(store.PrimaryKeyLookupCount > lookupsBefore);
        Assert.Equal(scansBefore, store.FullScanCount);

        var primary = QueryWithMetrics(db, "SELECT o.id FROM owners o WHERE EXISTS (SELECT 1 FROM credentials c WHERE c.owner = o.id AND c.credential = @key)", key);
        Assert.Equal(1L, Assert.Single(primary.Result.Rows)[0]);
        Assert.Equal("primary_key", primary.Metrics.LastExistsAccessPath);
        Assert.Null(primary.Metrics.LastExistsFallbackReason);
        var secondary = QueryWithMetrics(db, "SELECT o.id FROM owners o WHERE EXISTS (SELECT 1 FROM credentials c WHERE c.owner = o.id AND c.payload = @key)", key);
        Assert.Equal(1L, Assert.Single(secondary.Result.Rows)[0]);
        Assert.Equal("secondary_index", secondary.Metrics.LastExistsAccessPath);
        Assert.Equal("ix_credentials_payload", secondary.Metrics.LastExistsIndexName);
        Assert.Equal(scansBefore, store.FullScanCount);
        Assert.Empty(Query(db, "SELECT owner FROM credentials WHERE credential = @key", [0, 1, 127, 128]).Rows);
        Assert.Empty(Query(db, "SELECT owner FROM credentials WHERE credential = @key", [4, 3, 2]).Rows);
        Assert.Equal(1L, Assert.Single(Query(db, "SELECT owner FROM credentials WHERE credential = @key", []).Rows)[0]);
        Assert.Empty(Query(db, "SELECT owner FROM credentials WHERE payload = @key", null).Rows);
        Assert.Single(Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "SELECT owner FROM credentials WHERE payload IS NULL")).Rows);
        Assert.Equal(2, Query(db, "SELECT owner FROM credentials WHERE credential != @key", key).Rows.Count);
        Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db, "SELECT owner FROM credentials WHERE credential = 'AAF/gP8='"));
        Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db, "SELECT owner FROM credentials WHERE credential < X'00017F80FF'"));
    }

    /// <summary>字面量查询结果不能暴露解析缓存中的可变数组；已有字符串列也不接受 BLOB 参数。</summary>
    [Fact]
    public void Execute_BinaryProjection_DoesNotMutateCachedLiteral()
    {
        using var db = Open();
        SqlExecutor.Execute(db, "CREATE TABLE samples (id INT, label STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO samples (id, label) VALUES (1, 'AA==')");
        var first = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "SELECT X'00FF' FROM samples"));
        Assert.IsType<byte[]>(Assert.Single(first.Rows)[0])[0] = 42;
        var second = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "SELECT X'00FF' FROM samples"));
        Assert.Equal(new byte[] { 0, 255 }, Assert.IsType<byte[]>(Assert.Single(second.Rows)[0]));
        Assert.Throws<InvalidOperationException>(() => Query(db, "SELECT id FROM samples WHERE label = @key", [0]));
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });

    private static void Execute(Tsdb db, string sql, SqlParameters parameters)
        => SqlExecutor.Execute(db, databaseName: null, sql, parameters, controlPlane: null);

    private static SelectExecutionResult Query(Tsdb db, string sql, byte[]? key)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, databaseName: null, sql,
            new SqlParameters().AddNamed("key", key), controlPlane: null));

    private static (SelectExecutionResult Result, RelationalSelectExecutionMetrics Metrics) QueryWithMetrics(Tsdb db, string sql, byte[] key)
    {
        var statement = Assert.IsType<SelectStatement>(SqlParameterBinder.Bind(SqlParser.Parse(sql), new SqlParameters().AddNamed("key", key)));
        var metrics = new RelationalSelectExecutionMetrics();
        return (RelationalSelectExecutor.Execute(db, statement, metrics), metrics);
    }

    /// <summary>回收本测试独占的数据库目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
