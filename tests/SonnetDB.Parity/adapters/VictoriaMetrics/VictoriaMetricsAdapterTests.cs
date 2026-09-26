using System.Globalization;
using System.Net;
using Xunit;

namespace SonnetDB.Parity.Adapters.VictoriaMetrics;

/// <summary>验证 remote_write 确认之后的完整可见性与严格数量合同。</summary>
public sealed class VictoriaMetricsAdapterTests
{
    /// <summary>首批部分可见时继续等待，完整数量可见后才返回。</summary>
    [Fact]
    public async Task IngestAsync_PartialBatchVisible_WaitsForExactPointCount()
    {
        using var handler = new VisibilityHandler((_, attempt) => attempt == 1 ? 1 : 3);
        await using var adapter = CreateAdapter(handler);

        await adapter.IngestAsync(Points("partial", 3), CancellationToken.None);

        Assert.Equal(2, handler.QueryCount);
        Assert.All(handler.Queries, query => Assert.Contains("sum(count_over_time(partial[3s]))", query, StringComparison.Ordinal));
    }

    /// <summary>一个 measurement 已完整不能使另一个部分可见的 measurement 提前通过。</summary>
    [Fact]
    public async Task IngestAsync_MultipleMeasurements_WaitsForEveryMeasurement()
    {
        using var handler = new VisibilityHandler((query, attempt) => query.Contains("second", StringComparison.Ordinal) && attempt == 2 ? 1 : 2);
        await using var adapter = CreateAdapter(handler);

        await adapter.IngestAsync([.. Points("first", 2), .. Points("second", 2)], CancellationToken.None);

        Assert.Equal(4, handler.QueryCount);
    }

    /// <summary>始终缺点时有界失败，并保留期望和最后一次实测数量。</summary>
    [Fact]
    public async Task IngestAsync_IncompleteBatch_TimesOutWithObservedCount()
    {
        using var handler = new VisibilityHandler((_, _) => 2);
        await using var adapter = CreateAdapter(handler, TimeSpan.FromMilliseconds(50));

        var error = await Assert.ThrowsAsync<TimeoutException>(() => adapter.IngestAsync(Points("missing", 3), CancellationToken.None));

        Assert.Contains("'missing': expected 3, observed 2", error.Message, StringComparison.Ordinal);
        Assert.True(handler.QueryCount > 0);
    }

    /// <summary>多出的点不能用大于等于条件放行。</summary>
    [Fact]
    public async Task IngestAsync_ExtraPoint_FailsWithoutAcceptingCount()
    {
        using var handler = new VisibilityHandler((_, _) => 4);
        await using var adapter = CreateAdapter(handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.IngestAsync(Points("extra", 3), CancellationToken.None));

        Assert.Contains("4 visible points; expected exactly 3", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, handler.QueryCount);
    }

    /// <summary>调用方取消继续按取消传播，不转成可见性超时。</summary>
    [Fact]
    public async Task IngestAsync_CallerCancels_PropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new VisibilityHandler((_, _) => { cancellation.Cancel(); return 1; });
        await using var adapter = CreateAdapter(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adapter.IngestAsync(Points("cancelled", 3), cancellation.Token));
    }

    private static VictoriaMetricsAdapter CreateAdapter(HttpMessageHandler handler, TimeSpan? timeout = null)
        => new(new HttpClient(handler) { BaseAddress = new Uri("http://victoriametrics.invalid") }, timeout ?? TimeSpan.FromSeconds(15));

    private static TsdbPoint[] Points(string measurement, int count)
        => Enumerable.Range(0, count).Select(index => new TsdbPoint(measurement, 1_000L + index * 1_000L, "device", "region", index)).ToArray();

    private sealed class VisibilityHandler(Func<string, int, int> observe) : HttpMessageHandler
    {
        public int QueryCount { get; private set; }
        public List<string> Queries { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Method == HttpMethod.Post)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));

            string query = Uri.UnescapeDataString(request.RequestUri!.Query);
            Queries.Add(query);
            int count = observe(query, ++QueryCount);
            string body = "{\"status\":\"success\",\"data\":{\"resultType\":\"vector\",\"result\":[{\"metric\":{},\"value\":[1,\""
                          + count.ToString(CultureInfo.InvariantCulture) + "\"]}]}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
