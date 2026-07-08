using SonnetDB.Auth;
using SonnetDB.Configuration;

namespace SonnetDB.Coap;

/// <summary>
/// CoAP/DTLS 请求携带的 SonnetDB 调用方身份。
/// </summary>
internal sealed class SonnetCoapPrincipal
{
    private SonnetCoapPrincipal(string? userName, bool isSuperuser, string? role)
    {
        UserName = userName;
        IsSuperuser = isSuperuser;
        Role = role;
    }

    /// <summary>动态用户名称；静态 token 角色凭据为空。</summary>
    public string? UserName { get; }

    /// <summary>动态用户是否为超级用户。</summary>
    public bool IsSuperuser { get; }

    /// <summary>静态 token 角色；动态用户凭据为空。</summary>
    public string? Role { get; }

    /// <summary>
    /// 从动态用户认证结果创建 CoAP 身份。
    /// </summary>
    public static SonnetCoapPrincipal ForUser(AuthenticatedUser user)
        => new(user.UserName, user.IsSuperuser, null);

    /// <summary>
    /// 从静态 token 角色创建 CoAP 身份。
    /// </summary>
    public static SonnetCoapPrincipal ForRole(string role)
        => new(null, false, role);

    /// <summary>
    /// 计算当前身份对指定数据库的有效权限。
    /// </summary>
    public DatabasePermission GetEffectivePermission(GrantsStore grants, string database)
    {
        if (UserName is not null)
            return IsSuperuser ? DatabasePermission.Admin : grants.GetPermission(UserName, database);

        return Role switch
        {
            ServerRoles.Admin => DatabasePermission.Admin,
            ServerRoles.ReadWrite => DatabasePermission.Write,
            ServerRoles.ReadOnly => DatabasePermission.Read,
            _ => DatabasePermission.None,
        };
    }

    /// <summary>
    /// 判断当前身份是否满足指定数据库权限。
    /// </summary>
    public bool HasPermission(GrantsStore grants, string database, DatabasePermission required)
        => GetEffectivePermission(grants, database) >= required;
}
