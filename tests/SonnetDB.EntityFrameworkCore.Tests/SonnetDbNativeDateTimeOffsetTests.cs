using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using SonnetDB.EntityFrameworkCore.Extensions;
using Xunit;

namespace SonnetDB.EntityFrameworkCore.Tests;

public sealed class SonnetDbNativeDateTimeOffsetTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ef-dto-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DateTimeOffset_WithNativeOrExplicitLegacyMapping_PreservesStorageAndQueryContracts(bool legacy)
    {
        var original = new DateTimeOffset(2026, 7, 10, 12, 30, 0, TimeSpan.FromHours(8)).AddTicks(1234);
        var stored = legacy ? DateTimeOffset.FromUnixTimeMilliseconds(original.ToUnixTimeMilliseconds()) : original.ToUniversalTime();
        var expected = stored.ToOffset(TimeSpan.FromHours(-5));
        await using (var context = CreateContext(legacy))
        {
            string declaration = legacy ? "DATETIME" : "DATETIMEOFFSET";
            var property = context.Model.FindEntityType(typeof(OffsetRow))!.FindProperty(nameof(OffsetRow.Stamp))!;
            Assert.Equal(declaration, property.GetRelationalTypeMapping().StoreType);
            Assert.Equal(DbType.DateTimeOffset, property.GetRelationalTypeMapping().DbType);
            Assert.Contains("AS DATETIMEOFFSET)", property.GetRelationalTypeMapping().GenerateSqlLiteral(original), StringComparison.Ordinal);
            Assert.Contains(declaration, context.Database.GenerateCreateScript(), StringComparison.Ordinal);
            await context.Database.EnsureCreatedAsync();
            var insertStamp = legacy ? stored : original;
            context.AddRange(new OffsetRow { Id = 1, Stamp = insertStamp }, new OffsetRow { Id = 2, Stamp = insertStamp.AddDays(1), OptionalStamp = insertStamp });
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            var query = context.Set<OffsetRow>().Where(row => row.Stamp == expected);
            string sql = query.ToQueryString();
            if (legacy)
                Assert.Contains("AS DATETIMEOFFSET)", sql, StringComparison.Ordinal);
            var row = await query.SingleAsync();
            Assert.Equal(stored.UtcTicks, row.Stamp.UtcTicks);
            Assert.Equal(TimeSpan.Zero, row.Stamp.Offset);
            Assert.Null(row.OptionalStamp);
            Assert.Equal(stored.UtcTicks + 1, (await query.Select(item => item.Stamp.AddTicks(1)).SingleAsync()).UtcTicks);

            // 字面量与参数分别进入类型化日期合同；旧 DATETIME 列不因比较而改变存储精度。
            Assert.Equal(2, await context.Set<OffsetRow>().CountAsync(item => item.Stamp > new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));
            Assert.Equal(2, await context.Set<OffsetRow>().CountAsync(item => item.Stamp <= DateTimeOffset.UtcNow));
            var optional = await context.Set<OffsetRow>().Where(item => item.Id == 2).Select(item => item.OptionalStamp).SingleAsync();
            Assert.Equal(stored.UtcTicks, optional!.Value.UtcTicks);

            row.Stamp = stored.AddHours(1);
            await context.SaveChangesAsync();
            Assert.Equal(1, await context.Set<OffsetRow>().Where(item => item.Id == 1)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Stamp, item => item.Stamp.AddHours(1))));
            Assert.Equal(1, await context.Set<OffsetRow>().Where(item => item.Stamp > expected.AddHours(3)).ExecuteDeleteAsync());
        }

        await using var reopened = CreateContext(legacy);
        var remaining = await reopened.Set<OffsetRow>().AsNoTracking().SingleAsync();
        Assert.Equal(1, remaining.Id);
        Assert.Equal(stored.AddHours(2).UtcTicks, remaining.Stamp.UtcTicks);
    }

    [Fact]
    public void TypeMapping_DefaultOffsetAndDateTime_AreDistinctNativeDeclarations()
    {
        using var context = CreateContext(legacy: false);
        var mappings = context.GetService<IRelationalTypeMappingSource>();
        Assert.Equal("DATETIMEOFFSET", mappings.FindMapping(typeof(DateTimeOffset))!.StoreType);
        Assert.Equal("DATETIME", mappings.FindMapping(typeof(DateTime))!.StoreType);
        Assert.Equal(typeof(DateTimeOffset), mappings.FindMapping("DATETIMEOFFSET")!.ClrType);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private DbContext CreateContext(bool legacy)
    {
        Directory.CreateDirectory(_directory);
        return legacy
            ? new LegacyContext(new DbContextOptionsBuilder<LegacyContext>()
                .UseSonnetDB($"Data Source={_directory}")
                .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning)).Options)
            : new NativeContext(new DbContextOptionsBuilder<NativeContext>()
                .UseSonnetDB($"Data Source={_directory}")
                .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning)).Options);
    }

    private sealed class NativeContext(DbContextOptions<NativeContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => Configure(modelBuilder, legacy: false);
    }

    private sealed class LegacyContext(DbContextOptions<LegacyContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => Configure(modelBuilder, legacy: true);
    }

    private static void Configure(ModelBuilder modelBuilder, bool legacy)
    {
        var entity = modelBuilder.Entity<OffsetRow>();
        entity.ToTable("OffsetRows");
        entity.HasKey(row => row.Id);
        entity.Property(row => row.Id).ValueGeneratedNever();
        if (legacy)
        {
            entity.Property(row => row.Stamp).HasColumnType("DATETIME");
            entity.Property(row => row.OptionalStamp).HasColumnType("DATETIME");
        }
    }

    private sealed class OffsetRow
    {
        public long Id { get; set; }
        public DateTimeOffset Stamp { get; set; }
        public DateTimeOffset? OptionalStamp { get; set; }
    }
}
