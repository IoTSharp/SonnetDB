using System.Text.Json.Serialization;

namespace SonnetDB.Streaming;

/// <summary>持久订阅任务目录的容量与操作边界。</summary>
public sealed record FileStreamingTaskCatalogOptions
{
    /// <summary>目录最多保存的任务数。</summary>
    public int MaxEntries { get; init; } = 10_000;

    /// <summary>单页最多返回的任务数。</summary>
    public int MaxPageSize { get; init; } = 256;

    /// <summary>目录 JSON 文件的最大字节数。</summary>
    public int MaxCatalogBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>目录文件操作的超时毫秒数。</summary>
    public int OperationTimeoutMilliseconds { get; init; } = 10_000;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxEntries, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxEntries, 10_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxPageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxPageSize, MaxEntries);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxCatalogBytes, 1_024);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxCatalogBytes, 64 * 1024 * 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(OperationTimeoutMilliseconds, 50);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(OperationTimeoutMilliseconds, 300_000);
    }
}

/// <summary>目录中注册的持久订阅任务定义。</summary>
/// <param name="TaskId">目录中的稳定任务标识。</param>
/// <param name="DirectoryName">相对于目录根的订阅目录名称。</param>
/// <param name="SubscriptionDefinition">交给文件订阅的事件流定义。</param>
/// <param name="SubscriptionOptions">交给文件订阅的容量和操作边界。</param>
public sealed record StreamingTaskDefinition(
    string TaskId,
    string DirectoryName,
    StreamingSubscriptionDefinition SubscriptionDefinition,
    FileStreamingSubscriptionOptions SubscriptionOptions)
{
    /// <summary>订阅定义的便捷别名。</summary>
    [JsonIgnore]
    public StreamingSubscriptionDefinition Definition => SubscriptionDefinition;

    /// <summary>订阅边界的便捷别名。</summary>
    [JsonIgnore]
    public FileStreamingSubscriptionOptions Options => SubscriptionOptions;

    /// <summary>创建并校验任务定义。</summary>
    /// <param name="taskId">稳定任务标识。</param>
    /// <param name="directoryName">相对于目录根的目录名。</param>
    /// <param name="subscriptionDefinition">订阅定义。</param>
    /// <param name="subscriptionOptions">订阅边界；为空时使用默认值。</param>
    /// <returns>已校验的任务定义。</returns>
    public static StreamingTaskDefinition Create(
        string taskId,
        string directoryName,
        StreamingSubscriptionDefinition subscriptionDefinition,
        FileStreamingSubscriptionOptions? subscriptionOptions = null)
    {
        var result = new StreamingTaskDefinition(
            taskId,
            directoryName,
            subscriptionDefinition,
            subscriptionOptions ?? new FileStreamingSubscriptionOptions());
        result.Validate();
        return result;
    }

    /// <summary>校验任务身份、路径和订阅边界。</summary>
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(TaskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(DirectoryName);
        if (TaskId.Length > 256)
            throw new ArgumentOutOfRangeException(nameof(TaskId), "任务标识过长。");
        if (DirectoryName.Length > 512)
            throw new ArgumentOutOfRangeException(nameof(DirectoryName), "订阅目录名过长。");
        SubscriptionDefinition.Validate();
        SubscriptionOptions.Validate();

        if (DirectoryName == "." || DirectoryName == ".."
            || Path.IsPathRooted(DirectoryName)
            || DirectoryName.Contains('\0')
            || DirectoryName.Contains(':'))
        {
            throw new ArgumentException("订阅目录名必须是目录根下的相对路径。", nameof(DirectoryName));
        }

        string normalized = DirectoryName.Replace('\\', '/');
        string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(static segment => segment is "." or ".."))
            throw new ArgumentException("订阅目录名不能越过目录根。", nameof(DirectoryName));
    }
}

/// <summary>目录中一页任务定义及其有界续页游标。</summary>
/// <param name="Revision">生成此页时的目录 revision。</param>
/// <param name="Items">按任务标识排序的任务定义。</param>
/// <param name="ContinuationToken">下一页游标；没有下一页时为空。</param>
public sealed record StreamingTaskCatalogPage(
    long Revision,
    IReadOnlyList<StreamingTaskDefinition> Items,
    string? ContinuationToken);

/// <summary>目录文件的源生成 JSON 元数据。</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(StreamingTaskCatalogState))]
[JsonSerializable(typeof(StreamingTaskCatalogEnvelope))]
[JsonSerializable(typeof(StreamingTaskDefinition))]
[JsonSerializable(typeof(FileStreamingTaskCatalogOptions))]
internal sealed partial class StreamingTaskCatalogJsonContext : JsonSerializerContext;

internal sealed record StreamingTaskCatalogState(
    [property: JsonRequired] int FormatVersion,
    [property: JsonRequired] long Revision,
    [property: JsonRequired] IReadOnlyList<StreamingTaskDefinition> Entries);

internal sealed record StreamingTaskCatalogEnvelope(
    [property: JsonRequired] StreamingTaskCatalogState State,
    [property: JsonRequired] string Sha256);
