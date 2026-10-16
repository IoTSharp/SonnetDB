using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace SonnetDB.IO;

/// <summary>
/// 基于 <see cref="Span{T}"/> 的顺序二进制写入器（Safe-only，无 unsafe）。
/// 所有多字节整数使用 little-endian 字节序。
/// </summary>
public ref struct SpanWriter
{
    private Span<byte> _buffer;
    private int _position;

    /// <summary>
    /// 初始化 <see cref="SpanWriter"/>，使用指定缓冲区。
    /// </summary>
    /// <param name="buffer">目标写入缓冲区。</param>
    public SpanWriter(Span<byte> buffer)
    {
        _buffer = buffer;
        _position = 0;
    }

    /// <summary>
    /// 当前写入位置（已写入字节数）。
    /// </summary>
    public int Position => _position;

    /// <summary>
    /// 缓冲区总容量（字节数）。
    /// </summary>
    public int Capacity => _buffer.Length;

    /// <summary>
    /// 剩余可写字节数。
    /// </summary>
    public int Remaining => Capacity - _position;

    /// <summary>
    /// 已写入数据的 <see cref="Span{T}"/> 切片视图。
    /// </summary>
    public Span<byte> WrittenSpan => _buffer[.._position];

    /// <summary>
    /// 空闲（未写入）区域的 <see cref="Span{T}"/> 切片视图。
    /// </summary>
    public Span<byte> FreeSpan => _buffer[_position..];

    /// <summary>
    /// 确保剩余空间不少于 <paramref name="count"/> 字节，否则抛出异常。
    /// </summary>
    /// <param name="count">所需字节数。</param>
    /// <exception cref="InvalidOperationException">缓冲区空间不足时抛出。</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EnsureRemaining(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (Remaining < count)
            throw new InvalidOperationException("SpanWriter buffer overflow");
    }

    /// <summary>
    /// 手动推进写入位置（用于先预留头部、再回填的场景）。
    /// </summary>
    /// <param name="count">推进字节数。</param>
    /// <exception cref="InvalidOperationException">超出缓冲区容量时抛出。</exception>
    public void Advance(int count)
    {
        EnsureRemaining(count);
        _position += count;
    }

    /// <summary>
    /// 重置写入位置至 0。
    /// </summary>
    public void Reset() => _position = 0;

    /// <summary>
    /// 写入一个无符号字节。
    /// </summary>
    /// <param name="value">要写入的字节值。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteByte(byte value)
    {
        EnsureRemaining(1);
        _buffer[_position++] = value;
    }

    /// <summary>
    /// 写入一个有符号字节。
    /// </summary>
    /// <param name="value">要写入的有符号字节值。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteSByte(sbyte value)
    {
        EnsureRemaining(1);
        _buffer[_position++] = (byte)value;
    }

    /// <summary>
    /// 写入一个 little-endian 16 位有符号整数。
    /// </summary>
    /// <param name="value">要写入的值。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteInt16(short value)
    {
        EnsureRemaining(2);
        BinaryPrimitives.WriteInt16LittleEndian(_buffer.Slice(_position, 2), value);
        _position += 2;
    }

    /// <summary>
    /// 写入一个 little-endian 16 位无符号整数。
    /// </summary>
    /// <param name="value">要写入的值。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteUInt16(ushort value)
    {
        EnsureRemaining(2);
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.Slice(_position, 2), value);
        _position += 2;
    }

    /// <summary>
    /// 写入一个 little-endian 32 位有符号整数。
    /// </summary>
    /// <param name="value">要写入的值。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteInt32(int value)
    {
        EnsureRemaining(4);
        BinaryPrimitives.WriteInt32LittleEndian(_buffer.Slice(_position, 4), value);
        _position += 4;
    }

    /// <summary>
    /// 写入一个 little-endian 32 位无符号整数。
    /// </summary>
    /// <param name="value">要写入的值。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteUInt32(uint value)
    {
        EnsureRemaining(4);
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.Slice(_position, 4), value);
        _position += 4;
    }

    /// <summary>
    /// 写入一个 little-endian 64 位有符号整数。
    /// </summary>
    /// <param name="value">要写入的值。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteInt64(long value)
    {
        EnsureRemaining(8);
        BinaryPrimitives.WriteInt64LittleEndian(_buffer.Slice(_position, 8), value);
        _position += 8;
    }

    /// <summary>
    /// 写入一个 little-endian 64 位无符号整数。
    /// </summary>
    /// <param name="value">要写入的值。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteUInt64(ulong value)
    {
        EnsureRemaining(8);
        BinaryPrimitives.WriteUInt64LittleEndian(_buffer.Slice(_position, 8), value);
        _position += 8;
    }

    /// <summary>
    /// 写入一个 little-endian 单精度浮点数（IEEE 754）。
    /// </summary>
    /// <param name="value">要写入的值。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteSingle(float value)
    {
        EnsureRemaining(4);
        BinaryPrimitives.WriteSingleLittleEndian(_buffer.Slice(_position, 4), value);
        _position += 4;
    }

    /// <summary>
    /// 写入一个 little-endian 双精度浮点数（IEEE 754）。
    /// </summary>
    /// <param name="value">要写入的值。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteDouble(double value)
    {
        EnsureRemaining(8);
        BinaryPrimitives.WriteDoubleLittleEndian(_buffer.Slice(_position, 8), value);
        _position += 8;
    }

    /// <summary>
    /// 写入字节序列。
    /// </summary>
    /// <param name="source">要写入的字节序列。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteBytes(ReadOnlySpan<byte> source)
    {
        EnsureRemaining(source.Length);
        source.CopyTo(_buffer.Slice(_position, source.Length));
        _position += source.Length;
    }

    /// <summary>
    /// 写入一个 unmanaged 结构体（通过 <see cref="MemoryMarshal.Write{T}"/>，无 unsafe）。
    /// </summary>
    /// <typeparam name="T">unmanaged 结构体类型。</typeparam>
    /// <param name="value">要写入的结构体值。</param>
    public void WriteStruct<T>(in T value) where T : unmanaged
    {
        int size = Unsafe.SizeOf<T>();
        EnsureRemaining(size);
        MemoryMarshal.Write(_buffer.Slice(_position, size), in value);
        _position += size;
    }

    /// <summary>
    /// 批量写入 unmanaged 结构体数组（通过 <see cref="MemoryMarshal.AsBytes{T}"/>，无 unsafe）。
    /// </summary>
    /// <typeparam name="T">unmanaged 结构体类型。</typeparam>
    /// <param name="values">要写入的结构体序列。</param>
    public void WriteStructs<T>(ReadOnlySpan<T> values) where T : unmanaged
    {
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(values);
        EnsureRemaining(bytes.Length);
        bytes.CopyTo(FreeSpan);
        _position += bytes.Length;
    }

    /// <summary>
    /// 使用 LEB128 编码写入 32 位无符号整数（最多 5 字节）。
    /// </summary>
    /// <param name="value">要写入的值。</param>
    public void WriteVarUInt32(uint value)
    {
        do
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0)
                b |= 0x80;
            WriteByte(b);
        } while (value != 0);
    }

    /// <summary>
    /// 使用 LEB128 编码写入 64 位无符号整数（最多 10 字节）。
    /// </summary>
    /// <param name="value">要写入的值。</param>
    public void WriteVarUInt64(ulong value)
    {
        do
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0)
                b |= 0x80;
            WriteByte(b);
        } while (value != 0);
    }

    /// <summary>
    /// 写入字符串（先写 <see cref="int"/> 长度，再写编码字节）。
    /// length = -1 表示 null 字符串。
    /// </summary>
    /// <param name="value">要写入的字符串，null 表示写入 null 标记。</param>
    /// <param name="encoding">字符编码（通常为 <see cref="Encoding.UTF8"/>）。</param>
    public void WriteString(string? value, Encoding encoding)
    {
        if (value is null)
        {
            WriteInt32(-1);
            return;
        }

        int byteCount = encoding.GetByteCount(value);
        EnsureRemaining(sizeof(int) + byteCount);
        WriteInt32(byteCount);
        encoding.GetBytes(value, FreeSpan);
        _position += byteCount;
    }

    /// <summary>
    /// 写入变长前缀字符串（LEB128 varuint 字节长度 + UTF-8 字节，无 null 表示）。
    /// 面向线协议的紧凑编码，与 <see cref="WriteString"/> 的 int32+null 哨兵格式不兼容。
    /// </summary>
    /// <param name="value">要写入的字符串（不允许 null）。</param>
    public void WriteVarString(string value)
    {
        int byteCount = Encoding.UTF8.GetByteCount(value);
        WriteVarUInt32((uint)byteCount);
        EnsureRemaining(byteCount);
        Encoding.UTF8.GetBytes(value, FreeSpan);
        _position += byteCount;
    }

    /// <summary>
    /// 计算 <see cref="WriteVarString"/> 编码 <paramref name="value"/> 所需的字节数。
    /// </summary>
    /// <param name="value">待编码字符串。</param>
    /// <returns>varuint 长度前缀 + UTF-8 字节的总字节数。</returns>
    public static int MeasureVarString(string value)
    {
        int byteCount = Encoding.UTF8.GetByteCount(value);
        return MeasureVarUInt32((uint)byteCount) + byteCount;
    }

    /// <summary>
    /// 计算 LEB128 编码 32 位无符号整数所需的字节数（1~5）。
    /// </summary>
    /// <param name="value">待编码值。</param>
    /// <returns>编码字节数。</returns>
    public static int MeasureVarUInt32(uint value)
    {
        int count = 1;
        while (value >= 0x80)
        {
            value >>= 7;
            count++;
        }

        return count;
    }

    /// <summary>
    /// 计算 LEB128 编码 64 位无符号整数所需的字节数（1~10）。
    /// </summary>
    /// <param name="value">待编码值。</param>
    /// <returns>编码字节数。</returns>
    public static int MeasureVarUInt64(ulong value)
    {
        int count = 1;
        while (value >= 0x80)
        {
            value >>= 7;
            count++;
        }

        return count;
    }
}
