using System.Text;

namespace SonnetDB.Sql.Execution;

/// <summary>SQL 临时文件的 AOT 友好标量与行二进制编解码。</summary>
internal static class SqlSpillRowCodec
{
    private enum ValueKind : byte
    {
        Null,
        Boolean,
        Byte,
        SByte,
        Int16,
        UInt16,
        Int32,
        UInt32,
        Int64,
        UInt64,
        Single,
        Double,
        Decimal,
        String,
        Bytes,
        DateTime,
        DateTimeOffset,
        Guid,
        Char,
    }

    internal static long EstimateRowBytes(IReadOnlyList<object?> row)
    {
        ArgumentNullException.ThrowIfNull(row);
        long bytes = 32L + (row.Count * 8L);
        foreach (object? value in row)
        {
            bytes = checked(bytes + (value switch
            {
                null => 1,
                string text => 24L + (text.Length * 2L),
                byte[] data => 24L + data.Length,
                _ => 24L,
            }));
        }
        return bytes;
    }

    internal static void WriteRow(BinaryWriter writer, IReadOnlyList<object?> row)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(row);
        writer.Write(row.Count);
        foreach (object? value in row)
            WriteValue(writer, value);
    }

    internal static object?[] ReadRow(BinaryReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        int count = reader.ReadInt32();
        if (count < 0 || count > 1_048_576)
            throw new InvalidDataException($"SQL spill 行列数 {count} 非法。");
        var row = new object?[count];
        for (int i = 0; i < count; i++)
            row[i] = ReadValue(reader);
        return row;
    }

    private static void WriteValue(BinaryWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.Write((byte)ValueKind.Null);
                break;
            case bool item:
                writer.Write((byte)ValueKind.Boolean);
                writer.Write(item);
                break;
            case byte item:
                writer.Write((byte)ValueKind.Byte);
                writer.Write(item);
                break;
            case sbyte item:
                writer.Write((byte)ValueKind.SByte);
                writer.Write(item);
                break;
            case short item:
                writer.Write((byte)ValueKind.Int16);
                writer.Write(item);
                break;
            case ushort item:
                writer.Write((byte)ValueKind.UInt16);
                writer.Write(item);
                break;
            case int item:
                writer.Write((byte)ValueKind.Int32);
                writer.Write(item);
                break;
            case uint item:
                writer.Write((byte)ValueKind.UInt32);
                writer.Write(item);
                break;
            case long item:
                writer.Write((byte)ValueKind.Int64);
                writer.Write(item);
                break;
            case ulong item:
                writer.Write((byte)ValueKind.UInt64);
                writer.Write(item);
                break;
            case float item:
                writer.Write((byte)ValueKind.Single);
                writer.Write(item);
                break;
            case double item:
                writer.Write((byte)ValueKind.Double);
                writer.Write(item);
                break;
            case decimal item:
                writer.Write((byte)ValueKind.Decimal);
                foreach (int part in decimal.GetBits(item))
                    writer.Write(part);
                break;
            case string item:
                writer.Write((byte)ValueKind.String);
                writer.Write(item);
                break;
            case byte[] item:
                writer.Write((byte)ValueKind.Bytes);
                writer.Write(item.Length);
                writer.Write(item);
                break;
            case DateTime item:
                writer.Write((byte)ValueKind.DateTime);
                writer.Write(item.Ticks);
                writer.Write((byte)item.Kind);
                break;
            case DateTimeOffset item:
                writer.Write((byte)ValueKind.DateTimeOffset);
                writer.Write(item.Ticks);
                writer.Write(item.Offset.Ticks);
                break;
            case Guid item:
                writer.Write((byte)ValueKind.Guid);
                writer.Write(item.ToByteArray());
                break;
            case char item:
                writer.Write((byte)ValueKind.Char);
                writer.Write(item);
                break;
            default:
                throw new NotSupportedException(
                    $"SQL spill 不支持标量类型 '{value.GetType().FullName}'；查询已中止且未截断结果。");
        }
    }

    private static object? ReadValue(BinaryReader reader)
        => (ValueKind)reader.ReadByte() switch
        {
            ValueKind.Null => null,
            ValueKind.Boolean => reader.ReadBoolean(),
            ValueKind.Byte => reader.ReadByte(),
            ValueKind.SByte => reader.ReadSByte(),
            ValueKind.Int16 => reader.ReadInt16(),
            ValueKind.UInt16 => reader.ReadUInt16(),
            ValueKind.Int32 => reader.ReadInt32(),
            ValueKind.UInt32 => reader.ReadUInt32(),
            ValueKind.Int64 => reader.ReadInt64(),
            ValueKind.UInt64 => reader.ReadUInt64(),
            ValueKind.Single => reader.ReadSingle(),
            ValueKind.Double => reader.ReadDouble(),
            ValueKind.Decimal => new decimal([
                reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32()]),
            ValueKind.String => reader.ReadString(),
            ValueKind.Bytes => ReadBytes(reader),
            ValueKind.DateTime => new DateTime(reader.ReadInt64(), (DateTimeKind)reader.ReadByte()),
            ValueKind.DateTimeOffset => new DateTimeOffset(reader.ReadInt64(), new TimeSpan(reader.ReadInt64())),
            ValueKind.Guid => new Guid(ReadExactBytes(reader, 16)),
            ValueKind.Char => reader.ReadChar(),
            var kind => throw new InvalidDataException($"SQL spill 标量标记 {(byte)kind} 非法。"),
        };

    private static byte[] ReadBytes(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length < 0)
            throw new InvalidDataException("SQL spill 字节数组长度非法。");
        return ReadExactBytes(reader, length);
    }

    private static byte[] ReadExactBytes(BinaryReader reader, int length)
    {
        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
            throw new EndOfStreamException("SQL spill 文件在标量中间结束。");
        return bytes;
    }

    internal static BinaryWriter CreateWriter(string path)
        => new(
            new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024),
            Encoding.UTF8,
            leaveOpen: false);

    internal static BinaryReader CreateReader(string path)
        => new(
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024),
            Encoding.UTF8,
            leaveOpen: false);
}
