namespace SonnetDB.Contracts;

/// <summary>已脱敏的生效配置项；修改方式描述实际支持的生命周期。</summary>
/// <param name="Path">配置项路径。</param>
/// <param name="Value">显示值，秘密只显示配置状态。</param>
/// <param name="Redacted">是否已隐藏原始内容。</param>
/// <param name="ChangeMode">修改方式。</param>
public sealed record ConfigurationEntry(string Path, string Value, bool Redacted, string ChangeMode);

/// <summary>数据库或整个实例的只读生效配置快照。</summary>
/// <param name="Scope">授权范围。</param>
/// <param name="Source">配置来源和语义。</param>
/// <param name="CapturedAtUtc">采集时间。</param>
/// <param name="Entries">已脱敏的配置项。</param>
public sealed record ConfigurationSnapshotResponse(
    string Scope,
    string Source,
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<ConfigurationEntry> Entries);
