namespace SonnetDB.Generations;

/// <summary>
/// 在一次查询及其全部分页期间固定 active generation 的进程内租约。
/// </summary>
/// <remarks>
/// 调用方必须在查询、取消或异常结束时释放租约。租约存活期间，对应 retired generation 不会被清理。
/// </remarks>
public sealed class DatabaseGenerationQueryLease : IDisposable
{
    private DatabaseGenerationManager? _owner;

    internal DatabaseGenerationQueryLease(
        DatabaseGenerationManager owner,
        DatabaseGeneration generation)
    {
        _owner = owner;
        Generation = generation;
    }

    /// <summary>本次查询固定使用的 generation。</summary>
    public DatabaseGeneration Generation { get; }

    /// <summary>
    /// 按逻辑角色和类型取得本 generation 的唯一资源。
    /// </summary>
    /// <param name="role">资源逻辑角色。</param>
    /// <param name="kind">预期资源类型。</param>
    /// <returns>匹配的资源描述。</returns>
    /// <exception cref="InvalidOperationException">角色不存在或类型不匹配。</exception>
    public DatabaseGenerationResource GetRequiredResource(
        string role,
        DatabaseGenerationResourceKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        ObjectDisposedException.ThrowIf(_owner is null, this);
        DatabaseGenerationResource? resource = Generation.Resources.SingleOrDefault(
            resource => string.Equals(resource.Role, role, StringComparison.Ordinal));
        if (resource is null)
            throw new InvalidOperationException($"generation resource role '{role}' 不存在。");
        if (resource.Kind != kind)
        {
            throw new InvalidOperationException(
                $"generation resource role '{role}' 的类型为 {resource.Kind}，不是 {kind}。");
        }
        return resource;
    }

    /// <summary>
    /// 创建同时绑定 stream、generation identity、revision 和查询指纹的 opaque cursor。
    /// </summary>
    /// <param name="queryFingerprint">上层查询形状的稳定指纹。</param>
    /// <param name="continuationState">上层分页状态；SonnetDB 只负责完整性保护和 generation 绑定。</param>
    /// <returns>可在同一租约后续分页中使用的 opaque cursor。</returns>
    public string CreateCursor(string queryFingerprint, ReadOnlySpan<byte> continuationState)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queryFingerprint);
        ObjectDisposedException.ThrowIf(_owner is null, this);
        return DatabaseGenerationCursorCodec.Encode(Generation, queryFingerprint, continuationState);
    }

    /// <summary>
    /// 校验 cursor 属于本租约固定的 generation 和查询形状，并返回上层分页状态。
    /// </summary>
    /// <param name="cursor">opaque cursor。</param>
    /// <param name="queryFingerprint">当前查询形状的稳定指纹。</param>
    /// <returns>cursor 中的上层分页状态副本。</returns>
    /// <exception cref="DatabaseGenerationException">cursor 无效、查询不匹配或 revision 已失效。</exception>
    public byte[] ReadCursor(string cursor, string queryFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cursor);
        ArgumentException.ThrowIfNullOrWhiteSpace(queryFingerprint);
        ObjectDisposedException.ThrowIf(_owner is null, this);
        return DatabaseGenerationCursorCodec.Decode(cursor, Generation, queryFingerprint);
    }

    /// <summary>释放查询租约，使 retired generation 可以进入后续清理。</summary>
    public void Dispose()
    {
        DatabaseGenerationManager? owner = Interlocked.Exchange(ref _owner, null);
        owner?.Release(Generation.Stream, Generation.Revision);
    }
}
