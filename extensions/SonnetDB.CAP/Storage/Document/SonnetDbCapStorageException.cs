namespace SonnetDB.Exceptions;

/// <summary>CAP 文档存储写入失败；保留稳定错误码，不包含消息正文。</summary>
public sealed class SonnetDbCapStorageException : Exception
{
    /// <summary>创建包含文档错误码的存储异常。</summary>
    /// <param name="errorCode">文档写入错误码。</param>
    public SonnetDbCapStorageException(string errorCode) : base($"CAP Document Store 写入失败：{errorCode}。")
        => ErrorCode = errorCode;

    /// <summary>底层文档写入的稳定错误码。</summary>
    public string ErrorCode { get; }
}
