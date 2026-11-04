using System.Data;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using SonnetDB.Data;
using Xunit;

namespace SonnetDB.EntityFrameworkCore.Tests;

/// <summary>验证显式数据库参数经真实EF原始SQL执行时，名称和毫秒日期类型保持可绑定。</summary>
public sealed class SonnetDbRawParameterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-ef-raw-parameter-" + Guid.NewGuid().ToString("N"));

    /// <summary>空名由EF分配；普通名称和已有前缀名称都只能生成一个参数前缀。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("stamp")]
    [InlineData("@stamp")]
    public async Task ExplicitDateParameter_InRawSql_PreservesBindingAndComparison(string parameterName)
    {
        Directory.CreateDirectory(_root);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var options = new DbContextOptionsBuilder<ProbeContext>().UseSonnetDB("Data Source=" + _root).Options;
        await using var context = new ProbeContext(options);
        await context.Database.EnsureCreatedAsync(timeout.Token);
        var expected = new DateTime(2026, 11, 4, 12, 13, 14, 123, DateTimeKind.Utc);
        var parameter = new SndbParameter(parameterName, expected) { DbType = DbType.DateTime };
        Assert.Equal(1, await context.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "RawParameterRows" ("Id","Stamp") VALUES ({1},{parameter})""", timeout.Token));
        var stored = await context.Rows.AsNoTracking().SingleAsync(timeout.Token);
        Assert.Equal(expected, stored.Stamp);
        Assert.Equal(1, await context.Rows.CountAsync(row => row.Stamp == expected, timeout.Token));

        var helper = context.GetService<ISqlGenerationHelper>();
        Assert.Equal("@stamp", helper.GenerateParameterName("stamp"));
        Assert.Equal("@stamp", helper.GenerateParameterName("@stamp"));
        Assert.Equal("@stamp", helper.GenerateParameterNamePlaceholder("@stamp"));
        var builder = new StringBuilder();
        helper.GenerateParameterName(builder, "@stamp");
        Assert.Equal("@stamp", builder.ToString());
        builder.Clear();
        helper.GenerateParameterNamePlaceholder(builder, "@stamp");
        Assert.Equal("@stamp", builder.ToString());
    }

    /// <summary>只删除本用例创建的唯一目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    /// <summary>提供单表真实存储，不访问外部数据库。</summary>
    private sealed class ProbeContext(DbContextOptions<ProbeContext> options) : DbContext(options)
    {
        /// <summary>取得当前用例的日期行。</summary>
        public DbSet<ProbeRow> Rows => Set<ProbeRow>();

        /// <summary>显式声明毫秒列，以便区分参数名称故障和原生时间类型。</summary>
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ProbeRow>().ToTable("RawParameterRows").HasKey(row => row.Id);
            modelBuilder.Entity<ProbeRow>().Property(row => row.Stamp).HasColumnType("DATETIME");
        }
    }

    /// <summary>原始SQL保存和比较的最小真实记录。</summary>
    private sealed class ProbeRow
    {
        /// <summary>主键。</summary>
        public int Id { get; set; }
        /// <summary>UTC毫秒时间。</summary>
        public DateTime Stamp { get; set; }
    }
}
