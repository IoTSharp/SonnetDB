using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SonnetDB.EntityFrameworkCore.Extensions;
using Xunit;

namespace SonnetDB.EntityFrameworkCore.Tests;

public sealed partial class SonnetDbJsonAggregateModelTests
{
    private const string CollectionName = "items.'[root] 😃";
    private const string ItemName = "name.'[value] 中文";

    /// <summary>真实 OwnsMany 元素标量修改保留顺序、重复值及模型外兄弟属性。</summary>
    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("h2c")]
    public async Task OwnsMany_ElementScalarUpdate_PreservesSiblingMembersAndReopens(string profile)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        await using var fixture = new JsonFixture(profile, _output);
        await fixture.StartAsync(token);
        var capture = new JsonCommandCapture();
        await using (var setup = fixture.CreateCollectionContext(capture))
        {
            await setup.Database.EnsureCreatedAsync(token);
            setup.Add(new CollectionRow
            {
                Id = 1,
                Stamp = "first",
                Data = new CollectionData
                {
                    Title = "集合",
                    Items = [NewItem("duplicate"), NewItem("duplicate")],
                },
            });
            Assert.Equal(1, await setup.SaveChangesAsync(token));
        }

        await using (var context = fixture.CreateCollectionContext(capture))
        {
            var before = (await ReadRawAsync(context, 1, token))!;
            var withUnknown = before[..^1] + ",\"Unknown\":9007199254740993}";
            Assert.Equal(1, await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"AggregateRows\" SET \"Data\"={withUnknown} WHERE \"Id\"=1", token));
            var row = await context.Set<CollectionRow>().SingleAsync(row => row.Id == 1, token);
            Assert.Equal(2, row.Data!.Items.Count);
            row.Data.Items[0].Name = "changed O'Reilly 😃";
            Assert.Equal(1, await context.SaveChangesAsync(token));
            Assert.Contains("json_set_value(", capture.CompletedUpdates.Last().Command.Sql);
            using var raw = JsonDocument.Parse((await ReadRawAsync(context, 1, token))!);
            Assert.Equal(9007199254740993L, raw.RootElement.GetProperty("Unknown").GetInt64());
        }

        await fixture.ReopenAsync(token);
        await using (var reopened = fixture.CreateCollectionContext(capture))
        {
            var row = await reopened.Set<CollectionRow>().SingleAsync(row => row.Id == 1, token);
            Assert.Equal("changed O'Reilly 😃", row.Data!.Items[0].Name);
            Assert.Equal("duplicate", row.Data.Items[1].Name);
            Assert.Equal("lower", row.Data.Items[0].Converted);
        }
        fixture.AssertTransport();
    }

    /// <summary>嵌套元素、转换器、多属性、集合增删及清空均复用 EF 跟踪与 JSON writer。</summary>
    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("h2c")]
    public async Task OwnsMany_TrackedCollectionLifecycle_PreservesOrdinalsAndNestedData(string profile)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        await using var fixture = new JsonFixture(profile, _output);
        await fixture.StartAsync(token);
        var capture = new JsonCommandCapture();
        await SeedCollectionAsync(fixture, capture, token);
        await using (var context = fixture.CreateCollectionContext(capture))
        {
            var row = await context.Set<CollectionRow>().SingleAsync(row => row.Id == 1, token);
            row.Data!.Items[0].Parts[0].Count = 9;
            Assert.Equal(1, await context.SaveChangesAsync(token));
            using (var nested = JsonDocument.Parse((await ReadRawAsync(context, 1, token))!))
                Assert.Equal(9, nested.RootElement.GetProperty(CollectionName)[0].GetProperty("Parts")[0].GetProperty("Count").GetInt32());

            row.Data.Items[1].Name = "保留 \"quote\\";
            row.Data.Items[1].Optional = "可空→有值";
            row.Data.Items[1].Converted = "CHANGED";
            row.Data.Items[1].Token = Guid.Empty;
            Assert.Equal(1, await context.SaveChangesAsync(token));
            using (var changed = JsonDocument.Parse((await ReadRawAsync(context, 1, token))!))
            {
                var item = changed.RootElement.GetProperty(CollectionName)[1];
                Assert.Equal("CHANGED", item.GetProperty("Converted").GetString());
                Assert.Equal(Guid.Empty, item.GetProperty("Token").GetGuid());
                Assert.Equal("可空→有值", item.GetProperty("Optional").GetString());
            }

            row.Data.Items.RemoveAt(0);
            row.Data.Items.Add(NewItem("新增"));
            Assert.Equal(1, await context.SaveChangesAsync(token));
            using (var changed = JsonDocument.Parse((await ReadRawAsync(context, 1, token))!))
            {
                var items = changed.RootElement.GetProperty(CollectionName);
                Assert.Equal(2, items.GetArrayLength());
                Assert.Equal("保留 \"quote\\", items[0].GetProperty(ItemName).GetString());
                Assert.Equal("新增", items[1].GetProperty(ItemName).GetString());
            }

            // EF10 重排父集合 ordinal 后不会完整接受嵌套集合的 identifying FK 快照；
            // 显式重新加载持久身份后继续操作，不在 Provider 中绕过主键变更校验。
            context.ChangeTracker.Clear();
            row = await context.Set<CollectionRow>().SingleAsync(row => row.Id == 1, token);
            row.Data!.Items.Clear();
            Assert.Equal(1, await context.SaveChangesAsync(token));
            using (var empty = JsonDocument.Parse((await ReadRawAsync(context, 1, token))!))
                Assert.Equal(0, empty.RootElement.GetProperty(CollectionName).GetArrayLength());
            row.Data.Items.Add(NewItem("empty→new"));
            Assert.Equal(1, await context.SaveChangesAsync(token));
        }

        await fixture.ReopenAsync(token);
        await using (var reopened = fixture.CreateCollectionContext(capture))
        {
            var row = await reopened.Set<CollectionRow>().SingleAsync(row => row.Id == 1, token);
            Assert.Equal("集合", row.Data!.Title);
            var item = Assert.Single(row.Data.Items);
            Assert.Equal("empty→new", item.Name);
            Assert.Null(item.Optional);
            Assert.Equal("lower", item.Converted);
            Assert.Equal("part '中文", Assert.Single(item.Parts).Label);
        }
        fixture.AssertTransport();
    }

    /// <summary>EF10 的嵌套 ordinal 跟踪限制继续明确失败，不能静默放过 identifying key 变化。</summary>
    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("h2c")]
    public async Task OwnsMany_NestedOrdinalRebase_RejectsFollowingMutationUntilTrackerReload(string profile)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        await using var fixture = new JsonFixture(profile, _output);
        await fixture.StartAsync(token);
        var capture = new JsonCommandCapture();
        await SeedCollectionAsync(fixture, capture, token);
        await using var context = fixture.CreateCollectionContext(capture);
        var row = await context.Set<CollectionRow>().SingleAsync(row => row.Id == 1, token);
        row.Data!.Items.RemoveAt(0);
        row.Data.Items.Add(NewItem("added"));
        Assert.Equal(1, await context.SaveChangesAsync(token));
        var committed = await ReadRawAsync(context, 1, token);
        var updates = capture.CompletedUpdates.Count;
        row.Data.Items.Clear();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync(token));
        Assert.Contains("is part of a key", failure.Message, StringComparison.Ordinal);
        Assert.Equal(updates, capture.CompletedUpdates.Count);
        Assert.Equal(committed, await ReadRawAsync(context, 1, token));
        fixture.AssertTransport();
    }

    /// <summary>集合元素修改遵循显式事务回滚及行级乐观并发条件。</summary>
    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("h2c")]
    public async Task OwnsMany_TransactionAndConcurrency_KeepRollbackAndAffectedRowSemantics(string profile)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        await using var fixture = new JsonFixture(profile, _output);
        await fixture.StartAsync(token);
        var capture = new JsonCommandCapture();
        await SeedCollectionAsync(fixture, capture, token);
        string before;
        await using (var context = fixture.CreateCollectionContext(capture))
        {
            before = (await ReadRawAsync(context, 1, token))!;
            await using var transaction = await context.Database.BeginTransactionAsync(token);
            var row = await context.Set<CollectionRow>().SingleAsync(row => row.Id == 1, token);
            row.Data!.Items[0].Name = "rolled back";
            Assert.Equal(1, await context.SaveChangesAsync(token));
            await using (var outside = fixture.CreateCollectionContext(capture))
                Assert.Equal(before, await ReadRawAsync(outside, 1, token));
            await transaction.RollbackAsync(token);
            Assert.Equal(before, await ReadRawAsync(context, 1, token));
        }

        await using (var winner = fixture.CreateCollectionContext(capture))
        await using (var stale = fixture.CreateCollectionContext(capture))
        {
            var first = await winner.Set<CollectionRow>().SingleAsync(row => row.Id == 1, token);
            var old = await stale.Set<CollectionRow>().SingleAsync(row => row.Id == 1, token);
            first.Data!.Items[0].Name = "winner";
            first.Stamp = "second";
            Assert.Equal(1, await winner.SaveChangesAsync(token));
            old.Data!.Items[0].Name = "stale";
            var failure = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync(token));
            Assert.NotEmpty(failure.Entries);
        }

        await fixture.ReopenAsync(token);
        await using (var reopened = fixture.CreateCollectionContext(capture))
        {
            var row = await reopened.Set<CollectionRow>().SingleAsync(row => row.Id == 1, token);
            Assert.Equal("winner", row.Data!.Items[0].Name);
            Assert.Equal("second", row.Stamp);
        }
        fixture.AssertTransport();
    }

    /// <summary>有效对象中不存在目标数组元素时，不以追加元素或客户端读改写掩盖失败。</summary>
    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("h2c")]
    public async Task OwnsMany_MissingElement_RejectsUpdateWithoutAppending(string profile)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        await using var fixture = new JsonFixture(profile, _output);
        await fixture.StartAsync(token);
        var capture = new JsonCommandCapture();
        await SeedCollectionAsync(fixture, capture, token);
        await using var context = fixture.CreateCollectionContext(capture);
        var row = await context.Set<CollectionRow>().SingleAsync(row => row.Id == 1, token);
        var shortened = "{\"Title\":\"集合\",\"" + CollectionName + "\":[]}";
        Assert.Equal(1, await context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"AggregateRows\" SET \"Data\"={shortened} WHERE \"Id\"=1", token));
        row.Data!.Items[1].Name = "not appended";
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(token));
        Assert.Equal(shortened, await ReadRawAsync(context, 1, token));
        fixture.AssertTransport();
    }

    /// <summary>空集合、空导航、缺失成员与可空容器按 EF 的物化合同区分。</summary>
    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("h2c")]
    public async Task OwnsMany_EmptyNullAndMissingValues_PreserveJsonAndMaterializationSemantics(string profile)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        await using var fixture = new JsonFixture(profile, _output);
        await fixture.StartAsync(token);
        var capture = new JsonCommandCapture();
        await using (var setup = fixture.CreateCollectionContext(capture))
        {
            await setup.Database.EnsureCreatedAsync(token);
            setup.AddRange(
                new CollectionRow { Id = 1, Data = new CollectionData { Items = [] } },
                new CollectionRow { Id = 2, Data = new CollectionData { Items = null! } },
                new CollectionRow { Id = 3, Data = null },
                new CollectionRow { Id = 4, Data = new CollectionData { Items = [] } });
            Assert.Equal(4, await setup.SaveChangesAsync(token));
            var missingMembers = "{}";
            Assert.Equal(1, await setup.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"AggregateRows\" SET \"Data\"={missingMembers} WHERE \"Id\"=4", token));
            using var empty = JsonDocument.Parse((await ReadRawAsync(setup, 1, token))!);
            using var absent = JsonDocument.Parse((await ReadRawAsync(setup, 2, token))!);
            Assert.Equal(0, empty.RootElement.GetProperty(CollectionName).GetArrayLength());
            Assert.Equal(JsonValueKind.Null, absent.RootElement.GetProperty(CollectionName).ValueKind);
            Assert.Null(await ReadRawAsync(setup, 3, token));
        }
        await fixture.ReopenAsync(token);
        await using (var reopened = fixture.CreateCollectionContext(capture))
        {
            var rows = await reopened.Set<CollectionRow>().OrderBy(row => row.Id).ToArrayAsync(token);
            Assert.Equal(4, rows.Length);
            Assert.Empty(rows[0].Data!.Items);
            Assert.Null(rows[1].Data!.Items);
            Assert.Null(rows[2].Data);
            Assert.Null(rows[3].Data!.Items);
        }
        fixture.AssertTransport();
    }

    /// <summary>本切片只开放跟踪更新，集合元素 LINQ 投影继续在连接打开前明确拒绝。</summary>
    [Fact]
    public void OwnsMany_ElementQuery_RejectsUnsupportedShapeBeforeOpeningConnection()
    {
        var blocker = new ConnectionBlocker();
        using var context = new CollectionContext(CreateOptions<CollectionContext>(blocker), () => { });
        AssertUnsupported(() => context.Set<CollectionRow>().Select(row => row.Data!.Items[0].Name).ToQueryString());
        Assert.Equal(0, blocker.OpenAttempts);
    }

    /// <summary>直接 ToJson 的根 OwnsMany 数组也保留元素更新及整体数组追加。</summary>
    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("h2c")]
    public async Task OwnsMany_RootArray_UpdatesElementAppendsAndReopens(string profile)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        await using var fixture = new JsonFixture(profile, _output);
        await fixture.StartAsync(token);
        var capture = new JsonCommandCapture();
        await using (var setup = fixture.CreateRootCollectionContext(capture))
        {
            await setup.Database.EnsureCreatedAsync(token);
            setup.Add(new RootCollectionRow { Id = 1, Items = [NewItem("first"), NewItem("second")] });
            Assert.Equal(1, await setup.SaveChangesAsync(token));
        }
        await using (var context = fixture.CreateRootCollectionContext(capture))
        {
            var row = await context.Set<RootCollectionRow>().SingleAsync(row => row.Id == 1, token);
            row.Items[1].Name = "second updated";
            Assert.Equal(1, await context.SaveChangesAsync(token));
            Assert.Contains("'$[1]", capture.CompletedUpdates.Last().Command.Sql, StringComparison.Ordinal);
            row.Items.Add(NewItem("third"));
            Assert.Equal(1, await context.SaveChangesAsync(token));
        }
        await fixture.ReopenAsync(token);
        await using (var reopened = fixture.CreateRootCollectionContext(capture))
        {
            var row = await reopened.Set<RootCollectionRow>().SingleAsync(row => row.Id == 1, token);
            Assert.Equal(["first", "second updated", "third"], row.Items.Select(item => item.Name));
            using var raw = JsonDocument.Parse((await ReadRawAsync(reopened, 1, token))!);
            Assert.Equal(JsonValueKind.Array, raw.RootElement.ValueKind);
            Assert.Equal(3, raw.RootElement.GetArrayLength());
        }
        fixture.AssertTransport();
    }

    private static async Task SeedCollectionAsync(JsonFixture fixture, JsonCommandCapture capture, CancellationToken cancellationToken)
    {
        await using var setup = fixture.CreateCollectionContext(capture);
        await setup.Database.EnsureCreatedAsync(cancellationToken);
        setup.Add(new CollectionRow
        {
            Id = 1,
            Stamp = "first",
            Data = new CollectionData { Title = "集合", Items = [NewItem("duplicate"), NewItem("duplicate")] },
        });
        Assert.Equal(1, await setup.SaveChangesAsync(cancellationToken));
    }

    private static CollectionItem NewItem(string name) => new()
    {
        Name = name,
        Optional = null,
        Converted = "lower",
        Token = Guid.Parse("5a11d1b1-25ad-4776-81f2-bcd293289f3a"),
        Parts = [new CollectionPart { Label = "part '中文", Count = 1 }],
    };

    private sealed partial class JsonFixture
    {
        public CollectionContext CreateCollectionContext(JsonCommandCapture capture)
        {
            _contextReferences++;
            return new CollectionContext(new DbContextOptionsBuilder<CollectionContext>()
                .UseSonnetDB(_connectionString).AddInterceptors(capture).Options, () => _contextReferences--);
        }

        public RootCollectionContext CreateRootCollectionContext(JsonCommandCapture capture)
        {
            _contextReferences++;
            return new RootCollectionContext(new DbContextOptionsBuilder<RootCollectionContext>()
                .UseSonnetDB(_connectionString).AddInterceptors(capture).Options, () => _contextReferences--);
        }
    }

    private sealed class RootCollectionContext(DbContextOptions<RootCollectionContext> options, Action release)
        : AggregateContext(options, release)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            builder.Entity<RootCollectionRow>().ToTable("AggregateRows");
            builder.Entity<RootCollectionRow>().Property(row => row.Id).ValueGeneratedNever();
            builder.Entity<RootCollectionRow>().OwnsMany(row => row.Items, items =>
            {
                items.ToJson("Data");
                items.Property(item => item.Name).HasJsonPropertyName(ItemName);
                items.Property(item => item.Converted).HasConversion(value => value.ToUpperInvariant(), value => value.ToLowerInvariant());
                items.OwnsMany(item => item.Parts);
            });
        }
    }

    private sealed class RootCollectionRow
    {
        public long Id { get; set; }
        public List<CollectionItem> Items { get; set; } = [];
    }

    private sealed class CollectionContext(DbContextOptions<CollectionContext> options, Action release)
        : AggregateContext(options, release)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            builder.Entity<CollectionRow>().ToTable("AggregateRows");
            builder.Entity<CollectionRow>().Property(row => row.Id).ValueGeneratedNever();
            builder.Entity<CollectionRow>().Property(row => row.Stamp).IsConcurrencyToken();
            builder.Entity<CollectionRow>().OwnsOne(row => row.Data, data =>
            {
                data.ToJson("Data");
                data.OwnsMany(value => value.Items, items =>
                {
                    items.HasJsonPropertyName(CollectionName);
                    items.Property(item => item.Name).HasJsonPropertyName(ItemName);
                    items.Property(item => item.Converted).HasConversion(value => value.ToUpperInvariant(), value => value.ToLowerInvariant());
                    items.OwnsMany(item => item.Parts);
                });
            });
            builder.Entity<CollectionRow>().Navigation(row => row.Data).IsRequired(false);
        }
    }

    private sealed class CollectionRow
    {
        public long Id { get; set; }
        public string Stamp { get; set; } = string.Empty;
        public CollectionData? Data { get; set; }
    }

    private sealed class CollectionData
    {
        public string Title { get; set; } = string.Empty;
        public List<CollectionItem> Items { get; set; } = [];
    }

    private sealed class CollectionItem
    {
        public string Name { get; set; } = string.Empty;
        public string? Optional { get; set; }
        public string Converted { get; set; } = string.Empty;
        public Guid Token { get; set; }
        public List<CollectionPart> Parts { get; set; } = [];
    }

    private sealed class CollectionPart
    {
        public string Label { get; set; } = string.Empty;
        public int Count { get; set; }
    }
}
