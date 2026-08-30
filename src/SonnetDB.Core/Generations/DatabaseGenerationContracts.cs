namespace SonnetDB.Generations;

/// <summary>
/// 数据库 generation 中受统一发布和回收合同管理的资源类型。
/// </summary>
public enum DatabaseGenerationResourceKind
{
    /// <summary>KV keyspace。</summary>
    KvKeyspace = 1,

    /// <summary>Document collection 主数据及其派生索引。</summary>
    DocumentCollection = 2,

    /// <summary>属于 Document collection 的 FullText 派生索引。</summary>
    DocumentFullTextIndex = 3,
}

/// <summary>
/// 描述一个由数据库 generation 独占的持久化资源。
/// </summary>
/// <remarks>
/// 同一物理资源在清理前只能属于一个 generation。FullText 资源必须与其父 Document collection
/// 一同声明，以便 checkpoint、查询绑定和 retired 清理共享同一个生命周期。资源发布后由调用方视为
/// 不可变；后续构建必须使用新的物理名称，不能改写 active 或 retired generation 的已有资源。
/// </remarks>
public sealed class DatabaseGenerationResource
{
    /// <summary>
    /// 创建 generation 资源描述。
    /// </summary>
    /// <param name="role">上层查询使用的稳定逻辑角色。</param>
    /// <param name="kind">资源类型。</param>
    /// <param name="name">KV keyspace、Document collection 或 FullText index 名称。</param>
    /// <param name="parentName">FullText index 所属的 Document collection；其他类型必须为空。</param>
    public DatabaseGenerationResource(
        string role,
        DatabaseGenerationResourceKind kind,
        string name,
        string? parentName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (kind == DatabaseGenerationResourceKind.DocumentFullTextIndex)
            ArgumentException.ThrowIfNullOrWhiteSpace(parentName);
        else if (parentName is not null)
            throw new ArgumentException("只有 Document FullText 资源可以指定父资源。", nameof(parentName));

        Role = role;
        Kind = kind;
        Name = name;
        ParentName = parentName;
    }

    /// <summary>上层查询使用的稳定逻辑角色。</summary>
    public string Role { get; }

    /// <summary>资源类型。</summary>
    public DatabaseGenerationResourceKind Kind { get; }

    /// <summary>物理资源名称。</summary>
    public string Name { get; }

    /// <summary>FullText index 所属的 Document collection；其他类型为空。</summary>
    public string? ParentName { get; }
}

/// <summary>
/// 提交一个已经写完的多模型 generation 所需的参数。
/// </summary>
public sealed class DatabaseGenerationPublishRequest
{
    /// <summary>generation stream 名称，例如工作区或派生视图的稳定标识。</summary>
    public required string Stream { get; init; }

    /// <summary>上层提供的 generation 身份，例如源码 revision 或构建摘要。</summary>
    public required string GenerationId { get; init; }

    /// <summary>
    /// 调用方预期的当前 active revision；首个 generation 使用 0。
    /// SonnetDB 只发布紧邻该值的下一个 revision。
    /// </summary>
    public long ExpectedRevision { get; init; }

    /// <summary>本 generation 独占的 KV、Document 与 FullText 资源。</summary>
    public required IReadOnlyList<DatabaseGenerationResource> Resources { get; init; }
}

/// <summary>
/// 一个已经原子发布、可由查询租用的数据库 generation。
/// </summary>
public sealed class DatabaseGeneration
{
    internal DatabaseGeneration(
        string stream,
        string generationId,
        long revision,
        DateTimeOffset publishedAtUtc,
        IReadOnlyList<DatabaseGenerationResource> resources)
    {
        Stream = stream;
        GenerationId = generationId;
        Revision = revision;
        PublishedAtUtc = publishedAtUtc;
        Resources = Array.AsReadOnly(resources.ToArray());
    }

    /// <summary>generation stream 名称。</summary>
    public string Stream { get; }

    /// <summary>上层提供的 generation 身份。</summary>
    public string GenerationId { get; }

    /// <summary>SonnetDB 分配的单调连续 revision。</summary>
    public long Revision { get; }

    /// <summary>原子发布完成的 UTC 时间。</summary>
    public DateTimeOffset PublishedAtUtc { get; }

    /// <summary>该 generation 固定绑定的资源快照。</summary>
    public IReadOnlyList<DatabaseGenerationResource> Resources { get; }
}

/// <summary>
/// 选择 retired generation 清理候选的选项。
/// </summary>
public sealed class DatabaseGenerationCleanupOptions
{
    /// <summary>
    /// 创建按发布时间选择候选的清理选项。
    /// </summary>
    /// <param name="publishedBeforeUtc">
    /// 发布时间 cutoff；内部归一为 UTC，只有 <c>PublishedAtUtc &lt;= cutoff</c> 的 retired generation 才可清理。
    /// </param>
    public DatabaseGenerationCleanupOptions(DateTimeOffset publishedBeforeUtc)
    {
        PublishedBeforeUtc = publishedBeforeUtc.ToUniversalTime();
    }

    /// <summary>
    /// 已归一为 UTC 的 inclusive 发布时间 cutoff。
    /// </summary>
    public DateTimeOffset PublishedBeforeUtc { get; }
}

/// <summary>
/// 一次 retired generation 清理的结果。
/// </summary>
public sealed class DatabaseGenerationCleanupResult
{
    internal DatabaseGenerationCleanupResult(
        IReadOnlyList<long> removedRevisions,
        IReadOnlyList<long> deferredRevisions,
        IReadOnlyList<long> retentionDeferredRevisions)
    {
        RemovedRevisions = Array.AsReadOnly(removedRevisions.ToArray());
        DeferredRevisions = Array.AsReadOnly(deferredRevisions.ToArray());
        RetentionDeferredRevisions = Array.AsReadOnly(retentionDeferredRevisions.ToArray());
    }

    /// <summary>本轮已删除持久化资源和 catalog 记录的 revision。</summary>
    public IReadOnlyList<long> RemovedRevisions { get; }

    /// <summary>因仍有 query lease 而延后清理的 revision。</summary>
    public IReadOnlyList<long> DeferredRevisions { get; }

    /// <summary>因发布时间晚于本轮 inclusive cutoff 而保留的 retired revision。</summary>
    public IReadOnlyList<long> RetentionDeferredRevisions { get; }
}

/// <summary>
/// 数据库 generation 合同的稳定错误码。
/// </summary>
public static class DatabaseGenerationErrorCodes
{
    /// <summary>指定 stream 尚无 active generation。</summary>
    public const string NoActiveGeneration = "generation_not_active";

    /// <summary>发布时观察到的 active revision 与调用方预期不一致。</summary>
    public const string RevisionConflict = "generation_revision_conflict";

    /// <summary>指定 generation revision 不存在或已经清理。</summary>
    public const string RevisionUnavailable = "generation_revision_unavailable";

    /// <summary>generation 身份或受管资源与现有 catalog 冲突。</summary>
    public const string ResourceConflict = "generation_resource_conflict";

    /// <summary>generation 资源不存在、不完整或不满足发布前一致性检查。</summary>
    public const string ResourceInvalid = "generation_resource_invalid";

    /// <summary>cursor 格式或完整性校验失败。</summary>
    public const string CursorInvalid = "generation_cursor_invalid";

    /// <summary>cursor 不属于当前查询形状或 generation stream。</summary>
    public const string CursorMismatch = "generation_cursor_mismatch";

    /// <summary>cursor 绑定的 generation revision 与当前 lease 不一致。</summary>
    public const string CursorStale = "generation_cursor_stale";
}

/// <summary>
/// 数据库 generation 生命周期操作失败时抛出的异常。
/// </summary>
public sealed class DatabaseGenerationException : InvalidOperationException
{
    /// <summary>
    /// 创建 generation 异常。
    /// </summary>
    /// <param name="code">稳定机器错误码。</param>
    /// <param name="message">错误说明。</param>
    public DatabaseGenerationException(string code, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    /// <summary>
    /// 创建包含内部异常的 generation 异常。
    /// </summary>
    /// <param name="code">稳定机器错误码。</param>
    /// <param name="message">错误说明。</param>
    /// <param name="innerException">内部异常。</param>
    public DatabaseGenerationException(string code, string message, Exception innerException)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentNullException.ThrowIfNull(innerException);
        Code = code;
    }

    /// <summary>稳定机器错误码。</summary>
    public string Code { get; }
}
