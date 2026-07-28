using SonnetDB.Sql;
using SonnetDB.Sql.Ast;

namespace SonnetDB.Views;

/// <summary>
/// 一个持久化物化视图的不可变定义和刷新元数据。
/// </summary>
public sealed class MaterializedViewDefinition
{
    private MaterializedViewDefinition(
        Guid storageId,
        string name,
        string definitionSql,
        SelectStatement query,
        IReadOnlyList<string> dependencies,
        long definitionVersion,
        long createdAtUtcTicks,
        MaterializedViewRefreshStatus status,
        long activeGeneration,
        long rowCount,
        long lastRefreshAtUtcTicks,
        long lastSuccessfulRefreshAtUtcTicks,
        string? lastError)
    {
        StorageId = storageId;
        Name = name;
        DefinitionSql = definitionSql;
        Query = query;
        Dependencies = dependencies;
        DefinitionVersion = definitionVersion;
        CreatedAtUtcTicks = createdAtUtcTicks;
        Status = status;
        ActiveGeneration = activeGeneration;
        RowCount = rowCount;
        LastRefreshAtUtcTicks = lastRefreshAtUtcTicks;
        LastSuccessfulRefreshAtUtcTicks = lastSuccessfulRefreshAtUtcTicks;
        LastError = lastError;
    }

    /// <summary>用于隔离派生存储目录的稳定标识符。</summary>
    public Guid StorageId { get; }

    /// <summary>物化视图名称（区分大小写）。</summary>
    public string Name { get; }

    /// <summary>不含 <c>CREATE MATERIALIZED VIEW ... AS</c> 前缀的 SELECT 定义文本。</summary>
    public string DefinitionSql { get; }

    /// <summary>从 <see cref="DefinitionSql"/> 解析得到的 SELECT AST。</summary>
    public SelectStatement Query { get; }

    /// <summary>物化视图直接引用的数据源名称，按字典序排列。</summary>
    public IReadOnlyList<string> Dependencies { get; }

    /// <summary>定义版本；首版固定为 1，未来定义变更只允许递增。</summary>
    public long DefinitionVersion { get; }

    /// <summary>物化视图创建时间（UTC ticks）。</summary>
    public long CreatedAtUtcTicks { get; }

    /// <summary>最近一次刷新生命周期状态。</summary>
    public MaterializedViewRefreshStatus Status { get; }

    /// <summary>当前可读的物理代际；为 0 表示尚无成功刷新结果。</summary>
    public long ActiveGeneration { get; }

    /// <summary>当前可读代际的行数。</summary>
    public long RowCount { get; }

    /// <summary>最近一次刷新尝试结束时间（UTC ticks）；为 0 表示尚无记录。</summary>
    public long LastRefreshAtUtcTicks { get; }

    /// <summary>最近一次成功刷新时间（UTC ticks）；为 0 表示尚未成功刷新。</summary>
    public long LastSuccessfulRefreshAtUtcTicks { get; }

    /// <summary>最近一次刷新错误；最近一次刷新成功或尚未刷新时为 <c>null</c>。</summary>
    public string? LastError { get; }

    /// <summary>
    /// 从 SELECT SQL 创建并校验新的物化视图定义。
    /// </summary>
    /// <param name="name">物化视图名称。</param>
    /// <param name="definitionSql">不含 CREATE 前缀的 SELECT SQL。</param>
    /// <param name="createdAtUtcTicks">创建时间；为 0 时使用当前 UTC 时间。</param>
    /// <returns>尚未刷新的物化视图定义。</returns>
    public static MaterializedViewDefinition Create(
        string name,
        string definitionSql,
        long createdAtUtcTicks = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionSql);
        var query = SqlParser.Parse(definitionSql) as SelectStatement
            ?? throw new ArgumentException("物化视图定义必须是 SELECT 语句。", nameof(definitionSql));
        return Create(name, definitionSql, query, createdAtUtcTicks);
    }

    internal static MaterializedViewDefinition Create(
        string name,
        string definitionSql,
        SelectStatement query,
        long createdAtUtcTicks = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionSql);
        ArgumentNullException.ThrowIfNull(query);
        long createdAt = createdAtUtcTicks == 0 ? DateTime.UtcNow.Ticks : createdAtUtcTicks;
        ValidateTicks(createdAt, nameof(createdAtUtcTicks));

        var analysis = ViewDependencyCollector.Analyze(query);
        if (analysis.HasParameters)
            throw new ArgumentException("物化视图定义不能包含参数占位符。", nameof(definitionSql));

        return new MaterializedViewDefinition(
            Guid.NewGuid(),
            name,
            definitionSql.Trim(),
            query,
            analysis.Dependencies,
            definitionVersion: 1,
            createdAt,
            MaterializedViewRefreshStatus.Uninitialized,
            activeGeneration: 0,
            rowCount: 0,
            lastRefreshAtUtcTicks: 0,
            lastSuccessfulRefreshAtUtcTicks: 0,
            lastError: null);
    }

    internal static MaterializedViewDefinition Restore(
        Guid storageId,
        string name,
        string definitionSql,
        IReadOnlyList<string> persistedDependencies,
        long definitionVersion,
        long createdAtUtcTicks,
        MaterializedViewRefreshStatus status,
        long activeGeneration,
        long rowCount,
        long lastRefreshAtUtcTicks,
        long lastSuccessfulRefreshAtUtcTicks,
        string? lastError)
    {
        if (storageId == Guid.Empty)
            throw new InvalidDataException("MaterializedViewCatalog: storage id 不能为空。");
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionSql);
        ArgumentNullException.ThrowIfNull(persistedDependencies);
        if (definitionVersion <= 0)
            throw new InvalidDataException("MaterializedViewCatalog: definition version 必须为正数。");
        ValidateTicks(createdAtUtcTicks, nameof(createdAtUtcTicks));
        ValidateOptionalTicks(lastRefreshAtUtcTicks, nameof(lastRefreshAtUtcTicks));
        ValidateOptionalTicks(lastSuccessfulRefreshAtUtcTicks, nameof(lastSuccessfulRefreshAtUtcTicks));
        if (!Enum.IsDefined(status))
            throw new InvalidDataException($"MaterializedViewCatalog: 未知刷新状态 {(byte)status}。");
        if (activeGeneration < 0 || rowCount < 0)
            throw new InvalidDataException("MaterializedViewCatalog: generation 和 row count 不能为负数。");
        if (activeGeneration == 0 && rowCount != 0)
            throw new InvalidDataException("MaterializedViewCatalog: 无活动代际时 row count 必须为 0。");

        var query = SqlParser.Parse(definitionSql) as SelectStatement
            ?? throw new InvalidDataException("MaterializedViewCatalog: 定义不是 SELECT。");
        var analysis = ViewDependencyCollector.Analyze(query);
        if (analysis.HasParameters)
            throw new InvalidDataException("MaterializedViewCatalog: 定义包含参数占位符。");
        if (!analysis.Dependencies.SequenceEqual(persistedDependencies, StringComparer.Ordinal))
            throw new InvalidDataException("MaterializedViewCatalog: 持久化依赖与 SELECT 定义不一致。");

        return new MaterializedViewDefinition(
            storageId,
            name,
            definitionSql.Trim(),
            query,
            analysis.Dependencies,
            definitionVersion,
            createdAtUtcTicks,
            status,
            activeGeneration,
            rowCount,
            lastRefreshAtUtcTicks,
            lastSuccessfulRefreshAtUtcTicks,
            lastError);
    }

    internal MaterializedViewDefinition WithRefreshStarted()
        => Copy(status: MaterializedViewRefreshStatus.Refreshing, lastError: null);

    internal MaterializedViewDefinition WithRefreshSucceeded(long generation, long rowCount, long completedAtUtcTicks)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        ArgumentOutOfRangeException.ThrowIfNegative(rowCount);
        ValidateTicks(completedAtUtcTicks, nameof(completedAtUtcTicks));
        return Copy(
            status: MaterializedViewRefreshStatus.Ready,
            activeGeneration: generation,
            rowCount: rowCount,
            lastRefreshAtUtcTicks: completedAtUtcTicks,
            lastSuccessfulRefreshAtUtcTicks: completedAtUtcTicks,
            lastError: null);
    }

    internal MaterializedViewDefinition WithRefreshFailed(long completedAtUtcTicks, string error)
    {
        ValidateTicks(completedAtUtcTicks, nameof(completedAtUtcTicks));
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        return Copy(
            status: MaterializedViewRefreshStatus.Failed,
            lastRefreshAtUtcTicks: completedAtUtcTicks,
            lastError: error);
    }

    private MaterializedViewDefinition Copy(
        MaterializedViewRefreshStatus? status = null,
        long? activeGeneration = null,
        long? rowCount = null,
        long? lastRefreshAtUtcTicks = null,
        long? lastSuccessfulRefreshAtUtcTicks = null,
        string? lastError = null)
        => new(
            StorageId,
            Name,
            DefinitionSql,
            Query,
            Dependencies,
            DefinitionVersion,
            CreatedAtUtcTicks,
            status ?? Status,
            activeGeneration ?? ActiveGeneration,
            rowCount ?? RowCount,
            lastRefreshAtUtcTicks ?? LastRefreshAtUtcTicks,
            lastSuccessfulRefreshAtUtcTicks ?? LastSuccessfulRefreshAtUtcTicks,
            lastError);

    private static void ValidateTicks(long value, string parameterName)
    {
        if (value <= 0 || value > DateTime.MaxValue.Ticks)
            throw new ArgumentOutOfRangeException(parameterName);
    }

    private static void ValidateOptionalTicks(long value, string parameterName)
    {
        if (value < 0 || value > DateTime.MaxValue.Ticks)
            throw new ArgumentOutOfRangeException(parameterName);
    }
}
