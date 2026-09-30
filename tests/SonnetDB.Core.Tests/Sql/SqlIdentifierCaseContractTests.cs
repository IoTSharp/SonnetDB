using SonnetDB.Engine;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>验证表、measurement 及派生来源共享标识符大小写合同。</summary>
public sealed class SqlIdentifierCaseContractTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sndb-identifier-case-" + Guid.NewGuid().ToString("N"));

    private TsdbOptions Options() => new() { RootDirectory = _root };

    /// <summary>仅回收本测试创建的唯一数据库目录。</summary>
    public void Dispose()
    {
        string root = Path.GetFullPath(_root);
        string temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        Assert.StartsWith(temporaryRoot, root, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("sndb-identifier-case-", Path.GetFileName(root), StringComparison.Ordinal);
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    /// <summary>关系表普通引用忽略大小写，引号引用精确匹配，重启后保留原名。</summary>
    [Fact]
    public void RelationalIdentifiers_PreservedSpellingAndExactQuotes_WorkAcrossReopen()
    {
        using (var db = Tsdb.Open(Options()))
        {
            SqlExecutor.Execute(db, "CREATE TABLE Device (ID INT, Value INT, PRIMARY KEY (ID))");
            SqlExecutor.Execute(db, "INSERT INTO DEVICE (ID, VALUE) VALUES (1, 10)");
            SqlExecutor.Execute(db, "INSERT INTO \"Device\" (\"ID\", \"Value\") VALUES (2, 20)");

            Assert.Equal(["ID", "Value"], db.Tables.Catalog.TryGet("Device")!.Columns.Select(c => c.Name));
            Assert.Null(db.Tables.Catalog.TryGet("device"));
            Assert.Equal(10L, Select(db, "SELECT VALUE FROM device WHERE ID = 1").Rows[0][0]);
            Assert.Equal(20L, Select(db, "SELECT \"Value\" FROM \"Device\" WHERE \"ID\" = 2").Rows[0][0]);
            Assert.Throws<InvalidOperationException>(() => Select(db, "SELECT \"value\" FROM Device"));
            Assert.Throws<InvalidOperationException>(() => Select(db, "SELECT Value FROM \"device\""));
            Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db,
                "CREATE TABLE \"device\" (id INT, PRIMARY KEY (id))"));
        }

        using (var db = Tsdb.Open(Options()))
        {
            Assert.Equal(10L, Select(db, "SELECT value FROM DEVICE WHERE id = 1").Rows[0][0]);
            Assert.Equal(20L, Select(db, "SELECT \"Value\" FROM \"Device\" WHERE \"ID\" = 2").Rows[0][0]);
        }
    }

    /// <summary>新建关系表不能包含仅大小写不同的列。</summary>
    [Fact]
    public void RelationalColumns_NewCaseOnlyDuplicate_IsRejected()
    {
        using var db = Tsdb.Open(Options());
        Assert.Throws<ArgumentException>(() => SqlExecutor.Execute(db,
            "CREATE TABLE values_test (id INT, foo INT, \"Foo\" INT, PRIMARY KEY (id))"));
        Assert.Null(db.Tables.Catalog.TryGet("values_test"));
    }

    /// <summary>measurement 的名称与列在重启后仍保持相同引用规则。</summary>
    [Fact]
    public void MeasurementIdentifiers_PreservedSpellingAndExactQuotes_WorkAcrossReopen()
    {
        using (var db = Tsdb.Open(Options()))
        {
            SqlExecutor.Execute(db, "CREATE MEASUREMENT Sensor (Host TAG, Value FIELD FLOAT)");
            SqlExecutor.Execute(db, "INSERT INTO SENSOR (time, HOST, VALUE) VALUES (1000, 'a', 1.5)");
            SqlExecutor.Execute(db, "INSERT INTO \"Sensor\" (time, \"Host\", \"Value\") VALUES (2000, 'b', 2.5)");

            Assert.Equal(["Host", "Value"], db.Measurements.TryGet("Sensor")!.Columns.Select(c => c.Name));
            Assert.Null(db.Measurements.TryGet("sensor"));
            Assert.Equal(1.5, Select(db, "SELECT VALUE FROM sensor WHERE HOST = 'a'").Rows[0][0]);
            Assert.Equal(1.5, Select(db, "SELECT sensor.value FROM SENSOR WHERE sensor.host = 'a'").Rows[0][0]);
            Assert.Equal(2.5, Select(db, "SELECT \"Value\" FROM \"Sensor\" WHERE \"Host\" = 'b'").Rows[0][0]);
            Assert.Throws<InvalidOperationException>(() => Select(db, "SELECT \"value\" FROM Sensor"));
            Assert.Throws<InvalidOperationException>(() => Select(db, "SELECT Value FROM \"sensor\""));
            Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(db,
                "CREATE MEASUREMENT \"sensor\" (Value FIELD FLOAT)"));
        }

        using (var db = Tsdb.Open(Options()))
        {
            Assert.Equal(1.5, Select(db, "SELECT value FROM SENSOR WHERE host = 'a'").Rows[0][0]);
            Assert.Equal(2.5, Select(db, "SELECT \"Value\" FROM \"Sensor\" WHERE \"Host\" = 'b'").Rows[0][0]);
        }
    }

    /// <summary>引号 Time 保留用户列含义，普通 time 仍引用时间戳。</summary>
    [Fact]
    public void MeasurementTime_QuotedCaseVariant_RemainsAUserField()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE MEASUREMENT clock_test (\"Time\" FIELD FLOAT, value FIELD FLOAT)");
        SqlExecutor.Execute(db, "INSERT INTO clock_test (time, \"Time\", value) VALUES (1000, 2.5, 3.5)");

        var selected = Select(db, "SELECT time, \"Time\" FROM clock_test");
        Assert.Equal(["time", "Time"], selected.Columns);
        Assert.Equal(1000L, selected.Rows[0][0]);
        Assert.Equal(2.5, selected.Rows[0][1]);
    }

    /// <summary>CTE 遮蔽物理源，派生别名保留拼写并接受精确引用。</summary>
    [Fact]
    public void Select_CteShadowAndDerivedAliases_UsePreservedSpelling()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE MEASUREMENT Result (Actual FIELD FLOAT)");
        var cte = Select(db, "WITH Result AS (SELECT 7 AS Answer) SELECT answer FROM result");
        Assert.Equal(7L, cte.Rows[0][0]);

        SqlExecutor.Execute(db, "CREATE TABLE Device (ID INT, Name STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "INSERT INTO device (id, name) VALUES (1, 'a')");
        var derived = Select(db,
            "SELECT x.displayname FROM (SELECT Name AS DisplayName FROM device) AS X");
        Assert.Equal("a", derived.Rows[0][0]);
        Assert.Throws<InvalidOperationException>(() => Select(db,
            "SELECT x.\"displayname\" FROM (SELECT Name AS DisplayName FROM device) AS X"));
    }

    /// <summary>系统目录的普通引用忽略大小写，引号部分精确匹配。</summary>
    [Fact]
    public void InformationSchema_UnquotedIgnoresCaseAndQuotedPartsAreExact()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE Device (ID INT, PRIMARY KEY (id))");
        var result = Select(db,
            "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE table_name = 'Device'");
        Assert.Equal("Device", result.Rows[0][0]);
        Assert.Throws<InvalidOperationException>(() => Select(db,
            "SELECT \"TABLE_NAME\" FROM information_schema.tables"));
        Assert.Throws<InvalidOperationException>(() => Select(db,
            "SELECT table_name FROM information_schema.\"Tables\""));
    }

    /// <summary>measurement 与关系表连接使用已保存的列名和别名。</summary>
    [Fact]
    public void MeasurementJoin_StoredMixedSpelling_BindsNamesAndAliases()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE TABLE Inventory (ID INT, Host STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db, "CREATE MEASUREMENT Sensor (Host TAG, Value FIELD FLOAT)");
        SqlExecutor.Execute(db, "INSERT INTO inventory (id, host) VALUES (1, 'a')");
        SqlExecutor.Execute(db, "INSERT INTO sensor (time, host, value) VALUES (1000, 'a', 1.5)");

        var joined = Select(db,
            "SELECT s.value, i.id FROM sensor S JOIN inventory I ON s.host = i.host WHERE s.host = 'a'");
        Assert.Equal(new object?[] { 1.5, 1L }, joined.Rows[0]);
        Assert.Throws<InvalidOperationException>(() => Select(db,
            "SELECT s.\"value\" FROM sensor S JOIN inventory I ON s.host = i.host"));
    }

    /// <summary>预测字段、输出列及限定符统一接受普通大小写变体并保留元数据拼写。</summary>
    [Fact]
    public void Forecast_MixedCaseIdentifiers_PreserveOutputNamesAndExactQuotes()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE MEASUREMENT Meter (Host TAG, Reading FIELD FLOAT)");
        SqlExecutor.Execute(db,
            "INSERT INTO meter (time, host, reading) VALUES (1000, 'a', 1), (2000, 'a', 2), (3000, 'a', 3)");

        var result = Select(db,
            "SELECT mEtEr.HOST, mEtEr.VALUE, mEtEr.VALUE + 1 AS NextValue FROM forecast(METER, READING, 1, 'linear') WHERE HOST = 'a'");
        Assert.Equal(["Host", "value", "NextValue"], result.Columns);
        Assert.Equal(new object?[] { "a", 4.0, 5.0 }, result.Rows[0]);
        var exact = Select(db,
            "SELECT \"Meter\".\"Host\", \"value\" FROM forecast(\"Meter\", \"Reading\", 1, 'linear')");
        Assert.Equal(new object?[] { "a", 4.0 }, exact.Rows[0]);

        Assert.Throws<InvalidOperationException>(() => Select(db,
            "SELECT * FROM forecast(Meter, \"reading\", 1, 'linear')"));
        Assert.Throws<InvalidOperationException>(() => Select(db,
            "SELECT \"host\" FROM forecast(Meter, Reading, 1, 'linear')"));
        Assert.Throws<InvalidOperationException>(() => Select(db,
            "SELECT \"meter\".Host FROM forecast(Meter, Reading, 1, 'linear')"));
        Assert.Throws<InvalidOperationException>(() => Select(db,
            "SELECT \"HOST\" + 1 FROM forecast(Meter, Reading, 1, 'linear')"));
    }

    /// <summary>向量函数的字段参数、输出投影和引号限定符遵循统一名称合同。</summary>
    [Fact]
    public void Knn_MixedCaseIdentifiers_PreserveOutputNamesAndExactQuotes()
    {
        using var db = Tsdb.Open(Options());
        SqlExecutor.Execute(db, "CREATE MEASUREMENT Vectors (Host TAG, Embedding FIELD VECTOR(3))");
        SqlExecutor.Execute(db,
            "INSERT INTO vectors (time, host, embedding) VALUES (1000, 'a', [1, 0, 0])");

        var result = Select(db,
            "SELECT vEcToRs.HOST, vEcToRs.DISTANCE FROM knn(VECTORS, EMBEDDING, [1, 0, 0], 1) WHERE HOST = 'a'");
        Assert.Equal(["Host", "distance"], result.Columns);
        Assert.Equal("a", result.Rows[0][0]);
        Assert.Equal(0.0, result.Rows[0][1]);
        var exact = Select(db,
            "SELECT \"Vectors\".\"Host\" FROM knn(\"Vectors\", \"Embedding\", [1, 0, 0], 1)");
        Assert.Equal("a", exact.Rows[0][0]);

        Assert.Throws<InvalidOperationException>(() => Select(db,
            "SELECT * FROM knn(Vectors, \"embedding\", [1, 0, 0], 1)"));
        Assert.Throws<InvalidOperationException>(() => Select(db,
            "SELECT \"host\" FROM knn(Vectors, Embedding, [1, 0, 0], 1)"));
        Assert.Throws<InvalidOperationException>(() => Select(db,
            "SELECT \"vectors\".Host FROM knn(Vectors, Embedding, [1, 0, 0], 1)"));
    }

    private static SelectExecutionResult Select(Tsdb db, string sql)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, sql));
}
