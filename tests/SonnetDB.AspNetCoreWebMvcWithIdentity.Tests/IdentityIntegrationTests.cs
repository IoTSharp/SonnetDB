namespace SonnetDB.AspNetCoreWebMvcWithIdentity.Tests;

using System.Buffers.Binary;
using System.Data;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SonnetDB.AspNetCoreWebMvcWithIdentity.Data;
using SonnetDB.Tables;
using Xunit;

/// <summary>通过未修改的 MVC 和官方 Identity 页面验证 SonnetDB 组件替换合同。</summary>
public sealed class IdentityIntegrationTests
{
    private const string Email = "alice@example.test";
    private const string Password = "Original-Passw0rd!";
    private const string NewPassword = "Replacement-Passw0rd!";

    /// <summary>验证原模板的开发迁移端点可初始化新库并立即支持注册。</summary>
    [Fact]
    public async Task MigrationsEndpoint_WithNewDatabase_AppliesSampleMigration()
    {
        await using var sample = await IdentitySample.CreateAsync(applyMigrations: false);
        await using (var scope = sample.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.Empty(await context.Database.GetAppliedMigrationsAsync(sample.Token));
        }

        using var invalidContent = new FormUrlEncodedContent(new Dictionary<string, string> { ["context"] = "unknown" });
        using var invalid = await sample.Client.PostAsync("/ApplyDatabaseMigrations", invalidContent, sample.Token);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["context"] = typeof(ApplicationDbContext).AssemblyQualifiedName!
        });
        using var applied = await sample.Client.PostAsync("/ApplyDatabaseMigrations", content, sample.Token);
        Assert.Equal(HttpStatusCode.NoContent, applied.StatusCode);
        await using (var scope = sample.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.Single(await context.Database.GetAppliedMigrationsAsync(sample.Token));
            Assert.Empty(await context.Database.GetPendingMigrationsAsync(sample.Token));
        }

        await sample.RegisterAndConfirmAsync(Email, Password);
        using var login = await sample.LoginAsync(Email, Password);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
    }

    /// <summary>验证完整宿主关闭重开后用户、声明和持久登录 Cookie 仍可使用。</summary>
    [Fact]
    public async Task HttpIdentity_AfterHostRestart_RestoresUserAndPersistentCookie()
    {
        await using var sample = await IdentitySample.CreateAsync();
        await sample.RegisterAndConfirmAsync(Email, Password);
        await using (var scope = sample.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var user = (await users.FindByNameAsync(Email))!;
            AssertSucceeded(await users.AddClaimAsync(user, new Claim("department", "engineering")));
        }

        using var login = await sample.LoginAsync(Email, Password);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var cookie = Assert.Single(login.Headers.GetValues("Set-Cookie"), value =>
            value.StartsWith(".AspNetCore.Identity.Application=", StringComparison.Ordinal));
        Assert.Contains("expires=", cookie, StringComparison.OrdinalIgnoreCase);
        await sample.RestartAsync();
        sample.Client.DefaultRequestHeaders.Add("Cookie", cookie.Split(';', 2)[0]);
        using var managed = await sample.GetAsync("/Identity/Account/Manage");
        Assert.Equal(HttpStatusCode.OK, managed.StatusCode);
        await using var verification = sample.CreateScope();
        var restoredUsers = verification.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var restored = (await restoredUsers.FindByNameAsync(Email))!;
        Assert.True(restored.EmailConfirmed);
        Assert.True(await restoredUsers.CheckPasswordAsync(restored, Password));
        Assert.Contains(await restoredUsers.GetClaimsAsync(restored), claim =>
            claim.Type == "department" && claim.Value == "engineering");
    }

    /// <summary>验证实际迁移创建全部 Identity 表、唯一索引和历史记录，并可重复执行。</summary>
    [Fact]
    public async Task Migrate_WithSampleContext_CreatesSchemaIndexesAndIdempotentHistory()
    {
        await using var sample = await IdentitySample.CreateAsync();
        await using var scope = sample.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal("SonnetDB.EntityFrameworkCore", context.Database.ProviderName);
        Assert.False(context.Database.HasPendingModelChanges());
        Assert.True(scope.ServiceProvider.GetRequiredService<IOptions<IdentityOptions>>().Value.SignIn.RequireConfirmedAccount);
        var expected = context.Database.GetMigrations().ToArray();
        Assert.NotEmpty(expected);
        Assert.Equal(expected, (await context.Database.GetAppliedMigrationsAsync(sample.Token)).ToArray());
        Assert.Empty(await context.Database.GetPendingMigrationsAsync(sample.Token));

        await context.Database.OpenConnectionAsync(sample.Token);
        try
        {
            var connection = context.Database.GetDbConnection();
            var tables = connection.GetSchema("Tables").Rows.Cast<DataRow>().Select(row => (string)row["TABLE_NAME"]).ToArray();
            Assert.All(new[] { "AspNetUsers", "AspNetRoles", "AspNetUserClaims", "AspNetRoleClaims", "AspNetUserLogins", "AspNetUserRoles", "AspNetUserTokens", "__EFMigrationsHistory" }, name => Assert.Contains(name, tables));
            var modelTables = context.Model.GetEntityTypes().Select(entity => entity.GetTableName()).Distinct().ToArray();
            Assert.InRange(modelTables.Length, 7, 16);
            Assert.All(modelTables, name => Assert.Contains(name, tables));
            var userNameIndex = Assert.Single(connection.GetSchema("Indexes", [null, null, "AspNetUsers", "UserNameIndex"]).Rows.Cast<DataRow>());
            Assert.True((bool)userNameIndex["IS_UNIQUE"]);
            Assert.Equal("NormalizedUserName", userNameIndex["COLUMN_NAME"]);
            var roleNameIndex = Assert.Single(connection.GetSchema("Indexes", [null, null, "AspNetRoles", "RoleNameIndex"]).Rows.Cast<DataRow>());
            Assert.True((bool)roleNameIndex["IS_UNIQUE"]);
            Assert.Single(connection.GetSchema("Indexes", [null, null, "AspNetUsers", "EmailIndex"]).Rows.Cast<DataRow>());
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }

        await context.Database.MigrateAsync(sample.Token);
        Assert.Equal(expected, (await context.Database.GetAppliedMigrationsAsync(sample.Token)).ToArray());
    }

    /// <summary>验证真实注册、邮件确认、密码校验、认证 Cookie、资料和密码修改以及退出。</summary>
    [Fact]
    public async Task HttpIdentity_WithConfirmedRegistration_ManagesProfilePasswordAndCookie()
    {
        await using var sample = await IdentitySample.CreateAsync();
        using var registration = await sample.PostAsync("/Identity/Account/Register", new Dictionary<string, string>
        {
            ["Input.Email"] = Email,
            ["Input.Password"] = Password,
            ["Input.ConfirmPassword"] = Password
        });
        Assert.Equal(HttpStatusCode.Redirect, registration.StatusCode);
        using var unconfirmedLogin = await sample.LoginAsync(Email, Password);
        Assert.Equal(HttpStatusCode.OK, unconfirmedLogin.StatusCode);
        Assert.Contains("Invalid login attempt", await unconfirmedLogin.Content.ReadAsStringAsync(sample.Token), StringComparison.OrdinalIgnoreCase);
        using var denied = await sample.GetAsync("/Identity/Account/Manage");
        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
        Assert.Contains("/Identity/Account/Login", denied.Headers.Location!.OriginalString, StringComparison.Ordinal);

        using var confirmation = await sample.GetAsync(sample.Emails.LastLink(Email));
        Assert.Equal(HttpStatusCode.OK, confirmation.StatusCode);
        using var badPassword = await sample.LoginAsync(Email, "wrong-password");
        Assert.Equal(HttpStatusCode.OK, badPassword.StatusCode);
        using var login = await sample.LoginAsync(Email, Password);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Contains(login.Headers.GetValues("Set-Cookie"), value => value.StartsWith(".AspNetCore.Identity.Application=", StringComparison.Ordinal));
        using var profile = await sample.PostAsync("/Identity/Account/Manage", new Dictionary<string, string>
        {
            ["Input.PhoneNumber"] = "+8613800138000"
        });
        Assert.Equal(HttpStatusCode.Redirect, profile.StatusCode);
        await using (var scope = sample.CreateScope())
        {
            var user = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Users.SingleAsync(sample.Token);
            Assert.True(user.EmailConfirmed);
            Assert.Equal("+8613800138000", user.PhoneNumber);
        }

        using var changedPassword = await sample.PostAsync("/Identity/Account/Manage/ChangePassword", new Dictionary<string, string>
        {
            ["Input.OldPassword"] = Password,
            ["Input.NewPassword"] = NewPassword,
            ["Input.ConfirmPassword"] = NewPassword
        });
        Assert.Equal(HttpStatusCode.Redirect, changedPassword.StatusCode);
        using var logout = await sample.PostFromPageAsync("/", "/Identity/Account/Logout?returnUrl=%2F", new Dictionary<string, string>());
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        using var afterLogout = await sample.GetAsync("/Identity/Account/Manage");
        Assert.Equal(HttpStatusCode.Redirect, afterLogout.StatusCode);
        using var oldPassword = await sample.LoginAsync(Email, Password);
        Assert.Equal(HttpStatusCode.OK, oldPassword.StatusCode);
        using var newPassword = await sample.LoginAsync(Email, NewPassword);
        Assert.Equal(HttpStatusCode.Redirect, newPassword.StatusCode);
        using var signedIn = await sample.GetAsync("/");
        Assert.Contains($"Hello {Email}!", await signedIn.Content.ReadAsStringAsync(sample.Token), StringComparison.Ordinal);
    }

    /// <summary>验证官方忘记密码和重置页面，以及已使用的重置令牌不能再次使用。</summary>
    [Fact]
    public async Task HttpIdentity_WithPasswordReset_ReplacesPasswordAndInvalidatesToken()
    {
        await using var sample = await IdentitySample.CreateAsync();
        await sample.RegisterAndConfirmAsync(Email, Password);
        using var forgotten = await sample.PostAsync("/Identity/Account/ForgotPassword", new Dictionary<string, string> { ["Input.Email"] = Email });
        Assert.Equal(HttpStatusCode.Redirect, forgotten.StatusCode);
        var resetPath = sample.Emails.LastLink(Email);
        using var reset = await sample.PostAsync(resetPath, new Dictionary<string, string>
        {
            ["Input.Email"] = Email,
            ["Input.Password"] = NewPassword,
            ["Input.ConfirmPassword"] = NewPassword
        });
        Assert.Equal(HttpStatusCode.Redirect, reset.StatusCode);
        Assert.Contains("ResetPasswordConfirmation", reset.Headers.Location!.OriginalString, StringComparison.Ordinal);
        using var repeatedReset = await sample.PostAsync(resetPath, new Dictionary<string, string>
        {
            ["Input.Email"] = Email,
            ["Input.Password"] = Password,
            ["Input.ConfirmPassword"] = Password
        });
        Assert.Equal(HttpStatusCode.OK, repeatedReset.StatusCode);
        Assert.Contains("Invalid token", await repeatedReset.Content.ReadAsStringAsync(sample.Token), StringComparison.OrdinalIgnoreCase);
        using var oldLogin = await sample.LoginAsync(Email, Password);
        Assert.Equal(HttpStatusCode.OK, oldLogin.StatusCode);
        using var newLogin = await sample.LoginAsync(Email, NewPassword);
        Assert.Equal(HttpStatusCode.Redirect, newLogin.StatusCode);
    }

    /// <summary>验证缺少防伪令牌的真实注册请求被拒绝且没有创建账户。</summary>
    [Fact]
    public async Task HttpIdentity_WithoutAntiforgeryToken_RejectsRegistration()
    {
        await using var sample = await IdentitySample.CreateAsync();
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Email"] = Email,
            ["Input.Password"] = Password,
            ["Input.ConfirmPassword"] = Password
        });
        using var response = await sample.Client.PostAsync("/Identity/Account/Register", content, sample.Token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var scope = sample.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Users.AnyAsync(sample.Token));
    }

    /// <summary>验证声明、外部登录、命名令牌和角色复合键的存储及数据库级删除级联。</summary>
    [Fact]
    public async Task IdentityStores_WithClaimsLoginsTokensAndRoles_PersistsAndCascadesDeletion()
    {
        await using var sample = await IdentitySample.CreateAsync();
        string userId;
        string roleId;
        await using (var scope = sample.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var user = await CreateUserAsync(users);
            userId = user.Id;
            AssertSucceeded(await users.AddClaimsAsync(user, [new Claim("department", "engineering"), new Claim("permission", "read")]));
            AssertSucceeded(await users.ReplaceClaimAsync(user, new Claim("department", "engineering"), new Claim("department", "support")));
            AssertSucceeded(await users.AddLoginAsync(user, new UserLoginInfo("github", "external-key", "GitHub")));
            AssertSucceeded(await users.AddLoginAsync(user, new UserLoginInfo("oidc", "external-key", "OIDC")));
            AssertSucceeded(await users.SetAuthenticationTokenAsync(user, "github", "access_token", "first"));
            AssertSucceeded(await users.SetAuthenticationTokenAsync(user, "github", "access_token", "updated"));
            AssertSucceeded(await users.SetAuthenticationTokenAsync(user, "github", "refresh_token", "refresh"));
            AssertSucceeded(await users.SetAuthenticationTokenAsync(user, "oidc", "access_token", "other-provider"));
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var role = new IdentityRole("Reader") { NormalizedName = "READER" };
            roleId = role.Id;
            context.Roles.Add(role);
            context.UserRoles.Add(new IdentityUserRole<string> { UserId = userId, RoleId = roleId });
            context.RoleClaims.Add(new IdentityRoleClaim<string> { RoleId = roleId, ClaimType = "permission", ClaimValue = "read" });
            await context.SaveChangesAsync(sample.Token);
            Assert.All(await context.UserClaims.Select(claim => claim.Id).ToListAsync(sample.Token), id => Assert.True(id > 0));
            Assert.True(await context.RoleClaims.Select(claim => claim.Id).SingleAsync(sample.Token) > 0);
        }

        await using (var scope = sample.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var user = await users.FindByLoginAsync("github", "external-key");
            Assert.NotNull(user);
            Assert.Equal(userId, user.Id);
            Assert.Equal(userId, (await users.FindByLoginAsync("oidc", "external-key"))!.Id);
            Assert.Equal(2, (await users.GetLoginsAsync(user)).Count);
            Assert.Contains(await users.GetClaimsAsync(user), claim => claim.Type == "department" && claim.Value == "support");
            Assert.Equal(userId, Assert.Single(await users.GetUsersForClaimAsync(new Claim("permission", "read"))).Id);
            Assert.Equal("updated", await users.GetAuthenticationTokenAsync(user, "github", "access_token"));
            Assert.Equal("refresh", await users.GetAuthenticationTokenAsync(user, "github", "refresh_token"));
            Assert.Equal("other-provider", await users.GetAuthenticationTokenAsync(user, "oidc", "access_token"));
            AssertSucceeded(await users.RemoveAuthenticationTokenAsync(user, "github", "refresh_token"));
            Assert.Null(await users.GetAuthenticationTokenAsync(user, "github", "refresh_token"));
            AssertSucceeded(await users.DeleteAsync(user));
        }

        await using (var scope = sample.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.False(await context.Users.AnyAsync(sample.Token));
            Assert.False(await context.UserClaims.AnyAsync(sample.Token));
            Assert.False(await context.UserLogins.AnyAsync(sample.Token));
            Assert.False(await context.UserTokens.AnyAsync(sample.Token));
            Assert.False(await context.UserRoles.AnyAsync(sample.Token));
            var role = await context.Roles.SingleAsync(sample.Token);
            Assert.Equal(roleId, role.Id);
            context.Roles.Remove(role);
            await context.SaveChangesAsync(sample.Token);
            Assert.False(await context.RoleClaims.AnyAsync(sample.Token));
        }
    }

    /// <summary>验证绕过用户管理器时唯一用户名仍由数据库约束保证且整批写入回滚。</summary>
    [Fact]
    public async Task SaveChanges_WithDuplicateNormalizedUserName_RollsBackWholeBatch()
    {
        await using var sample = await IdentitySample.CreateAsync();
        await using var scope = sample.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        context.Users.AddRange(
            new IdentityUser { UserName = "first", NormalizedUserName = "DUPLICATE" },
            new IdentityUser { UserName = "second", NormalizedUserName = "DUPLICATE" });
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(sample.Token));
        context.ChangeTracker.Clear();
        Assert.False(await context.Users.AnyAsync(sample.Token));
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var created = await CreateUserAsync(users);
        var duplicate = await users.CreateAsync(new IdentityUser { UserName = Email.ToUpperInvariant(), Email = Email }, Password);
        Assert.False(duplicate.Succeeded);
        Assert.Contains(duplicate.Errors, error => error.Code == "DuplicateUserName");
        Assert.Equal(created.Id, (await users.FindByNameAsync(Email.ToUpperInvariant()))!.Id);
        Assert.Equal(1, await context.Users.CountAsync(sample.Token));
    }

    /// <summary>验证官方 EF 用户存储将过期的并发戳报告为失败并保留赢家的数据。</summary>
    [Fact]
    public async Task UserManager_WithStaleConcurrencyStamp_ReturnsConcurrencyFailure()
    {
        await using var sample = await IdentitySample.CreateAsync();
        await using var winnerScope = sample.CreateScope();
        var winnerUsers = winnerScope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var winner = await CreateUserAsync(winnerUsers);
        await using var staleScope = sample.CreateScope();
        var staleUsers = staleScope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var stale = await staleUsers.FindByIdAsync(winner.Id);
        Assert.NotNull(stale);
        AssertSucceeded(await winnerUsers.SetPhoneNumberAsync(winner, "winner"));
        var update = await staleUsers.SetPhoneNumberAsync(stale, "stale");
        Assert.False(update.Succeeded);
        Assert.Contains(update.Errors, error => error.Code == "ConcurrencyFailure");
        await using var verifyScope = sample.CreateScope();
        var persisted = await verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Users.SingleAsync(sample.Token);
        Assert.Equal("winner", persisted.PhoneNumber);
        Assert.Equal(winner.ConcurrencyStamp, persisted.ConcurrencyStamp);
    }

    /// <summary>验证确认邮件后的身份令牌、认证器密钥和只能使用一次的恢复码跨作用域持久化。</summary>
    [Fact]
    public async Task IdentityTokens_WithAuthenticatorAndRecoveryCodes_PersistsSecurityState()
    {
        await using var sample = await IdentitySample.CreateAsync();
        string authenticator;
        string recoveryCode;
        await using (var scope = sample.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var user = await CreateUserAsync(users);
            var emailToken = await users.GenerateTwoFactorTokenAsync(user, TokenOptions.DefaultEmailProvider);
            Assert.True(await users.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultEmailProvider, emailToken));
            Assert.False(await users.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultEmailProvider, "invalid"));
            AssertSucceeded(await users.ResetAuthenticatorKeyAsync(user));
            authenticator = (await users.GetAuthenticatorKeyAsync(user))!;
            Assert.False(string.IsNullOrEmpty(authenticator));
            AssertSucceeded(await users.SetTwoFactorEnabledAsync(user, true));
            var codes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 4);
            Assert.NotNull(codes);
            var generated = codes.ToArray();
            Assert.Equal(4, generated.Length);
            Assert.Equal(4, generated.Distinct(StringComparer.Ordinal).Count());
            recoveryCode = generated[0];
        }

        await using (var scope = sample.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var user = (await users.FindByNameAsync(Email))!;
            Assert.True(await users.GetTwoFactorEnabledAsync(user));
            Assert.Equal(authenticator, await users.GetAuthenticatorKeyAsync(user));
            Assert.Equal(4, await users.CountRecoveryCodesAsync(user));
            AssertSucceeded(await users.RedeemTwoFactorRecoveryCodeAsync(user, recoveryCode));
            Assert.Equal(3, await users.CountRecoveryCodesAsync(user));
            Assert.False((await users.RedeemTwoFactorRecoveryCodeAsync(user, recoveryCode)).Succeeded);
            Assert.Equal(3, await users.CountRecoveryCodesAsync(user));
        }
    }

    /// <summary>验证官方失败计数触发锁定，时间值重载后保留且锁定可被解除。</summary>
    [Fact]
    public async Task IdentityLockout_WithRepeatedFailures_PersistsLockoutAndUnlocks()
    {
        await using var sample = await IdentitySample.CreateAsync();
        await using (var scope = sample.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var user = await CreateUserAsync(users);
            var attempts = users.Options.Lockout.MaxFailedAccessAttempts;
            Assert.InRange(attempts, 1, 10);
            // 输入上限为十次，整个测试另受两分钟取消令牌限制。
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                sample.Token.ThrowIfCancellationRequested();
                AssertSucceeded(await users.AccessFailedAsync(user));
            }

            Assert.True(await users.IsLockedOutAsync(user));
            Assert.True((await users.GetLockoutEndDateAsync(user)) > DateTimeOffset.UtcNow);
        }

        await using (var scope = sample.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var user = (await users.FindByNameAsync(Email))!;
            Assert.True(await users.IsLockedOutAsync(user));
            using var lockedLogin = await sample.LoginAsync(Email, Password);
            Assert.Equal(HttpStatusCode.Redirect, lockedLogin.StatusCode);
            Assert.Contains("Lockout", lockedLogin.Headers.Location!.OriginalString, StringComparison.Ordinal);
            AssertSucceeded(await users.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddMinutes(-1)));
            Assert.False(await users.IsLockedOutAsync(user));
        }

        using var unlockedLogin = await sample.LoginAsync(Email, Password);
        Assert.Equal(HttpStatusCode.Redirect, unlockedLogin.StatusCode);
        Assert.DoesNotContain("Lockout", unlockedLogin.Headers.Location!.OriginalString, StringComparison.Ordinal);
    }

    /// <summary>验证修改安全戳后真实登录 Cookie 在下一次官方验证中失效。</summary>
    [Fact]
    public async Task HttpIdentity_WithUpdatedSecurityStamp_RejectsExistingCookie()
    {
        await using var sample = await IdentitySample.CreateAsync();
        await using (var scope = sample.CreateScope())
        {
            await CreateUserAsync(scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>());
        }

        using var login = await sample.LoginAsync(Email, Password);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        using var before = await sample.GetAsync("/Identity/Account/Manage");
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        await using (var scope = sample.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var user = (await users.FindByNameAsync(Email))!;
            AssertSucceeded(await users.UpdateSecurityStampAsync(user));
        }

        using var after = await sample.GetAsync("/Identity/Account/Manage");
        Assert.Equal(HttpStatusCode.Redirect, after.StatusCode);
        Assert.Contains("/Identity/Account/Login", after.Headers.Location!.OriginalString, StringComparison.Ordinal);
    }

    /// <summary>验证官方邮箱修改和确认页面同时更新邮箱和登录名，且旧登录名失效。</summary>
    [Fact]
    public async Task HttpIdentity_WithConfirmedEmailChange_UpdatesEmailAndLoginName()
    {
        const string newEmail = "changed@example.test";
        await using var sample = await IdentitySample.CreateAsync();
        await sample.RegisterAndConfirmAsync(Email, Password);
        using var login = await sample.LoginAsync(Email, Password);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        using var request = await sample.PostAsync("/Identity/Account/Manage/Email?handler=ChangeEmail", new Dictionary<string, string>
        {
            ["Input.NewEmail"] = newEmail
        });
        Assert.Equal(HttpStatusCode.Redirect, request.StatusCode);
        using var confirmation = await sample.GetAsync(sample.Emails.LastLink(newEmail));
        Assert.Equal(HttpStatusCode.OK, confirmation.StatusCode);
        await using (var scope = sample.CreateScope())
        {
            var user = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Users.SingleAsync(sample.Token);
            Assert.Equal(newEmail, user.Email);
            Assert.Equal(newEmail, user.UserName);
            Assert.Equal(newEmail.ToUpperInvariant(), user.NormalizedEmail);
            Assert.Equal(newEmail.ToUpperInvariant(), user.NormalizedUserName);
            Assert.True(user.EmailConfirmed);
        }

        using var logout = await sample.PostFromPageAsync("/", "/Identity/Account/Logout?returnUrl=%2F", new Dictionary<string, string>());
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        using var oldLogin = await sample.LoginAsync(Email, Password);
        Assert.Equal(HttpStatusCode.OK, oldLogin.StatusCode);
        using var newLogin = await sample.LoginAsync(newEmail, Password);
        Assert.Equal(HttpStatusCode.Redirect, newLogin.StatusCode);
        using var profile = await sample.GetAsync("/Identity/Account/Manage");
        Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
    }

    /// <summary>验证官方个人资料 JSON 下载和需要正确密码的账户删除及退出。</summary>
    [Fact]
    public async Task HttpIdentity_WithPersonalDataDownloadAndDeletion_ExportsAndRemovesAccount()
    {
        await using var sample = await IdentitySample.CreateAsync();
        await using (var scope = sample.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var user = await CreateUserAsync(users);
            AssertSucceeded(await users.SetPhoneNumberAsync(user, "+8613800138000"));
            AssertSucceeded(await users.AddClaimAsync(user, new Claim("department", "engineering")));
            AssertSucceeded(await users.AddLoginAsync(user, new UserLoginInfo("github", "personal-data-key", "GitHub")));
            AssertSucceeded(await users.SetAuthenticationTokenAsync(user, "github", "access_token", "test-token"));
        }

        using var login = await sample.LoginAsync(Email, Password);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        using var download = await sample.PostFromPageAsync("/Identity/Account/Manage/PersonalData", "/Identity/Account/Manage/DownloadPersonalData", new Dictionary<string, string>());
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/json", download.Content.Headers.ContentType!.MediaType);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);
        using (var json = JsonDocument.Parse(await download.Content.ReadAsStringAsync(sample.Token)))
        {
            Assert.Equal(Email, json.RootElement.GetProperty("Email").GetString());
            Assert.Equal("+8613800138000", json.RootElement.GetProperty("PhoneNumber").GetString());
            Assert.Equal("personal-data-key", json.RootElement.GetProperty("github external login provider key").GetString());
        }

        using var rejected = await sample.PostAsync("/Identity/Account/Manage/DeletePersonalData", new Dictionary<string, string> { ["Input.Password"] = "wrong-password" });
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        await using (var scope = sample.CreateScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Users.AnyAsync(sample.Token));
        }

        using var deleted = await sample.PostAsync("/Identity/Account/Manage/DeletePersonalData", new Dictionary<string, string> { ["Input.Password"] = Password });
        Assert.Equal(HttpStatusCode.Redirect, deleted.StatusCode);
        using var profile = await sample.GetAsync("/Identity/Account/Manage");
        Assert.Equal(HttpStatusCode.Redirect, profile.StatusCode);
        await using (var scope = sample.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.False(await context.Users.AnyAsync(sample.Token));
            Assert.False(await context.UserClaims.AnyAsync(sample.Token));
            Assert.False(await context.UserLogins.AnyAsync(sample.Token));
            Assert.False(await context.UserTokens.AnyAsync(sample.Token));
        }
    }

    /// <summary>验证二步登录恢复码只能使用一次，官方页面可重新生成恢复码并关闭二步验证。</summary>
    [Fact]
    public async Task HttpIdentity_WithRecoveryCodeLogin_RegeneratesCodesAndDisablesTwoFactor()
    {
        await using var sample = await IdentitySample.CreateAsync();
        string[] codes;
        await using (var scope = sample.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var user = await CreateUserAsync(users);
            AssertSucceeded(await users.ResetAuthenticatorKeyAsync(user));
            AssertSucceeded(await users.SetTwoFactorEnabledAsync(user, true));
            codes = (await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 2))!.ToArray();
            Assert.Equal(2, codes.Length);
        }

        using var login = await sample.LoginAsync(Email, Password);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Contains("LoginWith2fa", login.Headers.Location!.OriginalString, StringComparison.Ordinal);
        using var recovered = await sample.PostAsync("/Identity/Account/LoginWithRecoveryCode", new Dictionary<string, string> { ["Input.RecoveryCode"] = codes[0] });
        Assert.Equal(HttpStatusCode.Redirect, recovered.StatusCode);
        using var profile = await sample.GetAsync("/Identity/Account/Manage");
        Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
        using var logout = await sample.PostFromPageAsync("/", "/Identity/Account/Logout?returnUrl=%2F", new Dictionary<string, string>());
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        using var secondLogin = await sample.LoginAsync(Email, Password);
        Assert.Contains("LoginWith2fa", secondLogin.Headers.Location!.OriginalString, StringComparison.Ordinal);
        using var reused = await sample.PostAsync("/Identity/Account/LoginWithRecoveryCode", new Dictionary<string, string> { ["Input.RecoveryCode"] = codes[0] });
        Assert.Equal(HttpStatusCode.OK, reused.StatusCode);
        Assert.Contains("Invalid recovery code", await reused.Content.ReadAsStringAsync(sample.Token), StringComparison.OrdinalIgnoreCase);
        using var spare = await sample.PostAsync("/Identity/Account/LoginWithRecoveryCode", new Dictionary<string, string> { ["Input.RecoveryCode"] = codes[1] });
        Assert.Equal(HttpStatusCode.Redirect, spare.StatusCode);
        using var regenerated = await sample.PostAsync("/Identity/Account/Manage/GenerateRecoveryCodes", new Dictionary<string, string>());
        Assert.Equal(HttpStatusCode.Redirect, regenerated.StatusCode);
        using var showCodes = await sample.GetAsync(regenerated.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.OK, showCodes.StatusCode);
        var html = await showCodes.Content.ReadAsStringAsync(sample.Token);
        var renderedCodes = Regex.Matches(html, "<code class=\"recovery-code\">([^<]+)</code>", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        Assert.Equal(10, renderedCodes.Count);
        Assert.Equal(10, renderedCodes.Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal).Count());
        await using (var scope = sample.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            Assert.Equal(10, await users.CountRecoveryCodesAsync((await users.FindByNameAsync(Email))!));
        }

        using var disabled = await sample.PostAsync("/Identity/Account/Manage/Disable2fa", new Dictionary<string, string>());
        Assert.Equal(HttpStatusCode.Redirect, disabled.StatusCode);
        await using (var scope = sample.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            Assert.False(await users.GetTwoFactorEnabledAsync((await users.FindByNameAsync(Email))!));
        }

        using var invalidatedCookie = await sample.GetAsync("/Identity/Account/Manage");
        Assert.Equal(HttpStatusCode.Redirect, invalidatedCookie.StatusCode);
        Assert.Contains("/Identity/Account/Login", invalidatedCookie.Headers.Location!.OriginalString, StringComparison.Ordinal);
        using var ordinaryLogin = await sample.LoginAsync(Email, Password);
        Assert.Equal(HttpStatusCode.Redirect, ordinaryLogin.StatusCode);
        Assert.DoesNotContain("LoginWith2fa", ordinaryLogin.Headers.Location!.OriginalString, StringComparison.Ordinal);
        using var signedIn = await sample.GetAsync("/Identity/Account/Manage");
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
    }

    /// <summary>验证官方验证器设置和重启后的真实 TOTP 二步登录。</summary>
    [Fact]
    public async Task HttpIdentity_WithAuthenticatorSetup_AcceptsTotpAfterHostRestart()
    {
        await using var sample = await IdentitySample.CreateAsync();
        await sample.RegisterAndConfirmAsync(Email, Password);
        using var login = await sample.LoginAsync(Email, Password);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        using var setup = await sample.GetAsync("/Identity/Account/Manage/EnableAuthenticator");
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        string key;
        await using (var scope = sample.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var user = (await users.FindByNameAsync(Email))!;
            key = (await users.GetAuthenticatorKeyAsync(user))!;
            Assert.False(await users.GetTwoFactorEnabledAsync(user));
        }

        // 首次生成验证器密钥会更新安全戳；测试将校验间隔设为零，因此重新取得有效 Cookie。
        using var refreshedLogin = await sample.LoginAsync(Email, Password);
        Assert.Equal(HttpStatusCode.Redirect, refreshedLogin.StatusCode);
        using var enabled = await sample.PostAsync("/Identity/Account/Manage/EnableAuthenticator", new Dictionary<string, string>
        {
            ["Input.Code"] = GenerateAuthenticatorCode(key, sample.Token)
        });
        Assert.Equal(HttpStatusCode.Redirect, enabled.StatusCode);
        await sample.RestartAsync();
        await using (var scope = sample.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var user = (await users.FindByNameAsync(Email))!;
            Assert.True(await users.GetTwoFactorEnabledAsync(user));
            Assert.Equal(key, await users.GetAuthenticatorKeyAsync(user));
            Assert.Equal(10, await users.CountRecoveryCodesAsync(user));
        }

        using var passwordLogin = await sample.LoginAsync(Email, Password);
        Assert.Equal(HttpStatusCode.Redirect, passwordLogin.StatusCode);
        Assert.Contains("LoginWith2fa", passwordLogin.Headers.Location!.OriginalString, StringComparison.Ordinal);
        using var authenticated = await sample.PostAsync("/Identity/Account/LoginWith2fa", new Dictionary<string, string>
        {
            ["Input.TwoFactorCode"] = GenerateAuthenticatorCode(key, sample.Token),
            ["Input.RememberMe"] = "true",
            ["Input.RememberMachine"] = "false"
        });
        Assert.Equal(HttpStatusCode.Redirect, authenticated.StatusCode);
        using var profile = await sample.GetAsync("/Identity/Account/Manage");
        Assert.Equal(HttpStatusCode.OK, profile.StatusCode);
    }

    /// <summary>验证原注册页面的超长邮箱由数据库拒绝，而 256 字符边界仍可注册和登录。</summary>
    [Fact]
    public async Task HttpIdentity_WithEmailLengthBoundary_RejectsOverlongRegistration()
    {
        await using var sample = await IdentitySample.CreateAsync();
        var boundaryEmail = new string('a', 243) + "@example.test";
        Assert.Equal(256, boundaryEmail.Length);
        using var rejected = await sample.PostAsync("/Identity/Account/Register", new Dictionary<string, string>
        {
            ["Input.Email"] = "a" + boundaryEmail,
            ["Input.Password"] = Password,
            ["Input.ConfirmPassword"] = Password
        });
        Assert.Equal(HttpStatusCode.InternalServerError, rejected.StatusCode);
        await using (var scope = sample.CreateScope())
        {
            Assert.False(await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Users.AnyAsync(sample.Token));
        }

        await sample.RegisterAndConfirmAsync(boundaryEmail, Password);
        using var login = await sample.LoginAsync(boundaryEmail, Password);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        await using var verification = sample.CreateScope();
        Assert.Equal(boundaryEmail, (await verification.ServiceProvider.GetRequiredService<ApplicationDbContext>().Users.SingleAsync(sample.Token)).Email);
    }

    /// <summary>验证样例的外部登录和令牌复合键保留 128 字符数据库限长。</summary>
    [Fact]
    public async Task IdentityStores_WithKeyLengthBoundary_RejectsOverlongLoginAndTokenKeys()
    {
        await using var sample = await IdentitySample.CreateAsync();
        var boundaryKey = new string('k', 128);
        await using (var scope = sample.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var user = await CreateUserAsync(users);
            AssertSucceeded(await users.AddLoginAsync(user, new UserLoginInfo(boundaryKey, boundaryKey, "Boundary")));
            AssertSucceeded(await users.SetAuthenticationTokenAsync(user, boundaryKey, boundaryKey, "boundary-token"));
        }

        await using (var scope = sample.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var user = (await users.FindByNameAsync(Email))!;
            var failure = await Assert.ThrowsAsync<DbUpdateException>(() => users.AddLoginAsync(user, new UserLoginInfo("github", boundaryKey + "k", "Overlong")));
            Assert.Equal(TableConstraintException.CheckViolation, Assert.IsType<TableConstraintException>(failure.InnerException).ErrorCode);
        }

        await using (var scope = sample.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var user = (await users.FindByNameAsync(Email))!;
            var failure = await Assert.ThrowsAsync<DbUpdateException>(() => users.SetAuthenticationTokenAsync(user, "github", boundaryKey + "k", "overlong-token"));
            Assert.Equal(TableConstraintException.CheckViolation, Assert.IsType<TableConstraintException>(failure.InnerException).ErrorCode);
        }

        await sample.RestartAsync();
        await using var verification = sample.CreateScope();
        var restoredUsers = verification.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var restored = (await restoredUsers.FindByNameAsync(Email))!;
        Assert.Equal(boundaryKey, Assert.Single(await restoredUsers.GetLoginsAsync(restored)).ProviderKey);
        Assert.Equal("boundary-token", await restoredUsers.GetAuthenticationTokenAsync(restored, boundaryKey, boundaryKey));
        Assert.Null(await restoredUsers.GetAuthenticationTokenAsync(restored, "github", boundaryKey + "k"));
    }

    private static string GenerateAuthenticatorCode(string authenticatorKey, CancellationToken cancellationToken)
    {
        // 默认 Identity 密钥固定为 32 个 Base32 字符，解码最多 32 次且受测试截止时间约束。
        Assert.Equal(32, authenticatorKey.Length);
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var key = new byte[20];
        var buffer = 0;
        var bits = 0;
        var index = 0;
        foreach (var character in authenticatorKey)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var digit = alphabet.IndexOf(character);
            Assert.InRange(digit, 0, 31);
            buffer = (buffer << 5) | digit;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                key[index++] = (byte)(buffer >> bits);
                buffer &= (1 << bits) - 1;
            }
        }

        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        var hash = HMACSHA1.HashData(key, counter);
        var offset = hash[^1] & 15;
        var value = BinaryPrimitives.ReadInt32BigEndian(hash.AsSpan(offset, 4)) & int.MaxValue;
        return (value % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<IdentityUser> CreateUserAsync(UserManager<IdentityUser> users)
    {
        var user = new IdentityUser { UserName = Email, Email = Email, EmailConfirmed = true };
        AssertSucceeded(await users.CreateAsync(user, Password));
        return user;
    }

    private static void AssertSucceeded(IdentityResult result) => Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(error => $"{error.Code}: {error.Description}")));
}
