using Microsoft.EntityFrameworkCore;
using SonnetDB.EntityFrameworkCore.Extensions;
using Xunit;

namespace SonnetDB.EntityFrameworkCore.Tests;

public sealed partial class SonnetDbJsonAggregateModelTests
{
    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("h2c")]
    public async Task ExecuteUpdate_NullRootThenChild_RejectsWithoutPartialWrite(string profile)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        await using var fixture = new JsonFixture(profile, _output);
        await fixture.StartAsync(token);
        var capture = new JsonCommandCapture();
        await using var context = fixture.CreateContext(true, capture);
        await context.Database.EnsureCreatedAsync(token);
        context.Add(new AggregateRow { Id = 1, Stamp = "initial", Data = InitialAggregate() });
        Assert.Equal(1, await context.SaveChangesAsync(token));
        var before = await ReadRawAsync(context, 1, token);
        await Assert.ThrowsAnyAsync<Exception>(() => context.Set<AggregateRow>().ExecuteUpdateAsync(setters => setters
            .SetProperty(row => row.Data, (AggregateData)null!)
            .SetProperty(row => row.Data.Counter, 999)
            .SetProperty(row => row.Stamp, "must-not-write"), token));
        Assert.Equal(before, await ReadRawAsync(context, 1, token));
        Assert.Equal("initial", await context.Set<AggregateRow>().Select(row => row.Stamp).SingleAsync(token));
        Assert.Equal(1, await context.Set<AggregateRow>().ExecuteUpdateAsync(setters => setters
            .SetProperty(row => row.Data, (AggregateData)null!), token));
        Assert.Null(await ReadRawAsync(context, 1, token));
        var restored = InitialAggregate();
        Assert.Equal(1, await context.Set<AggregateRow>().ExecuteUpdateAsync(setters => setters
            .SetProperty(row => row.Data, restored), token));
        Assert.Equal(restored.Counter, (await context.Set<AggregateRow>().AsNoTracking().SingleAsync(token)).Data.Counter);
        fixture.AssertTransport();
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task ExecuteUpdate_EncodedValueBudget_RejectsBeforeSendingCommand(bool owned, bool binary)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        await using var fixture = new JsonFixture("embedded", _output);
        await fixture.StartAsync(token);
        var capture = new JsonCommandCapture();
        await using var context = fixture.CreateContext(owned, capture);
        byte[] bytes = binary ? new byte[8 * 1024 * 1024 + 1] : [];
        List<string> items = binary ? [] : Enumerable.Repeat("a", 100001).ToList();
        await Assert.ThrowsAsync<InvalidOperationException>(() => binary
            ? context.Set<AggregateRow>().ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Data.Bytes, bytes), token)
            : context.Set<AggregateRow>().ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Data.Tags, items), token));
        Assert.Empty(capture.Commands);
    }

    [Theory]
    [InlineData(64)]
    [InlineData(65)]
    public async Task ExecuteUpdate_SetterBudget_RejectsBeforeCommandOrAppliesInOrder(int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        await using var fixture = new JsonFixture("embedded", _output);
        await fixture.StartAsync(token);
        var capture = new JsonCommandCapture();
        await using var context = fixture.CreateContext(true, capture);
        await context.Database.EnsureCreatedAsync(token);
        context.Add(new AggregateRow { Id = 1, Stamp = "initial", Data = InitialAggregate() });
        Assert.Equal(1, await context.SaveChangesAsync(token));
        var before = capture.Commands.Count;
        async Task<int> UpdateAsync() => await context.Set<AggregateRow>().ExecuteUpdateAsync(setters =>
        {
            for (int i = 0; i < count; i++)
                setters.SetProperty(row => row.Data.Counter, i);
        }, token);
        if (count == 65)
        {
            await Assert.ThrowsAsync<NotSupportedException>(UpdateAsync);
            Assert.Equal(before, capture.Commands.Count);
        }
        else
        {
            Assert.Equal(1, await UpdateAsync());
            Assert.Single(capture.Commands.Skip(before));
            Assert.Equal(63, await context.Set<AggregateRow>().Select(row => row.Data.Counter).SingleAsync(token));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExecuteUpdate_ObjectWithOnlyNestedObjects_UsesActualModelPath(bool owned)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        await using var fixture = new JsonFixture("embedded", _output);
        await fixture.StartAsync(token);
        var capture = new JsonCommandCapture();
        await using var context = fixture.CreateWrapperContext(owned, capture);
        await context.Database.EnsureCreatedAsync(token);
        context.Add(new WrapperRow { Id = 1, Data = new WrapperData { Child = new AggregateChild { Label = "old", Counter = 1 } } });
        Assert.Equal(1, await context.SaveChangesAsync(token));
        var replacement = new WrapperData { Child = new AggregateChild { Label = "new 中文😃", Counter = 2 } };
        var before = capture.Commands.Count;
        Assert.Equal(1, await context.Set<WrapperRow>().ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Data, replacement), token));
        Assert.Single(capture.Commands.Skip(before));
        var actual = await context.Set<WrapperRow>().AsNoTracking().SingleAsync(token);
        Assert.Equal(replacement.Child.Label, actual.Data.Child.Label);
        Assert.Equal(2, actual.Data.Child.Counter);
    }

    private sealed partial class JsonFixture
    {
        public DbContext CreateWrapperContext(bool owned, JsonCommandCapture capture)
        {
            _contextReferences++;
            return owned
                ? new WrapperOwnedContext(new DbContextOptionsBuilder<WrapperOwnedContext>()
                    .UseSonnetDB(_connectionString).AddInterceptors(capture).Options, () => _contextReferences--)
                : new WrapperComplexContext(new DbContextOptionsBuilder<WrapperComplexContext>()
                    .UseSonnetDB(_connectionString).AddInterceptors(capture).Options, () => _contextReferences--);
        }
    }

    private sealed class WrapperOwnedContext(DbContextOptions<WrapperOwnedContext> options, Action release) : AggregateContext(options, release)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            builder.Entity<WrapperRow>().ToTable("Wrappers");
            builder.Entity<WrapperRow>().Property(row => row.Id).ValueGeneratedNever();
            builder.Entity<WrapperRow>().OwnsOne(row => row.Data, data =>
            {
                data.ToJson("Data");
                data.OwnsOne(value => value.Child);
            });
        }
    }

    private sealed class WrapperComplexContext(DbContextOptions<WrapperComplexContext> options, Action release) : AggregateContext(options, release)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            builder.Entity<WrapperRow>().ToTable("Wrappers");
            builder.Entity<WrapperRow>().Property(row => row.Id).ValueGeneratedNever();
            builder.Entity<WrapperRow>().ComplexProperty(row => row.Data, data =>
            {
                data.ToJson("Data");
                data.ComplexProperty(value => value.Child);
            });
        }
    }

    private sealed class WrapperRow
    {
        public long Id { get; set; }
        public WrapperData Data { get; set; } = new();
    }

    private sealed class WrapperData
    {
        public AggregateChild Child { get; set; } = new();
    }
}
