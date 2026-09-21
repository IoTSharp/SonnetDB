using SonnetDB.Engine;
using SonnetDB.Query.Functions;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>
/// GH-Issue #180：验证 SQL JSON 标量、数组和包含查询的确定性边界。
/// </summary>
public sealed class SqlJsonFunctionTests : IDisposable
{
    private readonly string _root;

    public SqlJsonFunctionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sndb-json-functions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private TsdbOptions Options() => new() { RootDirectory = _root };

    private static SelectExecutionResult Query(Tsdb db, string sql, SqlParameters parameters)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            db,
            databaseName: null,
            sql,
            parameters,
            controlPlane: null));

    [Fact]
    public void Execute_RelationJsonFunctions_SupportParameterizedPathAndContainment()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, metadata JSON, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, """
            INSERT INTO devices (id, metadata) VALUES
                (1, '{"tags":["industrial","pump"],"owner":{"name":"alice","active":true},"deleted":null}'),
                (2, '{"tags":[],"owner":{"name":"bob"}}'),
                (3, '{"owner":null}')
            """);

        var result = Query(db, """
            SELECT id,
                   json_array_length(metadata, @tags_path) AS tag_count,
                   json_exists(metadata, @first_path) AS has_first,
                   json_exists(metadata, @deleted_path) AS has_deleted,
                   json_contains(metadata, @tags_path, @member) AS has_member,
                   json_contains(metadata, '$.tags', '["industrial"]') AS has_array,
                   json_contains(metadata, '$.owner', 'name') AS has_owner_field,
                   json_contains(metadata, '$.owner', '{"active":true}') AS has_owner_subset
            FROM devices
            ORDER BY id
            """, new SqlParameters()
            .AddNamed("tags_path", "$.tags")
            .AddNamed("first_path", "$.tags[0]")
            .AddNamed("deleted_path", "$.deleted")
            .AddNamed("member", "industrial"));

        Assert.Equal(3, result.Rows.Count);

        Assert.Equal(1L, result.Rows[0][0]);
        Assert.Equal(2L, result.Rows[0][1]);
        Assert.Equal(true, result.Rows[0][2]);
        Assert.Equal(true, result.Rows[0][3]);
        Assert.Equal(true, result.Rows[0][4]);
        Assert.Equal(true, result.Rows[0][5]);
        Assert.Equal(true, result.Rows[0][6]);
        Assert.Equal(true, result.Rows[0][7]);

        Assert.Equal(2L, result.Rows[1][0]);
        Assert.Equal(0L, result.Rows[1][1]);
        Assert.Equal(false, result.Rows[1][2]);
        Assert.Equal(false, result.Rows[1][3]);
        Assert.Equal(false, result.Rows[1][4]);
        Assert.Equal(false, result.Rows[1][5]);
        Assert.Equal(true, result.Rows[1][6]);
        Assert.Equal(false, result.Rows[1][7]);

        Assert.Equal(3L, result.Rows[2][0]);
        Assert.Null(result.Rows[2][1]);
        Assert.Equal(false, result.Rows[2][2]);
        Assert.Equal(false, result.Rows[2][3]);
        Assert.Equal(false, result.Rows[2][4]);
        Assert.Equal(false, result.Rows[2][5]);
        Assert.Equal(false, result.Rows[2][6]);
        Assert.Equal(false, result.Rows[2][7]);
    }

    [Fact]
    public void Execute_RelationJsonFunctions_RejectInvalidPathAndNonArray()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE devices (id INT, metadata JSON, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO devices (id, metadata) VALUES (1, '{\"owner\":{\"name\":\"alice\"}}')");

        var nonArray = Assert.Throws<InvalidOperationException>(() =>
            Query(db, "SELECT json_array_length(metadata, '$.owner') FROM devices", new SqlParameters()));
        Assert.Contains("必须是 JSON 数组", nonArray.Message, StringComparison.Ordinal);

        var invalidPath = Assert.Throws<InvalidOperationException>(() =>
            Query(db, "SELECT json_exists(metadata, '$.owner[') FROM devices", new SqlParameters()));
        Assert.Contains("JSON path 无效", invalidPath.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Execute_DocumentJsonFunctions_ReuseTheSameScalarContract()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE DOCUMENT COLLECTION device_docs");
        SqlExecutor.Execute(db, """
            INSERT INTO device_docs (id, document) VALUES
                ('a', '{"tags":["north","pump"],"profile":{"role":"operator"}}'),
                ('b', '{"tags":["south"]}')
            """);

        var result = Query(db, """
            SELECT id,
                   json_array_length(document, '$.tags') AS tag_count,
                   json_contains(document, '$.tags', 'pump') AS has_pump,
                   json_contains(document, '$.profile', 'role') AS has_role
            FROM device_docs
            ORDER BY id
            """, new SqlParameters());

        Assert.Equal(["a", "b"], result.Rows.Select(row => (string)row[0]!).ToArray());
        Assert.Equal(2L, result.Rows[0][1]);
        Assert.Equal(true, result.Rows[0][2]);
        Assert.Equal(true, result.Rows[0][3]);
        Assert.Equal(1L, result.Rows[1][1]);
        Assert.Equal(false, result.Rows[1][2]);
        Assert.Equal(false, result.Rows[1][3]);
    }

    [Fact]
    public void JsonFunctions_NullAndResourceBoundaries_AreDeterministic()
    {
        Assert.True(FunctionRegistry.TryGetScalar("json_exists", out var exists));
        Assert.True(FunctionRegistry.TryGetScalar("json_array_length", out var arrayLength));
        Assert.True(FunctionRegistry.TryGetScalar("json_contains", out var contains));

        Assert.Null(exists.Evaluate([null, "$.value"]));
        Assert.Null(arrayLength.Evaluate(["{\"value\":null}", "$.value"]));
        Assert.Null(contains.Evaluate(["{\"value\":1}", "$.value", null]));
        Assert.Equal(true, contains.Evaluate(["[\"\\u0061\"]", "$", "[\"a\"]"]));

        string deepJson = "{" + string.Concat(Enumerable.Repeat("\"x\":{", 65))
            + "0" + new string('}', 65) + "}";
        var deep = Assert.Throws<InvalidOperationException>(() => exists.Evaluate([deepJson, "$"]));
        Assert.Contains("嵌套深度", deep.Message, StringComparison.Ordinal);

        string largeArray = "[" + string.Join(',', Enumerable.Repeat("0", 100_001)) + "]";
        var large = Assert.Throws<InvalidOperationException>(() => arrayLength.Evaluate([largeArray, "$"]));
        Assert.Contains("元素数超过", large.Message, StringComparison.Ordinal);

        string comparisonTarget = "[" + string.Join(',', Enumerable.Range(0, 1_500)) + "]";
        string comparisonCandidate = "[" + string.Join(',', Enumerable.Range(0, 1_500).Reverse()) + "]";
        var comparison = Assert.Throws<InvalidOperationException>(() =>
            contains.Evaluate([comparisonTarget, "$", comparisonCandidate]));
        Assert.Contains("比较次数超过", comparison.Message, StringComparison.Ordinal);

        string objectTarget = "{" + string.Join(',', Enumerable.Range(0, 1_500).Select(index => $"\"p{index}\":{index}")) + "}";
        string objectCandidate = "{" + string.Join(',', Enumerable.Range(0, 1_500).Reverse().Select(index => $"\"p{index}\":{index}")) + "}";
        var objectComparison = Assert.Throws<InvalidOperationException>(() =>
            contains.Evaluate([objectTarget, "$", objectCandidate]));
        Assert.Contains("比较次数超过", objectComparison.Message, StringComparison.Ordinal);
    }
}
