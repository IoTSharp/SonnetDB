namespace SonnetDB.Parity.Runner;

/// <summary>
/// 后端选择器：读取 <c>BACKEND</c> 环境变量（逗号分隔，大小写不敏感）决定本次 run
/// 要跑哪些后端。未设置时默认 <c>sonnetdb,postgres</c>。
/// </summary>
/// <remarks>
/// PR #127 的冒烟测试在进程内同时实例化 SonnetDB 与 Postgres 两个适配器并内联 diff，
/// 因此并不严格依赖此选择器；但保留它以与 docker-compose 的 <c>harness</c> 服务对齐，
/// 并为 PR #128+ 的跨后端分跑模式做前向兼容。
/// </remarks>
public static class BackendSelector
{
    private static readonly IReadOnlySet<string> Default =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "sonnetdb", "postgres" };

    /// <summary>当前选中的后端名集合。</summary>
    /// <returns>大小写不敏感的后端名集合。</returns>
    public static IReadOnlySet<string> Selected()
    {
        var raw = Environment.GetEnvironmentVariable("BACKEND");
        if (string.IsNullOrWhiteSpace(raw))
            return Default;

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            set.Add(part);
        return set.Count == 0 ? Default : set;
    }

    /// <summary>判断指定后端是否被选中。</summary>
    /// <param name="backend">后端名（大小写不敏感）。</param>
    /// <returns>选中返回 true。</returns>
    public static bool Includes(string backend) => Selected().Contains(backend);
}
