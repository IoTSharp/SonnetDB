using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>
/// PR #55 — Forecast TVF + anomaly / changepoint 端到端 SQL 集成测试。
/// </summary>
public sealed class SqlExecutorForecastTests : IDisposable
{
    private readonly string _root;

    public SqlExecutorForecastTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sndb-forecast-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    private static SelectExecutionResult Select(Tsdb db, string sql)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, sql));

    private Tsdb OpenWithLinearSeries(int sampleCount = 20)
    {
        var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db,
            "CREATE MEASUREMENT meter (device TAG, value FIELD FLOAT)");
        var sql = "INSERT INTO meter (time, device, value) VALUES " +
            string.Join(", ",
                Enumerable.Range(0, sampleCount)
                    .Select(i => $"({i * 1000L}, 'm1', {10 + i * 2})"));
        SqlExecutor.Execute(db, sql);
        return db;
    }

    [Fact]
    public void Select_ForecastLinear_ProducesHorizonRowsWithExpectedColumns()
    {
        using var db = OpenWithLinearSeries();
        var r = Select(db, "SELECT * FROM forecast(meter, value, 5, 'linear') WHERE device='m1'");

        Assert.Equal(new[] { "time", "value", "lower", "upper", "device" }, r.Columns.ToArray());
        Assert.Equal(5, r.Rows.Count);

        // 时间应紧随最后一个观测点 (t = 19_000) 之后
        Assert.Equal(20_000L, (long)r.Rows[0][0]!);
        Assert.Equal(24_000L, (long)r.Rows[4][0]!);

        // 期望值 ≈ 10 + 2t (t 单位为索引位置)
        Assert.Equal(50.0, (double)r.Rows[0][1]!, 6);
        Assert.Equal(58.0, (double)r.Rows[4][1]!, 6);

        // tag 列保留
        Assert.Equal("m1", r.Rows[0][4]);
    }

    [Fact]
    public void Select_ForecastLinear_NoMatchedSeries_ReturnsEmpty()
    {
        using var db = OpenWithLinearSeries();
        var r = Select(db, "SELECT * FROM forecast(meter, value, 3, 'linear') WHERE device='absent'");
        Assert.Empty(r.Rows);
    }

    [Fact]
    public void Select_ForecastHoltWinters_WithSeason_ProducesHorizonRows()
    {
        var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db,
            "CREATE MEASUREMENT meter (device TAG, value FIELD FLOAT)");

        // 4 个季 × 4 个点的简单季节序列 [10, 20, 30, 20]
        int season = 4;
        int seasons = 6;
        var rows = new List<string>();
        for (int s = 0; s < seasons; s++)
        {
            for (int i = 0; i < season; i++)
            {
                int idx = s * season + i;
                double v = i switch { 0 => 10, 1 => 20, 2 => 30, _ => 20 };
                rows.Add($"({idx * 1000L}, 'm1', {v})");
            }
        }
        SqlExecutor.Execute(db, "INSERT INTO meter (time, device, value) VALUES " + string.Join(", ", rows));

        var r = Select(db, $"SELECT * FROM forecast(meter, value, {season}, 'holt_winters', {season}) WHERE device='m1'");
        Assert.Equal(season, r.Rows.Count);

        // 第 3 个预测点应该是季节峰值
        double v0 = (double)r.Rows[0][1]!;
        double v2 = (double)r.Rows[2][1]!;
        double v3 = (double)r.Rows[3][1]!;
        Assert.True(v2 > v0, $"季节峰值应在 idx=2，实际 {v0} vs {v2}");
        Assert.True(v2 > v3);

        db.Dispose();
    }

    [Fact]
    public void Select_ForecastInvalidAlgorithm_Throws()
    {
        using var db = OpenWithLinearSeries();
        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(db, "SELECT * FROM forecast(meter, value, 5, 'arima')"));
    }

    [Fact]
    public void Select_ForecastLinear_ProjectTimeAndValue_ReturnsStableColumns()
    {
        // ROADMAP #129.2：forecast(...) 外层支持显式列引用，输出按投影顺序裁剪。
        using var db = OpenWithLinearSeries();
        var r = Select(db, "SELECT time, value FROM forecast(meter, value, 5, 'linear') WHERE device='m1'");

        Assert.Equal(new[] { "time", "value" }, r.Columns.ToArray());
        Assert.Equal(5, r.Rows.Count);
        Assert.All(r.Rows, row => Assert.Equal(2, row.Count));
        Assert.Equal(20_000L, (long)r.Rows[0][0]!);
        Assert.Equal(50.0, (double)r.Rows[0][1]!, 6);
    }

    [Fact]
    public void Select_ForecastLinear_ProjectAlias_PreservesAlias()
    {
        using var db = OpenWithLinearSeries();
        var r = Select(db, "SELECT time AS ts, value AS forecast_value FROM forecast(meter, value, 2, 'linear') WHERE device='m1'");

        Assert.Equal(new[] { "ts", "forecast_value" }, r.Columns.ToArray());
        Assert.Equal(2, r.Rows.Count);
        Assert.Equal(20_000L, (long)r.Rows[0][0]!);
        Assert.Equal(50.0, (double)r.Rows[0][1]!, 6);
    }

    [Fact]
    public void Select_ForecastUnknownProjection_Throws()
    {
        using var db = OpenWithLinearSeries();
        var ex = Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(db, "SELECT bogus FROM forecast(meter, value, 5, 'linear') WHERE device='m1'"));
        Assert.Contains("bogus", ex.Message);
    }

    [Fact]
    public void Select_ForecastRequiresFieldArgument()
    {
        using var db = OpenWithLinearSeries();
        Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(db, "SELECT * FROM forecast(meter, device, 5, 'linear')"));
    }

    // ── anomaly / changepoint via SQL ────────────────────────────────────

    [Fact]
    public void Select_AnomalyZScore_FlagsOutlierRow()
    {
        var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db,
            "CREATE MEASUREMENT cpu (host TAG, usage FIELD FLOAT)");
        SqlExecutor.Execute(db,
            "INSERT INTO cpu (time, host, usage) VALUES " +
            "(0,'h1',10),(1000,'h1',11),(2000,'h1',9),(3000,'h1',10)," +
            "(4000,'h1',12),(5000,'h1',100),(6000,'h1',11),(7000,'h1',9)");

        var r = Select(db, "SELECT time, anomaly(usage, 'zscore', 2.0) FROM cpu");
        Assert.Equal(8, r.Rows.Count);
        Assert.True((bool)r.Rows[5][1]!);
        Assert.False((bool)r.Rows[0][1]!);
        db.Dispose();
    }

    [Fact]
    public void Select_ChangepointCusum_DetectsLevelShift()
    {
        var db = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(db,
            "CREATE MEASUREMENT cpu (host TAG, usage FIELD FLOAT)");
        var values = new double[]
        {
            10, 10.2, 9.8, 10.1, 10, 9.9, 10.3, 10, 9.7, 10.1,
            20, 20.1, 19.9, 20.2, 20, 19.8, 20.1, 19.9, 20, 20.1,
        };
        var sb = new System.Text.StringBuilder();
        sb.Append("INSERT INTO cpu (time, host, usage) VALUES ");
        for (int i = 0; i < values.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append($"({i * 1000L}, 'h1', {values[i].ToString(System.Globalization.CultureInfo.InvariantCulture)})");
        }
        SqlExecutor.Execute(db, sb.ToString());

        var r = Select(db, "SELECT time, changepoint(usage, 'cusum', 3.0) FROM cpu");
        // 跳变后 5 个点内必须出现至少一个 true
        bool fired = false;
        for (int i = 10; i < 15; i++)
            if ((bool)r.Rows[i][1]!) { fired = true; break; }
        Assert.True(fired);
        // 跳变前不应触发
        for (int i = 0; i < 10; i++)
            Assert.False((bool)r.Rows[i][1]!);
        db.Dispose();
    }
}
