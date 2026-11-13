using System.Buffers;
using System.Diagnostics;
using System.Text.Encodings.Web;
using SonnetDB.Exceptions;

namespace SonnetDB.FullText;

/// <summary>
/// 全文检索的统一资源上限。调用方可以降低默认上限，但不能通过请求提高硬上限。
/// </summary>
public sealed record FullTextSearchBudgetOptions
{
    /// <summary>默认最多扫描文档数。</summary>
    public const int DefaultMaxDocuments = 100_000;

    /// <summary>默认最多读取的文档字节数。</summary>
    public const long DefaultMaxDocumentBytes = 64L * 1024 * 1024;

    /// <summary>默认最多检查的 posting 次数。</summary>
    public const long DefaultMaxPostingVisits = 1_000_000;

    /// <summary>默认最多计费的工作单元数。</summary>
    public const long DefaultMaxWorkUnits = 10_000_000;

    /// <summary>默认最大 UTF-8 响应字节数。</summary>
    public const long DefaultMaxResponseBytes = 4L * 1024 * 1024;

    /// <summary>默认端到端期限（毫秒）。</summary>
    public const int DefaultTimeoutMilliseconds = 10_000;

    /// <summary>默认查询文本字符数上限。</summary>
    public const int DefaultMaxQueryLength = 4_096;

    /// <summary>最多扫描文档数；为空使用默认值。</summary>
    public int? MaxDocuments { get; init; }

    /// <summary>最多读取的文档字节数；为空使用默认值。</summary>
    public long? MaxDocumentBytes { get; init; }

    /// <summary>最多检查的 posting 次数；为空使用默认值。</summary>
    public long? MaxPostingVisits { get; init; }

    /// <summary>最多计费的工作单元数；为空使用默认值。</summary>
    public long? MaxWorkUnits { get; init; }

    /// <summary>最大 UTF-8 响应字节数；为空使用默认值。</summary>
    public long? MaxResponseBytes { get; init; }

    /// <summary>端到端期限（毫秒）；为空使用默认值。</summary>
    public int? TimeoutMilliseconds { get; init; }

    /// <summary>查询文本字符数上限；为空使用默认值。</summary>
    public int? MaxQueryLength { get; init; }
}

/// <summary>
/// 在全文查询的扫描、排序、投影和序列化阶段共享的有界预算。
/// </summary>
public sealed partial class FullTextSearchBudget : IDisposable
{
    private readonly object _counterSync = new();
    private readonly CancellationToken _callerToken;
    private readonly CancellationTokenSource _deadlineSource;
    private readonly Stopwatch _clock;
    private long _workUnits;
    private long _postingVisits;
    private long _projectedStringBytes;
    private long _inputCharacters;
    private long _responseBytes;
    private bool _disposed;

    /// <summary>使用指定的预算选项和调用方取消令牌创建预算。</summary>
    /// <param name="options">可选的较低预算。</param>
    /// <param name="cancellationToken">调用方取消令牌。</param>
    public FullTextSearchBudget(
        FullTextSearchBudgetOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new FullTextSearchBudgetOptions();
        MaxDocuments = ResolvePositive(options.MaxDocuments, FullTextSearchBudgetOptions.DefaultMaxDocuments, nameof(options.MaxDocuments));
        MaxDocumentBytes = ResolvePositive(options.MaxDocumentBytes, FullTextSearchBudgetOptions.DefaultMaxDocumentBytes, nameof(options.MaxDocumentBytes));
        MaxPostingVisits = ResolvePositive(options.MaxPostingVisits, FullTextSearchBudgetOptions.DefaultMaxPostingVisits, nameof(options.MaxPostingVisits));
        MaxWorkUnits = ResolvePositive(options.MaxWorkUnits, FullTextSearchBudgetOptions.DefaultMaxWorkUnits, nameof(options.MaxWorkUnits));
        MaxResponseBytes = ResolvePositive(options.MaxResponseBytes, FullTextSearchBudgetOptions.DefaultMaxResponseBytes, nameof(options.MaxResponseBytes));
        TimeoutMilliseconds = ResolvePositive(options.TimeoutMilliseconds, FullTextSearchBudgetOptions.DefaultTimeoutMilliseconds, nameof(options.TimeoutMilliseconds));
        MaxQueryLength = ResolvePositive(options.MaxQueryLength, FullTextSearchBudgetOptions.DefaultMaxQueryLength, nameof(options.MaxQueryLength));
        _callerToken = cancellationToken;
        _deadlineSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _deadlineSource.CancelAfter(TimeSpan.FromMilliseconds(TimeoutMilliseconds));
        _clock = Stopwatch.StartNew();
    }

    /// <summary>实际采用的文档扫描上限。</summary>
    public int MaxDocuments { get; }

    /// <summary>实际采用的文档字节上限。</summary>
    public long MaxDocumentBytes { get; }

    /// <summary>实际采用的 posting 检查上限。</summary>
    public long MaxPostingVisits { get; }

    /// <summary>实际采用的工作单元上限。</summary>
    public long MaxWorkUnits { get; }

    /// <summary>实际采用的 UTF-8 响应字节上限。</summary>
    public long MaxResponseBytes { get; }

    /// <summary>实际采用的端到端期限（毫秒）。</summary>
    public int TimeoutMilliseconds { get; }

    /// <summary>实际采用的查询字符数上限。</summary>
    public int MaxQueryLength { get; }

    /// <summary>供内部阶段传递的取消令牌；期限到达时也会取消。</summary>
    public CancellationToken Token => _deadlineSource.Token;

    /// <summary>已计费的工作单元数。</summary>
    public long WorkUnits => Interlocked.Read(ref _workUnits);

    /// <summary>已计费的响应字节数。</summary>
    public long ResponseBytes => Interlocked.Read(ref _responseBytes);

    /// <summary>已计费的 posting 检查次数。</summary>
    public long PostingVisits => Interlocked.Read(ref _postingVisits);

    /// <summary>检查调用方取消和端到端期限。</summary>
    public void Check()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_callerToken.IsCancellationRequested)
            throw new OperationCanceledException(_callerToken);
        if (_deadlineSource.IsCancellationRequested || _clock.ElapsedMilliseconds > TimeoutMilliseconds)
            throw new FullTextSearchBudgetExceededException($"全文查询超过 {TimeoutMilliseconds} 毫秒执行预算。");
    }

    /// <summary>为一个扫描、排序、facet 或投影操作计费。</summary>
    /// <param name="units">正的工作单元数量。</param>
    public void ChargeWork(long units = 1)
    {
        if (units <= 0)
            throw new ArgumentOutOfRangeException(nameof(units));
        Check();
        lock (_counterSync)
        {
            if (units > MaxWorkUnits - _workUnits)
                throw new FullTextSearchBudgetExceededException($"全文查询超过 {MaxWorkUnits} 工作单元预算。");
            _workUnits += units;
        }
    }

    /// <summary>为一次倒排 posting 检查计费。</summary>
    public void ChargePostingVisit()
    {
        Check();
        lock (_counterSync)
        {
            if (_postingVisits >= MaxPostingVisits)
                throw new FullTextSearchBudgetExceededException($"全文查询超过 {MaxPostingVisits} posting 检查预算。");
            _postingVisits++;
        }
    }

    /// <summary>为序列化结果计费并检查总响应字节上限。</summary>
    /// <param name="bytes">本次写入的 UTF-8 字节数。</param>
    public void ChargeResponseBytes(long bytes)
    {
        if (bytes < 0)
            throw new ArgumentOutOfRangeException(nameof(bytes));
        Check();
        lock (_counterSync)
        {
            if (bytes > MaxResponseBytes - _responseBytes)
                throw new FullTextSearchBudgetExceededException($"全文响应超过 {MaxResponseBytes} 字节预算。");
            _responseBytes += bytes;
        }
    }

    /// <summary>
    /// 在构造响应时有界检查一个输出字符串。按默认 JSON 转义后的 UTF-8 字节计费，
    /// 避免单个大值在序列化回调前分配或编码超过响应上限。
    /// </summary>
    /// <param name="value">将要出现在响应中的一个字符串值；每个实际出现位置检查一次。</param>
    public void CheckOutputString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Check();
        if (value.Length > MaxResponseBytes - 2)
            throw new FullTextSearchBudgetExceededException($"全文响应字符串超过 {MaxResponseBytes} 字节预算。");
        long encodedBytes = 2;
        Span<char> encoded = stackalloc char[384];
        int offset = 0;
        for (int iteration = 0; offset < value.Length && iteration <= value.Length; iteration++)
        {
            Check();
            OperationStatus status = JavaScriptEncoder.Default.Encode(value.AsSpan(offset), encoded,
                out int consumed, out int written, isFinalBlock: true);
            if (consumed == 0 || status is not (OperationStatus.Done or OperationStatus.DestinationTooSmall))
                throw new InvalidDataException("全文响应字符串无法有界编码。");
            ChargeWork(Math.Max(1, consumed / 64));
            // 默认编码器只保留 Basic Latin，其余标量和 surrogate 均输出 ASCII 转义。
            encodedBytes += written;
            if (encodedBytes > MaxResponseBytes)
                throw new FullTextSearchBudgetExceededException($"全文响应字符串超过 {MaxResponseBytes} 字节预算。");
            offset += consumed;
        }
        lock (_counterSync)
        {
            if (encodedBytes > MaxResponseBytes - _projectedStringBytes)
                throw new FullTextSearchBudgetExceededException($"全文响应字符串总量超过 {MaxResponseBytes} 字节预算。");
            _projectedStringBytes += encodedBytes;
        }
        Check();
    }

    /// <summary>在构造查询或请求指纹前，对输入字符串有界计费，不占用响应字节预算。</summary>
    /// <param name="value">可选输入字符串。</param>
    public void ChargeInputString(string? value)
    {
        Check();
        if (value is null)
            return;
        lock (_counterSync)
        {
            long maximum = FullTextSearchBudgetOptions.DefaultMaxDocumentBytes / sizeof(char);
            if (value.Length > maximum - _inputCharacters)
                throw new FullTextSearchBudgetExceededException("全文请求输入文本超过 64 MiB 预算。");
            _inputCharacters += value.Length;
        }
        for (int offset = 0; offset < value.Length; offset += Math.Min(256, value.Length - offset))
            ChargeWork(Math.Max(1, Math.Min(256, value.Length - offset) / 64));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _deadlineSource.Dispose();
    }

    private static T ResolvePositive<T>(T? requested, T fallback, string parameterName)
        where T : struct, IComparable<T>
    {
        T value = requested ?? fallback;
        if (value.CompareTo(default) <= 0)
            throw new ArgumentOutOfRangeException(parameterName, value, "全文预算必须为正数。");
        return value.CompareTo(fallback) > 0 ? fallback : value;
    }
}
