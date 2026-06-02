namespace SonnetDB.Tables;

/// <summary>
/// 关系表二级索引声明。
/// </summary>
/// <param name="Name">索引名，在单表内唯一。</param>
/// <param name="Columns">索引列名，按声明顺序排列。</param>
/// <param name="IsUnique">是否为唯一索引。</param>
/// <param name="CreatedAtUtcTicks">创建时间 UTC ticks。</param>
/// <param name="JsonPath">JSON path 索引路径；仅当索引列是单个 JSON 列时可用。</param>
public sealed record TableIndex(
    string Name,
    IReadOnlyList<string> Columns,
    bool IsUnique,
    long CreatedAtUtcTicks,
    string? JsonPath = null);

/// <summary>
/// 创建或加载索引时使用的轻量声明。
/// </summary>
/// <param name="Name">索引名。</param>
/// <param name="Columns">索引列名。</param>
/// <param name="IsUnique">是否为唯一索引。</param>
/// <param name="CreatedAtUtcTicks">创建时间 UTC ticks；为 0 时使用当前时间。</param>
/// <param name="JsonPath">JSON path 索引路径；仅当索引列是单个 JSON 列时可用。</param>
public sealed record TableIndexDefinition(
    string Name,
    IReadOnlyList<string> Columns,
    bool IsUnique,
    long CreatedAtUtcTicks = 0,
    string? JsonPath = null);
