using System.Globalization;
using System.Text.Json;
using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.Query.Functions;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Documents;

/// <summary>#249：独立动态 JSON 辅助组件的保真、资源与诊断合同；不代表 SQL 接线已完成。</summary>
public sealed class JsonDynamicSqlFunctionsTests
{
    /// <summary>动态 path 与静态 path 使用同一值与 CLR 类型合同。</summary>
    [Theory]
    [InlineData("$.text")]
    [InlineData("$.active")]
    [InlineData("$.integer")]
    [InlineData("$.fraction")]
    [InlineData("$.nil")]
    [InlineData("$.missing")]
    [InlineData("$.objects[0].name")]
    [InlineData("$.objects")]
    [InlineData("$['quote''key']")]
    public void Value_DynamicPath_MatchesExistingStaticValueAndType(string path)
    {
        const string json = """{"text":"中文","active":true,"integer":9007199254740993,"fraction":1.25,"nil":null,"objects":[{"name":"pump"}],"quote'key":"escaped"}""";
        object? expected = JsonPathEvaluator.Evaluate(json, path);
        object? actual = JsonDynamicSqlFunctions.Value([json, path]);
        Assert.Equal(expected, actual);
        Assert.Equal(expected?.GetType(), actual?.GetType());
    }

    /// <summary>缺失、越界、JSON null 和 SQL NULL 均返回 SQL NULL，不截断或取模数组下标。</summary>
    [Theory]
    [InlineData(null, "$[0]")]
    [InlineData("[1]", null)]
    [InlineData("[1]", "$[2]")]
    [InlineData("[null]", "$[0]")]
    [InlineData("{}", "$.missing")]
    public void Value_NullAndMissing_ReturnNull(string? json, string? path)
        => Assert.Null(JsonDynamicSqlFunctions.Value([json, path]));

    /// <summary>非法动态路径产生明确诊断并保留路径解析原因。</summary>
    [Theory]
    [InlineData("invalid")]
    [InlineData("$[-1]")]
    [InlineData("$[*]")]
    [InlineData("$[2147483648]")]
    [InlineData("$.field[")]
    public void Value_InvalidPath_ReportsPathDiagnostic(string path)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => JsonDynamicSqlFunctions.Value(["{}", path]));
        Assert.Contains("JSON path 无效", exception.Message, StringComparison.Ordinal);
        Assert.IsType<ArgumentException>(exception.InnerException);
    }

    /// <summary>非字符串 path、过长路径、过深路径与非法 JSON 在固定预算内拒绝。</summary>
    [Fact]
    public void Value_InvalidTypesAndBudgets_RejectExplicitly()
    {
        Assert.Throws<InvalidOperationException>(() => JsonDynamicSqlFunctions.Value(["{}", 1L]));
        Assert.Throws<InvalidOperationException>(() => JsonDynamicSqlFunctions.Value([true, "$"]));
        Assert.Throws<InvalidOperationException>(() => JsonDynamicSqlFunctions.Value(["{}", "$" + new string('a', 1024)]));
        Assert.Throws<InvalidOperationException>(() => JsonDynamicSqlFunctions.Value(["{}", "$" + string.Concat(Enumerable.Repeat(".x", 65))]));
        Assert.Throws<InvalidOperationException>(() => JsonDynamicSqlFunctions.Value(["{invalid", "$"]));
        Assert.Throws<InvalidOperationException>(() => JsonDynamicSqlFunctions.Value([new string(' ', 8 * 1024 * 1024 + 1), "$"]));
    }

    /// <summary>构造器保持字符串、布尔、整数、精确 decimal、null 和字段名转义。</summary>
    [Fact]
    public void CreateObject_TypedValues_PreserveJsonScalarKindsAndDecimal()
    {
        const decimal amount = 1234567890123456.123456789012m;
        const string escaped = "quote'\"\\\n中文";
        string json = JsonDynamicSqlFunctions.CreateObject([
            "text", escaped, "bool", true, "integer", 9007199254740993L,
            "amount", amount, "nil", null, "quote'\"key", escaped, "raw", "{\"x\":1}"]);
        using var parsed = JsonDocument.Parse(json);
        JsonElement root = parsed.RootElement;
        Assert.Equal(escaped, root.GetProperty("text").GetString());
        Assert.True(root.GetProperty("bool").GetBoolean());
        Assert.Equal(9007199254740993L, root.GetProperty("integer").GetInt64());
        Assert.Equal(amount, root.GetProperty("amount").GetDecimal());
        Assert.Equal(amount.ToString(CultureInfo.InvariantCulture), root.GetProperty("amount").GetRawText());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("nil").ValueKind);
        Assert.Equal(escaped, root.GetProperty("quote'\"key").GetString());
        Assert.Equal(JsonValueKind.String, root.GetProperty("raw").ValueKind);
        Assert.Equal("{\"x\":1}", root.GetProperty("raw").GetString());
        Assert.Equal("{}", JsonDynamicSqlFunctions.CreateObject([]));
    }

    /// <summary>对象包含能区别在 double 中相同的相邻 decimal，且多个字段必须属于同一个数组元素。</summary>
    [Fact]
    public void CreateObject_ArrayObjectPredicate_PreservesExactDecimalAndSameElement()
    {
        Assert.True(FunctionRegistry.TryGetScalar("json_contains", out var contains));
        const decimal target = 1234567890123456.123456789012m;
        const decimal other = 1234567890123456.123456789013m;
        Assert.Equal((double)target, (double)other);
        string json = "[{\"amount\":" + target.ToString(CultureInfo.InvariantCulture) + ",\"nil\":null}]";
        Assert.Equal(true, contains.Evaluate([json, "$", JsonDynamicSqlFunctions.CreateObject(["amount", target, "nil", null])]));
        Assert.Equal(false, contains.Evaluate([json, "$", JsonDynamicSqlFunctions.CreateObject(["amount", other])]));
        Assert.Equal(false, contains.Evaluate(["[{\"x\":1},{\"y\":2}]", "$", JsonDynamicSqlFunctions.CreateObject(["x", 1L, "y", 2L])]));
    }

    /// <summary>无效字段名、重复字段、非有限数值和复杂 CLR 值不能绕过构造器合同。</summary>
    [Fact]
    public void CreateObject_InvalidArguments_RejectWithoutStringifying()
    {
        Assert.Throws<InvalidOperationException>(() => JsonDynamicSqlFunctions.CreateObject(["key"]));
        Assert.Throws<InvalidOperationException>(() => JsonDynamicSqlFunctions.CreateObject([null, 1L]));
        Assert.Throws<InvalidOperationException>(() => JsonDynamicSqlFunctions.CreateObject([1L, "value"]));
        Assert.Throws<InvalidOperationException>(() => JsonDynamicSqlFunctions.CreateObject(["key", 1L, "key", 2L]));
        foreach (object value in new object[] { double.NaN, float.PositiveInfinity, DateTime.UnixEpoch, new[] { 1, 2 } })
            Assert.Throws<InvalidOperationException>(() => JsonDynamicSqlFunctions.CreateObject(["key", value]));
        Assert.Throws<InvalidOperationException>(() => JsonDynamicSqlFunctions.CreateObject([new string('a', 1025), 1L]));
        Assert.Throws<InvalidOperationException>(() => JsonDynamicSqlFunctions.CreateObject(["key", new string('"', 2 * 1024 * 1024)]));
    }

    /// <summary>辅助函数遵守当前 SQL 查询的取消与共享内存预算，并归还失败预留。</summary>
    [Fact]
    public void Functions_QueryCancellationAndMemoryBudget_RejectAndRelease()
    {
        string directory = Path.Combine(Path.GetTempPath(), "dynamic-json-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var database = Tsdb.Open(new TsdbOptions { RootDirectory = directory });
            using (SqlQueryResources.EnterRoot(database, new SqlExecutionOptions { BlockingOperatorMemoryLimitBytes = 512 }))
            {
                Assert.Contains("查询内存预算", Assert.Throws<InvalidOperationException>(() => JsonDynamicSqlFunctions.Value(["{}", "$"])).Message);
                Assert.Contains("查询内存预算", Assert.Throws<InvalidOperationException>(() => JsonDynamicSqlFunctions.CreateObject(["key", "value"])).Message);
                using var reservation = SqlQueryResources.Current!.CreateReservation();
                Assert.True(reservation.TryReserve(512));
            }
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            using (SqlQueryResources.EnterRoot(database, new SqlExecutionOptions { CancellationToken = cancellation.Token }))
            {
                Assert.Throws<OperationCanceledException>(() => JsonDynamicSqlFunctions.Value(["{}", "$"]));
                Assert.Throws<OperationCanceledException>(() => JsonDynamicSqlFunctions.CreateObject([]));
            }
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
