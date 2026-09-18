namespace SonnetDB.SemanticContent;

/// <summary>由可信宿主从认证主体和已批准用途构造的生物特征访问上下文。</summary>
public sealed record BiometricAccessContext
{
    /// <summary>认证主体标识；不能直接信任远程请求提供的同名字段。</summary>
    public string Actor { get; init; } = string.Empty;

    /// <summary>已经获得批准的用途标识。</summary>
    public string Purpose { get; init; } = string.Empty;
}

/// <summary>相互独立的生物特征权限；普通数据库读写权限不隐含其中任何一项。</summary>
public enum BiometricOperation
{
    /// <summary>登记派生模板。</summary>
    Enroll,
    /// <summary>对明确指定的模板执行一对一验证。</summary>
    Verify,
    /// <summary>查询一对多候选。</summary>
    Search,
    /// <summary>导出模板和向量。</summary>
    Export,
    /// <summary>删除模板。</summary>
    Delete,
    /// <summary>读取脱敏访问审计。</summary>
    ReadAudit,
    /// <summary>删除超过保留期限的模板。</summary>
    ApplyRetention,
}

/// <summary>可信宿主注入的生物特征授权边界；默认实现应拒绝未知能力、主体、用途和操作。</summary>
public interface IBiometricAuthorizer
{
    /// <summary>判断认证主体能否以该用途执行指定能力的操作。</summary>
    /// <param name="context">由宿主认证后构造的访问上下文。</param>
    /// <param name="capability">独立能力标识，例如 face、reid、gait、pose 或 action。</param>
    /// <param name="operation">必须显式授予的操作。</param>
    /// <returns>仅在完整授权时返回 true；异常也会阻止操作。</returns>
    bool IsAuthorized(BiometricAccessContext context, string capability, BiometricOperation operation);
}
