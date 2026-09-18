using System.Text.Json.Serialization;

namespace SonnetDB.SemanticContent;

/// <summary>相互隔离的人员外观/动作模型用途。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PersonAppearanceTask>))]
public enum PersonAppearanceTask
{
    /// <summary>人员外观重识别。</summary>
    ReIdentification,
    /// <summary>步态片段相似查询。</summary>
    Gait,
    /// <summary>姿态相似查询。</summary>
    Pose,
    /// <summary>动作片段相似查询。</summary>
    Action,
}

/// <summary>专业用途、输入布局与完整 embedding 合同；同维度不表示可混用。</summary>
/// <param name="Task">独立用途。</param><param name="Embedding">完整模型合同。</param>
/// <param name="InputLayoutRevision">裁剪、骨架布局或序列采样规范的不可变版本。</param>
/// <param name="WindowMilliseconds">时序窗口毫秒数；静态图片为零。</param>
public sealed record PersonAppearanceProfile(PersonAppearanceTask Task, EmbeddingProfile Embedding,
    string InputLayoutRevision, int WindowMilliseconds = 0);

/// <summary>外部模型生成、已授权给当前查询的有限候选。</summary>
/// <param name="Target">有版本来源的派生目标。</param><param name="DetectorProfile">对应 detector。</param>
/// <param name="Profile">专业用途完整 profile。</param><param name="Embedding">外部模型生成的向量。</param>
/// <param name="Purpose">登记该候选时批准的用途，必须与查询用途相等。</param>
/// <param name="ExpiresAtUtc">候选保留期限；过期候选不交付。</param>
public sealed record PersonAppearanceCandidate(VisualDerivedTarget Target, VisualDetectorProfile DetectorProfile,
    PersonAppearanceProfile Profile, IReadOnlyList<float> Embedding, string Purpose, DateTimeOffset ExpiresAtUtc);

/// <summary>只带目标来源和距离的人员外观查询结果。</summary>
/// <param name="CandidateId">原对象 bucket/key/version/ETag、detector profile 与局部目标 ID 的稳定 SHA-256。</param>
/// <param name="Target">命中目标。</param><param name="Distance">越小越相似；不是身份结论或概率。</param>
public sealed record PersonAppearanceHit(string CandidateId, VisualDerivedTarget Target, float Distance);

/// <summary>不含向量、对象键或候选身份的查询审计。</summary>
/// <param name="OperationId">开始和终态共用的操作标识。</param><param name="Actor">调用身份。</param>
/// <param name="Purpose">已批准用途。</param><param name="Capability">独立能力名称。</param>
/// <param name="Outcome">denied、started、succeeded 或 failed。</param><param name="CandidateCount">检查候选数。</param>
/// <param name="HitCount">结果数。</param><param name="AtUtc">发生时刻。</param>
public sealed record PersonAppearanceAudit(string OperationId, string Actor, string Purpose, string Capability,
    string Outcome, int CandidateCount, int HitCount, DateTimeOffset AtUtc);

/// <summary>默认关闭的单一专业能力配置。</summary>
public sealed record PersonAppearanceSearchOptions
{
    /// <summary>创建默认关闭的配置。</summary>
    public PersonAppearanceSearchOptions() { }
    /// <summary>显式启用当前 profile 的单一用途。</summary>
    public bool Enabled { get; init; }
    /// <summary>允许的用途名称；最多 32 项。</summary>
    public IReadOnlyList<string> AllowedPurposes { get; init; } = [];
    /// <summary>候选上界，1..10000。</summary>
    public int MaxCandidates { get; init; } = 1000;
    /// <summary>单次向量标量总量上界，1..10000000。</summary>
    public int MaxVectorValues { get; init; } = 1_000_000;
    /// <summary>协作取消期限，最多一分钟。</summary>
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>人员外观/步态/姿态/动作的 source-generated JSON 合同。</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(PersonAppearanceProfile))]
[JsonSerializable(typeof(PersonAppearanceCandidate))]
[JsonSerializable(typeof(PersonAppearanceHit))]
[JsonSerializable(typeof(PersonAppearanceAudit))]
[JsonSerializable(typeof(PersonAppearanceSearchOptions))]
public sealed partial class PersonAppearanceJsonContext : JsonSerializerContext;
