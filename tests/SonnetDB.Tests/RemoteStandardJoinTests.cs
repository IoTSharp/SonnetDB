using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Data;
using SonnetDB.Data.Remote;
using SonnetDB.Json;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>
/// GH-Issue #177：同一标准关系 JOIN 经嵌入式、真实 Kestrel REST 与 HTTP/2 Frame 执行并对账。
/// </summary>
public sealed class RemoteStandardJoinTests : IAsyncLifetime
{
    private const string AdminToken = "standard-join-admin";
    private const string ReadOnlyToken = "standard-join-reader";
    private const string DatabaseName = "standard_join";
    private static readonly string[] Modes = ["embedded", "rest", "frame-http2"];
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-standard-join-remote-" + Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(45));
    private readonly ConcurrentQueue<ObservedRequest> _requests = new();
    private WebApplication? _app;
    private string _restUrl = string.Empty;
    private string _frameUrl = string.Empty;

    /// <summary>建立彼此独立的嵌入式及远程数据库并写入相同数据。</summary>
    public async Task InitializeAsync()
    {
        try
        {
            await InitializeDatabasesAsync();
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    private async Task InitializeDatabasesAsync()
    {
        Directory.CreateDirectory(_root);
        await StartServerAsync();
        using var http = new HttpClient { BaseAddress = new Uri(_restUrl), Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        using var response = await http.PostAsync("/v1/db",
            JsonContent.Create(new CreateDatabaseRequest(DatabaseName), ServerJsonContext.Default.CreateDatabaseRequest),
            _deadline.Token);
        response.EnsureSuccessStatusCode();

        string[] statements =
        [
            "CREATE TABLE left_rows (id INT, join_key INT NULL, PRIMARY KEY (id))",
            "CREATE TABLE right_rows (id INT, join_key INT NULL, PRIMARY KEY (id))",
            "CREATE TABLE third_rows (id INT, join_key INT NULL, PRIMARY KEY (id))",
            "CREATE TABLE empty_rows (id INT, join_key INT NULL, PRIMARY KEY (id))",
            "CREATE TABLE typed_rows (id INT, name STRING, enabled BOOL, reading FLOAT, PRIMARY KEY (id))",
            "INSERT INTO left_rows (id, join_key) VALUES (1, 10), (2, 20), (3, 20), (4, NULL)",
            "INSERT INTO right_rows (id, join_key) VALUES (11, 20), (12, 20), (13, 30), (14, NULL)",
            "INSERT INTO third_rows (id, join_key) VALUES (21, 20), (22, 30), (23, 40)",
            "INSERT INTO typed_rows (id, name, enabled, reading) VALUES (2, 'two', TRUE, 2.5), (5, 'five', FALSE, 5.5)",
            "CREATE MEASUREMENT samples (source TAG, value FIELD FLOAT)",
        ];
        foreach (string mode in new[] { "embedded", "rest" })
        {
            using var connection = await OpenAsync(mode);
            foreach (string sql in statements)
            {
                _deadline.Token.ThrowIfCancellationRequested();
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync(_deadline.Token);
            }
        }
        _requests.Clear();
    }

    /// <summary>关闭本测试的宿主并删除唯一的临时数据目录。</summary>
    public async Task DisposeAsync()
    {
        try
        {
            await StopServerAsync();
        }
        finally
        {
            _deadline.Dispose();
            string root = Path.GetFullPath(_root);
            string temporaryRoot = Path.GetFullPath(Path.GetTempPath());
            if (root.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(root).StartsWith("sndb-standard-join-remote-", StringComparison.Ordinal)
                && Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>重复匹配保留全部组合，NULL 键互不匹配，外连接补齐未匹配端。</summary>
    [Theory]
    [InlineData("RIGHT")]
    [InlineData("FULL OUTER")]
    [InlineData("CROSS")]
    public async Task StandardJoin_DuplicatesAndNullKeys_MatchesEmbeddedRestAndFrame(string kind)
    {
        object?[][] expected = kind switch
        {
            "RIGHT" => [[2L, 11L], [2L, 12L], [3L, 11L], [3L, 12L], [null, 13L], [null, 14L]],
            "FULL OUTER" => [[1L, null], [2L, 11L], [2L, 12L], [3L, 11L], [3L, 12L], [4L, null], [null, 13L], [null, 14L]],
            _ => [.. Enumerable.Range(1, 4).SelectMany(left => Enumerable.Range(11, 4).Select(right => new object?[] { (long)left, (long)right }))],
        };
        string predicate = kind == "CROSS" ? string.Empty : " ON l.join_key = r.join_key";
        await AssertAcrossTransportsAsync(
            $"SELECT l.id AS left_id, r.id AS right_id FROM left_rows l {kind} JOIN right_rows r{predicate}",
            ["left_id", "right_id"], expected);
    }

    /// <summary>空左端、空右端及两端均空的结果遵守各连接类型的保留规则。</summary>
    [Theory]
    [InlineData("RIGHT")]
    [InlineData("FULL")]
    [InlineData("CROSS")]
    public async Task StandardJoin_EmptyInputs_PreservesOnlyRequiredSide(string kind)
    {
        string predicate = kind == "CROSS" ? string.Empty : " ON l.join_key = r.join_key";
        object?[][] leftEmpty = kind == "CROSS" ? [] : [[null, 11L], [null, 12L], [null, 13L], [null, 14L]];
        object?[][] rightEmpty = kind == "FULL" ? [[1L, null], [2L, null], [3L, null], [4L, null]] : [];
        await AssertAcrossTransportsAsync(
            $"SELECT l.id AS left_id, r.id AS right_id FROM empty_rows l {kind} JOIN right_rows r{predicate}",
            ["left_id", "right_id"], leftEmpty);
        await AssertAcrossTransportsAsync(
            $"SELECT l.id AS left_id, r.id AS right_id FROM left_rows l {kind} JOIN empty_rows r{predicate}",
            ["left_id", "right_id"], rightEmpty);
        await AssertAcrossTransportsAsync(
            $"SELECT l.id AS left_id, r.id AS right_id FROM empty_rows l {kind} JOIN empty_rows r{predicate}",
            ["left_id", "right_id"], []);
    }

    /// <summary>后续 FULL JOIN 仍按声明顺序识别前次 RIGHT JOIN 保留的右端记录。</summary>
    [Fact]
    public async Task StandardJoin_MultipleJoinsAndParameters_PreservesDeclaredOrderAndPagination()
    {
        await AssertAcrossTransportsAsync("""
            SELECT l.id AS left_id, r.id AS right_id, t.id AS third_id
            FROM left_rows l RIGHT JOIN right_rows r ON l.join_key = r.join_key
            FULL JOIN third_rows t ON r.join_key = t.join_key
            WHERE t.id >= @minimum
            ORDER BY t.id, r.id, l.id
            LIMIT @take OFFSET @skip
            """, ["left_id", "right_id", "third_id"], [[3L, 12L, 21L], [null, 13L, 22L], [null, null, 23L]],
            [("minimum", 21L), ("take", 3L), ("skip", 3L)], preserveOrder: true);
    }

    /// <summary>WHERE 在 NULL 扩展后应用，ON 中被排除的匹配不会丢失右端行。</summary>
    [Fact]
    public async Task StandardJoin_WhereAndOnPredicates_KeepOuterJoinSemantics()
    {
        await AssertAcrossTransportsAsync("""
            SELECT l.id AS left_id, r.id AS right_id
            FROM left_rows l FULL JOIN right_rows r ON l.join_key = r.join_key
            WHERE l.id IS NULL OR r.id IS NULL
            """, ["left_id", "right_id"], [[1L, null], [4L, null], [null, 13L], [null, 14L]]);
        await AssertAcrossTransportsAsync("""
            SELECT l.id AS left_id, r.id AS right_id
            FROM left_rows l RIGHT JOIN right_rows r ON l.join_key = r.join_key AND l.id = @id
            """, ["left_id", "right_id"], [[2L, 11L], [2L, 12L], [null, 13L], [null, 14L]], [("id", 2L)]);
    }

    /// <summary>LEFT JOIN 原有 NULL 扩展、重复键和右端 ON 过滤在新增连接类型后保持不变。</summary>
    [Fact]
    public async Task StandardJoin_LeftJoinRegression_PreservesOriginalSemantics()
    {
        await AssertAcrossTransportsAsync("""
            SELECT l.id AS left_id, r.id AS right_id FROM left_rows l
            LEFT JOIN right_rows r ON l.join_key = r.join_key AND r.id = @right_id
            ORDER BY l.id
            """, ["left_id", "right_id"], [[1L, null], [2L, 11L], [3L, 11L], [4L, null]],
            [("right_id", 11L)], preserveOrder: true);
        await AssertAcrossTransportsAsync("""
            SELECT l.id AS left_id, r.id AS right_id FROM left_rows l
            LEFT JOIN empty_rows r ON l.join_key = r.join_key ORDER BY l.id
            """, ["left_id", "right_id"], [[1L, null], [2L, null], [3L, null], [4L, null]], preserveOrder: true);
    }

    /// <summary>FULL JOIN 保留各投影的标量类型，降序 NULL 排在最后，并接受结果列别名排序。</summary>
    [Fact]
    public async Task FullJoin_TypedProjectionAndOrdering_PreservesTypesAndNullPlacement()
    {
        await AssertAcrossTransportsAsync("""
            SELECT l.id AS left_id, r.id AS right_id, r.name AS name, r.enabled AS enabled, r.reading AS reading
            FROM left_rows l FULL JOIN typed_rows r ON l.id = r.id
            ORDER BY right_id DESC, left_id ASC
            """, ["left_id", "right_id", "name", "enabled", "reading"],
            [[null, 5L, "five", false, 5.5], [2L, 2L, "two", true, 2.5],
             [1L, null, null, null, null], [3L, null, null, null, null], [4L, null, null, null, null]],
            preserveOrder: true);
        await AssertAcrossTransportsAsync("""
            SELECT l.id AS left_id, r.id AS right_id FROM left_rows l
            FULL JOIN right_rows r ON l.join_key = r.join_key ORDER BY l.id ASC, r.id DESC
            """, ["left_id", "right_id"],
            [[null, 14L], [null, 13L], [1L, null], [2L, 12L], [2L, 11L], [3L, 12L], [3L, 11L], [4L, null]],
            preserveOrder: true);
    }

    /// <summary>限定列名避免来源冲突；重复显式结果别名仍保留两个按序号访问的列。</summary>
    [Fact]
    public async Task FullJoin_QualifiedColumnsAndDuplicateAliases_PreserveDistinctOrdinals()
    {
        object?[][] expected = [[1L, null], [2L, 11L], [2L, 12L], [3L, 11L], [3L, 12L], [4L, null], [null, 13L], [null, 14L]];
        await AssertAcrossTransportsAsync(
            "SELECT l.id, r.id FROM left_rows l FULL JOIN right_rows r ON l.join_key = r.join_key",
            ["l.id", "r.id"], expected);
        await AssertAcrossTransportsAsync(
            "SELECT l.id AS same, r.id AS same FROM left_rows l FULL JOIN right_rows r ON l.join_key = r.join_key",
            ["same", "same"], expected);
    }

    /// <summary>有冲突的来源列必须限定；未知排序列明确报错。</summary>
    [Theory]
    [InlineData("SELECT id FROM left_rows l FULL JOIN right_rows r ON l.join_key = r.join_key", "存在歧义")]
    [InlineData("SELECT l.id FROM left_rows l FULL JOIN right_rows r ON l.join_key = r.join_key ORDER BY absent_column", "不存在")]
    public async Task FullJoin_AmbiguousColumnOrUnknownSortColumn_RejectsInvalidReference(string sql, string diagnostic)
    {
        foreach (string mode in Modes)
        {
            using var connection = await OpenAsync(mode);
            _requests.Clear();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            var error = await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                using var reader = await command.ExecuteReaderAsync(_deadline.Token);
                await reader.ReadAsync(_deadline.Token);
            });
            Assert.Contains(diagnostic, error.Message, StringComparison.Ordinal);
            AssertTransport(mode);
        }
    }

    /// <summary>三种入口的 SDK 能力元数据均向 ORM 公布已支持的四类连接标志。</summary>
    [Fact]
    public async Task StandardJoin_DataSourceInformation_AdvertisesAllSupportedOuterJoins()
    {
        int expected = (int)(SupportedJoinOperators.Inner | SupportedJoinOperators.LeftOuter
            | SupportedJoinOperators.RightOuter | SupportedJoinOperators.FullOuter);
        foreach (string mode in Modes)
        {
            using var connection = await OpenAsync(mode);
            DataTable metadata = connection.GetSchema(DbMetaDataCollectionNames.DataSourceInformation);
            Assert.Equal(expected, Assert.Single(metadata.Rows.Cast<DataRow>())[DbMetaDataColumnNames.SupportedJoinOperators]);
        }
    }

    /// <summary>measurement 混合 JOIN 在三种入口均明确拒绝，远程 Frame 保留错误诊断。</summary>
    [Theory]
    [InlineData("LEFT")]
    [InlineData("RIGHT")]
    [InlineData("FULL")]
    [InlineData("CROSS")]
    public async Task StandardJoin_MeasurementSource_RejectsUnsupportedContract(string kind)
    {
        string predicate = kind == "CROSS" ? string.Empty : " ON s.source = r.id";
        string sql = $"SELECT r.id FROM samples s {kind} JOIN right_rows r{predicate}";
        foreach (string mode in Modes)
        {
            using var connection = await OpenAsync(mode);
            foreach (string prefix in new[] { string.Empty, "EXPLAIN " })
            {
                _deadline.Token.ThrowIfCancellationRequested();
                _requests.Clear();
                using var command = connection.CreateCommand();
                command.CommandText = prefix + sql;
                var error = await Assert.ThrowsAnyAsync<Exception>(async () =>
                {
                    using var reader = await command.ExecuteReaderAsync(_deadline.Token);
                    await reader.ReadAsync(_deadline.Token);
                });
                Assert.Contains("当前仅支持关系表 FROM", error.Message, StringComparison.Ordinal);
                AssertTransport(mode);
            }
        }
    }

    /// <summary>只读凭据可查询标准 JOIN，写请求仍被服务器拒绝。</summary>
    [Theory]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task StandardJoin_ReadOnlyRole_CanReadWithoutWritePrivilege(string mode)
    {
        using var connection = await OpenAsync(mode, ReadOnlyToken);
        var result = await QueryAsync(connection, mode,
            "SELECT COUNT(*) AS joined_count FROM left_rows l CROSS JOIN right_rows r");
        Assert.Equal(16L, Assert.Single(result.Rows)[0]);
        using var write = connection.CreateCommand();
        write.CommandText = "DELETE FROM left_rows WHERE id = 1";
        var error = await Assert.ThrowsAsync<SndbServerException>(() => write.ExecuteNonQueryAsync(_deadline.Token));
        Assert.Equal("forbidden", error.Error);
        var unchanged = await QueryAsync(connection, mode,
            "SELECT COUNT(*) AS joined_count FROM left_rows l CROSS JOIN right_rows r");
        Assert.Equal(16L, Assert.Single(unchanged.Rows)[0]);
    }

    /// <summary>预取消 JOIN 不发送查询；相同连接随后仍能完整执行。</summary>
    [Theory]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task StandardJoin_CanceledRequest_DoesNotSendAndConnectionRemainsUsable(string mode)
    {
        using var connection = await OpenAsync(mode);
        _requests.Clear();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT l.id FROM left_rows l CROSS JOIN right_rows r";
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            using var reader = await command.ExecuteReaderAsync(canceled.Token);
        });
        Assert.Empty(_requests);
        var result = await QueryAsync(connection, mode,
            "SELECT COUNT(*) AS joined_count FROM left_rows l CROSS JOIN right_rows r");
        Assert.Equal(16L, Assert.Single(result.Rows)[0]);
    }

    /// <summary>服务器完整关闭重启及嵌入式重新打开后，持久化数据仍产生相同 JOIN。</summary>
    [Fact]
    public async Task StandardJoin_ServerRestart_RestoresSamePersistedResults()
    {
        const string sql = "SELECT l.id AS left_id, r.id AS right_id FROM left_rows l FULL JOIN right_rows r ON l.join_key = r.join_key";
        object?[][] expected = [[1L, null], [2L, 11L], [2L, 12L], [3L, 11L], [3L, 12L], [4L, null], [null, 13L], [null, 14L]];
        await AssertAcrossTransportsAsync(sql, ["left_id", "right_id"], expected);
        await StopServerAsync();
        await StartServerAsync();
        await AssertAcrossTransportsAsync(sql, ["left_id", "right_id"], expected);
    }

    private async Task AssertAcrossTransportsAsync(
        string sql, string[] columns, object?[][] expected,
        (string Name, object Value)[]? parameters = null, bool preserveOrder = false)
    {
        foreach (string mode in Modes)
        {
            _deadline.Token.ThrowIfCancellationRequested();
            using var connection = await OpenAsync(mode);
            QueryResult result = await QueryAsync(connection, mode, sql, parameters);
            Assert.Equal(columns, result.Columns);
            Assert.Equal(CanonicalRows(expected, preserveOrder), CanonicalRows(result.Rows, preserveOrder));
        }
    }

    private async Task<QueryResult> QueryAsync(
        SndbConnection connection, string mode, string sql, (string Name, object Value)[]? parameters = null)
    {
        _requests.Clear();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 10;
        if (parameters is not null)
        {
            Assert.InRange(parameters.Length, 0, 3);
            foreach (var parameter in parameters)
                command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }
        using var reader = await command.ExecuteReaderAsync(_deadline.Token);
        string[] columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        Assert.InRange(columns.Length, 1, 5);
        for (int columnIndex = 0; columnIndex < columns.Length; columnIndex++)
            Assert.Equal(Array.FindIndex(columns, name => string.Equals(name, columns[columnIndex], StringComparison.OrdinalIgnoreCase)),
                reader.GetOrdinal(columns[columnIndex]));
        var rows = new List<object?[]>();
        for (int rowIndex = 0; rowIndex < 64; rowIndex++)
        {
            if (!await reader.ReadAsync(_deadline.Token))
            {
                AssertTransport(mode);
                return new QueryResult(columns, rows.ToArray());
            }
            object?[] row = new object?[reader.FieldCount];
            for (int columnIndex = 0; columnIndex < row.Length; columnIndex++)
            {
                row[columnIndex] = reader.IsDBNull(columnIndex) ? null : reader.GetValue(columnIndex);
                if (row[columnIndex] is { } value)
                    Assert.Equal(value.GetType(), reader.GetFieldType(columnIndex));
            }
            rows.Add(row);
        }
        throw new InvalidOperationException("标准 JOIN fixture 超过 63 行的固定验收预算。");
    }

    private static string[] CanonicalRows(object?[][] rows, bool preserveOrder)
    {
        string[] values = rows.Select(row => string.Join('|', row.Select(value =>
            value is null ? "NULL" : value.GetType().Name + ":" + Convert.ToString(value, CultureInfo.InvariantCulture)))).ToArray();
        if (!preserveOrder)
            Array.Sort(values, StringComparer.Ordinal);
        return values;
    }

    private void AssertTransport(string mode)
    {
        ObservedRequest[] requests = _requests.ToArray();
        if (mode == "embedded")
        {
            Assert.Empty(requests);
            return;
        }
        ObservedRequest request = Assert.Single(requests);
        Assert.Equal(mode == "frame-http2" ? "/v1/frame" : $"/v1/db/{DatabaseName}/sql", request.Path);
        Assert.Equal(mode == "frame-http2" ? "HTTP/2" : "HTTP/1.1", request.Protocol);
    }

    private async Task<SndbConnection> OpenAsync(string mode, string token = AdminToken)
    {
        string source = mode == "embedded" ? Path.Combine(_root, "embedded")
            : $"sonnetdb+http://{new Uri(mode == "frame-http2" ? _frameUrl : _restUrl).Authority}/{DatabaseName}";
        var connection = new SndbConnection(mode == "embedded" ? $"Data Source={source}"
            : $"Data Source={source};Token={token};Protocol={mode};Timeout=10");
        try
        {
            await connection.OpenAsync(_deadline.Token);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private async Task StartServerAsync()
    {
        _restUrl = string.Empty;
        _frameUrl = string.Empty;
        _app = TestServerHost.Build(new ServerOptions
        {
            DataRoot = Path.Combine(_root, "server"),
            AutoLoadExistingDatabases = true,
            AllowAnonymousProbes = true,
            Tokens = new Dictionary<string, string> { [AdminToken] = ServerRoles.Admin, [ReadOnlyToken] = ServerRoles.ReadOnly },
        }, extraArgs:
        [
            "--Kestrel:Endpoints:FrameH2:Url=http://127.0.0.1:0",
            "--Kestrel:Endpoints:FrameH2:Protocols=Http2",
        ]);
        _app.Use(async (context, next) =>
        {
            _requests.Enqueue(new ObservedRequest(context.Request.Path.Value ?? string.Empty, context.Request.Protocol));
            await next(context);
        });
        await _app.StartAsync(_deadline.Token);
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel 未暴露监听地址。");
        Assert.Equal(2, addresses.Addresses.Count);
        foreach (string address in addresses.Addresses)
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var request = new HttpRequestMessage(HttpMethod.Get, address + "/healthz")
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            };
            try
            {
                using var response = await client.SendAsync(request, _deadline.Token);
                if (response.IsSuccessStatusCode)
                    _restUrl = address;
                else
                    _frameUrl = address;
            }
            catch (HttpRequestException)
            {
                _frameUrl = address;
            }
        }
        Assert.NotEmpty(_restUrl);
        Assert.NotEmpty(_frameUrl);
    }

    private async Task StopServerAsync()
    {
        WebApplication? app = _app;
        _app = null;
        if (app is null)
            return;
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await app.StopAsync(shutdown.Token);
        }
        finally
        {
            using var disposal = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await app.DisposeAsync().AsTask().WaitAsync(disposal.Token);
        }
    }

    private sealed record ObservedRequest(string Path, string Protocol);
    private sealed record QueryResult(string[] Columns, object?[][] Rows);
}
