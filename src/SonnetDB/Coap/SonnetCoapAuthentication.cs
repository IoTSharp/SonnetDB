using System.Net;
using SonnetDB.Auth;
using SonnetDB.Configuration;

namespace SonnetDB.Coap;

/// <summary>
/// CoAP 请求身份解析工具，统一处理 Uri-Query 中的 SonnetDB token。
/// </summary>
internal static class SonnetCoapAuthentication
{
    /// <summary>
    /// 从 CoAP Uri-Query 解析 SonnetDB token 并映射为调用方身份。
    /// </summary>
    /// <param name="queries">CoAP Uri-Query 选项集合。</param>
    /// <param name="options">服务器配置。</param>
    /// <param name="users">动态用户存储。</param>
    /// <param name="principal">认证成功后的调用方身份。</param>
    /// <returns>认证是否成功。</returns>
    public static bool TryAuthenticate(
        IReadOnlyList<string> queries,
        ServerOptions options,
        UserStore users,
        out SonnetCoapPrincipal principal)
    {
        principal = null!;
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(users);

        if (!TryGetToken(queries, out var token))
            return false;

        if (options.Tokens.TryGetValue(token, out var role))
        {
            principal = SonnetCoapPrincipal.ForRole(role);
            return true;
        }

        if (users.TryAuthenticate(token, out var user))
        {
            principal = SonnetCoapPrincipal.ForUser(user);
            return true;
        }

        return false;
    }

    private static bool TryGetToken(IReadOnlyList<string> queries, out string token)
    {
        token = string.Empty;
        if (queries is null)
            return false;

        foreach (var query in queries)
        {
            var decoded = WebUtility.UrlDecode(query);
            if (string.IsNullOrWhiteSpace(decoded))
                continue;

            var split = decoded.IndexOf('=', StringComparison.Ordinal);
            if (split <= 0)
                continue;

            var name = decoded[..split].Trim();
            var value = decoded[(split + 1)..].Trim();
            if (string.IsNullOrEmpty(value))
                continue;

            if (string.Equals(name, "token", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "access_token", StringComparison.OrdinalIgnoreCase))
            {
                token = value;
                return true;
            }

            if (!string.Equals(name, "authorization", StringComparison.OrdinalIgnoreCase))
                continue;

            if (value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                token = value["Bearer ".Length..].Trim();
                return !string.IsNullOrEmpty(token);
            }

            if (value.StartsWith("Token ", StringComparison.OrdinalIgnoreCase))
            {
                token = value["Token ".Length..].Trim();
                return !string.IsNullOrEmpty(token);
            }
        }

        return false;
    }
}
