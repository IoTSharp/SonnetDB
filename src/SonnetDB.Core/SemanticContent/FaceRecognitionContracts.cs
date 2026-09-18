namespace SonnetDB.SemanticContent;

/// <summary>人脸向量空间及检测来源的不可变合同；本地确定性计算不执行模型推理。</summary>
public sealed record FaceRecognitionProfile
{
    /// <summary>人脸专用 embedding 合同，仅支持余弦度量。</summary>
    public EmbeddingProfile Embedding { get; init; } = new();

    /// <summary>生成区域的检测模型合同。</summary>
    public VisualDetectorProfile Detector { get; init; } = new();
}

/// <summary>人脸能力门禁及单次处理预算。</summary>
public sealed record FaceRecognitionOptions
{
    /// <summary>显式启用登记、验证、搜索和导出；默认关闭，关闭后仍允许授权删除和审计。</summary>
    public bool Enabled { get; init; }

    /// <summary>允许的用途标识，默认为空；授权器还须独立检查主体与操作。</summary>
    public IReadOnlyList<string> AllowedPurposes { get; init; } = Array.Empty<string>();

    /// <summary>单个图库每个用途最多的模板数，默认 256，范围 1..10000；序列化总量另限 16 MiB。</summary>
    public int MaxTemplates { get; init; } = 256;

    /// <summary>模板最长保留期限，默认 30 天，最高 365 天。</summary>
    public TimeSpan MaxRetention { get; init; } = TimeSpan.FromDays(30);

    /// <summary>单次操作含锁等待的协作期限，默认 5 秒，最高 1 分钟。</summary>
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>可重建的人脸模板；原始媒体继续由目标的原对象引用唯一定位。</summary>
public sealed record FaceRecognitionTemplate
{
    /// <summary>图库内稳定模板标识。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>用途内的主体标识；建议由宿主提供假名标识。</summary>
    public string SubjectId { get; init; } = string.Empty;

    /// <summary>模板所属的批准用途；查询不会跨用途匹配。</summary>
    public string Purpose { get; init; } = string.Empty;

    /// <summary>完整人脸 embedding profile 的 ID。</summary>
    public string ProfileId { get; init; } = string.Empty;

    /// <summary>检测目标与固定原对象来源。</summary>
    public VisualDerivedTarget Target { get; init; } = new();

    /// <summary>外部受治理模型已经生成的有限、非零向量。</summary>
    public float[] Vector { get; init; } = [];

    /// <summary>UTC 到期时间；到期后立即排除于验证、搜索及导出。</summary>
    public DateTimeOffset ExpiresUtc { get; init; }
}

/// <summary>一次人脸查询输入，不包含可由调用方自行声明的权限。</summary>
public sealed record FaceRecognitionProbe
{
    /// <summary>生成输入向量的不可变 embedding profile ID。</summary>
    public string ProfileId { get; init; } = string.Empty;

    /// <summary>有限、非零的预计算向量。</summary>
    public float[] Vector { get; init; } = [];
}

/// <summary>一对一的阈值比较结果；不等同于经评测证明的真实身份结论。</summary>
public sealed record FaceVerificationResult
{
    /// <summary>被明确指定的模板 ID。</summary>
    public string TemplateId { get; init; } = string.Empty;

    /// <summary>余弦相似度，范围 -1..1。</summary>
    public double Similarity { get; init; }

    /// <summary>本次显式阈值。</summary>
    public double Threshold { get; init; }

    /// <summary>相似度是否达到阈值。</summary>
    public bool IsMatch { get; init; }
}

/// <summary>一对多的候选，排序及分数不作自动身份认定。</summary>
public sealed record FaceRecognitionCandidate
{
    /// <summary>候选模板标识。</summary>
    public string TemplateId { get; init; } = string.Empty;

    /// <summary>图库内用途限定的主体标识。</summary>
    public string SubjectId { get; init; } = string.Empty;

    /// <summary>余弦相似度，值越大越相近。</summary>
    public double Similarity { get; init; }
}

/// <summary>不含原始主体、用途、媒体路径、向量和分数的持久访问审计。</summary>
public sealed record FaceRecognitionAuditEntry
{
    /// <summary>单次操作的唯一审计标识。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>开始时间。</summary>
    public DateTimeOffset StartedUtc { get; init; }

    /// <summary>认证主体的 SHA-256 摘要。</summary>
    public string ActorHash { get; init; } = string.Empty;

    /// <summary>批准用途的 SHA-256 摘要。</summary>
    public string PurposeHash { get; init; } = string.Empty;

    /// <summary>独立操作。</summary>
    public BiometricOperation Operation { get; init; }

    /// <summary>started、succeeded、denied、failed、cancelled 或 unknown。</summary>
    public string Outcome { get; init; } = string.Empty;

    /// <summary>返回或修改的记录数；失败为零。</summary>
    public int AffectedCount { get; init; }
}
