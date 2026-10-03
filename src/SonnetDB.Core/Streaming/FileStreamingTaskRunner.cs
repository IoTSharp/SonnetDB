using System.Diagnostics;

namespace SonnetDB.Streaming;

/// <summary>把持久任务目录中已登记的任务接到真实文件订阅的有界自动投递入口。</summary>
/// <remarks>
/// <para>目录和订阅均由调用方打开、独占管理并释放。此入口从不创建、重开或关闭订阅，缺失状态不能被重置为空基线。</para>
/// <para>每次运行先取得任务定义快照并校验订阅路径、定义与容量。随后更新或删除目录条目只影响下一次运行，不撤销当前回调或切换其目标。</para>
/// <para>宿主不能并行直接消费、确认、重置或隔离同一订阅；超时或取消后必须等待 <see cref="HandlerCompletion"/>，再释放订阅及回调使用的其它资源。</para>
/// <para>文件路径必须没有符号链接或 reparse 别名。检查期间及运行期间宿主必须独占存储目录的布局，不能外部移动、删除或替换文件。</para>
/// <para>目录预检遵循目录操作超时，路径预检另有两秒和 256 层上限；投递的数量、墙钟、失败重试与默认保留耗尽批次的策略完全复用 dispatcher。</para>
/// </remarks>
public sealed class FileStreamingTaskRunner
{
    private readonly FileStreamingTaskCatalog _catalog;
    private readonly string _taskId;
    private readonly FileStreamingSubscription _subscription;
    private readonly FileStreamingSubscriptionDispatcher _dispatcher;

    /// <summary>绑定调用方持有的目录、任务标识和已打开订阅；构造时不运行也不创建持久状态。</summary>
    /// <param name="catalog">调用方持有的持久任务目录。</param>
    /// <param name="taskId">目录中已登记的任务标识。</param>
    /// <param name="subscription">调用方已打开并独占消费的订阅。</param>
    /// <param name="options">有界投递选项；省略时默认保留耗尽批次，不隐式授权死信隔离。</param>
    public FileStreamingTaskRunner(
        FileStreamingTaskCatalog catalog,
        string taskId,
        FileStreamingSubscription subscription,
        FileStreamingDispatcherOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentNullException.ThrowIfNull(subscription);
        _catalog = catalog;
        _taskId = taskId;
        _subscription = subscription;
        _dispatcher = new FileStreamingSubscriptionDispatcher(subscription, options);
    }

    /// <summary>最近处理函数的收敛任务；包含超时或取消后同订阅执行租约的释放，处理异常已被观察。</summary>
    /// <remarks>不协作的函数一直不返回时不会收敛；宿主不得在此前释放函数仍使用的订阅或业务资源。</remarks>
    public Task HandlerCompletion => _dispatcher.HandlerCompletion;

    /// <summary>校验已登记任务与已打开订阅的一致性，然后串行运行一轮有界投递。</summary>
    /// <param name="handler">业务处理函数；正常完成后才允许持久确认，副作用幂等由宿主负责。</param>
    /// <param name="cancellationToken">取消预检、本次运行及处理函数的令牌。</param>
    /// <returns>dispatcher 的停止原因和成功返回的确认、隔离及投递统计。</returns>
    /// <exception cref="KeyNotFoundException">本次预检时任务未登记或已删除。</exception>
    /// <exception cref="InvalidDataException">已打开订阅与登记身份不匹配、状态缺失或路径含符号链接别名。</exception>
    /// <exception cref="InvalidOperationException">同一订阅已有运行或尚未结束的处理函数。</exception>
    public async ValueTask<FileStreamingDispatcherResult> RunAsync(
        Func<StreamingDeliveryBatch, CancellationToken, ValueTask> handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        cancellationToken.ThrowIfCancellationRequested();
        StreamingTaskDefinition definition = await _catalog.GetAsync(_taskId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"任务标识 '{_taskId}' 未登记。");
        ValidateSubscription(definition, cancellationToken);
        return await _dispatcher.RunAsync(handler, cancellationToken).ConfigureAwait(false);
    }

    private void ValidateSubscription(StreamingTaskDefinition definition, CancellationToken cancellationToken)
    {
        definition.Validate();
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_catalog.RootPath));
        string expectedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(root, definition.DirectoryName)));
        string actualPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_subscription.DirectoryPath));
        string rootPrefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!expectedPath.StartsWith(rootPrefix, comparison)
            || !string.Equals(expectedPath, actualPath, comparison)
            || definition.SubscriptionDefinition != _subscription.Definition
            || definition.SubscriptionOptions != _subscription.Options)
        {
            throw new InvalidDataException("持久任务与已打开订阅的目录、定义或容量身份不匹配。");
        }

        string statePath = Path.Combine(actualPath, "subscription.json");
        // 只检查调用方已打开的对象，绝不调用可能初始化丢失状态的 OpenAsync。
        if (!File.Exists(statePath))
            throw new InvalidDataException("持久任务订阅状态缺失，不能创建新基线。");
        var elapsed = Stopwatch.StartNew();
        string? candidate = statePath;
        // 每轮沿父路径前进；同时限制层数和墙钟，取消不依赖下一次文件 I/O。
        for (int depth = 0; depth < 256 && candidate is not null; depth++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (elapsed.Elapsed >= TimeSpan.FromSeconds(2))
                throw new TimeoutException("持久任务路径身份预检超时。");
            if ((File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("持久任务路径包含符号链接或 reparse 别名。");
            candidate = Path.GetDirectoryName(candidate);
        }
        if (candidate is not null)
            throw new InvalidDataException("持久任务路径超过预检层数上限。");
        cancellationToken.ThrowIfCancellationRequested();
    }
}
