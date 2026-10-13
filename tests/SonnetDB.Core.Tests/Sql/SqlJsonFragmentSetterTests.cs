using System.Text.Json;
using SonnetDB.Documents;
using SonnetDB.Exceptions;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>验证显式 JSON 片段更新和原三参数标量合同的隔离。</summary>
public sealed partial class SqlJsonSetterTests
{
    /// <summary>第二行在片段校验、片段输出或返回前取消，不提交已完成的第一行。</summary>
    [Theory]
    [InlineData("ValidationNode")]
    [InlineData("RewritingNode")]
    [InlineData("BeforeReturn")]
    public void Execute_SerializedFragmentCancellation_PreservesCompletedFirstRow(string stageName)
    {
        var targetStage = Enum.Parse<JsonPathUpdater.WorkStage>(stageName);
        var beforeReturn = targetStage == JsonPathUpdater.WorkStage.BeforeReturn;
        using var db = Open();
        Setup(db, "{\"value\":[0]}");
        SqlExecutor.Execute(db, "INSERT INTO setter_rows (id, stamp, metadata) VALUES (2, 'initial', '{\"value\":[0]}')");
        var before = ReadRows(db);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        long activeRow = 0;
        var firstCompleted = 0;
        var visits = 0;
        var fragment = "[" + string.Join(',', Enumerable.Range(0, 100)) + "]";
        db.Functions.RegisterScalar("fragment_for_row", args =>
        {
            activeRow = Assert.IsType<long>(args[0]);
            return fragment;
        }, 1, 1);
        using (JsonPathUpdater.ObserveWork(stage =>
        {
            if (activeRow == 1 && stage == JsonPathUpdater.WorkStage.BeforeReturn)
                firstCompleted++;
            if (activeRow == 2 && stage == targetStage && ++visits == (beforeReturn ? 1 : 10))
                cancellation.Cancel();
        }))
        {
            var exception = Assert.Throws<RoutineExecutionException>(() => SqlExecutor.Execute(db, null,
                "UPDATE setter_rows SET metadata=json_set_value(metadata, '$.value', fragment_for_row(id), TRUE) WHERE id>=1",
                null, null, new SqlExecutionOptions { CancellationToken = cancellation.Token }));
            Assert.IsType<OperationCanceledException>(exception.InnerException);
        }
        Assert.Equal(2, activeRow);
        Assert.Equal(1, firstCompleted);
        Assert.Equal(beforeReturn ? 1 : 10, visits);
        Assert.Equal(before, ReadRows(db));
    }

    /// <summary>四参数函数实际执行后可回滚、重开，并在第二行失败时保留先前数据。</summary>
    [Fact]
    public void Execute_SerializedFragment_RollbackFailureAndReopenRemainAtomic()
    {
        using (var db = Open())
        {
            Setup(db, "{\"value\":[0],\"keep\":9007199254740993}");
            var before = ReadRows(db);
            var transaction = Assert.IsType<SqlTransactionContext>(SqlExecutor.Execute(db, "BEGIN"));
            Execute(db, "UPDATE setter_rows SET metadata=json_set_value(metadata, '$.value', '[1,{\"n\":9007199254740993}]', TRUE) WHERE id=1", transaction);
            using (var changed = JsonDocument.Parse(ReadRows(db, transaction)[0].Json))
                Assert.Equal("[1,{\"n\":9007199254740993}]", changed.RootElement.GetProperty("value").GetRawText());
            Assert.Equal(before, ReadRows(db));
            Execute(db, "ROLLBACK", transaction);
            Assert.Equal(before, ReadRows(db));
            SqlExecutor.Execute(db, "INSERT INTO setter_rows (id, stamp, metadata) VALUES (2, 'initial', 'null')");
            var beforeFailure = ReadRows(db);
            var completed = 0;
            using (JsonPathUpdater.ObserveWork(stage =>
            {
                if (stage == JsonPathUpdater.WorkStage.BeforeReturn)
                    completed++;
            }))
            {
                Assert.Throws<InvalidOperationException>(() => Execute(db,
                    "UPDATE setter_rows SET metadata=json_set_value(metadata, '$.value', '[2]', TRUE) WHERE id>=1"));
            }
            Assert.Equal(1, completed);
            Assert.Equal(beforeFailure, ReadRows(db));
            Execute(db, "UPDATE setter_rows SET metadata=json_set_value(metadata, '$.value', '[3]', TRUE) WHERE id=1");
        }
        using var reopened = Open();
        using var persisted = JsonDocument.Parse(ReadRows(reopened)[0].Json);
        Assert.Equal("[3]", persisted.RootElement.GetProperty("value").GetRawText());
        Assert.Equal(9007199254740993L, persisted.RootElement.GetProperty("keep").GetInt64());
    }

    /// <summary>数组、对象及 NULL 片段按 JSON 写入，保留其它成员。</summary>
    [Theory]
    [InlineData("[]")]
    [InlineData("[1,9007199254740993,{\"n\":1.2300e+04}]")]
    [InlineData("{\"n\":9007199254740993,\"values\":[true,null]}")]
    [InlineData("null")]
    [InlineData(null)]
    public void SetValue_SerializedFragment_PreservesJsonAndSiblings(string? fragment)
    {
        const string source = "{\"target\":[0],\"keep\":{\"number\":9007199254740993,\"lexical\":1.2300e+04}}";
        var result = Assert.IsType<string>(JsonSqlFunctions.SetValue([source, "$.target", fragment, true]));
        using var document = JsonDocument.Parse(result);
        Assert.Equal(fragment ?? "null", document.RootElement.GetProperty("target").GetRawText());
        Assert.Equal("{\"number\":9007199254740993,\"lexical\":1.2300e+04}", document.RootElement.GetProperty("keep").GetRawText());
        Assert.Equal(new[] { "target", "keep" }, document.RootElement.EnumerateObject().Select(property => property.Name));
    }

    /// <summary>新增对象末端和已有数组元素支持容器片段，不创建父级或扩展数组。</summary>
    [Fact]
    public void SetValue_SerializedFragment_UsesExistingParentAndArrayBounds()
    {
        Assert.Equal("{\"p\":{\"new\":[1]}}", JsonSqlFunctions.SetValue(["{\"p\":{}}", "$.p.new", "[1]", true]));
        Assert.Equal("{\"p\":[{\"x\":1},2]}", JsonSqlFunctions.SetValue(["{\"p\":[0,2]}", "$.p[0]", "{\"x\":1}", true]));
        Assert.Throws<InvalidOperationException>(() => JsonSqlFunctions.SetValue(["{}", "$.p.new", "[]", true]));
        Assert.Throws<InvalidOperationException>(() => JsonSqlFunctions.SetValue(["{\"p\":[0]}", "$.p[1]", "[]", true]));
    }

    /// <summary>默认和 false 模式仍把 JSON 外观文本作为字符串，不能隐式覆盖容器。</summary>
    [Fact]
    public void SetValue_ExplicitMode_PreservesOriginalScalarContract()
    {
        using var original = JsonDocument.Parse(Assert.IsType<string>(JsonSqlFunctions.SetValue(["{}", "$.p", "[1]"])));
        using var explicitScalar = JsonDocument.Parse(Assert.IsType<string>(JsonSqlFunctions.SetValue(["{}", "$.p", "[1]", false])));
        Assert.Equal("[1]", original.RootElement.GetProperty("p").GetString());
        Assert.Equal("[1]", explicitScalar.RootElement.GetProperty("p").GetString());
        Assert.Throws<InvalidOperationException>(() => JsonSqlFunctions.SetValue(["{\"p\":[]}", "$.p", "[]"]));
        Assert.Throws<InvalidOperationException>(() => JsonSqlFunctions.SetValue(["{}", "$.p", "[]", 1L]));
        Assert.Throws<InvalidOperationException>(() => JsonSqlFunctions.SetValue(["{}", "$.p", 1L, true]));
    }

    /// <summary>无效片段和逻辑重复键不能进入结果。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("[1")]
    [InlineData("NaN")]
    [InlineData("{} {}")]
    [InlineData("{\"a\":1,\"\\u0061\":2}")]
    public void SetValue_InvalidSerializedFragment_Rejects(string fragment)
        => Assert.Throws<InvalidOperationException>(() => JsonSqlFunctions.SetValue(["{}", "$.p", fragment, true]));

    /// <summary>片段与组合后深度、节点和输出大小仍受固定预算保护。</summary>
    [Fact]
    public void SetValue_SerializedFragment_EnforcesCombinedBudgets()
    {
        var deep = new string('[', 64) + "0" + new string(']', 64);
        using var validFragment = JsonDocument.Parse(deep, new JsonDocumentOptions { MaxDepth = 64 });
        Assert.Throws<InvalidOperationException>(() => JsonSqlFunctions.SetValue(["{}", "$.p", deep, true]));
        var tooManyNodes = "[" + string.Join(',', Enumerable.Repeat("0", 100_000)) + "]";
        Assert.Throws<InvalidOperationException>(() => JsonSqlFunctions.SetValue(["{}", "$.p", tooManyNodes, true]));
        var large = "\"" + new string('a', 8 * 1024 * 1024 - 2) + "\"";
        Assert.Throws<InvalidOperationException>(() => JsonSqlFunctions.SetValue(["{}", "$.p", large, true]));
        Assert.Throws<InvalidOperationException>(() => JsonSqlFunctions.SetValue(["{}", "$.p", large + " ", true]));
    }
}
