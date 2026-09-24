using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Configuration;
using SonnetDB.Data;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>
/// GH-Issue #180：JSON 标量函数在嵌入式、真实 REST 和 HTTP/2 Frame 上遵守同一合同。
/// </summary>
public sealed class RemoteJsonFunctionTests : IAsyncLifetime
{
    private const string AdminToken = "json-parity-admin";
    private const string DatabaseName = "json_parity";
    private readonly ConcurrentQueue<ObservedRequest> _requests = new();
    private readonly CancellationTokenSource _deadline = new(TimeSpan.FromMinutes(2));
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-json-parity-" + Guid.NewGuid().ToString("N"));
    private WebApplication? _app;
    private string _http11Url = string.Empty;
    private string _frameH2Url = string.Empty;

    /// <summary>启动具有独立 HTTP/1.1 和 HTTP/2 监听端点的真实测试服务。</summary>
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _app = TestServerHost.Build(new ServerOptions
        {
            DataRoot = Path.Combine(_root, "server"),
            AutoLoadExistingDatabases = true,
            AllowAnonymousProbes = true,
            Tokens = new Dictionary<string, string> { [AdminToken] = ServerRoles.Admin },
        }, extraArgs:
        [
            "--Kestrel:Endpoints:FrameH2:Url=http://127.0.0.1:0",
            "--Kestrel:Endpoints:FrameH2:Protocols=Http2",
        ]);
        _app.Use(async (context, next) =>
        {
            _requests.Enqueue(new ObservedRequest(
                context.Connection.LocalPort,
                context.Request.Path.Value ?? string.Empty,
                context.Request.Protocol));
            await next(context);
        });
        await _app.StartAsync(_deadline.Token);
        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel 未暴露监听地址。");
        Assert.Equal(2, addresses.Addresses.Count);
        foreach (string address in addresses.Addresses)
        {
            _deadline.Token.ThrowIfCancellationRequested();
            if (await ProbeIsHttp11Async(address))
                _http11Url = address;
            else
                _frameH2Url = address;
        }
        Assert.NotEmpty(_http11Url);
        Assert.NotEmpty(_frameH2Url);

        using var http = new HttpClient { BaseAddress = new Uri(_http11Url), Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        using var response = await http.PostAsync("/v1/db", new StringContent(
            $"{{\"name\":\"{DatabaseName}\"}}", Encoding.UTF8, "application/json"), _deadline.Token);
        response.EnsureSuccessStatusCode();
        _requests.Clear();
    }

    /// <summary>停止本测试服务并清理仅由本测试创建的数据目录。</summary>
    public async Task DisposeAsync()
    {
        try
        {
            if (_app is not null)
            {
                using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try { await _app.StopAsync(shutdown.Token); }
                finally { await _app.DisposeAsync(); }
            }
        }
        finally
        {
            _deadline.Dispose();
            string fullRoot = Path.GetFullPath(_root);
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetDirectoryName(fullRoot));
            Assert.StartsWith("sndb-json-parity-", Path.GetFileName(fullRoot), StringComparison.Ordinal);
            if (Directory.Exists(fullRoot))
                Directory.Delete(fullRoot, recursive: true);
        }
    }

    /// <summary>关系 JSON 列的投影、谓词及 path/candidate 参数在三个入口结果一致。</summary>
    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task Execute_RelationJsonProjectionAndFilter_MatchesAcrossTransports(string protocol)
    {
        using var connection = OpenConnection(protocol);
        await ExecuteAsync(connection, "CREATE TABLE devices (id INT, metadata JSON, PRIMARY KEY (id))");
        await ExecuteAsync(connection, """
            INSERT INTO devices (id, metadata) VALUES
                (1, '{"tags":["industrial","pump"],"owner":{"name":"alice","active":true},"deleted":null}'),
                (2, '{"tags":[],"owner":{"name":"bob"}}'),
                (3, '{"owner":null}'),
                (4, NULL)
            """);

        await AssertQueryAsync(connection, protocol, """
            SELECT id,
                   json_array_length(metadata, @tags_path) AS tag_count,
                   json_exists(metadata, @first_path) AS has_first,
                   json_exists(metadata, @deleted_path) AS has_deleted,
                   json_contains(metadata, @tags_path, @member) AS has_member,
                   json_contains(metadata, '$.tags', @subset) AS has_subset,
                   json_contains(metadata, '$.owner', 'name') AS has_owner_field,
                   json_contains(metadata, '$.owner', '{"active":true}') AS has_active_owner
            FROM devices ORDER BY id
            """,
            [
                [1L, 2L, true, true, true, true, true, true],
                [2L, 0L, false, false, false, false, true, false],
                [3L, null, false, false, false, false, false, false],
                [4L, null, null, null, null, null, null, null],
            ],
            ("tags_path", "$.tags"), ("first_path", "$.tags[0]"),
            ("deleted_path", "$.deleted"), ("member", "industrial"), ("subset", "[\"pump\",\"industrial\"]"));

        await AssertQueryAsync(connection, protocol, """
            SELECT id FROM devices
            WHERE json_exists(metadata, @path) = true
              AND json_array_length(metadata, @path) > 0
              AND json_contains(metadata, @path, @member) = true
            ORDER BY id
            """, [[1L]], ("path", "$.tags"), ("member", "pump"));
    }

    /// <summary>文档集合保留嵌套数组、对象子集、标量类型和特殊字符串参数的语义。</summary>
    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task Execute_DocumentNestedValuesAndQuotedParameters_MatchesAcrossTransports(string protocol)
    {
        using var connection = OpenConnection(protocol);
        await ExecuteAsync(connection, "CREATE DOCUMENT COLLECTION device_docs");
        await ExecuteAsync(connection, "INSERT INTO device_docs (id, document) VALUES ('a', @json)",
            ("json", """{"tags":["泵-α","O'Reilly; --",[true,2.0]],"profile":{"roles":["operator","reader"],"active":true},"display-name":["\u0061"],"empty":[]}"""));
        await ExecuteAsync(connection, "INSERT INTO device_docs (id, document) VALUES ('b', '{\"tags\":[],\"profile\":null}')");

        await AssertQueryAsync(connection, protocol, """
            SELECT id,
                   json_array_length(document, '$.tags') AS tag_count,
                   json_exists(document, '$.tags[2][1]') AS has_nested,
                   json_contains(document, @path, @candidate) AS has_quoted_string,
                   json_contains(document, '$.tags', @number) AS has_number,
                   json_contains(document, '$.tags', @boolean) AS has_boolean,
                   json_contains(document, '$.profile', @subset) AS has_profile,
                   json_contains(document, @quoted_path, '["a"]') AS has_unescaped_string,
                   json_contains(document, '$.empty', '[]') AS has_empty_subset
            FROM device_docs ORDER BY id
            """,
            [
                ["a", 3L, true, true, true, true, true, true, true],
                ["b", 0L, false, false, false, false, false, false, false],
            ],
            ("path", "$.tags"), ("candidate", "O'Reilly; --"), ("number", 2L), ("boolean", true),
            ("subset", """{"roles":["reader"],"active":true}"""), ("quoted_path", "$['display-name']"));

        await AssertQueryAsync(connection, protocol, """
            SELECT id FROM device_docs
            WHERE json_contains(document, @path, @candidate) = true ORDER BY id
            """, [["a"]], ("path", "$.tags"), ("candidate", "泵-α"));
    }

    /// <summary>SQL NULL、JSON null、缺失路径、空数组和候选重复项保持不同语义。</summary>
    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task Execute_NullMissingEmptyAndDuplicateCandidates_PreservesContract(string protocol)
    {
        using var connection = OpenConnection(protocol);
        await CreateAnchorAsync(connection);
        await AssertQueryAsync(connection, protocol, """
            SELECT json_exists(@sql_null, '$') AS sql_null,
                   json_exists('null', '$') AS json_null_exists,
                   json_exists('{}', '$.missing') AS missing,
                   json_array_length('[]', '$') AS empty_length,
                   json_array_length('null', '$') AS null_length,
                   json_array_length('{}', '$.missing') AS missing_length,
                   json_contains('null', '$', 1) AS null_contains,
                   json_contains('[]', '$', '[]') AS empty_contains,
                   json_contains('[1]', '$', '[1,1]') AS duplicate_contains,
                   json_contains('[1,1]', '$', '[1,1]') AS duplicate_match,
                   json_contains('[1]', '$', '1') AS string_is_not_number,
                   json_contains('[1]', '$', @sql_null) AS null_candidate,
                   json_exists('{}', @sql_null) AS null_path,
                   json_array_length('[]', @sql_null) AS null_array_path,
                   json_contains('[]', @sql_null, 1) AS null_contains_path,
                   json_contains('{}', '$.missing', '[') AS missing_skips_candidate,
                   json_contains('null', '$', '[') AS json_null_skips_candidate,
                   json_contains('{', '$', @sql_null) AS sql_null_skips_json
            FROM anchor
            """, [[null, true, false, 0L, null, null, false, true, false, true, false, null, null, null, null, false, false, null]],
            ("sql_null", DBNull.Value));
    }

    /// <summary>非法 JSON、path、候选对象和参数类型经真实传输返回可诊断的执行错误。</summary>
    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task Execute_InvalidJsonArguments_ReturnsDeterministicDiagnostics(string protocol)
    {
        using var connection = OpenConnection(protocol);
        await CreateAnchorAsync(connection);
        await AssertErrorAsync(connection, protocol, "json_exists(@json, '$')", "JSON 无效", ("json", "{"));
        await AssertErrorAsync(connection, protocol, "json_exists('{}', @path)", "JSON path 无效", ("path", "$.owner["));
        await AssertErrorAsync(connection, protocol, "json_exists('{}', @path)", "JSON path 无效", ("path", "$.*"));
        await AssertErrorAsync(connection, protocol, "json_array_length('{\"owner\":{}}', '$.owner')", "必须是 JSON 数组");
        await AssertErrorAsync(connection, protocol, "json_contains('[]', '$', @candidate)", "JSON 无效", ("candidate", "["));
        await AssertErrorAsync(connection, protocol, "json_exists(@json, '$')", "json 参数必须是 STRING", ("json", 1L));
        await AssertErrorAsync(connection, protocol, "json_exists('{}', @path)", "path 参数必须是 STRING", ("path", 1L));

        // 错误不能破坏连接后续请求；同一连接仍能执行真实数据查询。
        await AssertQueryAsync(connection, protocol, "SELECT json_array_length('[1,2]', '$') FROM anchor", [[2L]]);
    }

    /// <summary>资源边界在远程入口同样拒绝超限输入，且边界内查询正常完成。</summary>
    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task Execute_PathDepthCollectionAndComparisonBudgets_AreEnforced(string protocol)
    {
        using var connection = OpenConnection(protocol);
        await CreateAnchorAsync(connection);

        string maximumPath = "$." + new string('x', 1022);
        await AssertQueryAsync(connection, protocol, "SELECT json_exists('{}', @path) AS result FROM anchor", [[false]], ("path", maximumPath));
        await AssertErrorAsync(connection, protocol, "json_exists('{}', @path)", "path 超过 1024", ("path", maximumPath + "x"));
        await AssertQueryAsync(connection, protocol, "SELECT json_exists('{}', @path) AS result FROM anchor", [[false]],
            ("path", "$" + string.Concat(Enumerable.Repeat(".x", 64))));
        await AssertErrorAsync(connection, protocol, "json_exists('{}', @path)", "path 片段数超过 64",
            ("path", "$" + string.Concat(Enumerable.Repeat(".x", 65))));

        string maximumDepth = new string('[', 64) + "0" + new string(']', 64);
        await AssertQueryAsync(connection, protocol, "SELECT json_exists(@json, '$') AS result FROM anchor", [[true]], ("json", maximumDepth));
        await AssertErrorAsync(connection, protocol, "json_exists(@json, '$')", "嵌套深度超过 64", ("json", "[" + maximumDepth + "]"));

        string maximumArray = "[" + string.Join(',', Enumerable.Repeat("0", 100_000)) + "]";
        await AssertQueryAsync(connection, protocol, "SELECT json_array_length(@json, '$') AS result FROM anchor", [[100_000L]], ("json", maximumArray));
        await AssertErrorAsync(connection, protocol, "json_array_length(@json, '$')", "元素数超过 100000",
            ("json", maximumArray[..^1] + ",0]"));

        string target = "[" + string.Join(',', Enumerable.Range(0, 1500)) + "]";
        string candidate = "[" + string.Join(',', Enumerable.Range(0, 1500).Reverse()) + "]";
        await AssertErrorAsync(connection, protocol, "json_contains(@json, '$', @candidate)", "比较次数超过 1000000",
            ("json", target), ("candidate", candidate));
        await AssertQueryAsync(connection, protocol, "SELECT json_contains(@json, '$', '[1499]') AS result FROM anchor", [[true]], ("json", target));
    }

    /// <summary>对象数组的重叠子集、嵌套数组和 JSON null 不依赖候选排列顺序。</summary>
    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task Execute_OverlappingArrayCandidates_ReassignsDistinctMatches(string protocol)
    {
        using var connection = OpenConnection(protocol);
        await CreateAnchorAsync(connection);
        await AssertQueryAsync(connection, protocol, """
            SELECT json_contains(@target, '$', @broad_first) AS broad_first,
                   json_contains(@target, '$', @specific_first) AS specific_first,
                   json_contains(@target, '$', @duplicate_specific) AS duplicate_specific,
                   json_contains('[[1,2],[1]]', '$', '[[1],[1,2]]') AS nested_arrays,
                   json_contains('[null,{"x":null}]', '$', '[{"x":null},null]') AS nested_nulls,
                   json_exists(@quoted_json, @quoted_path) AS quoted_property
            FROM anchor
            """, [[true, true, false, true, true, true]],
            ("target", """[{"a":1,"b":2},{"a":1}]"""),
            ("broad_first", """[{"a":1},{"a":1,"b":2}]"""),
            ("specific_first", """[{"a":1,"b":2},{"a":1}]"""),
            ("duplicate_specific", """[{"a":1,"b":2},{"a":1,"b":2}]"""),
            ("quoted_json", """{"O'Reilly":null}"""), ("quoted_path", "$['O''Reilly']"));
    }

    /// <summary>固定 512 行语料的 JSON 谓词扫描返回全部预期匹配项，不把功能语料当成性能证据。</summary>
    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task Execute_BoundedCorpusFilter_ReturnsEveryExpectedMatch(string protocol)
    {
        using var connection = OpenConnection(protocol);
        await ExecuteAsync(connection, "CREATE TABLE corpus (id INT, metadata JSON, PRIMARY KEY (id))");
        string rows = string.Join(',', Enumerable.Range(0, 512).Select(index =>
            $"({index}, '{{\"group\":{index % 16},\"tags\":[\"item-{index % 16}\"],\"nested\":{{\"present\":null}}}}')"));
        await ExecuteAsync(connection, "INSERT INTO corpus (id, metadata) VALUES " + rows);
        object?[][] expected = Enumerable.Range(0, 32).Select(index => new object?[] { (long)(index * 16 + 7), 1L }).ToArray();
        await AssertQueryAsync(connection, protocol, """
            SELECT id, json_array_length(metadata, '$.tags') AS tag_count
            FROM corpus
            WHERE json_contains(metadata, @path, @candidate) = true
              AND json_exists(metadata, '$.nested.present') = true
            ORDER BY id
            """, expected, ("path", "$.tags"), ("candidate", "item-7"));
    }

    /// <summary>JSON 标量残余谓词可与文档 path 等值索引协作，独立包含查询明确显示扫描。</summary>
    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("frame-http2")]
    public async Task Explain_IndexedEqualityAndJsonResidual_ReportsActualAccessPath(string protocol)
    {
        using var connection = OpenConnection(protocol);
        await ExecuteAsync(connection, "CREATE DOCUMENT COLLECTION indexed_docs");
        await ExecuteAsync(connection, """
            INSERT INTO indexed_docs (id, document) VALUES
                ('a', '{"type":"pump","tags":["industrial"]}'),
                ('b', '{"type":"pump","tags":[]}'),
                ('c', '{"type":"fan","tags":["industrial"]}')
            """);
        await ExecuteAsync(connection, "CREATE JSON INDEX idx_device_type ON indexed_docs ('$.type')");
        const string indexedSql = """
            SELECT id FROM indexed_docs
            WHERE json_value(document, @type_path) = @type
              AND json_exists(document, '$.tags[0]')
              AND json_array_length(document, '$.tags') > 0
              AND json_contains(document, '$.tags', @member)
            ORDER BY id
            """;
        (string Name, object Value)[] parameters = [("type_path", "$.type"), ("type", "pump"), ("member", "industrial")];
        await AssertQueryAsync(connection, protocol, indexedSql, [["a"]], parameters);
        await AssertPlanAsync(connection, protocol, indexedSql, "document_index", "idx_device_type", parameters);

        const string scanSql = "SELECT id FROM indexed_docs WHERE json_contains(document, '$.tags', @member) ORDER BY id";
        await AssertQueryAsync(connection, protocol, scanSql, [["a"], ["c"]], ("member", "industrial"));
        await AssertPlanAsync(connection, protocol, scanSql, "document_scan", null, ("member", "industrial"));
    }

    private SndbConnection OpenConnection(string protocol)
    {
        string connectionString = protocol == "embedded"
            ? $"Data Source={Path.Combine(_root, "embedded")};Timeout=20"
            : $"Data Source=sonnetdb+http://{new Uri(protocol == "frame-http2" ? _frameH2Url : _http11Url).Authority}/{DatabaseName};" +
              $"Token={AdminToken};Protocol={protocol};Timeout=20";
        var connection = new SndbConnection(connectionString);
        connection.Open();
        return connection;
    }

    private async Task CreateAnchorAsync(SndbConnection connection)
    {
        await ExecuteAsync(connection, "CREATE TABLE anchor (id INT, PRIMARY KEY (id))");
        await ExecuteAsync(connection, "INSERT INTO anchor (id) VALUES (1)");
    }

    private SndbCommand CreateCommand(SndbConnection connection, string sql, (string Name, object Value)[] parameters)
    {
        _deadline.Token.ThrowIfCancellationRequested();
        Assert.InRange(parameters.Length, 0, 16);
        var command = connection.CreateCommand();
        command.CommandTimeout = 20;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue("@" + name, value);
        return command;
    }

    private async Task ExecuteAsync(SndbConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = CreateCommand(connection, sql, parameters);
        await command.ExecuteNonQueryAsync(_deadline.Token);
    }

    private async Task AssertQueryAsync(
        SndbConnection connection,
        string protocol,
        string sql,
        object?[][] expected,
        params (string Name, object Value)[] parameters)
    {
        Assert.InRange(expected.Length, 1, 512);
        using var command = CreateCommand(connection, sql, parameters);
        _requests.Clear();
        using var reader = await command.ExecuteReaderAsync(_deadline.Token);
        Assert.Equal(expected[0].Length, reader.FieldCount);
        foreach (object?[] row in expected)
        {
            Assert.True(await reader.ReadAsync(_deadline.Token));
            Assert.Equal(reader.FieldCount, row.Length);
            for (int column = 0; column < row.Length; column++)
            {
                _deadline.Token.ThrowIfCancellationRequested();
                if (row[column] is null)
                    Assert.True(reader.IsDBNull(column));
                else
                    Assert.Equal(row[column], reader.GetValue(column));
            }
        }
        Assert.False(await reader.ReadAsync(_deadline.Token));
        AssertTransport(protocol);
    }

    private async Task AssertErrorAsync(
        SndbConnection connection,
        string protocol,
        string expression,
        string diagnostic,
        params (string Name, object Value)[] parameters)
    {
        using var command = CreateCommand(connection, $"SELECT {expression} AS result FROM anchor", parameters);
        _requests.Clear();
        var error = await Record.ExceptionAsync(async () =>
        {
            using var reader = await command.ExecuteReaderAsync(_deadline.Token);
            await reader.ReadAsync(_deadline.Token);
        });
        Assert.NotNull(error);
        if (protocol == "embedded")
            Assert.IsType<InvalidOperationException>(error);
        else
            Assert.IsType<SndbServerException>(error);
        Assert.Contains(diagnostic, error.Message, StringComparison.Ordinal);
        AssertTransport(protocol);
    }

    private async Task AssertPlanAsync(
        SndbConnection connection,
        string protocol,
        string sql,
        string expectedAccessPath,
        string? expectedIndex,
        params (string Name, object Value)[] parameters)
    {
        using var command = CreateCommand(connection, "EXPLAIN " + sql, parameters);
        _requests.Clear();
        using var reader = await command.ExecuteReaderAsync(_deadline.Token);
        var plan = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (int row = 0; row < 128 && await reader.ReadAsync(_deadline.Token); row++)
            plan.Add(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetValue(1));
        Assert.False(await reader.ReadAsync(_deadline.Token));
        Assert.Equal(expectedAccessPath, plan["access_path"]);
        Assert.Equal(expectedIndex, plan["index_name"]);
        AssertTransport(protocol);
    }

    private void AssertTransport(string protocol)
    {
        ObservedRequest[] requests = _requests.ToArray();
        if (protocol == "embedded")
        {
            Assert.Empty(requests);
            return;
        }

        // 每次只读命令必须恰好命中所选数据端点，避免把 Frame 的 REST 回落计作 parity。
        ObservedRequest request = Assert.Single(requests);
        Assert.Equal(protocol == "frame-http2" ? "/v1/frame" : $"/v1/db/{DatabaseName}/sql", request.Path);
        Assert.Equal(protocol == "frame-http2" ? "HTTP/2" : "HTTP/1.1", request.Protocol);
        Assert.Equal(new Uri(protocol == "frame-http2" ? _frameH2Url : _http11Url).Port, request.LocalPort);
    }

    private async Task<bool> ProbeIsHttp11Async(string address)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, address + "/healthz")
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            };
            using var response = await client.SendAsync(request, _deadline.Token);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    private sealed record ObservedRequest(int LocalPort, string Path, string Protocol);
}
