namespace SonnetDB.Hosting;

/// <summary>
/// 容器首启引导配置。环境变量使用 <c>SONNETDB_</c> 前缀注入后由配置系统剥离前缀再绑定。
/// </summary>
internal sealed class EnvironmentBootstrapOptions
{
    /// <summary>
    /// 初始超级用户名。
    /// </summary>
    public string? User { get; set; }

    /// <summary>
    /// 初始超级用户密码。
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// 可选的初始数据库名称。
    /// </summary>
    public string? DB { get; set; }
}
