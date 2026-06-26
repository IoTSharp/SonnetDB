using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SonnetDB.Vector.Format;

/// <summary>
/// 8 字节 inline 缓冲，用于存储向量索引格式 magic 标识符。
/// </summary>
[InlineArray(8)]
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Magic8
{
    private byte _e0;
}
