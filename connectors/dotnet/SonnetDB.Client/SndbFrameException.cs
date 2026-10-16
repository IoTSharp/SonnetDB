namespace SonnetDB.Client;

/// <summary>远程 Frame 请求的协议、传输或服务端错误；发送后不推断写入未提交。</summary>
public sealed class SndbFrameException : Exception
{
    internal SndbFrameException(string code, string message, Exception? inner = null)
        : base(message, inner) => Code = code;

    /// <summary>机器可读错误码。</summary>
    public string Code { get; }

    /// <summary>请求已经开始发送，需按操作对账，不能盲目重放写入。</summary>
    public bool OutcomeUnknown => true;
}

/// <summary>已经开始发送的 Frame 请求被调用方取消；仍保留结果未知的边界。</summary>
public sealed class SndbFrameCanceledException : OperationCanceledException
{
    internal SndbFrameCanceledException(CancellationToken token, Exception inner)
        : base("Frame 请求已取消；写入结果可能未知，请对账后再操作。", inner, token) { }

    /// <summary>取消不能证明已发送请求没有提交。</summary>
    public bool OutcomeUnknown => true;
}
