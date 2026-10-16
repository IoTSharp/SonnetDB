namespace SonnetDB.Protocol;

/// <summary>
/// doc service（<see cref="FrameService.Doc"/>）的 opcode（M28 P5b #240）。
/// </summary>
public enum DocFrameOp : byte
{
    /// <summary>按 ID 列表或扫描分页读取文档（复杂 filter/projection/sort 查询走 REST / SQL）。</summary>
    Find = 1,

    /// <summary>批量插入 JSON 文档（文本原始 UTF-8 直传，零转义/零嵌套 JSON 信封）。</summary>
    Insert = 2,
}
