using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Query.Functions;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>验证有界 JSON 标量更新和真实关系表的整语句失败边界。</summary>
public sealed partial class SqlJsonSetterTests : IDisposable
{
    private const int ByteLimit = 8 * 1024 * 1024;
    private readonly string _owner = Guid.NewGuid().ToString("N");
    private readonly string _root;

    /// <summary>为需要数据库的用例保留独占目录名称。</summary>
    public SqlJsonSetterTests()
        => _root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "js-" + _owner[..12]));

    /// <summary>所有数值标量均保持各自精度，不通过 double 转换。</summary>
    public static TheoryData<object?, string> ScalarValues => new()
    {
        { null, "null" },
        { true, "true" },
        { (byte)255, "255" },
        { (sbyte)-128, "-128" },
        { (short)-32768, "-32768" },
        { (ushort)65535, "65535" },
        { int.MinValue, "-2147483648" },
        { uint.MaxValue, "4294967295" },
        { long.MinValue, "-9223372036854775808" },
        { long.MaxValue, "9223372036854775807" },
        { ulong.MaxValue, "18446744073709551615" },
        { 9007199254740993L, "9007199254740993" },
        { 1.25f, "1.25" },
        { 1.5d, "1.5" },
        { 12345678901234567890.123456789m, "12345678901234567890.123456789" },
    };

    /// <summary>标量类型及大整数、decimal 必须原样写入 JSON 数字。</summary>
    [Theory]
    [MemberData(nameof(ScalarValues))]
    public void SetValue_SupportedScalar_PreservesExactJson(object? value, string expected)
    {
        string result = Set("{\"value\":0,\"keep\":900719925474099312345678901234567890}", "$.value", value);
        using var document = JsonDocument.Parse(result);
        Assert.Equal(expected, document.RootElement.GetProperty("value").GetRawText());
        Assert.Equal("900719925474099312345678901234567890", document.RootElement.GetProperty("keep").GetRawText());
    }

    /// <summary>JSON 外观的字符串保持字符串，特殊属性名按精确拼写匹配。</summary>
    [Fact]
    public void SetValue_NestedSpecialNames_PreservesSiblingsAndStringValue()
    {
        const string source = """{"parent":{"quo'te.dot[0]😃":"old","A":1,"a":2},"unknown":[1,{"n":9223372036854775807}]}""";
        const string value = "{\"x\":1} O'Reilly 中文😃\\";
        using var result = JsonDocument.Parse(Set(source, "$['parent']['quo''te.dot[0]😃']", value));
        var parent = result.RootElement.GetProperty("parent");
        Assert.Equal(value, parent.GetProperty("quo'te.dot[0]😃").GetString());
        Assert.Equal(1, parent.GetProperty("A").GetInt32());
        Assert.Equal(2, parent.GetProperty("a").GetInt32());
        Assert.Equal("[1,{\"n\":9223372036854775807}]", result.RootElement.GetProperty("unknown").GetRawText());
        Assert.Equal(["parent", "unknown"], result.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
    }

    /// <summary>仅可增加现有对象的末端属性，NULL 保留成员。</summary>
    [Fact]
    public void SetValue_NewLeafAndNull_KeepObjectAndArrayOrder()
    {
        using var inserted = JsonDocument.Parse(Set("{\"parent\":{\"first\":1}}", "$.parent.last", null));
        Assert.Equal(["first", "last"], inserted.RootElement.GetProperty("parent").EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal(JsonValueKind.Null, inserted.RootElement.GetProperty("parent").GetProperty("last").ValueKind);
        Assert.Equal("{\"items\":[1,null,3]}", Set("{\"items\":[1,2,3]}", "$.items[1]", null));
    }

    /// <summary>拒绝无效输入、缺失 parent、越界和全树重复键。</summary>
    [Theory]
    [InlineData(null, "$.value")]
    [InlineData("{}", null)]
    [InlineData("{}", "$")]
    [InlineData("{}", "$.missing.value")]
    [InlineData("{\"parent\":null}", "$.parent.value")]
    [InlineData("{\"parent\":1}", "$.parent.value")]
    [InlineData("null", "$.value")]
    [InlineData("1", "$.value")]
    [InlineData("{\"items\":[0]}", "$.items[1]")]
    [InlineData("{\"items\":[0]}", "$.items[-1]")]
    [InlineData("{\"items\":[0]}", "$.items[*]")]
    [InlineData("{\"value\":{\"keep\":1}}", "$.value")]
    [InlineData("{\"value\":[1]}", "$.value")]
    [InlineData("{\"items\":[{}]}", "$.items[0]")]
    [InlineData("{", "$.value")]
    [InlineData("{\"value\":1,\"value\":2}", "$.value")]
    [InlineData("{\"value\":1,\"other\":{\"a\":1,\"\\u0061\":2}}", "$.value")]
    public void SetValue_InvalidDocumentOrPath_RejectsWithoutReturningPartialJson(string? source, string? path)
        => Assert.Throws<InvalidOperationException>(() => Set(source, path, 1L));

    /// <summary>不接受非标量 CLR 类型或非有限数。</summary>
    [Fact]
    public void SetValue_UnsupportedScalars_Rejects()
    {
        Assert.Throws<InvalidOperationException>(() => Set("{}", "$.value", double.NaN));
        Assert.Throws<InvalidOperationException>(() => Set("{}", "$.value", double.PositiveInfinity));
        Assert.Throws<InvalidOperationException>(() => Set("{}", "$.value", float.NegativeInfinity));
        Assert.Throws<InvalidOperationException>(() => Set("{}", "$.value", Guid.Empty));
        Assert.Throws<InvalidOperationException>(() => Set("{}", "$.value", new byte[] { 1 }));
        Assert.Throws<InvalidOperationException>(() => Set("{}", "$.value", DateTime.UnixEpoch));
        Assert.Throws<InvalidOperationException>(() => Set("{}", "$.value", "\uD800"));
    }

    /// <summary>输出正好 8 MiB 可写入，超限的 ASCII 与转义膨胀都必须在目标写入前失败。</summary>
    [Fact]
    public void SetValue_Utf8OutputLimit_IsExactAndIncludesEscaping()
    {
        string exact = new('a', ByteLimit - 12);
        Assert.Equal(ByteLimit, Encoding.UTF8.GetByteCount(SetWithBoundedScratch("{}", "$.value", exact)));
        var overflow = Assert.Throws<InvalidOperationException>(() => SetWithBoundedScratch("{}", "$.value", exact + "a"));
        Assert.Contains("输出超过", overflow.Message);

        string escaped = new('\0', (ByteLimit - 12) / 6);
        escaped += "aa";
        Assert.Equal(ByteLimit, Encoding.UTF8.GetByteCount(SetWithBoundedScratch("{}", "$.value", escaped)));
        Assert.Contains("输出超过", Assert.Throws<InvalidOperationException>(() => SetWithBoundedScratch("{}", "$.value", escaped + "a")).Message);
    }

    /// <summary>未知字符串成员和长属性名使用同一固定编码缓冲，输出上限不能误当作 writer 预留长度。</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SetValue_LargePreservedToken_RespectsExactOutputAndScratchLimits(bool propertyName, bool escaped)
    {
        string prefix = propertyName ? "{\"value\":0,\"" : "{\"value\":0,\"keep\":\"";
        string suffix = propertyName ? "\":0}" : "\"}";
        int tokenBytes = ByteLimit - prefix.Length - suffix.Length;
        string token = escaped
            ? string.Concat(Enumerable.Repeat("\\u0000", tokenBytes / 6)) + new string('a', tokenBytes % 6)
            : new string('a', tokenBytes);
        string source = prefix + token + suffix;
        Assert.Equal(ByteLimit, Encoding.UTF8.GetByteCount(source));
        string result = SetWithBoundedScratch(source, "$.value", 1L);
        Assert.Equal(ByteLimit, Encoding.UTF8.GetByteCount(result));
        Assert.Equal("{\"value\":1," + source[11..], result);
        Assert.Contains("输出超过", Assert.Throws<InvalidOperationException>(() =>
            SetWithBoundedScratch(source, "$.value", 10L)).Message);
    }

    /// <summary>分块边缘的 surrogate 和默认 HTML/Unicode 转义与原 JSON writer 保持相同字节。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void SetValue_ChunkBoundaryEscaping_MatchesDefaultWriter(int tokenKind)
    {
        const string special = "\"'&+<>\\\n\t\0中😃\u2028\u0001\u001F\u007F\u0080\u00A0\uFFFF";
        string value = new string('a', 1023) + "😃" + new string('b', 1023) + special;
        string source = CreateWriterReference(value, tokenKind, updated: false);
        string expected = CreateWriterReference(value, tokenKind, updated: true);
        object replacement = tokenKind == 0 ? value : 1L;
        Assert.Equal(expected, SetWithBoundedScratch(source, "$.value", replacement));
    }

    /// <summary>输入以 UTF-8 字节计数，不能以字符长度替代。</summary>
    [Fact]
    public void SetValue_Utf8InputLimit_RejectsMultibyteOverflow()
    {
        const string prefix = "{\"value\":0}";
        string exact = prefix + new string(' ', ByteLimit - prefix.Length);
        Assert.Equal(prefix, Set(exact, "$.value", 0L));
        Assert.Throws<InvalidOperationException>(() => Set(exact + " ", "$.value", 0L));
        string multibyte = "{\"value\":\"" + new string('中', ByteLimit / 3) + "\"}";
        Assert.True(multibyte.Length < ByteLimit);
        Assert.Contains("输入超过", Assert.Throws<InvalidOperationException>(() => Set(multibyte, "$.value", 0L)).Message);
    }

    /// <summary>节点、深度及路径预算具有实际可执行的边界。</summary>
    [Fact]
    public void SetValue_TreeAndPathBudgets_RespectBoundaries()
    {
        string atNodes = "{\"items\":[" + string.Join(',', Enumerable.Repeat("0", 99_998)) + "]}";
        using var result = JsonDocument.Parse(Set(atNodes, "$.items[0]", 1L));
        Assert.Equal(99_998, result.RootElement.GetProperty("items").GetArrayLength());
        Assert.Contains("节点数", Assert.Throws<InvalidOperationException>(() => Set(atNodes, "$.added", 1L)).Message);
        string overNodes = "{\"items\":[" + string.Join(',', Enumerable.Repeat("0", 99_999)) + "]}";
        Assert.Contains("节点数", Assert.Throws<InvalidOperationException>(() => Set(overNodes, "$.items[0]", 1L)).Message);

        string depth64 = new string('[', 64) + "0" + new string(']', 64);
        string path64 = "$" + string.Concat(Enumerable.Repeat("[0]", 64));
        Assert.Equal(new string('[', 64) + "1" + new string(']', 64), Set(depth64, path64, 1L));
        Assert.Throws<InvalidOperationException>(() => Set("[" + depth64 + "]", "$[0]", 1L));
        Assert.Throws<InvalidOperationException>(() => Set(depth64, path64 + "[0]", 1L));

        string property = new('a', 1019);
        using var named = JsonDocument.Parse(Set("{}", "$['" + property + "']", 1L));
        Assert.Equal(1, named.RootElement.GetProperty(property).GetInt32());
        Assert.Throws<InvalidOperationException>(() => Set("{}", "$['" + property + "a']", 1L));
    }

    /// <summary>真实 SQL 参数、嵌套 setter 和 JSON 列更新保持精确标量及未知成员。</summary>
    [Fact]
    public void Execute_SetterProjectionAndUpdate_PreservesExactParameters()
    {
        using var db = Open();
        Setup(db, "{\"value\":0,\"amount\":0,\"keep\":9223372036854775807}");
        const decimal amount = 12345678901234567890.123456789m;
        var parameters = new SqlParameters().AddNamed("integer", long.MaxValue).AddNamed("amount", amount);
        var projection = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, null,
            "SELECT json_set_value(json_set_value(metadata, '$.value', @integer), '$.amount', @amount) FROM setter_rows",
            parameters));
        using (var projected = JsonDocument.Parse(Assert.IsType<string>(Assert.Single(projection.Rows)[0])))
        {
            Assert.Equal(long.MaxValue, projected.RootElement.GetProperty("value").GetInt64());
            Assert.Equal(amount, projected.RootElement.GetProperty("amount").GetDecimal());
        }
        SqlExecutor.Execute(db, null,
            "UPDATE setter_rows SET metadata=json_set_value(json_set_value(metadata, '$.value', @integer), '$.amount', @amount) WHERE id>=1", parameters);
        using var stored = JsonDocument.Parse(ReadRows(db)[0].Json);
        Assert.Equal(long.MaxValue, stored.RootElement.GetProperty("value").GetInt64());
        Assert.Equal(amount, stored.RootElement.GetProperty("amount").GetDecimal());
        Assert.Equal(long.MaxValue, stored.RootElement.GetProperty("keep").GetInt64());
    }

    /// <summary>没有结果行时，内置 setter 仍应提供准确的字符串声明元数据。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Execute_EmptySetterProjection_DeclaresString(bool insertRow)
    {
        using var db = Open();
        SqlExecutor.Execute(db, "CREATE TABLE setter_rows (id INT, stamp STRING, metadata JSON, PRIMARY KEY (id))");
        if (insertRow)
            SqlExecutor.Execute(db, "INSERT INTO setter_rows (id, stamp, metadata) VALUES (1, 'initial', '{\"value\":1}')");
        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT json_set_value(metadata, '$.value', 9) AS changed FROM setter_rows WHERE id<0"));
        Assert.Empty(result.Rows);
        Assert.Equal(TableColumnType.String, Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<SelectColumnInfo>>(result.ColumnInfo)).DataType);
    }

    /// <summary>同名 UDF 的真实返回值和未知声明不能错误继承内置 setter 的字符串类型。</summary>
    [Fact]
    public void Execute_ShadowedSetterProjection_PreservesUserFunctionDeclaration()
    {
        using var db = Open();
        Setup(db, "{\"value\":1}");
        db.Functions.RegisterScalar("json_set_value", _ => 42L, 3, 3);
        var empty = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT json_set_value(metadata, '$.value', 9) FROM setter_rows WHERE id<0"));
        Assert.Empty(empty.Rows);
        Assert.Null(Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<SelectColumnInfo>>(empty.ColumnInfo)).DataType);
        var actual = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db,
            "SELECT json_set_value(metadata, '$.value', 9) FROM setter_rows WHERE id=1"));
        Assert.Equal(42L, Assert.IsType<long>(Assert.Single(actual.Rows)[0]));
    }

    /// <summary>后续行路径失败时，前面已求值的行不能部分提交。</summary>
    [Fact]
    public void Execute_SecondRowFailure_KeepsEveryOriginalRow()
    {
        using var db = Open();
        Setup(db, "{\"parent\":{\"value\":1},\"keep\":7}");
        SqlExecutor.Execute(db, "INSERT INTO setter_rows (id, stamp, metadata) VALUES (2, 'initial', '{\"parent\":null,\"keep\":8}')");
        var before = ReadRows(db);
        var reached = new List<long>();
        db.Functions.RegisterScalar("record_json_row", args =>
        {
            reached.Add(Assert.IsType<long>(args[0]));
            return 9L;
        }, 1, 1);
        Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db,
            "UPDATE setter_rows SET metadata=json_set_value(metadata, '$.parent.value', record_json_row(id)) WHERE id>=1"));
        Assert.Equal([1L, 2L], reached);
        Assert.Equal(before, ReadRows(db));
    }

    /// <summary>事务之前的写入在 setter 失败后保留，rollback 与正常 commit/reopen 都有效。</summary>
    [Fact]
    public void Execute_TransactionFailureRollbackAndReopen_PreservesAtomicity()
    {
        using (var db = Open())
        {
            Setup(db, "{\"parent\":{\"value\":1},\"keep\":9223372036854775807}");
            SqlExecutor.Execute(db, "INSERT INTO setter_rows (id, stamp, metadata) VALUES (2, 'initial', '{\"parent\":null}')");
            var before = ReadRows(db);
            var rolledBack = Assert.IsType<SqlTransactionContext>(SqlExecutor.Execute(db, "BEGIN"));
            Execute(db, "UPDATE setter_rows SET metadata=json_set_value(metadata, '$.parent.value', 7) WHERE id=1", rolledBack);
            using (var changed = JsonDocument.Parse(ReadRows(db, rolledBack)[0].Json))
                Assert.Equal(7, changed.RootElement.GetProperty("parent").GetProperty("value").GetInt32());
            Assert.Equal(before[1], ReadRows(db, rolledBack)[1]);
            Assert.Equal(before, ReadRows(db));
            Execute(db, "ROLLBACK", rolledBack);
            Assert.Equal(before, ReadRows(db));

            var reached = new List<long>();
            db.Functions.RegisterScalar("record_transaction_json_row", args =>
            {
                reached.Add(Assert.IsType<long>(args[0]));
                return 9L;
            }, 1, 1);
            var transaction = Assert.IsType<SqlTransactionContext>(SqlExecutor.Execute(db, "BEGIN"));
            Execute(db, "UPDATE setter_rows SET stamp='prior' WHERE id=1", transaction);
            Assert.Throws<InvalidOperationException>(() => Execute(db,
                "UPDATE setter_rows SET metadata=json_set_value(metadata, '$.parent.value', record_transaction_json_row(id)) WHERE id>=1", transaction));
            Assert.Equal([1L, 2L], reached);
            var pending = ReadRows(db, transaction);
            Assert.Equal("prior", pending[0].Stamp);
            Assert.Equal(before[0].Json, pending[0].Json);
            Assert.Equal(before[1].Json, pending[1].Json);
            Assert.Equal(before, ReadRows(db));
            Execute(db, "ROLLBACK", transaction);
            Assert.Equal(before, ReadRows(db));

            var committed = Assert.IsType<SqlTransactionContext>(SqlExecutor.Execute(db, "BEGIN"));
            Execute(db, "UPDATE setter_rows SET metadata=json_set_value(metadata, '$.parent.value', 9) WHERE id=1", committed);
            Execute(db, "COMMIT", committed);
        }
        using var reopened = Open();
        using var json = JsonDocument.Parse(ReadRows(reopened)[0].Json);
        Assert.Equal(9, json.RootElement.GetProperty("parent").GetProperty("value").GetInt32());
        Assert.Equal(long.MaxValue, json.RootElement.GetProperty("keep").GetInt64());
    }

    /// <summary>第二行参数求值触发取消后，setter 必须读取实际 SQL ambient token 而不提交第一行。</summary>
    [Fact]
    public void Execute_CancellationInsideStatement_DoesNotCommitEarlierRows()
    {
        using var db = Open();
        Setup(db, "{\"value\":1}");
        SqlExecutor.Execute(db, "INSERT INTO setter_rows (id, stamp, metadata) VALUES (2, 'initial', '{\"value\":2}')");
        var before = ReadRows(db);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        int calls = 0;
        db.Functions.RegisterScalar("cancel_json_value", _ =>
        {
            calls++;
            if (calls == 2)
                cancellation.Cancel();
            return 9L;
        }, 0, 0);
        var exception = Assert.Throws<RoutineExecutionException>(() => SqlExecutor.Execute(db, null,
            "UPDATE setter_rows SET metadata=json_set_value(metadata, '$.value', cancel_json_value()) WHERE id>=1",
            parameters: null, controlPlane: null, new SqlExecutionOptions { CancellationToken = cancellation.Token }));
        Assert.IsType<OperationCanceledException>(exception.InnerException);
        Assert.Equal(2, calls);
        Assert.Equal(before, ReadRows(db));
    }

    /// <summary>第一行求值成功后，第二行在校验、重写或返回前取消仍保持语句和事务原子性。</summary>
    [Theory]
    [InlineData(false, "ValidationNode")]
    [InlineData(false, "RewritingNode")]
    [InlineData(false, "BeforeReturn")]
    [InlineData(true, "ValidationNode")]
    [InlineData(true, "RewritingNode")]
    [InlineData(true, "BeforeReturn")]
    public void Execute_CancellationAtKnownStage_PreservesRowsAndTransaction(bool inTransaction, string stageName)
    {
        var stageToCancel = stageName switch
        {
            "ValidationNode" => JsonPathUpdater.WorkStage.ValidationNode,
            "RewritingNode" => JsonPathUpdater.WorkStage.RewritingNode,
            "BeforeReturn" => JsonPathUpdater.WorkStage.BeforeReturn,
            _ => throw new ArgumentOutOfRangeException(nameof(stageName)),
        };
        (long Id, string Stamp, string Json)[] before;
        using (var db = Open())
        {
            Setup(db, "{\"value\":1,\"keep\":[3,4]}");
            SqlExecutor.Execute(db, "INSERT INTO setter_rows (id, stamp, metadata) VALUES (2, 'initial', '{\"value\":2,\"keep\":[5,6]}')");
            before = ReadRows(db);
            SqlTransactionContext? transaction = inTransaction
                ? Assert.IsType<SqlTransactionContext>(SqlExecutor.Execute(db, "BEGIN"))
                : null;
            if (transaction is not null)
                Execute(db, "UPDATE setter_rows SET stamp='prior' WHERE id=1", transaction);
            using var cancellation = new CancellationTokenSource();
            var reached = new List<long>();
            long activeRow = 0;
            int firstRowBeforeReturn = 0;
            int secondRowStageVisits = 0;
            int cancelAt = stageToCancel == JsonPathUpdater.WorkStage.BeforeReturn ? 1 : 2;
            db.Functions.RegisterScalar("record_stage_json_row", args =>
            {
                activeRow = Assert.IsType<long>(args[0]);
                reached.Add(activeRow);
                return 9L;
            }, 1, 1);
            using (JsonPathUpdater.ObserveWork(stage =>
            {
                if (activeRow == 1 && stage == JsonPathUpdater.WorkStage.BeforeReturn)
                    firstRowBeforeReturn++;
                if (activeRow == 2 && stage == stageToCancel && ++secondRowStageVisits == cancelAt)
                    cancellation.Cancel();
            }))
            {
                var exception = Assert.Throws<RoutineExecutionException>(() => SqlExecutor.ExecuteStatement(db, null,
                    SqlParser.Parse("UPDATE setter_rows SET metadata=json_set_value(metadata, '$.value', record_stage_json_row(id)) WHERE id>=1"),
                    null, transaction, new SqlExecutionOptions { CancellationToken = cancellation.Token }));
                Assert.IsType<OperationCanceledException>(exception.InnerException);
            }
            Assert.Equal([1L, 2L], reached);
            Assert.Equal(1, firstRowBeforeReturn);
            Assert.Equal(cancelAt, secondRowStageVisits);
            Assert.True(cancellation.IsCancellationRequested);
            Assert.Equal(before, ReadRows(db));
            if (transaction is not null)
            {
                var pending = ReadRows(db, transaction);
                Assert.Equal("prior", pending[0].Stamp);
                Assert.Equal(before[0].Json, pending[0].Json);
                Assert.Equal(before[1], pending[1]);
                Execute(db, "ROLLBACK", transaction);
                Assert.Equal(before, ReadRows(db));
            }
        }
        using var reopened = Open();
        Assert.Equal(before, ReadRows(reopened));
    }

    /// <summary>预取消令牌和已过期 SQL deadline 分别在入口拒绝，不能冒充遍历中取消。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Execute_PreCancelledOrExpiredDeadline_RejectsBeforeSetter(bool useDeadline)
    {
        using var db = Open();
        Setup(db, "{\"value\":1}");
        var before = ReadRows(db);
        using var cancellation = new CancellationTokenSource();
        if (!useDeadline)
            cancellation.Cancel();
        int stages = 0;
        using (JsonPathUpdater.ObserveWork(_ => stages++))
        {
            var exception = Assert.Throws<RoutineExecutionException>(() => SqlExecutor.Execute(db, null,
                "UPDATE setter_rows SET metadata=json_set_value(metadata, '$.value', 9) WHERE id>=1",
                parameters: null, controlPlane: null, new SqlExecutionOptions
                {
                    CancellationToken = cancellation.Token,
                    DeadlineUtc = useDeadline ? DateTimeOffset.UtcNow.AddSeconds(-1) : null,
                }));
            if (useDeadline)
                Assert.IsType<TimeoutException>(exception.InnerException);
            else
                Assert.IsType<OperationCanceledException>(exception.InnerException);
        }
        Assert.Equal(0, stages);
        Assert.Equal(!useDeadline, cancellation.IsCancellationRequested);
        Assert.Equal(before, ReadRows(db));
    }

    /// <summary>独立单次预算以单调时钟计时，恰好十秒通过而多一个 tick 在已到达重写阶段拒绝。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SetValue_LocalDeadline_IsIndependentAndExact(bool overLimit)
    {
        var clock = new SetterTimeProvider();
        int rewritingVisits = 0;
        using var observation = JsonPathUpdater.ObserveWork(stage =>
        {
            if (stage == JsonPathUpdater.WorkStage.RewritingNode && ++rewritingVisits == 1)
                clock.Advance(TimeSpan.FromSeconds(10) + TimeSpan.FromTicks(overLimit ? 1 : 0));
        }, clock);
        if (overLimit)
            Assert.Contains("10 秒", Assert.Throws<InvalidOperationException>(() => Set("{\"value\":1}", "$.value", 9L)).Message);
        else
            Assert.Equal("{\"value\":9}", Set("{\"value\":1}", "$.value", 9L));
        Assert.True(rewritingVisits >= 1);
    }

    /// <summary>SQL 令牌仍有效时，第二行独立本地预算超时也不能提交前面已成功求值的行。</summary>
    [Fact]
    public void Execute_LocalDeadlineDuringSecondRow_PreservesAtomicityAndReopen()
    {
        (long Id, string Stamp, string Json)[] before;
        using (var db = Open())
        {
            Setup(db, "{\"value\":1}");
            SqlExecutor.Execute(db, "INSERT INTO setter_rows (id, stamp, metadata) VALUES (2, 'initial', '{\"value\":2}')");
            before = ReadRows(db);
            var clock = new SetterTimeProvider();
            using var cancellation = new CancellationTokenSource();
            var reached = new List<long>();
            long activeRow = 0;
            int firstRowBeforeReturn = 0;
            int secondRowRewritingVisits = 0;
            db.Functions.RegisterScalar("record_deadline_json_row", args =>
            {
                activeRow = Assert.IsType<long>(args[0]);
                reached.Add(activeRow);
                return 9L;
            }, 1, 1);
            using (JsonPathUpdater.ObserveWork(stage =>
            {
                if (activeRow == 1 && stage == JsonPathUpdater.WorkStage.BeforeReturn)
                    firstRowBeforeReturn++;
                if (activeRow == 2 && stage == JsonPathUpdater.WorkStage.RewritingNode && ++secondRowRewritingVisits == 2)
                    clock.Advance(TimeSpan.FromSeconds(10) + TimeSpan.FromTicks(1));
            }, clock))
            {
                var exception = Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db, null,
                    "UPDATE setter_rows SET metadata=json_set_value(metadata, '$.value', record_deadline_json_row(id)) WHERE id>=1",
                    parameters: null, controlPlane: null, new SqlExecutionOptions
                    {
                        CancellationToken = cancellation.Token,
                        DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(1),
                    }));
                Assert.Contains("10 秒", exception.Message);
            }
            Assert.Equal([1L, 2L], reached);
            Assert.Equal(1, firstRowBeforeReturn);
            Assert.Equal(2, secondRowRewritingVisits);
            Assert.False(cancellation.IsCancellationRequested);
            Assert.Equal(before, ReadRows(db));
        }
        using var reopened = Open();
        Assert.Equal(before, ReadRows(reopened));
    }

    private static string SetWithBoundedScratch(string source, string path, object? value)
    {
        var scratch = new List<int>();
        using var observation = JsonPathUpdater.ObserveWork(_ => { }, scratchObserver: scratch.Add);
        try
        {
            return Set(source, path, value);
        }
        finally
        {
            // 只约束活跃编码 payload 缓冲；DOM、输出数组及返回字符串不计入该数值。
            Assert.NotEmpty(scratch);
            Assert.Equal(5120, scratch.Max());
            Assert.All(scratch, bytes => Assert.InRange(bytes, 0, 5120));
            Assert.Equal(0, scratch[^1]);
        }
    }

    private static string CreateWriterReference(string value, int tokenKind, bool updated)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (tokenKind == 0)
                writer.WriteString("value", updated ? value : "old");
            else
            {
                writer.WriteNumber("value", updated ? 1 : 0);
                if (tokenKind == 1)
                    writer.WriteString("keep", value);
                else
                    writer.WriteNumber(value, 7);
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private sealed class SetterTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        internal void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
    }

    private static string Set(object? source, object? path, object? value)
    {
        Assert.True(FunctionRegistry.TryGetScalar("json_set_value", out var setter));
        return Assert.IsType<string>(setter.Evaluate([source, path, value]));
    }

    private Tsdb Open()
    {
        if (!Directory.Exists(_root))
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(Path.Combine(_root, "owner"), _owner);
        }
        return Tsdb.Open(new TsdbOptions { RootDirectory = _root });
    }

    private static void Setup(Tsdb db, string json)
    {
        SqlExecutor.Execute(db, "CREATE TABLE setter_rows (id INT, stamp STRING, metadata JSON, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, null, "INSERT INTO setter_rows (id, stamp, metadata) VALUES (1, 'initial', @json)",
            new SqlParameters().AddNamed("json", json));
    }

    private static object? Execute(Tsdb db, string sql, SqlTransactionContext? transaction = null)
        => SqlExecutor.ExecuteStatement(db, null, SqlParser.Parse(sql), null, transaction);

    private static (long Id, string Stamp, string Json)[] ReadRows(Tsdb db, SqlTransactionContext? transaction = null)
        => Assert.IsType<SelectExecutionResult>(Execute(db,
            "SELECT id, stamp, metadata FROM setter_rows ORDER BY id", transaction)).Rows
            .Select(row => (Assert.IsType<long>(row[0]), Assert.IsType<string>(row[1]), Assert.IsType<string>(row[2])))
            .ToArray();

    /// <summary>仅清理经过路径、owner、数量和时间核验的本用例目录。</summary>
    public void Dispose()
    {
        if (!Directory.Exists(_root))
            return;
        if (Path.GetDirectoryName(_root) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()))
            || File.ReadAllText(Path.Combine(_root, "owner")) != _owner)
            throw new InvalidOperationException("JSON setter fixture owner mismatch.");
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((_root, 0));
        int count = 0;
        var clock = Stopwatch.StartNew();
        while (pending.Count > 0 && count < 8192 && clock.Elapsed < TimeSpan.FromSeconds(10))
        {
            var item = pending.Pop();
            count++;
            var attributes = File.GetAttributes(item.Path);
            if (item.Depth > 16 || (attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("JSON setter fixture depth/reparse guard.");
            if ((attributes & FileAttributes.Directory) != 0)
            {
                foreach (string child in Directory.EnumerateFileSystemEntries(item.Path).Take(8193))
                {
                    if (pending.Count + count >= 8192 || clock.Elapsed > TimeSpan.FromSeconds(10))
                        throw new InvalidOperationException("JSON setter fixture inventory budget.");
                    var absolute = Path.GetFullPath(child);
                    if (!absolute.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("JSON setter fixture escaped root.");
                    pending.Push((absolute, item.Depth + 1));
                }
            }
        }
        if (pending.Count != 0)
            throw new InvalidOperationException("JSON setter fixture cleanup budget.");
        Directory.Delete(_root, recursive: true);
    }
}
