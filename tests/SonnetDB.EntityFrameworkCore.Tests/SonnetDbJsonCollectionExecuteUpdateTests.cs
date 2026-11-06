using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace SonnetDB.EntityFrameworkCore.Tests;

public sealed partial class SonnetDbJsonAggregateModelTests
{
    /// <summary>固定集合下标的标量批量更新保持参数、原行右值、事务与真实重开合同。</summary>
    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("h2c")]
    public async Task ExecuteUpdate_ConstantCollectionElement_PreservesAtomicityAndReopens(string profile)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        await using var fixture = new JsonFixture(profile, _output);
        await fixture.StartAsync(token);
        var capture = new JsonCommandCapture();
        await SeedCollectionAsync(fixture, capture, token);
        var display = "参数 O'Reilly \"\\ 中文😃";
        await using (var context = fixture.CreateCollectionContext(capture))
        {
            var initial = (await ReadRawAsync(context, 1, token))!;
            var withUnknown = initial[..^1] + ",\"Unknown\":{\"large\":9007199254740993,\"lexical\":1.2300e+04}}";
            Assert.Equal(1, await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"AggregateRows\" SET \"Data\"={withUnknown} WHERE \"Id\"=1", token));

            var commandCount = capture.Commands.Count;
            Assert.Equal(1, await context.Set<CollectionRow>().Where(row => row.Id == 1 && row.Stamp == "first")
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(row => row.Data!.Title, "changed title")
                    .SetProperty(row => row.Data!.Items[0].Name, row => row.Data!.Title)
                    .SetProperty(row => row.Data!.Items.ElementAt(1).Name, display)
                    .SetProperty(row => row.Data!.Items[0].Parts[0].Count, 17)
                    .SetProperty(row => row.Data!.Items[0].NumericKey, "numeric property")
                    .SetProperty(row => row.Data!.Items[1].Optional, "created")
                    .SetProperty(row => row.Stamp, "winner"), token));
            var update = Assert.Single(capture.Commands.Skip(commandCount));
            Assert.StartsWith("UPDATE", update.Sql, StringComparison.Ordinal);
            Assert.DoesNotContain(display, update.Sql);
            Assert.Contains(update.Parameters, parameter => Equals(display, parameter.Value));
            Assert.Contains(update.Parameters, parameter => parameter.Value is string path && path.Contains("[0]", StringComparison.Ordinal));
            using var raw = JsonDocument.Parse((await ReadRawAsync(context, 1, token))!);
            var items = raw.RootElement.GetProperty(CollectionName);
            Assert.Equal("集合", items[0].GetProperty(ItemName).GetString());
            Assert.Equal(display, items[1].GetProperty(ItemName).GetString());
            Assert.Equal(17, items[0].GetProperty("Parts")[0].GetProperty("Count").GetInt32());
            Assert.Equal("numeric property", items[0].GetProperty("0").GetString());
            Assert.Equal("created", items[1].GetProperty("Optional").GetString());
            Assert.Equal("LOWER", items[0].GetProperty("Converted").GetString());
            Assert.Equal("{\"large\":9007199254740993,\"lexical\":1.2300e+04}", raw.RootElement.GetProperty("Unknown").GetRawText());

            Assert.Equal(0, context.Set<CollectionRow>().Where(row => row.Id == 1 && row.Stamp == "first")
                .ExecuteUpdate(setters => setters.SetProperty(row => row.Data!.Items[0].Name, "stale")));
            Assert.Equal(1, context.Set<CollectionRow>().Where(row => row.Id == 1)
                .ExecuteUpdate(setters => setters
                    .SetProperty(row => row.Data!.Items[1].Optional, "overwritten")
                    .SetProperty(row => row.Data!.Items[1].Optional, (string?)null)));

            await using var transaction = await context.Database.BeginTransactionAsync(token);
            Assert.Equal(1, await context.Set<CollectionRow>().Where(row => row.Id == 1)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Data!.Items[0].Name, "rolled back"), token));
            await using (var outside = fixture.CreateCollectionContext(capture))
            {
                using var visible = JsonDocument.Parse((await ReadRawAsync(outside, 1, token))!);
                Assert.Equal("集合", visible.RootElement.GetProperty(CollectionName)[0].GetProperty(ItemName).GetString());
            }
            await transaction.RollbackAsync(token);
        }

        await fixture.ReopenAsync(token);
        await using (var reopened = fixture.CreateCollectionContext(capture))
        {
            var row = await reopened.Set<CollectionRow>().AsNoTracking().SingleAsync(token);
            Assert.Equal("winner", row.Stamp);
            Assert.Equal("changed title", row.Data!.Title);
            Assert.Equal("集合", row.Data.Items[0].Name);
            Assert.Equal(display, row.Data.Items[1].Name);
            Assert.Equal(17, row.Data.Items[0].Parts[0].Count);
            Assert.Equal("numeric property", row.Data.Items[0].NumericKey);
            Assert.Null(row.Data.Items[1].Optional);
        }
        fixture.AssertTransport();
    }

    /// <summary>根 OwnsMany 数组允许固定元素更新，保留其它元素及完整数组。</summary>
    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("h2c")]
    public async Task ExecuteUpdate_RootCollectionElement_PreservesOtherElements(string profile)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        await using var fixture = new JsonFixture(profile, _output);
        await fixture.StartAsync(token);
        var capture = new JsonCommandCapture();
        await using (var context = fixture.CreateRootCollectionContext(capture))
        {
            await context.Database.EnsureCreatedAsync(token);
            context.Add(new RootCollectionRow { Id = 1, Items = [NewItem("first"), NewItem("second")] });
            Assert.Equal(1, await context.SaveChangesAsync(token));
            var commandCount = capture.Commands.Count;
            Assert.Equal(1, await context.Set<RootCollectionRow>().Where(row => row.Id == 1)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(row => row.Items[1].Name, "changed")
                    .SetProperty(row => row.Items[1].Parts[0].Count, 7), token));
            var command = Assert.Single(capture.Commands.Skip(commandCount));
            Assert.Contains(command.Parameters, parameter => parameter.Value is string path && path.StartsWith("$[1]", StringComparison.Ordinal));
        }
        await fixture.ReopenAsync(token);
        await using (var reopened = fixture.CreateRootCollectionContext(capture))
        {
            var row = await reopened.Set<RootCollectionRow>().AsNoTracking().SingleAsync(token);
            Assert.Equal(["first", "changed"], row.Items.Select(item => item.Name));
            Assert.Equal(1, row.Items[0].Parts[0].Count);
            Assert.Equal(7, row.Items[1].Parts[0].Count);
        }
        fixture.AssertTransport();
    }

    /// <summary>生成条件目标而非直接 JSON 标量路径的 ElementAtOrDefault 仍在写前拒绝。</summary>
    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("h2c")]
    public async Task ExecuteUpdate_ConditionalCollectionTarget_RejectsBeforeSendingSql(string profile)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var fixture = new JsonFixture(profile, _output);
        await fixture.StartAsync(timeout.Token);
        var capture = new JsonCommandCapture();
        await using var context = fixture.CreateRootCollectionContext(capture);
        var exception = await Assert.ThrowsAsync<NotSupportedException>(() => context.Set<RootCollectionRow>()
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Items[1].Name, "changed")
                .SetProperty(row => row.Items.ElementAtOrDefault(1)!.Parts.ElementAtOrDefault(0)!.Count, 7), timeout.Token));
        Assert.Contains("This JSON operation is not supported", exception.Message);
        Assert.Empty(capture.Commands);
    }

    /// <summary>固定 primitive 元素目标与元素右值保持原行、参数和真实协议合同。</summary>
    [Theory]
    [InlineData("embedded", true)]
    [InlineData("embedded", false)]
    [InlineData("rest", true)]
    [InlineData("rest", false)]
    [InlineData("h2c", true)]
    [InlineData("h2c", false)]
    public async Task ExecuteUpdate_PrimitiveCollectionElement_UpdatesFromOriginalRow(string profile, bool owned)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        await using var fixture = new JsonFixture(profile, _output);
        await fixture.StartAsync(token);
        var capture = new JsonCommandCapture();
        await using var context = fixture.CreateContext(owned, capture);
        await context.Database.EnsureCreatedAsync(token);
        var initial = InitialAggregate();
        context.Add(new AggregateRow { Id = 1, Stamp = "initial", Data = initial });
        Assert.Equal(1, await context.SaveChangesAsync(token));
        var before = capture.Commands.Count;
        Assert.Equal(1, await context.Set<AggregateRow>().ExecuteUpdateAsync(setters => setters
            .SetProperty(row => row.Data.Tags[0], "changed")
            .SetProperty(row => row.Data.Optional, row => row.Data.Tags[0]), token));
        var command = Assert.Single(capture.Commands.Skip(before));
        Assert.Contains(command.Parameters, parameter => Equals(parameter.Value, "changed"));
        using var raw = JsonDocument.Parse((await ReadRawAsync(context, 1, token))!);
        Assert.Equal("changed", raw.RootElement.GetProperty("Tags")[0].GetString());
        Assert.Equal(initial.Tags[0], raw.RootElement.GetProperty("Optional").GetString());
        var after = capture.Commands.Count;
        await Assert.ThrowsAsync<NotSupportedException>(() => context.Set<AggregateRow>().ExecuteUpdateAsync(setters => setters
            .SetProperty(row => row.Data.Optional, row => row.Data.Tags[-1]), token));
        Assert.Equal(after, capture.Commands.Count);
        fixture.AssertTransport();
    }

    /// <summary>第二行数组缺失或父级类型错误时，第一行及普通列也必须整条回滚。</summary>
    [Theory]
    [InlineData("embedded", "[]")]
    [InlineData("embedded", "3")]
    [InlineData("rest", "[]")]
    [InlineData("rest", "3")]
    [InlineData("h2c", "[]")]
    [InlineData("h2c", "3")]
    public async Task ExecuteUpdate_MissingCollectionElement_RejectsWholeStatement(string profile, string invalidItems)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        await using var fixture = new JsonFixture(profile, _output);
        await fixture.StartAsync(token);
        var capture = new JsonCommandCapture();
        await SeedCollectionAsync(fixture, capture, token);
        await using var context = fixture.CreateCollectionContext(capture);
        context.Add(new CollectionRow { Id = 2, Stamp = "first", Data = new CollectionData { Items = [] } });
        Assert.Equal(1, await context.SaveChangesAsync(token));
        var invalidJson = "{\"Title\":\"invalid\",\"" + CollectionName + "\":" + invalidItems + "}";
        Assert.Equal(1, await context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"AggregateRows\" SET \"Data\"={invalidJson} WHERE \"Id\"=2", token));
        var before = await ReadRawAsync(context, 1, token);
        await Assert.ThrowsAnyAsync<Exception>(() => context.Set<CollectionRow>().Where(row => row.Id <= 2)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Data!.Items[0].Name, "must-not-write")
                .SetProperty(row => row.Stamp, "must-not-write"), token));
        Assert.Equal(before, await ReadRawAsync(context, 1, token));
        Assert.Equal(invalidJson, await ReadRawAsync(context, 2, token));
        Assert.Equal(["first", "first"], await context.Set<CollectionRow>().OrderBy(row => row.Id).Select(row => row.Stamp).ToArrayAsync(token));
        fixture.AssertTransport();
    }

    /// <summary>变量、负数、转换器、Guid 与集合右值在发送命令前保留明确拒绝。</summary>
    [Theory]
    [InlineData("captured")]
    [InlineData("row-index")]
    [InlineData("negative")]
    [InlineData("converter-rhs")]
    [InlineData("guid-rhs")]
    [InlineData("indexed-value-dynamic")]
    [InlineData("element-at-dynamic")]
    public async Task ExecuteUpdate_UnsupportedCollectionShape_RejectsBeforeSendingSql(string shape)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var fixture = new JsonFixture("embedded", _output);
        await fixture.StartAsync(timeout.Token);
        var capture = new JsonCommandCapture();
        await using var context = fixture.CreateCollectionContext(capture);
        var index = 0;
        var rows = context.Set<CollectionRow>();
        var exception = Assert.Throws<NotSupportedException>(() =>
        {
            _ = shape switch
            {
                "captured" => rows.ExecuteUpdate(setters => setters.SetProperty(row => row.Data!.Items[index].Name, "changed")),
                "row-index" => rows.ExecuteUpdate(setters => setters.SetProperty(row => row.Data!.Items[(int)row.Id].Name, "changed")),
                "negative" => rows.ExecuteUpdate(setters => setters.SetProperty(row => row.Data!.Items[-1].Name, "changed")),
                "converter-rhs" => rows.ExecuteUpdate(setters => setters.SetProperty(row => row.Data!.Items[0].Converted, row => row.Stamp)),
                "guid-rhs" => rows.ExecuteUpdate(setters => setters.SetProperty(row => row.Data!.Items[0].Token, row => row.Data!.Items[0].Token)),
                "indexed-value-dynamic" => rows.ExecuteUpdate(setters => setters.SetProperty(row => row.Data!.Title, row => row.Data!.Items[index].Name)),
                "element-at-dynamic" => rows.ExecuteUpdate(setters => setters.SetProperty(row => row.Data!.Items.ElementAt(index).Name, "changed")),
                _ => throw new InvalidOperationException("Unknown test shape."),
            };
        });
        Assert.Contains("This JSON operation is not supported", exception.Message);
        Assert.Empty(capture.Commands);
    }
}
