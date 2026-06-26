using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Auth;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Json;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>
/// PR #34a-5：服务端用户/权限/控制面 SQL 端到端测试。
/// </summary>
public sealed class AuthControlPlaneEndToEndTests : IAsyncLifetime
{
    private WebApplication? _app;
    private string? _baseUrl;
    private string? _dataRoot;
    private const string _adminStaticToken = "static-admin-token";

    public async Task InitializeAsync()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "sndb-auth-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);

        var options = new ServerOptions
        {
            DataRoot = _dataRoot,
            AutoLoadExistingDatabases = true,
            AllowAnonymousProbes = true,
            Tokens = new Dictionary<string, string>
            {
                [_adminStaticToken] = ServerRoles.Admin,
            },
        };

        _app = TestServerHost.Build(options);
        await _app.StartAsync();

        var addresses = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel 未暴露监听地址。");
        _baseUrl = addresses.Addresses.First();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        if (_dataRoot is not null && Directory.Exists(_dataRoot))
        {
            TryDeleteDirectory(_dataRoot);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"清理临时目录失败（IO）：{path} / {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine($"清理临时目录失败（权限）：{path} / {ex.Message}");
        }
    }

    private HttpClient CreateClient(string? token)
    {
        var client = new HttpClient { BaseAddress = new Uri(_baseUrl!) };
        if (token is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Login_WithoutCredentials_Returns400()
    {
        using var client = CreateClient(token: null);
        var resp = await client.PostAsync("/v1/auth/login",
            JsonContent.Create(new LoginRequest("", ""), ServerJsonContext.Default.LoginRequest));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Login_WithUnknownUser_Returns401()
    {
        using var client = CreateClient(token: null);
        var resp = await client.PostAsync("/v1/auth/login",
            JsonContent.Create(new LoginRequest("ghost", "irrelevant"), ServerJsonContext.Default.LoginRequest));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task EndToEnd_CreateUser_Login_AndUseToken()
    {
        // 1) 用静态 admin token 通过 SQL 端点创建用户 + 数据库 + 授权
        await CreateDatabaseAsync("metrics");
        await ExecuteSqlAsync("metrics", "CREATE USER alice WITH PASSWORD 'pa$$'", _adminStaticToken);
        await ExecuteSqlAsync("metrics", "GRANT WRITE ON DATABASE metrics TO alice", _adminStaticToken);

        // 2) alice 用密码登录获取 token
        using var anon = CreateClient(token: null);
        var loginResp = await anon.PostAsync("/v1/auth/login",
            JsonContent.Create(new LoginRequest("alice", "pa$$"), ServerJsonContext.Default.LoginRequest));
        Assert.Equal(HttpStatusCode.OK, loginResp.StatusCode);
        var login = await loginResp.Content.ReadFromJsonAsync<LoginResponse>(ServerJsonContext.Default.LoginResponse);
        Assert.NotNull(login);
        Assert.Equal("alice", login!.Username);
        Assert.False(login.IsSuperuser);
        Assert.False(string.IsNullOrEmpty(login.Token));
        Assert.StartsWith("tok_", login.TokenId);

        // 3) 用动态 token 调用 /healthz 与 SQL（应通过认证）
        using var alice = CreateClient(login.Token);
        var hz = await alice.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, hz.StatusCode);

        // 4) alice 是普通用户，无法执行控制面 DDL
        var ddlResp = await alice.PostAsync("/v1/db/metrics/sql",
            JsonContent.Create(new SqlRequest("CREATE USER bob WITH PASSWORD 'p'"), ServerJsonContext.Default.SqlRequest));
        Assert.Equal(HttpStatusCode.Forbidden, ddlResp.StatusCode);
    }

    [Fact]
    public async Task ControlPlaneDdl_WithNonAdminDynamicToken_IsForbidden()
    {
        await CreateDatabaseAsync("foo");
        await ExecuteSqlAsync("foo", "CREATE USER carol WITH PASSWORD 'p'", _adminStaticToken);

        using var anon = CreateClient(token: null);
        var loginResp = await anon.PostAsync("/v1/auth/login",
            JsonContent.Create(new LoginRequest("carol", "p"), ServerJsonContext.Default.LoginRequest));
        var login = await loginResp.Content.ReadFromJsonAsync<LoginResponse>(ServerJsonContext.Default.LoginResponse);
        Assert.NotNull(login);

        using var carol = CreateClient(login!.Token);
        var resp = await carol.PostAsync("/v1/db/foo/sql",
            JsonContent.Create(new SqlRequest("DROP USER ghost"), ServerJsonContext.Default.SqlRequest));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task RevokedToken_AfterAlterUserPassword_FailsAuth()
    {
        await CreateDatabaseAsync("m1");
        await ExecuteSqlAsync("m1", "CREATE USER dave WITH PASSWORD 'old'", _adminStaticToken);

        using var anon = CreateClient(token: null);
        var loginResp = await anon.PostAsync("/v1/auth/login",
            JsonContent.Create(new LoginRequest("dave", "old"), ServerJsonContext.Default.LoginRequest));
        var login = await loginResp.Content.ReadFromJsonAsync<LoginResponse>(ServerJsonContext.Default.LoginResponse);
        Assert.NotNull(login);

        // admin 改密码 → dave 旧 token 失效
        await ExecuteSqlAsync("m1", "ALTER USER dave WITH PASSWORD 'new'", _adminStaticToken);

        using var dave = CreateClient(login!.Token);
        var hz = await dave.GetAsync("/v1/db");
        Assert.Equal(HttpStatusCode.Unauthorized, hz.StatusCode);
    }

    [Fact]
    public async Task ShowUsers_AsAdmin_ReturnsRows()
    {
        await CreateDatabaseAsync("m");
        await ExecuteSqlAsync("m", "CREATE USER eve WITH PASSWORD 'p'", _adminStaticToken);

        using var admin = CreateClient(_adminStaticToken);
        var resp = await admin.PostAsync("/v1/db/m/sql",
            JsonContent.Create(new SqlRequest("SHOW USERS"), ServerJsonContext.Default.SqlRequest));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("eve", body);
    }

    [Fact]
    public async Task ShowUsers_AsRegularUser_Forbidden()
    {
        await CreateDatabaseAsync("m");
        await ExecuteSqlAsync("m", "CREATE USER frank WITH PASSWORD 'p'", _adminStaticToken);

        using var anon = CreateClient(token: null);
        var loginResp = await anon.PostAsync("/v1/auth/login",
            JsonContent.Create(new LoginRequest("frank", "p"), ServerJsonContext.Default.LoginRequest));
        var login = await loginResp.Content.ReadFromJsonAsync<LoginResponse>(ServerJsonContext.Default.LoginResponse);

        using var frank = CreateClient(login!.Token);
        var resp = await frank.PostAsync("/v1/db/m/sql",
            JsonContent.Create(new SqlRequest("SHOW USERS"), ServerJsonContext.Default.SqlRequest));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task ShowDatabases_AsAdmin_ReturnsRows()
    {
        await CreateDatabaseAsync("alpha");
        await CreateDatabaseAsync("beta");
        using var admin = CreateClient(_adminStaticToken);
        var resp = await admin.PostAsync("/v1/db/alpha/sql",
            JsonContent.Create(new SqlRequest("SHOW DATABASES"), ServerJsonContext.Default.SqlRequest));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("alpha", body);
        Assert.Contains("beta", body);
    }

    [Fact]
    public async Task DynamicUser_DatabaseList_AndShowDatabases_AreFilteredByGrant()
    {
        await CreateDatabaseAsync("alpha");
        await CreateDatabaseAsync("beta");
        await CreateDatabaseAsync("gamma");
        await ExecuteSqlAsync("alpha", "CREATE USER viewer WITH PASSWORD 'p'", _adminStaticToken);
        await ExecuteSqlAsync("alpha", "GRANT READ ON DATABASE alpha TO viewer", _adminStaticToken);
        await ExecuteSqlAsync("alpha", "GRANT WRITE ON DATABASE gamma TO viewer", _adminStaticToken);

        var token = await LoginAsync("viewer", "p");
        using var viewer = CreateClient(token);

        var listResp = await viewer.GetAsync("/v1/db");
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        var list = await listResp.Content.ReadFromJsonAsync<DatabaseListResponse>(ServerJsonContext.Default.DatabaseListResponse);
        Assert.NotNull(list);
        Assert.Equal(new[] { "alpha", "gamma" }, list!.Databases);

        var showResp = await viewer.PostAsync("/v1/db/alpha/sql",
            JsonContent.Create(new SqlRequest("SHOW DATABASES"), ServerJsonContext.Default.SqlRequest));
        Assert.Equal(HttpStatusCode.OK, showResp.StatusCode);
        var showBody = await showResp.Content.ReadAsStringAsync();
        Assert.Equal(new[] { "alpha", "gamma" }, ParseSingleStringColumn(showBody));
    }

    [Fact]
    public async Task DataPlaneSql_WithDynamicTokenWithoutGrant_IsForbidden()
    {
        await CreateDatabaseAsync("metrics");
        await ExecuteSqlAsync("metrics", "CREATE MEASUREMENT cpu (host TAG, value FIELD FLOAT)", _adminStaticToken);
        await ExecuteSqlAsync("metrics", "CREATE USER alice WITH PASSWORD 'p'", _adminStaticToken);

        var token = await LoginAsync("alice", "p");
        using var alice = CreateClient(token);
        var resp = await alice.PostAsync("/v1/db/metrics/sql",
            JsonContent.Create(new SqlRequest("SELECT count(*) FROM cpu"), ServerJsonContext.Default.SqlRequest));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task DataPlaneSql_WithReadGrant_CannotWrite_AndDoesNotMutateData()
    {
        await CreateDatabaseAsync("metrics");
        await ExecuteSqlAsync("metrics", "CREATE MEASUREMENT cpu (host TAG, value FIELD FLOAT)", _adminStaticToken);
        await ExecuteSqlAsync("metrics", "CREATE USER bob WITH PASSWORD 'p'", _adminStaticToken);
        await ExecuteSqlAsync("metrics", "GRANT READ ON DATABASE metrics TO bob", _adminStaticToken);

        var token = await LoginAsync("bob", "p");
        using var bob = CreateClient(token);

        var readResp = await bob.PostAsync("/v1/db/metrics/sql",
            JsonContent.Create(new SqlRequest("SELECT count(*) FROM cpu"), ServerJsonContext.Default.SqlRequest));
        Assert.Equal(HttpStatusCode.OK, readResp.StatusCode);

        var writeResp = await bob.PostAsync("/v1/db/metrics/sql",
            JsonContent.Create(new SqlRequest("INSERT INTO cpu (time, host, value) VALUES (1, 'h1', 1.0)"),
                ServerJsonContext.Default.SqlRequest));
        Assert.Equal(HttpStatusCode.Forbidden, writeResp.StatusCode);

        using var admin = CreateClient(_adminStaticToken);
        var countResp = await admin.PostAsync("/v1/db/metrics/sql",
            JsonContent.Create(new SqlRequest("SELECT time, host, value FROM cpu"), ServerJsonContext.Default.SqlRequest));
        Assert.Equal(HttpStatusCode.OK, countResp.StatusCode);
        var countBody = await countResp.Content.ReadAsStringAsync();
        Assert.Equal(0, CountDataRows(countBody));
    }

    [Fact]
    public async Task DataPlaneSql_WithWriteGrant_CanWriteDatabaseData()
    {
        await CreateDatabaseAsync("metrics");
        await ExecuteSqlAsync("metrics", "CREATE USER writer WITH PASSWORD 'p'", _adminStaticToken);
        await ExecuteSqlAsync("metrics", "GRANT WRITE ON DATABASE metrics TO writer", _adminStaticToken);

        var token = await LoginAsync("writer", "p");
        await ExecuteSqlAsync("metrics", "CREATE MEASUREMENT cpu (host TAG, value FIELD FLOAT)", token);
        await ExecuteSqlAsync("metrics", "INSERT INTO cpu (time, host, value) VALUES (1, 'h1', 1.0)", token);

        using var admin = CreateClient(_adminStaticToken);
        var countResp = await admin.PostAsync("/v1/db/metrics/sql",
            JsonContent.Create(new SqlRequest("SELECT time, host, value FROM cpu"), ServerJsonContext.Default.SqlRequest));
        Assert.Equal(HttpStatusCode.OK, countResp.StatusCode);
        var countBody = await countResp.Content.ReadAsStringAsync();
        Assert.Equal(1, CountDataRows(countBody));
    }

    // PR #34b-3：/v1/sql 控制面端点（无 db 路径）

    [Fact]
    public async Task ControlPlaneEndpoint_AsAdmin_RunsCreateUserAndShowUsers()
    {
        using var admin = CreateClient(_adminStaticToken);
        var createResp = await admin.PostAsync("/v1/sql",
            JsonContent.Create(new SqlRequest("CREATE USER cpuser WITH PASSWORD 'p'"), ServerJsonContext.Default.SqlRequest));
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);

        var showResp = await admin.PostAsync("/v1/sql",
            JsonContent.Create(new SqlRequest("SHOW USERS"), ServerJsonContext.Default.SqlRequest));
        Assert.Equal(HttpStatusCode.OK, showResp.StatusCode);
        var body = await showResp.Content.ReadAsStringAsync();
        Assert.Contains("cpuser", body);
    }

    [Fact]
    public async Task ControlPlaneEndpoint_CreateSuperuser_FlagPersisted()
    {
        using var admin = CreateClient(_adminStaticToken);
        var resp = await admin.PostAsync("/v1/sql",
            JsonContent.Create(new SqlRequest("CREATE USER suid WITH PASSWORD 'p' SUPERUSER"),
                ServerJsonContext.Default.SqlRequest));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var showResp = await admin.PostAsync("/v1/sql",
            JsonContent.Create(new SqlRequest("SHOW USERS"), ServerJsonContext.Default.SqlRequest));
        var body = await showResp.Content.ReadAsStringAsync();
        // SHOW USERS 行格式 [name, is_superuser, created_utc, token_count]；UserStore 名字 lower-case。
        Assert.Contains("[\"suid\",true,", body);
    }

    [Fact]
    public async Task ControlPlaneEndpoint_AsRegularUser_Forbidden()
    {
        await CreateDatabaseAsync("rdb");
        using var admin = CreateClient(_adminStaticToken);
        await admin.PostAsync("/v1/sql",
            JsonContent.Create(new SqlRequest("CREATE USER ruser WITH PASSWORD 'p'"), ServerJsonContext.Default.SqlRequest));

        using var anon = CreateClient(token: null);
        var loginResp = await anon.PostAsync("/v1/auth/login",
            JsonContent.Create(new LoginRequest("ruser", "p"), ServerJsonContext.Default.LoginRequest));
        var login = await loginResp.Content.ReadFromJsonAsync<LoginResponse>(ServerJsonContext.Default.LoginResponse);

        using var ru = CreateClient(login!.Token);
        var resp = await ru.PostAsync("/v1/sql",
            JsonContent.Create(new SqlRequest("SHOW USERS"), ServerJsonContext.Default.SqlRequest));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task ControlPlaneEndpoint_RejectsDataPlaneStatement()
    {
        using var admin = CreateClient(_adminStaticToken);
        var resp = await admin.PostAsync("/v1/sql",
            JsonContent.Create(new SqlRequest("SELECT a FROM m"), ServerJsonContext.Default.SqlRequest));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("/v1/sql", body);
    }

    [Fact]
    public async Task ControlPlaneEndpoint_IssueAndRevokeToken_WorksEndToEnd()
    {
        using var admin = CreateClient(_adminStaticToken);
        var createResp = await admin.PostAsync("/v1/sql",
            JsonContent.Create(new SqlRequest("CREATE USER tokenuser WITH PASSWORD 'p'"), ServerJsonContext.Default.SqlRequest));
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);

        var issueResp = await admin.PostAsync("/v1/sql",
            JsonContent.Create(new SqlRequest("ISSUE TOKEN FOR tokenuser"), ServerJsonContext.Default.SqlRequest));
        Assert.Equal(HttpStatusCode.OK, issueResp.StatusCode);
        var issueBody = await issueResp.Content.ReadAsStringAsync();
        var (tokenId, token) = ParseIssuedToken(issueBody);

        var showResp = await admin.PostAsync("/v1/sql",
            JsonContent.Create(new SqlRequest("SHOW TOKENS FOR tokenuser"), ServerJsonContext.Default.SqlRequest));
        Assert.Equal(HttpStatusCode.OK, showResp.StatusCode);
        var showBody = await showResp.Content.ReadAsStringAsync();
        Assert.Contains(tokenId, showBody);

        using var tokenClient = CreateClient(token);
        var health = await tokenClient.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        var revokeResp = await admin.PostAsync("/v1/sql",
            JsonContent.Create(new SqlRequest($"REVOKE TOKEN '{tokenId}'"), ServerJsonContext.Default.SqlRequest));
        Assert.Equal(HttpStatusCode.OK, revokeResp.StatusCode);

        using var revokedClient = CreateClient(token);
        var dbList = await revokedClient.GetAsync("/v1/db");
        Assert.Equal(HttpStatusCode.Unauthorized, dbList.StatusCode);
    }

    [Fact]
    public async Task DynamicUser_DbScopedShowGrants_OnlyReturnsOwnRows()
    {
        await CreateDatabaseAsync("alpha");
        await CreateDatabaseAsync("beta");
        await ExecuteSqlAsync("alpha", "CREATE USER alice WITH PASSWORD 'p'", _adminStaticToken);
        await ExecuteSqlAsync("alpha", "CREATE USER bob WITH PASSWORD 'p'", _adminStaticToken);
        await ExecuteSqlAsync("alpha", "GRANT READ ON DATABASE alpha TO alice", _adminStaticToken);
        await ExecuteSqlAsync("alpha", "GRANT WRITE ON DATABASE beta TO alice", _adminStaticToken);
        await ExecuteSqlAsync("alpha", "GRANT ADMIN ON DATABASE beta TO bob", _adminStaticToken);

        var aliceToken = await LoginAsync("alice", "p");
        using var alice = CreateClient(aliceToken);

        var resp = await alice.PostAsync("/v1/db/alpha/sql",
            JsonContent.Create(new SqlRequest("SHOW GRANTS"), ServerJsonContext.Default.SqlRequest));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync();
        var rows = ParseRows(body);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal("alice", row[0].GetString()));
        Assert.Contains(rows, row => row[1].GetString() == "alpha" && row[2].GetString() == "Read");
        Assert.Contains(rows, row => row[1].GetString() == "beta" && row[2].GetString() == "Write");
    }

    [Fact]
    public async Task DynamicUser_DbScopedShowGrantsForOther_IsForbidden()
    {
        await CreateDatabaseAsync("alpha");
        await ExecuteSqlAsync("alpha", "CREATE USER alice WITH PASSWORD 'p'", _adminStaticToken);
        await ExecuteSqlAsync("alpha", "CREATE USER bob WITH PASSWORD 'p'", _adminStaticToken);
        await ExecuteSqlAsync("alpha", "GRANT READ ON DATABASE alpha TO alice", _adminStaticToken);
        await ExecuteSqlAsync("alpha", "GRANT READ ON DATABASE alpha TO bob", _adminStaticToken);

        var aliceToken = await LoginAsync("alice", "p");
        using var alice = CreateClient(aliceToken);

        var resp = await alice.PostAsync("/v1/db/alpha/sql",
            JsonContent.Create(new SqlRequest("SHOW GRANTS FOR bob"), ServerJsonContext.Default.SqlRequest));
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task DynamicUser_ControlPlaneEndpoint_ShowTokens_OnlyReturnsOwnRows()
    {
        using var admin = CreateClient(_adminStaticToken);
        await admin.PostAsync("/v1/sql",
            JsonContent.Create(new SqlRequest("CREATE USER alice WITH PASSWORD 'p'"), ServerJsonContext.Default.SqlRequest));
        await admin.PostAsync("/v1/sql",
            JsonContent.Create(new SqlRequest("CREATE USER bob WITH PASSWORD 'p'"), ServerJsonContext.Default.SqlRequest));

        var aliceToken = await LoginAsync("alice", "p");
        var bobToken = await LoginAsync("bob", "p");
        Assert.False(string.IsNullOrEmpty(bobToken));

        using var alice = CreateClient(aliceToken);
        var issueResp = await alice.PostAsync("/v1/sql",
            JsonContent.Create(new SqlRequest("ISSUE TOKEN FOR alice"), ServerJsonContext.Default.SqlRequest));
        Assert.Equal(HttpStatusCode.OK, issueResp.StatusCode);
        var issueBody = await issueResp.Content.ReadAsStringAsync();
        var (issuedTokenId, _) = ParseIssuedToken(issueBody);

        var showResp = await alice.PostAsync("/v1/sql",
            JsonContent.Create(new SqlRequest("SHOW TOKENS"), ServerJsonContext.Default.SqlRequest));
        Assert.Equal(HttpStatusCode.OK, showResp.StatusCode);

        var rows = ParseRows(await showResp.Content.ReadAsStringAsync());
        Assert.True(rows.Count >= 2);
        Assert.All(rows, row => Assert.Equal("alice", row[1].GetString()));
        Assert.Contains(rows, row => row[0].GetString() == issuedTokenId);
        Assert.DoesNotContain(rows, row => row[1].GetString() == "bob");
    }

    [Fact]
    public async Task DynamicUser_ControlPlaneEndpoint_IssueAndRevokeOwnToken_WorksWithoutDatabaseGrant()
    {
        using var admin = CreateClient(_adminStaticToken);
        await admin.PostAsync("/v1/sql",
            JsonContent.Create(new SqlRequest("CREATE USER selfsvc WITH PASSWORD 'p'"), ServerJsonContext.Default.SqlRequest));

        var primaryToken = await LoginAsync("selfsvc", "p");
        using var selfsvc = CreateClient(primaryToken);

        var issueResp = await selfsvc.PostAsync("/v1/sql",
            JsonContent.Create(new SqlRequest("ISSUE TOKEN FOR selfsvc"), ServerJsonContext.Default.SqlRequest));
        Assert.Equal(HttpStatusCode.OK, issueResp.StatusCode);
        var issueBody = await issueResp.Content.ReadAsStringAsync();
        var (tokenId, token) = ParseIssuedToken(issueBody);

        using var issuedClient = CreateClient(token);
        var listResp = await issuedClient.GetAsync("/v1/db");
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        var list = await listResp.Content.ReadFromJsonAsync<DatabaseListResponse>(ServerJsonContext.Default.DatabaseListResponse);
        Assert.NotNull(list);
        Assert.Empty(list!.Databases);

        var revokeResp = await selfsvc.PostAsync("/v1/sql",
            JsonContent.Create(new SqlRequest($"REVOKE TOKEN '{tokenId}'"), ServerJsonContext.Default.SqlRequest));
        Assert.Equal(HttpStatusCode.OK, revokeResp.StatusCode);

        using var revokedClient = CreateClient(token);
        var revokedResp = await revokedClient.GetAsync("/v1/db");
        Assert.Equal(HttpStatusCode.Unauthorized, revokedResp.StatusCode);
    }

    [Fact]
    public async Task DynamicUser_ControlPlaneEndpoint_CannotTargetOtherUsers()
    {
        using var admin = CreateClient(_adminStaticToken);
        await admin.PostAsync("/v1/sql",
            JsonContent.Create(new SqlRequest("CREATE USER alice WITH PASSWORD 'p'"), ServerJsonContext.Default.SqlRequest));
        await admin.PostAsync("/v1/sql",
            JsonContent.Create(new SqlRequest("CREATE USER bob WITH PASSWORD 'p'"), ServerJsonContext.Default.SqlRequest));

        var bobIssueResp = await admin.PostAsync("/v1/sql",
            JsonContent.Create(new SqlRequest("ISSUE TOKEN FOR bob"), ServerJsonContext.Default.SqlRequest));
        Assert.Equal(HttpStatusCode.OK, bobIssueResp.StatusCode);
        var (bobTokenId, _) = ParseIssuedToken(await bobIssueResp.Content.ReadAsStringAsync());

        var aliceToken = await LoginAsync("alice", "p");
        using var alice = CreateClient(aliceToken);

        var showGrantsResp = await alice.PostAsync("/v1/sql",
            JsonContent.Create(new SqlRequest("SHOW GRANTS FOR bob"), ServerJsonContext.Default.SqlRequest));
        Assert.Equal(HttpStatusCode.Forbidden, showGrantsResp.StatusCode);

        var showTokensResp = await alice.PostAsync("/v1/sql",
            JsonContent.Create(new SqlRequest("SHOW TOKENS FOR bob"), ServerJsonContext.Default.SqlRequest));
        Assert.Equal(HttpStatusCode.Forbidden, showTokensResp.StatusCode);

        var issueResp = await alice.PostAsync("/v1/sql",
            JsonContent.Create(new SqlRequest("ISSUE TOKEN FOR bob"), ServerJsonContext.Default.SqlRequest));
        Assert.Equal(HttpStatusCode.Forbidden, issueResp.StatusCode);

        var revokeResp = await alice.PostAsync("/v1/sql",
            JsonContent.Create(new SqlRequest($"REVOKE TOKEN '{bobTokenId}'"), ServerJsonContext.Default.SqlRequest));
        Assert.Equal(HttpStatusCode.Forbidden, revokeResp.StatusCode);
    }

    private async Task CreateDatabaseAsync(string name)
    {
        using var admin = CreateClient(_adminStaticToken);
        var resp = await admin.PostAsync("/v1/db",
            JsonContent.Create(new CreateDatabaseRequest(name), ServerJsonContext.Default.CreateDatabaseRequest));
        Assert.True(resp.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK,
            $"创建数据库 {name} 失败：{resp.StatusCode}");
    }

    private async Task ExecuteSqlAsync(string db, string sql, string token)
    {
        using var client = CreateClient(token);
        var resp = await client.PostAsync($"/v1/db/{db}/sql",
            JsonContent.Create(new SqlRequest(sql), ServerJsonContext.Default.SqlRequest));
        Assert.True(resp.IsSuccessStatusCode, $"SQL '{sql}' 失败：{resp.StatusCode} / {await resp.Content.ReadAsStringAsync()}");
    }

    private async Task<string> LoginAsync(string username, string password)
    {
        using var anon = CreateClient(token: null);
        var response = await anon.PostAsync("/v1/auth/login",
            JsonContent.Create(new LoginRequest(username, password), ServerJsonContext.Default.LoginRequest));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"登录失败：{response.StatusCode} / {body}");

        var login = JsonSerializer.Deserialize(body, ServerJsonContext.Default.LoginResponse);
        Assert.NotNull(login);
        return login!.Token;
    }

    private static (string TokenId, string Token) ParseIssuedToken(string ndjson)
    {
        foreach (var line in ndjson.Split("\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith("[", StringComparison.Ordinal))
            {
                continue;
            }

            var values = JsonSerializer.Deserialize<string[]>(line);
            if (values is [var tokenId, var token])
            {
                return (tokenId, token);
            }
        }

        throw new InvalidOperationException("未在 ndjson 响应中找到已签发 token。");
    }

    private static int CountDataRows(string ndjson)
    {
        var count = 0;
        foreach (var line in ndjson.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith("[", StringComparison.Ordinal))
                continue;
            count++;
        }
        return count;
    }

    private static List<JsonElement[]> ParseRows(string ndjson)
    {
        var rows = new List<JsonElement[]>();
        foreach (var line in ndjson.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith("[", StringComparison.Ordinal))
                continue;

            var values = JsonSerializer.Deserialize<JsonElement[]>(line);
            if (values is not null)
                rows.Add(values);
        }

        return rows;
    }

    private static string[] ParseSingleStringColumn(string ndjson)
    {
        var rows = new List<string>();
        foreach (var line in ndjson.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith("[", StringComparison.Ordinal))
                continue;

            var values = JsonSerializer.Deserialize<string[]>(line);
            if (values is [var value])
                rows.Add(value);
        }
        return rows.ToArray();
    }
}
