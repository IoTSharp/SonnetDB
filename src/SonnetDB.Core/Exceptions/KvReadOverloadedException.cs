namespace SonnetDB.Exceptions;

/// <summary>请求侧物理 KV 读取达到队列容量或等待时限，应稍后重试。</summary>
public sealed class KvReadOverloadedException : IOException
{
    /// <summary>供各协议一致识别的稳定过载错误码。</summary>
    public const string Code = "kv_read_overloaded";

    /// <summary>创建不包含文件路径或请求内容的过载错误。</summary>
    /// <param name="message">安全的错误摘要。</param>
    public KvReadOverloadedException(string message) : base(message) { }
}
