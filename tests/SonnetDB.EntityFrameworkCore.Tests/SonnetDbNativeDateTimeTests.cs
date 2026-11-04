using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using SonnetDB.EntityFrameworkCore.Extensions;
using Xunit;

namespace SonnetDB.EntityFrameworkCore.Tests;

/// <summary>验证真实亚毫秒日期的默认、显式原生和旧列存储合同。</summary>
public sealed class SonnetDbNativeDateTimeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ef-datetime-" + Guid.NewGuid().ToString("N"));

    /// <summary>原生参数和字面量保留亚毫秒，显式旧列对完整毫秒输入保持重开结果。</summary>
    [Theory]
    [InlineData("default", DateTimeKind.Utc)]
    [InlineData("default", DateTimeKind.Local)]
    [InlineData("default", DateTimeKind.Unspecified)]
    [InlineData("native", DateTimeKind.Utc)]
    [InlineData("native", DateTimeKind.Local)]
    [InlineData("native", DateTimeKind.Unspecified)]
    [InlineData("legacy", DateTimeKind.Utc)]
    [InlineData("legacy", DateTimeKind.Local)]
    [InlineData("legacy", DateTimeKind.Unspecified)]
    public async Task DateTime_WithParameterAndLiteral_PreservesDeclaredPrecisionAfterReopen(string mode, DateTimeKind kind)
    {
        var milliseconds = new DateTime(2026, 11, 3, 13, 14, 15, 123, DateTimeKind.Utc);
        var utc = mode == "legacy" ? milliseconds : milliseconds.AddTicks(4567);
        var original = kind switch
        {
            DateTimeKind.Local => utc.ToLocalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(utc, DateTimeKind.Unspecified),
            _ => utc
        };
        var expected = utc;
        var capture = new ParameterCapture();
        await using (var context = CreateContext(mode, capture))
        {
            var mapping = context.Model.FindEntityType(typeof(StampRow))!.FindProperty(nameof(StampRow.Stamp))!.GetRelationalTypeMapping();
            Assert.Equal(mode == "legacy" ? "DATETIME" : "DATETIME2", mapping.StoreType);
            var parameterType = mode == "legacy" ? DbType.DateTime : DbType.DateTime2;
            Assert.Equal(parameterType, mapping.DbType);
            Assert.Contains(mapping.StoreType, context.Database.GenerateCreateScript(), StringComparison.Ordinal);
            await context.Database.EnsureCreatedAsync();
            context.Add(new StampRow { Id = 1, Stamp = original });
            await context.SaveChangesAsync();
            Assert.Contains(capture.Values, value => value.Type == parameterType && value.Value is DateTime date && date.Ticks == original.Ticks && date.Kind == original.Kind);

            var literal = mapping.GenerateSqlLiteral(original);
            Assert.Contains(mode == "legacy" ? "AS DATETIME)" : "AS DATETIME2)", literal, StringComparison.Ordinal);
            Assert.Contains(mode == "legacy" ? "2026-11-03T13:14:15.1230000Z" : "2026-11-03T13:14:15.1234567Z", literal, StringComparison.Ordinal);
            // 仅拼接当前映射生成的 SQL 字面量，实际执行以覆盖无参数日期表达式。
            var insertSql = string.Concat("INSERT INTO \"StampRows\" (\"Id\",\"Stamp\",\"OptionalStamp\") VALUES (2,", literal, ",", literal, ")");
            await context.Database.ExecuteSqlRawAsync(insertSql);
            context.ChangeTracker.Clear();

            var rows = await context.Set<StampRow>().OrderBy(row => row.Id).ToArrayAsync();
            Assert.Equal(2, rows.Length);
            Assert.All(rows, row =>
            {
                Assert.Equal(expected.Ticks, row.Stamp.Ticks);
                Assert.Equal(DateTimeKind.Utc, row.Stamp.Kind);
            });
            Assert.Null(rows[0].OptionalStamp);
            Assert.Equal(expected.Ticks, rows[1].OptionalStamp!.Value.Ticks);
            Assert.Equal(2, await context.Set<StampRow>().CountAsync(row => row.Stamp == expected));
            rows[0].Stamp = original.AddHours(1);
            rows[0].OptionalStamp = original;
            await context.SaveChangesAsync();
        }

        await using var reopened = CreateContext(mode, new ParameterCapture());
        var stored = await reopened.Set<StampRow>().AsNoTracking().OrderBy(row => row.Id).ToArrayAsync();
        Assert.Equal(expected.AddHours(1).Ticks, stored[0].Stamp.Ticks);
        Assert.Equal(expected.Ticks, stored[0].OptionalStamp!.Value.Ticks);
        Assert.Equal(expected.Ticks, stored[1].Stamp.Ticks);
    }

    /// <summary>旧列拒绝参数和字面量中的亚毫秒，失败后没有写入且连接仍可使用。</summary>
    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public async Task DateTime_WithExplicitLegacyColumn_RejectsSubMillisecondWithoutChangingInputOrRows(DateTimeKind kind)
    {
        var utc = new DateTime(2026, 11, 3, 13, 14, 15, 123, DateTimeKind.Utc).AddTicks(4567);
        var original = kind switch
        {
            DateTimeKind.Local => utc.ToLocalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(utc, DateTimeKind.Unspecified),
            _ => utc
        };
        var capture = new ParameterCapture();
        await using var context = CreateContext("legacy", capture);
        await context.Database.EnsureCreatedAsync();
        var row = new StampRow { Id = 1, Stamp = original };
        context.Add(row);
        var failure = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        Assert.IsType<ArgumentException>(failure.InnerException);
        Assert.Equal(original.Ticks, row.Stamp.Ticks);
        Assert.Contains(capture.Values, value => value.Type == DbType.DateTime && value.Value is DateTime date && date.Ticks == original.Ticks);
        var mapping = context.Model.FindEntityType(typeof(StampRow))!.FindProperty(nameof(StampRow.Stamp))!.GetRelationalTypeMapping();
        Assert.Throws<ArgumentException>(() => mapping.GenerateSqlLiteral(original));
        context.ChangeTracker.Clear();
        Assert.Equal(0, await context.Set<StampRow>().CountAsync());
        var valid = new DateTime(2026, 11, 3, 13, 14, 15, 123, DateTimeKind.Utc);
        context.Add(new StampRow { Id = 2, Stamp = valid });
        await context.SaveChangesAsync();
        Assert.Equal(valid, await context.Set<StampRow>().Select(item => item.Stamp).SingleAsync());
    }

    /// <summary>已有旧日期行升级到原生列后保留原值，并接受亚毫秒写入和重开。</summary>
    [Fact]
    public async Task DateTime_WithExistingLegacyRows_PreservesValuesAfterNativeColumnMigrationAndReopen()
    {
        var original = new DateTime(2026, 11, 3, 13, 14, 15, 123, DateTimeKind.Utc);
        await using (var legacy = CreateContext("legacy", new ParameterCapture()))
        {
            await legacy.Database.EnsureCreatedAsync();
            legacy.Add(new StampRow { Id = 1, Stamp = original, OptionalStamp = original });
            await legacy.SaveChangesAsync();
        }

        var precise = original.AddTicks(4567);
        await using (var native = CreateContext("default", new ParameterCapture()))
        {
            await native.Database.ExecuteSqlRawAsync("ALTER TABLE \"StampRows\" ALTER COLUMN \"Stamp\" TYPE DATETIME2 NOT NULL DROP DEFAULT");
            await native.Database.ExecuteSqlRawAsync("ALTER TABLE \"StampRows\" ALTER COLUMN \"OptionalStamp\" TYPE DATETIME2 NULL DROP DEFAULT");
            var row = await native.Set<StampRow>().SingleAsync();
            Assert.Equal(original.Ticks, row.Stamp.Ticks);
            Assert.Equal(DateTimeKind.Utc, row.Stamp.Kind);
            Assert.Equal(original.Ticks, row.OptionalStamp!.Value.Ticks);
            row.Stamp = precise;
            row.OptionalStamp = precise;
            await native.SaveChangesAsync();
        }

        await using var reopened = CreateContext("default", new ParameterCapture());
        var stored = await reopened.Set<StampRow>().AsNoTracking().SingleAsync();
        Assert.Equal(precise.Ticks, stored.Stamp.Ticks);
        Assert.Equal(DateTimeKind.Utc, stored.Stamp.Kind);
        Assert.Equal(precise.Ticks, stored.OptionalStamp!.Value.Ticks);
        Assert.Equal(1, await reopened.Set<StampRow>().CountAsync(row => row.Stamp == precise));
    }

    /// <summary>原生与旧存储类型都暴露日期 CLR 类型，默认映射使用原生参数精度。</summary>
    [Fact]
    public void FindMapping_DefaultAndExplicitStoreTypes_ReportMatchingDeclarations()
    {
        using var context = CreateContext("default", new ParameterCapture());
        var mappings = context.GetService<IRelationalTypeMappingSource>();
        Assert.Equal("DATETIME2", mappings.FindMapping(typeof(DateTime))!.StoreType);
        Assert.Equal(DbType.DateTime2, mappings.FindMapping(typeof(DateTime))!.DbType);
        Assert.Equal(typeof(DateTime), mappings.FindMapping("DATETIME2")!.ClrType);
        Assert.Equal(typeof(DateTime), mappings.FindMapping("DATETIME")!.ClrType);
        Assert.Equal("DATETIME", mappings.FindMapping("DATETIME")!.StoreType);
        Assert.Equal(DbType.DateTime, mappings.FindMapping("DATETIME")!.DbType);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private DbContext CreateContext(string mode, ParameterCapture capture)
    {
        Directory.CreateDirectory(_directory);
        var connection = "Data Source=" + _directory;
        return mode switch
        {
            "legacy" => new LegacyContext(new DbContextOptionsBuilder<LegacyContext>().UseSonnetDB(connection).AddInterceptors(capture).Options),
            "native" => new NativeContext(new DbContextOptionsBuilder<NativeContext>().UseSonnetDB(connection).AddInterceptors(capture).Options),
            _ => new DefaultContext(new DbContextOptionsBuilder<DefaultContext>().UseSonnetDB(connection).AddInterceptors(capture).Options)
        };
    }

    private static void Configure(ModelBuilder builder, string? declaration)
    {
        var entity = builder.Entity<StampRow>();
        entity.ToTable("StampRows");
        entity.HasKey(row => row.Id);
        entity.Property(row => row.Id).ValueGeneratedNever();
        if (declaration is not null)
        {
            entity.Property(row => row.Stamp).HasColumnType(declaration);
            entity.Property(row => row.OptionalStamp).HasColumnType(declaration);
        }
    }

    private sealed class DefaultContext(DbContextOptions<DefaultContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder) => Configure(builder, null);
    }

    private sealed class NativeContext(DbContextOptions<NativeContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder) => Configure(builder, "DATETIME2");
    }

    private sealed class LegacyContext(DbContextOptions<LegacyContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder) => Configure(builder, "DATETIME");
    }

    private sealed class StampRow
    {
        public long Id { get; set; }
        public DateTime Stamp { get; set; }
        public DateTime? OptionalStamp { get; set; }
    }

    private sealed class ParameterCapture : DbCommandInterceptor
    {
        public List<(DbType Type, object? Value)> Values { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Values.AddRange(command.Parameters.Cast<DbParameter>().Select(parameter => (parameter.DbType, parameter.Value)));
            return ValueTask.FromResult(result);
        }
    }
}
