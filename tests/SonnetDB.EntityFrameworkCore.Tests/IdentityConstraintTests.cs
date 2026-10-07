using System.Data.Common;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SonnetDB.Data;
using SonnetDB.EntityFrameworkCore.Extensions;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.EntityFrameworkCore.Tests;

/// <summary>验证 Identity 唯一用户名及事务提交错误遵循 EF Core 保存异常合同。</summary>
public sealed class IdentityConstraintTests : IDisposable
{
    private const string DirectoryPrefix = "sndb-ef-identity-constraints-";
    private readonly string _owner = Guid.NewGuid().ToString("N");
    private readonly string _directory;
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(2));

    /// <summary>为每个回归测试创建独占、带所有权标记的数据库目录。</summary>
    public IdentityConstraintTests()
    {
        _directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), DirectoryPrefix + _owner));
        if (Directory.Exists(_directory))
        {
            throw new InvalidOperationException("回归测试目录已存在，拒绝复用。");
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

    /// <summary>同步和异步自动事务的唯一约束失败必须关联全部条目、回滚整批并允许后续保存。</summary>
    /// <param name="useAsync">是否使用异步保存。</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveChanges_WithDuplicateNormalizedUserName_ReportsEntriesAndRollsBack(bool useAsync)
    {
        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync(_timeout.Token);
        var first = new ConstraintUser { Id = "first", NormalizedUserName = "DUPLICATE" };
        var second = new ConstraintUser { Id = "second", NormalizedUserName = "DUPLICATE" };
        context.Users.AddRange(first, second);

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(context, useAsync));

        Assert.Equal(TableConstraintException.UniqueViolation, Assert.IsType<TableConstraintException>(exception.InnerException).ErrorCode);
        Assert.Equal(2, exception.Entries.Count);
        Assert.Contains(exception.Entries, entry => ReferenceEquals(entry.Entity, first));
        Assert.Contains(exception.Entries, entry => ReferenceEquals(entry.Entity, second));
        context.ChangeTracker.Clear();
        Assert.False(await context.Users.AnyAsync(_timeout.Token));
        context.Users.Add(new ConstraintUser { Id = "retry", NormalizedUserName = "VALID" });
        Assert.Equal(1, await SaveAsync(context, useAsync));
        Assert.Equal("retry", (await context.Users.SingleAsync(_timeout.Token)).Id);
    }

    /// <summary>单条命令已包装的约束错误必须保留原始内层错误和唯一失败条目。</summary>
    /// <param name="useAsync">是否使用异步保存。</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveChanges_WithSingleDuplicateUser_DoesNotWrapUpdateExceptionTwice(bool useAsync)
    {
        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync(_timeout.Token);
        context.Users.Add(new ConstraintUser { Id = "existing", NormalizedUserName = "DUPLICATE" });
        await SaveAsync(context, useAsync);
        context.ChangeTracker.Clear();
        var duplicate = new ConstraintUser { Id = "duplicate", NormalizedUserName = "DUPLICATE" };
        context.Users.Add(duplicate);

        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(context, useAsync));

        Assert.Equal(TableConstraintException.UniqueViolation, Assert.IsType<TableConstraintException>(exception.InnerException).ErrorCode);
        Assert.Same(duplicate, Assert.Single(exception.Entries).Entity);
        context.ChangeTracker.Clear();
        Assert.Equal("existing", (await context.Users.SingleAsync(_timeout.Token)).Id);
    }

    /// <summary>远程提交返回稳定并发错误码时必须报告并发异常并关联实际修改条目。</summary>
    /// <param name="useAsync">是否使用异步保存。</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveChanges_WithRemoteCommitConcurrencyFailure_ReportsConcurrencyEntries(bool useAsync)
    {
        var failure = new SndbServerException(TableConstraintException.ConcurrencyConflict, "提交版本冲突。", HttpStatusCode.Conflict);
        await using var context = CreateContext(new CommitFailureInterceptor(failure));
        await context.Database.EnsureCreatedAsync(_timeout.Token);
        context.Users.AddRange(
            new ConstraintUser { Id = "first", NormalizedUserName = "FIRST" },
            new ConstraintUser { Id = "second", NormalizedUserName = "SECOND" });

        var exception = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => SaveAsync(context, useAsync));

        Assert.Same(failure, exception.InnerException);
        Assert.Equal(2, exception.Entries.Count);
        context.ChangeTracker.Clear();
        Assert.False(await context.Users.AnyAsync(_timeout.Token));
    }

    /// <summary>提交阶段的 I/O 及非约束远程错误必须保持原异常，不能误报为数据库约束错误。</summary>
    /// <param name="useAsync">是否使用异步保存。</param>
    /// <param name="useRemoteError">是否注入非约束远程错误。</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SaveChanges_WithUnrelatedCommitFailure_PreservesOriginalException(bool useAsync, bool useRemoteError)
    {
        Exception failure = useRemoteError
            ? new SndbServerException("frame_transport_error", "提交结果未知。", HttpStatusCode.BadGateway)
            : new IOException("提交存储不可用。");
        await using var context = CreateContext(new CommitFailureInterceptor(failure));
        await context.Database.EnsureCreatedAsync(_timeout.Token);
        context.Users.AddRange(
            new ConstraintUser { Id = "first", NormalizedUserName = "FIRST" },
            new ConstraintUser { Id = "second", NormalizedUserName = "SECOND" });

        var exception = await Record.ExceptionAsync(() => SaveAsync(context, useAsync));

        Assert.Same(failure, exception);
        context.ChangeTracker.Clear();
        Assert.False(await context.Users.AnyAsync(_timeout.Token));
    }

    /// <summary>仅回收本测试拥有且仍带有匹配标记的独占数据库目录。</summary>
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
            throw new InvalidOperationException("拒绝删除绝对路径或所有权不匹配的测试目录。");
        }

        Directory.Delete(absolute, recursive: true);
    }

    private ConstraintContext CreateContext(params IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<ConstraintContext>()
            .UseSonnetDB($"Data Source={_directory}")
            .AddInterceptors(interceptors)
            .Options;
        var context = new ConstraintContext(options);
        context.Database.SetCommandTimeout(10);
        return context;
    }

    private Task<int> SaveAsync(ConstraintContext context, bool useAsync)
    {
        _timeout.Token.ThrowIfCancellationRequested();
        return useAsync ? context.SaveChangesAsync(_timeout.Token) : Task.FromResult(context.SaveChanges());
    }

    private sealed class ConstraintContext(DbContextOptions<ConstraintContext> options) : DbContext(options)
    {
        internal DbSet<ConstraintUser> Users => Set<ConstraintUser>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ConstraintUser>(entity =>
            {
                entity.ToTable("AspNetUsers");
                entity.HasKey(user => user.Id);
                entity.Property(user => user.Id).ValueGeneratedNever();
                entity.HasIndex(user => user.NormalizedUserName).HasDatabaseName("UserNameIndex").IsUnique();
            });
        }
    }

    private sealed class ConstraintUser
    {
        public string Id { get; set; } = string.Empty;

        public string? NormalizedUserName { get; set; }
    }

    private sealed class CommitFailureInterceptor(Exception failure) : DbTransactionInterceptor
    {
        /// <inheritdoc />
        public override InterceptionResult TransactionCommitting(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result)
            => throw failure;

        /// <inheritdoc />
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<InterceptionResult>(failure);
    }
}
