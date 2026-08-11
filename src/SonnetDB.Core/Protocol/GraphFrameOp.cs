namespace SonnetDB.Protocol;

/// <summary>原生属性图 Frame service 的操作码。</summary>
public enum GraphFrameOp : byte
{
    /// <summary>按方向和可选边标签流式扩展单跳邻接。</summary>
    Expand = 1,
}
