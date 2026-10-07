using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.EntityFrameworkCore.Tests;

/// <summary>验证字符串最大长度经模型约定成为可持久化的真实数据库约束。</summary>
public sealed class StringLengthConstraintTests : IDisposable
{
    private const string DirectoryPrefix = "sndb-ef-string-length-";
    private readonly string _owner = Guid.NewGuid().ToString("N");
    private readonly string _directory;
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(2));

    /// <summary>创建本测试独占且带所有权标记的数据库目录。</summary>
    public StringLengthConstraintTests()
    {
        _directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), DirectoryPrefix + _owner));
        if (Directory.Exists(_directory))
        {
            throw new InvalidOperationException("测试目录已存在，拒绝复用。");
        }

        Directory.CreateDirectory(_directory);
        try
        {
            File.WriteAllText(Path.Combine(_directory, "test-owner"), _owner);
        }
        catch
        {
            Directory.Delete(_directory);
            _timeout.Dispose();
            throw;
        }
    }

    /// <summary>同步及异步保存超限字符串必须拒绝并回滚同批中的有效行。</summary>
    /// <param name="useAsync">是否使用异步保存。</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveChanges_WithOverlengthString_RejectsAndRollsBackWholeBatch(bool useAsync)
    {
        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync(_timeout.Token);
        context.Items.AddRange(
            new LengthItem { Id = 1, Value = "valid" },
            new LengthItem { Id = 2, Value = "toolong" });

        _timeout.Token.ThrowIfCancellationRequested();
        var exception = useAsync
            ? await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(_timeout.Token))
            : Assert.Throws<DbUpdateException>(() => context.SaveChanges());

        Assert.Equal(TableConstraintException.CheckViolation, Assert.IsType<TableConstraintException>(exception.InnerException).ErrorCode);
        context.ChangeTracker.Clear();
        Assert.False(await context.Items.AnyAsync(_timeout.Token));
        context.Items.Add(new LengthItem { Id = 3, Value = "valid" });
        Assert.Equal(1, await context.SaveChangesAsync(_timeout.Token));
    }

    /// <summary>允许长度边界、NULL 和无最大长度列，重开后原生 SQL 写入仍受数据库约束。</summary>
    [Fact]
    public async Task Database_AfterReopen_PreservesUnicodeNullAndEnforcesRawWrites()
    {
        await using (var context = CreateContext())
        {
            await context.Database.EnsureCreatedAsync(_timeout.Token);
            context.Items.AddRange(
                new LengthItem { Id = 1, Value = "😀😀a", Unlimited = new string('x', 300) },
                new LengthItem { Id = 2, Value = null });
            await context.SaveChangesAsync(_timeout.Token);
        }

        await using var reopened = CreateContext();
        var items = await reopened.Items.OrderBy(item => item.Id).ToArrayAsync(_timeout.Token);
        Assert.Equal("😀😀a", items[0].Value);
        Assert.Equal(300, items[0].Unlimited!.Length);
        Assert.Null(items[1].Value);
        var exception = await Assert.ThrowsAsync<TableConstraintException>(() => reopened.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"LengthItems\" (\"Id\", \"value\"\"quoted\") VALUES (3, '😀😀ab')", _timeout.Token));
        Assert.Equal(TableConstraintException.CheckViolation, exception.ErrorCode);
        Assert.Equal(2, await reopened.Items.CountAsync(_timeout.Token));
    }

    /// <summary>自动约束出现在设计模型中且不覆盖用户同名的不同 CHECK。</summary>
    [Fact]
    public void Model_WithConflictingUserCheckConstraint_RejectsSilentReplacement()
    {
        using var normal = CreateContext();
        var model = normal.GetService<IDesignTimeModel>().Model;
        var checks = model.FindEntityType(typeof(LengthItem))!.GetCheckConstraints().ToArray();
        Assert.Single(checks);
        Assert.Contains("\"value\"\"quoted\"", checks[0].Sql, StringComparison.Ordinal);
        using var conflicting = new ConflictingLengthContext(CreateOptions<ConflictingLengthContext>());
        Assert.Contains("CHECK", Assert.Throws<InvalidOperationException>(() => { _ = conflicting.Model; }).Message, StringComparison.Ordinal);
    }

    /// <summary>长度变更通过标准模型差异产生旧 CHECK 删除和新 CHECK 添加。</summary>
    [Fact]
    public void Migrations_WithChangedMaximumLength_ReplacesDatabaseConstraint()
    {
        using var initial = CreateContext();
        using var changed = new ExtendedLengthContext(CreateOptions<ExtendedLengthContext>());
        var source = initial.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        var target = changed.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        var operations = initial.GetService<IMigrationsModelDiffer>().GetDifferences(source, target);
        var removed = Assert.Single(operations.OfType<DropCheckConstraintOperation>());
        var added = Assert.Single(operations.OfType<AddCheckConstraintOperation>());
        Assert.Equal(removed.Name, added.Name);
        Assert.Contains("<= 8", added.Sql, StringComparison.Ordinal);
    }

    /// <summary>同表继承实体共享列时只生成一个约束，并对两种实体写入共同生效。</summary>
    [Fact]
    public async Task SaveChanges_WithSharedInheritanceColumn_UsesOneDatabaseConstraint()
    {
        await using var context = new SharedLengthContext(CreateOptions<SharedLengthContext>());
        context.Database.SetCommandTimeout(10);
        var model = context.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        // EF 还为继承判别列声明限长；只核共享业务列的约束没有因两种实体重复生成。
        Assert.Single(model.Tables.Single(table => table.Name == "SharedLengths").CheckConstraints,
            check => check.Name == "CK_SharedLengths_SharedValue_MaxLength");
        await context.Database.EnsureCreatedAsync(_timeout.Token);
        context.AddRange(new FirstSharedItem { Id = 1, Value = "first" }, new SecondSharedItem { Id = 2, Value = "other" });
        await context.SaveChangesAsync(_timeout.Token);
        context.Add(new SecondSharedItem { Id = 3, Value = "too long" });
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(_timeout.Token));
        context.ChangeTracker.Clear();
        Assert.Equal(2, await context.Set<SharedItem>().CountAsync(_timeout.Token));
    }

    /// <summary>字符串经显式转换存为 BLOB 时，不对非字符串列错误添加字符长度约束。</summary>
    /// <param name="useConverterType">是否通过转换器类型而非实例配置转换。</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Model_WithStringConvertedToBytes_DoesNotApplyCharacterLengthCheck(bool useConverterType)
    {
        using DbContext context = useConverterType
            ? new BinaryConverterTypeContext(CreateOptions<BinaryConverterTypeContext>())
            : new BinaryLengthContext(CreateOptions<BinaryLengthContext>());
        var model = context.GetService<IDesignTimeModel>().Model;
        Assert.Empty(model.FindEntityType(typeof(LengthItem))!.GetCheckConstraints());
    }

    /// <summary>只回收仍具有匹配所有权标记和绝对路径的测试目录。</summary>
    public void Dispose()
    {
        _timeout.Dispose();
        var absolute = Path.GetFullPath(_directory);
        var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var marker = Path.Combine(absolute, "test-owner");
        if (!string.Equals(Path.GetDirectoryName(absolute), temp, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(absolute), DirectoryPrefix + _owner, StringComparison.Ordinal)
            || !File.Exists(marker)
            || File.ReadAllText(marker) != _owner)
        {
            throw new InvalidOperationException("拒绝删除所有权或绝对路径不匹配的测试目录。");
        }

        Directory.Delete(absolute, recursive: true);
    }

    private LengthContext CreateContext()
    {
        var context = new LengthContext(CreateOptions<LengthContext>());
        context.Database.SetCommandTimeout(10);
        return context;
    }

    private DbContextOptions<TContext> CreateOptions<TContext>() where TContext : DbContext
        => new DbContextOptionsBuilder<TContext>().UseSonnetDB($"Data Source={_directory}").Options;

    private sealed class LengthContext(DbContextOptions<LengthContext> options) : DbContext(options)
    {
        internal DbSet<LengthItem> Items => Set<LengthItem>();

        protected override void OnModelCreating(ModelBuilder modelBuilder) => ConfigureItem(modelBuilder);
    }

    private sealed class ConflictingLengthContext(DbContextOptions<ConflictingLengthContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            ConfigureItem(modelBuilder);
            modelBuilder.Entity<LengthItem>().ToTable("LengthItems", table =>
                table.HasCheckConstraint("CK_LengthItems_value\"quoted_MaxLength", "1 = 1"));
        }
    }

    private sealed class ExtendedLengthContext(DbContextOptions<ExtendedLengthContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => ConfigureItem(modelBuilder, maximumLength: 8);
    }

    private sealed class SharedLengthContext(DbContextOptions<SharedLengthContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<SharedItem>(entity =>
            {
                entity.ToTable("SharedLengths");
                entity.HasKey(item => item.Id);
                entity.Property(item => item.Id).ValueGeneratedNever();
            });
            modelBuilder.Entity<FirstSharedItem>().Property(item => item.Value).HasColumnName("SharedValue").HasMaxLength(5);
            modelBuilder.Entity<SecondSharedItem>().Property(item => item.Value).HasColumnName("SharedValue").HasMaxLength(5);
        }
    }

    private sealed class BinaryLengthContext(DbContextOptions<BinaryLengthContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            ConfigureItem(modelBuilder);
            modelBuilder.Entity<LengthItem>().Property(item => item.Value).HasConversion(
                value => System.Text.Encoding.UTF8.GetBytes(value!),
                value => System.Text.Encoding.UTF8.GetString(value));
        }
    }

    private sealed class BinaryConverterTypeContext(DbContextOptions<BinaryConverterTypeContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            ConfigureItem(modelBuilder);
            modelBuilder.Entity<LengthItem>().Property(item => item.Value).HasConversion<BinaryValueConverter>();
        }
    }

    private sealed class BinaryValueConverter : ValueConverter<string, byte[]>
    {
        /// <summary>提供可由 EF 类型映射实例化的 UTF-8 转换器。</summary>
        public BinaryValueConverter()
            : base(value => System.Text.Encoding.UTF8.GetBytes(value), value => System.Text.Encoding.UTF8.GetString(value))
        {
        }
    }

    private static void ConfigureItem(ModelBuilder modelBuilder, int maximumLength = 5)
    {
        modelBuilder.Entity<LengthItem>(entity =>
        {
            entity.ToTable("LengthItems");
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).ValueGeneratedNever();
            entity.Property(item => item.Value).HasColumnName("value\"quoted").HasMaxLength(maximumLength);
        });
    }

    private sealed class LengthItem
    {
        public int Id { get; set; }

        public string? Value { get; set; }

        public string? Unlimited { get; set; }
    }

    private abstract class SharedItem
    {
        public int Id { get; set; }
    }

    private sealed class FirstSharedItem : SharedItem
    {
        public string? Value { get; set; }
    }

    private sealed class SecondSharedItem : SharedItem
    {
        public string? Value { get; set; }
    }
}
