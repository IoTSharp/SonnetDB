using SonnetDB.Auth;
using Xunit;

namespace SonnetDB.Tests.Auth;

/// <summary>
/// PR #34a-1：UserStore + GrantsStore + PasswordHasher + ApiToken 单元测试。
/// </summary>
public sealed class UserStoreTests : IDisposable
{
    private readonly string _dir;

    public UserStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sndb-userstore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        TryDeleteDirectory(_dir);
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

    [Fact]
    public void PasswordHasher_VerifyMatchingPassword_ReturnsTrue()
    {
        var (salt, hash, iter) = PasswordHasher.Hash("hunter2");
        Assert.Equal(PasswordHasher.SaltBytes, salt.Length);
        Assert.Equal(PasswordHasher.HashBytes, hash.Length);
        Assert.True(PasswordHasher.Verify("hunter2", salt, hash, iter));
    }

    [Fact]
    public void PasswordHasher_VerifyMismatch_ReturnsFalse()
    {
        var (salt, hash, iter) = PasswordHasher.Hash("hunter2");
        Assert.False(PasswordHasher.Verify("HUNTER2", salt, hash, iter));
        Assert.False(PasswordHasher.Verify("", salt, hash, iter));
    }

    [Fact]
    public void ApiToken_GenerateProducesUniqueBase64UrlAndHashIsDeterministic()
    {
        var t1 = ApiToken.Generate();
        var t2 = ApiToken.Generate();
        Assert.NotEqual(t1, t2);
        Assert.DoesNotContain('+', t1);
        Assert.DoesNotContain('/', t1);
        Assert.DoesNotContain('=', t1);
        Assert.Equal(ApiToken.HashHex(t1), ApiToken.HashHex(t1));
        Assert.NotEqual(ApiToken.HashHex(t1), ApiToken.HashHex(t2));
    }

    [Fact]
    public void UserStore_CreateAndAuthenticate_RoundTrip()
    {
        var store = new UserStore(_dir);
        store.CreateUser("Alice", "pa$$w0rd!", isSuperuser: true);

        Assert.True(store.Exists("alice"));
        Assert.True(store.Exists("ALICE")); // 大小写无关
        Assert.True(store.IsSuperuser("alice"));
        Assert.True(store.VerifyPassword("alice", "pa$$w0rd!"));
        Assert.False(store.VerifyPassword("alice", "wrong"));
    }

    [Fact]
    public void UserStore_EmailLoginAndTargetedReset_PreserveOtherUsers()
    {
        var store = new UserStore(_dir);
        const string demo = "demo@example.test";
        store.CreateUser(demo, "initial-demo", isSuperuser: false);
        store.CreateUser("admin@example.test", "admin-unchanged", isSuperuser: true);
        store.CreateUser("customer@example.test", "customer-unchanged", isSuperuser: false);
        var (oldDemoToken, _) = store.IssueToken(demo);
        var (customerToken, _) = store.IssueToken("customer@example.test");

        store.ChangePassword(demo, "restored-demo");

        var reopened = new UserStore(_dir);
        Assert.True(reopened.VerifyPassword("DEMO@example.test", "restored-demo"));
        Assert.False(reopened.VerifyPassword(demo, "initial-demo"));
        Assert.True(reopened.VerifyPassword("admin@example.test", "admin-unchanged"));
        Assert.True(reopened.VerifyPassword("customer@example.test", "customer-unchanged"));
        Assert.False(reopened.TryAuthenticate(oldDemoToken, out _));
        Assert.True(reopened.TryAuthenticate(customerToken, out _));
        Assert.False(reopened.IsSuperuser(demo));
    }

    [Theory]
    [InlineData("../demo")]
    [InlineData("demo user")]
    [InlineData("demo\nuser")]
    public void UserStore_EmailSupport_DoesNotAcceptSeparatorsOrWhitespace(string name)
    {
        var store = new UserStore(_dir);
        Assert.Throws<ArgumentException>(() => store.CreateUser(name, "test-password", false));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void UserStore_CreateUser_DuplicateThrows()
    {
        var store = new UserStore(_dir);
        store.CreateUser("bob", "p1", isSuperuser: false);
        Assert.Throws<InvalidOperationException>(() => store.CreateUser("BOB", "p2", isSuperuser: false));
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("中文")]
    [InlineData("a/b")]
    public void UserStore_CreateUser_InvalidName_Throws(string name)
    {
        var store = new UserStore(_dir);
        Assert.ThrowsAny<ArgumentException>(() => store.CreateUser(name, "pwd", false));
    }

    [Fact]
    public void UserStore_IssueToken_AuthenticatesAndPersists()
    {
        var store = new UserStore(_dir);
        store.CreateUser("alice", "pwd", true);
        var (token, tokenId) = store.IssueToken("alice");

        Assert.True(store.TryAuthenticate(token, out var u));
        Assert.Equal("alice", u.UserName);
        Assert.True(u.IsSuperuser);

        // 重新打开（模拟进程重启）后 token 仍生效
        var store2 = new UserStore(_dir);
        Assert.True(store2.TryAuthenticate(token, out var u2));
        Assert.Equal("alice", u2.UserName);

        Assert.True(store2.RevokeToken("alice", tokenId));
        Assert.False(store2.TryAuthenticate(token, out _));
    }

    [Fact]
    public void UserStore_ChangePassword_RevokesAllTokens()
    {
        var store = new UserStore(_dir);
        store.CreateUser("alice", "old", false);
        var (token, _) = store.IssueToken("alice");
        Assert.True(store.TryAuthenticate(token, out _));

        store.ChangePassword("alice", "new");
        Assert.False(store.TryAuthenticate(token, out _));
        Assert.False(store.VerifyPassword("alice", "old"));
        Assert.True(store.VerifyPassword("alice", "new"));
    }

    [Fact]
    public void UserStore_DeleteUser_RemovesAndRevokesTokens()
    {
        var store = new UserStore(_dir);
        store.CreateUser("alice", "pwd", false);
        var (token, _) = store.IssueToken("alice");
        Assert.True(store.DeleteUser("alice"));
        Assert.False(store.Exists("alice"));
        Assert.False(store.TryAuthenticate(token, out _));
        Assert.False(store.DeleteUser("alice"));
    }

    [Fact]
    public void UserStore_ListTokensDetailed_AndRevokeById_Work()
    {
        var store = new UserStore(_dir);
        store.CreateUser("bob", "pwd", false);
        store.CreateUser("alice", "pwd", false);

        var (aliceToken, aliceTokenId) = store.IssueToken("alice");
        store.IssueToken("bob");

        var all = store.ListTokensDetailed(null);
        Assert.Equal(2, all.Count);
        Assert.Equal("alice", all[0].UserName);
        Assert.Equal(aliceTokenId, all[0].TokenId);

        var aliceOnly = store.ListTokensDetailed("alice");
        Assert.Single(aliceOnly);
        Assert.Equal(aliceTokenId, aliceOnly[0].TokenId);

        Assert.True(store.RevokeTokenById(aliceTokenId));
        Assert.False(store.TryAuthenticate(aliceToken, out _));
        Assert.False(store.RevokeTokenById("tok_missing"));
    }

    [Fact]
    public void UserStore_TryAuthenticate_UnknownTokenReturnsFalse()
    {
        var store = new UserStore(_dir);
        Assert.False(store.TryAuthenticate("not-a-real-token", out _));
        Assert.False(store.TryAuthenticate("", out _));
    }

    [Fact]
    public void GrantsStore_GrantAndQuery_HighestWins()
    {
        var grants = new GrantsStore(_dir);
        grants.Grant("alice", "metrics", DatabasePermission.Read);
        grants.Grant("alice", "metrics", DatabasePermission.Write); // 升级
        grants.Grant("alice", "metrics", DatabasePermission.Read);  // 不应降级

        Assert.Equal(DatabasePermission.Write, grants.GetPermission("alice", "metrics"));
        Assert.Equal(DatabasePermission.None, grants.GetPermission("alice", "events"));
    }

    [Fact]
    public void GrantsStore_WildcardDatabase_MatchesAny()
    {
        var grants = new GrantsStore(_dir);
        grants.Grant("alice", "*", DatabasePermission.Read);
        grants.Grant("alice", "metrics", DatabasePermission.Admin);

        Assert.Equal(DatabasePermission.Read, grants.GetPermission("alice", "events"));
        Assert.Equal(DatabasePermission.Admin, grants.GetPermission("alice", "metrics")); // 取最高
    }

    [Fact]
    public void GrantsStore_Revoke_DeletesEntry()
    {
        var grants = new GrantsStore(_dir);
        grants.Grant("bob", "metrics", DatabasePermission.Write);
        Assert.True(grants.Revoke("bob", "metrics"));
        Assert.Equal(DatabasePermission.None, grants.GetPermission("bob", "metrics"));
        Assert.False(grants.Revoke("bob", "metrics"));
    }

    [Fact]
    public void GrantsStore_DeleteUserGrants_RemovesAllForUser()
    {
        var grants = new GrantsStore(_dir);
        grants.Grant("alice", "a", DatabasePermission.Read);
        grants.Grant("alice", "b", DatabasePermission.Write);
        grants.Grant("bob", "a", DatabasePermission.Read);

        grants.DeleteUserGrants("alice");
        Assert.Equal(DatabasePermission.None, grants.GetPermission("alice", "a"));
        Assert.Equal(DatabasePermission.None, grants.GetPermission("alice", "b"));
        Assert.Equal(DatabasePermission.Read, grants.GetPermission("bob", "a"));
    }

    [Fact]
    public void GrantsStore_DeleteDatabaseGrants_RemovesAllForDatabase()
    {
        var grants = new GrantsStore(_dir);
        grants.Grant("alice", "metrics", DatabasePermission.Read);
        grants.Grant("bob", "metrics", DatabasePermission.Write);
        grants.Grant("alice", "events", DatabasePermission.Read);

        grants.DeleteDatabaseGrants("metrics");
        Assert.Equal(DatabasePermission.None, grants.GetPermission("alice", "metrics"));
        Assert.Equal(DatabasePermission.None, grants.GetPermission("bob", "metrics"));
        Assert.Equal(DatabasePermission.Read, grants.GetPermission("alice", "events"));
    }

    [Fact]
    public void GrantsStore_Persistence_SurvivesReopen()
    {
        var g1 = new GrantsStore(_dir);
        g1.Grant("alice", "metrics", DatabasePermission.Admin);

        var g2 = new GrantsStore(_dir);
        Assert.Equal(DatabasePermission.Admin, g2.GetPermission("alice", "metrics"));
    }

    [Fact]
    public async Task UserStore_ConcurrentIssueAndAuthenticate_NoExceptions()
    {
        var store = new UserStore(_dir);
        store.CreateUser("alice", "pwd", false);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var issuers = Enumerable.Range(0, 4).Select(__ => Task.Run(() =>
        {
            var locals = new List<string>();
            // do-while 确保每个 Task 至少颁发一次 token，避免慢 CI 机器在 Task 启动前 CTS 已超时导致 locals 为空。
            do
            {
                var (t, _) = store.IssueToken("alice");
                locals.Add(t);
            }
            while (!cts.Token.IsCancellationRequested && locals.Count < 50);
            return locals;
        })).ToArray();

        var readers = Enumerable.Range(0, 4).Select(__ => Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
                _ = store.ListUserNames();
        })).ToArray();

        await Task.WhenAll([.. issuers, .. readers]);

        // 任意一个颁发出来的 token 都应该可以认证
        var sample = (await issuers[0]).First();
        Assert.True(store.TryAuthenticate(sample, out _));
    }
}
