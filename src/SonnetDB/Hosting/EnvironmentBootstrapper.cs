using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SonnetDB.Auth;

namespace SonnetDB.Hosting;

/// <summary>
/// 执行容器环境变量驱动的首启初始化。
/// </summary>
internal static class EnvironmentBootstrapper
{
    /// <summary>
    /// 在服务器尚未初始化时，根据环境变量创建初始超级用户和可选默认数据库。
    /// </summary>
    /// <param name="app">已构建的 Web 应用。</param>
    /// <remarks>
    /// 配置入口已调用 <c>AddEnvironmentVariables(prefix:"SONNETDB_")</c>，因此环境变量前缀被剥离后对应
    /// 配置键 <c>USER</c> / <c>PASSWORD</c> / <c>DB</c>。任何失败只记日志、不抛出，保证容器重启幂等。
    /// </remarks>
    public static void Run(WebApplication app)
    {
        var configuration = app.Services.GetRequiredService<IConfiguration>();
        var bootstrapOptions = new EnvironmentBootstrapOptions();
        configuration.Bind(bootstrapOptions);
        var user = bootstrapOptions.User;
        var password = bootstrapOptions.Password;
        var database = bootstrapOptions.DB;

        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SonnetDB.Bootstrap");

        // 用户名 + 密码是引导的最小充分条件；缺任一则不做（回退到手动 setup / 静态 token）。
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(password))
            return;

        var users = app.Services.GetRequiredService<UserStore>();
        var grants = app.Services.GetRequiredService<GrantsStore>();
        var registry = app.Services.GetRequiredService<TsdbRegistry>();
        var installation = app.Services.GetRequiredService<InstallationStore>();

        // 幂等门控：仅在从未初始化时执行。
        if (!installation.GetStatus(users.Count, registry.Count).NeedsSetup)
            return;

        try
        {
            users.CreateUser(user, password, isSuperuser: true);

            if (!string.IsNullOrWhiteSpace(database))
            {
                registry.TryCreate(database, out _);
                // 超级用户本已具备全库权限，此授权仅为让 SHOW GRANTS 显式可见。
                grants.Grant(user, database, DatabasePermission.Admin);
            }

            var (_, tokenId) = users.IssueToken(user);
            installation.CompleteInitialization(
                InstallationStore.GetSuggestedServerId(),
                organization: "SonnetDB",
                adminUserName: user,
                initialTokenId: tokenId,
                userCount: users.Count,
                databaseCount: registry.Count);

            logger.LogInformation(
                "环境变量引导完成：已创建超级用户 '{User}'{Db}。",
                user,
                string.IsNullOrWhiteSpace(database) ? string.Empty : $" 与默认数据库 '{database}'");
        }
        catch (Exception ex)
        {
            // 幂等：用户已存在 / 库已存在等异常只记日志，不阻断启动。
            logger.LogWarning(ex, "环境变量引导未完成（可能已初始化或参数非法）：{Message}", ex.Message);
        }
    }
}
