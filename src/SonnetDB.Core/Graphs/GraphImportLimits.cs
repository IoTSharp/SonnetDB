namespace SonnetDB.Graphs;

/// <summary>Graph importer 和批量写入口共享的固定资源预算。</summary>
public static class GraphImportLimits
{
    /// <summary>单个原子导入批次允许的最大元素数。</summary>
    public const int MaxBatchElements = 10_000;

    /// <summary>单个原子导入请求规范化 JSON 编码允许的最大字节数。</summary>
    public const int MaxBatchBytes = 8 * 1024 * 1024;

    /// <summary>CSV importer 默认允许的最大单行 UTF-8 字节数。</summary>
    public const int DefaultMaxCsvLineBytes = 1024 * 1024;
}

/// <summary>Graph import 输入超过显式字节预算。</summary>
public sealed class GraphImportLimitExceededException : Exception
{
    /// <summary>创建 Graph import 字节预算错误。</summary>
    /// <param name="limitName">稳定预算名称。</param>
    /// <param name="actualBytes">观察到的字节数。</param>
    /// <param name="maximumBytes">允许的最大字节数。</param>
    public GraphImportLimitExceededException(string limitName, long actualBytes, long maximumBytes)
        : base($"Graph import {limitName} 为 {actualBytes} 字节，超过上限 {maximumBytes} 字节。")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(limitName);
        ArgumentOutOfRangeException.ThrowIfNegative(actualBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        LimitName = limitName;
        ActualBytes = actualBytes;
        MaximumBytes = maximumBytes;
    }

    /// <summary>稳定预算名称，例如 <c>batch</c> 或 <c>csv_line</c>。</summary>
    public string LimitName { get; }

    /// <summary>观察到的字节数。</summary>
    public long ActualBytes { get; }

    /// <summary>允许的最大字节数。</summary>
    public long MaximumBytes { get; }
}
