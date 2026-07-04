using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace SonnetDB.IO;

/// <summary>
/// 基于 <see cref="ReadOnlySpan{T}"/> 的顺序二进制读取器（Safe-only，无 unsafe）。
/// 所有多字节整数使用 little-endian 字节序。
/// </summary>
public ref struct SpanReader
{
    private ReadOnlySpan<byte> _buffer;
    private int _position;

    /// <summary>
    /// 初始化 <see cref="SpanReader"/>，使用指定缓冲区。
    /// </summary>
    /// <param name="buffer">源缓冲区。</param>
    public SpanReader(ReadOnlySpan<byte> buffer)
    {
        _buffer = buffer;
        _position = 0;
    }

    /// <summary>
    /// 当前读取位置（已读取字节数）。
    /// </summary>
    public int Position => _position;

    /// <summary>
    /// 缓冲区总长度（字节数）。
    /// </summary>
    public int Length => _buffer.Length;

    /// <summary>
    /// 剩余可读字节数。
    /// </summary>
    public int Remaining => Length - _position;

    /// <summary>
    /// 是否已读到末尾。
    /// </summary>
    public bool IsEnd => _position >= _buffer.Length;

    /// <summary>
    /// 剩余数据的 <see cref="ReadOnlySpan{T}"/> 切片视图。
    /// </summary>
    public ReadOnlySpan<byte> RemainingSpan => _buffer[_position..];

    /// <summary>
    /// 确保剩余数据不少于 <paramref name="count"/> 字节，否则抛出异常。
    /// </summary>
    /// <param name="count">所需字节数。</param>
    /// <exception cref="InvalidOperationException">缓冲区数据不足时抛出。</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void EnsureRemaining(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (Remaining < count)
            throw new InvalidOperationException("SpanReader buffer underflow");
    }

    /// <summary>
    /// 跳过指定字节数。
    /// </summary>
    /// <param name="count">要跳过的字节数。</param>
    /// <exception cref="InvalidOperationException">超出缓冲区范围时抛出。</exception>
    public void Skip(int count)
    {
        EnsureRemaining(count);
        _position += count;
    }

    /// <summary>
    /// 重置读取位置至 0。
    /// </summary>
    public void Reset() => _position = 0;

    /// <summary>
    /// 读取一个无符号字节。
    /// </summary>
    /// <returns>读取到的字节值。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte ReadByte()
    {
        EnsureRemaining(1);
        return _buffer[_position++];
    }

    /// <summary>
    /// 读取一个有符号字节。
    /// </summary>
    /// <returns>读取到的有符号字节值。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public sbyte ReadSByte()
    {
        EnsureRemaining(1);
        return (sbyte)_buffer[_position++];
    }

    /// <summary>
    /// 读取一个 little-endian 16 位有符号整数。
    /// </summary>
    /// <returns>读取到的值。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public short ReadInt16()
    {
        EnsureRemaining(2);
        short v = BinaryPrimitives.ReadInt16LittleEndian(_buffer.Slice(_position, 2));
        _position += 2;
        return v;
    }

    /// <summary>
    /// 读取一个 little-endian 16 位无符号整数。
    /// </summary>
    /// <returns>读取到的值。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort ReadUInt16()
    {
        EnsureRemaining(2);
        ushort v = BinaryPrimitives.ReadUInt16LittleEndian(_buffer.Slice(_position, 2));
        _position += 2;
        return v;
    }

    /// <summary>
    /// 读取一个 little-endian 32 位有符号整数。
    /// </summary>
    /// <returns>读取到的值。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadInt32()
    {
        EnsureRemaining(4);
        int v = BinaryPrimitives.ReadInt32LittleEndian(_buffer.Slice(_position, 4));
        _position += 4;
        return v;
    }

    /// <summary>
    /// 读取一个 little-endian 32 位无符号整数。
    /// </summary>
    /// <returns>读取到的值。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadUInt32()
    {
        EnsureRemaining(4);
        uint v = BinaryPrimitives.ReadUInt32LittleEndian(_buffer.Slice(_position, 4));
        _position += 4;
        return v;
    }

    /// <summary>
    /// 读取一个 little-endian 64 位有符号整数。
    /// </summary>
    /// <returns>读取到的值。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long ReadInt64()
    {
        EnsureRemaining(8);
        long v = BinaryPrimitives.ReadInt64LittleEndian(_buffer.Slice(_position, 8));
        _position += 8;
        return v;
    }

    /// <summary>
    /// 读取一个 little-endian 64 位无符号整数。
    /// </summary>
    /// <returns>读取到的值。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong ReadUInt64()
    {
        EnsureRemaining(8);
        ulong v = BinaryPrimitives.ReadUInt64LittleEndian(_buffer.Slice(_position, 8));
        _position += 8;
        return v;
    }

    /// <summary>
    /// 读取一个 little-endian 单精度浮点数（IEEE 754）。
    /// </summary>
    /// <returns>读取到的值。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float ReadSingle()
    {
        EnsureRemaining(4);
        float v = BinaryPrimitives.ReadSingleLittleEndian(_buffer.Slice(_position, 4));
        _position += 4;
        return v;
    }

    /// <summary>
    /// 读取一个 little-endian 双精度浮点数（IEEE 754）。
    /// </summary>
    /// <returns>读取到的值。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public double ReadDouble()
    {
        EnsureRemaining(8);
        double v = BinaryPrimitives.ReadDoubleLittleEndian(_buffer.Slice(_position, 8));
        _position += 8;
        return v;
    }

    /// <summary>
    /// 读取指定长度的字节序列（零拷贝切片视图）。
    /// </summary>
    /// <param name="length">要读取的字节数。</param>
    /// <returns>对应字节的 <see cref="ReadOnlySpan{T}"/> 视图。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<byte> ReadBytes(int length)
    {
        EnsureRemaining(length);
        ReadOnlySpan<byte> slice = _buffer.Slice(_position, length);
        _position += length;
        return slice;
    }

    /// <summary>
    /// 读取一个 unmanaged 结构体（通过 <see cref="MemoryMarshal.Read{T}"/>，无 unsafe）。
    /// </summary>
    /// <typeparam name="T">unmanaged 结构体类型。</typeparam>
    /// <returns>读取到的结构体值。</returns>
    public T ReadStruct<T>() where T : unmanaged
    {
        int size = Unsafe.SizeOf<T>();
        EnsureRemaining(size);
        T v = MemoryMarshal.Read<T>(_buffer.Slice(_position, size));
        _position += size;
        return v;
    }

    /// <summary>
    /// 读取多个 unmanaged 结构体（通过 <see cref="MemoryMarshal.Cast{TFrom, TTo}"/>，零拷贝视图）。
    /// </summary>
    /// <typeparam name="T">unmanaged 结构体类型。</typeparam>
    /// <param name="count">要读取的结构体数量。</param>
    /// <returns>指向缓冲区的 <see cref="ReadOnlySpan{T}"/> 视图。</returns>
    public ReadOnlySpan<T> ReadStructs<T>(int count) where T : unmanaged
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        int size = checked(Unsafe.SizeOf<T>() * count);
        EnsureRemaining(size);
        ReadOnlySpan<T> result = MemoryMarshal.Cast<byte, T>(_buffer.Slice(_position, size));
        _position += size;
        return result;
    }

    /// <summary>
    /// 读取 LEB128 编码的 32 位无符号整数。
    /// </summary>
    /// <returns>解码后的值。</returns>
    /// <exception cref="InvalidOperationException">数据格式非法或超出范围时抛出。</exception>
    public uint ReadVarUInt32()
    {
        uint result = 0;
        int shift = 0;
        while (true)
        {
            byte b = ReadByte();
            if (shift == 28)
            {
                // 5th byte: only low 4 bits are valid for uint32
                if ((b & 0xF0) != 0)
                    throw new InvalidOperationException("VarUInt32 overflow");
                result |= (uint)(b & 0x0F) << shift;
                break;
            }

            result |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                break;
            shift += 7;
        }

        return result;
    }

    /// <summary>
    /// 读取 LEB128 编码的 64 位无符号整数。
    /// </summary>
    /// <returns>解码后的值。</returns>
    /// <exception cref="InvalidOperationException">数据格式非法或超出范围时抛出。</exception>
    public ulong ReadVarUInt64()
    {
        ulong result = 0;
        int shift = 0;
        while (true)
        {
            byte b = ReadByte();
            if (shift == 63)
            {
                // 10th byte: only bit 0 is valid for uint64
                if ((b & 0xFE) != 0)
                    throw new InvalidOperationException("VarUInt64 overflow");
                result |= (ulong)(b & 0x01) << shift;
                break;
            }

            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                break;
            shift += 7;
        }

        return result;
    }

    /// <summary>
    /// 读取字符串（先读 <see cref="int"/> 长度，再读编码字节）。
    /// length = -1 表示 null 字符串。
    /// </summary>
    /// <param name="encoding">字符编码（通常为 <see cref="Encoding.UTF8"/>）。</param>
    /// <returns>读取到的字符串，若长度为 -1 则返回 null。</returns>
    public string? ReadString(Encoding encoding)
    {
        int length = ReadInt32();
        if (length == -1)
            return null;
        if (length == 0)
            return string.Empty;
        if (length < 0)
            throw new InvalidOperationException($"Invalid string length: {length}");
        if (length > Remaining)
            throw new InvalidOperationException("String length exceeds remaining buffer");
        ReadOnlySpan<byte> bytes = ReadBytes(length);
        return encoding.GetString(bytes);
    }

    /// <summary>
    /// 读取变长前缀字符串（LEB128 varuint 字节长度 + UTF-8 字节，无 null 表示）。
    /// 与 <see cref="SpanWriter.WriteVarString"/> 配对。
    /// </summary>
    /// <returns>读取到的字符串。</returns>
    /// <exception cref="InvalidOperationException">长度超出剩余缓冲区时抛出。</exception>
    public string ReadVarString()
    {
        uint length = ReadVarUInt32();
        if (length == 0)
            return string.Empty;
        if (length > (uint)Remaining)
            throw new InvalidOperationException("VarString length exceeds remaining buffer");
        ReadOnlySpan<byte> bytes = ReadBytes((int)length);
        return Encoding.UTF8.GetString(bytes);
    }
}
