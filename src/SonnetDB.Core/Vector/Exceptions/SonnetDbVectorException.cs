namespace SonnetDB.Vector.Exceptions;

/// <summary>
/// SonnetDB 向量引擎操作的基础异常类型。
/// </summary>
public class SonnetDbVectorException : Exception
{
    /// <summary>
    /// 初始化 <see cref="SonnetDbVectorException"/> 的新实例。
    /// </summary>
    public SonnetDbVectorException()
    {
    }

    /// <summary>
    /// 使用指定的错误消息初始化 <see cref="SonnetDbVectorException"/> 的新实例。
    /// </summary>
    /// <param name="message">描述错误的消息。</param>
    public SonnetDbVectorException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// 使用指定的错误消息和内部异常初始化 <see cref="SonnetDbVectorException"/> 的新实例。
    /// </summary>
    /// <param name="message">描述错误的消息。</param>
    /// <param name="innerException">导致当前异常的异常。</param>
    public SonnetDbVectorException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
