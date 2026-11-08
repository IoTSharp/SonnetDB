namespace SonnetDB.Kv.Lsm;

/// <summary>
/// 一次原子的版本变更：删除/新增 SST 文件并更新实例级计数器。
/// </summary>
/// <remarks>
/// 编码为 varint tag 序列；未知 tag 拒绝解析（新增字段时升级 manifest 版本）：
/// <list type="table">
///   <item><term>1</term><description>Generation</description></item>
///   <item><term>2</term><description>LogNumber：小于该值的 WAL 已全部持久化到 SST</description></item>
///   <item><term>3</term><description>LastSequence</description></item>
///   <item><term>4</term><description>NextFileNumber</description></item>
///   <item><term>5</term><description>DeletedFile：level、fileNumber</description></item>
///   <item><term>6</term><description>AddedFile：level、fileNumber、fileSize、smallest、largest、minSeq、maxSeq</description></item>
/// </list>
/// </remarks>
internal sealed class KvVersionEdit
{
    private const ulong TagGeneration = 1;
    private const ulong TagLogNumber = 2;
    private const ulong TagLastSequence = 3;
    private const ulong TagNextFileNumber = 4;
    private const ulong TagDeletedFile = 5;
    private const ulong TagAddedFile = 6;

    /// <summary>新的 keyspace generation；null 表示不变。</summary>
    internal long? Generation { get; set; }

    /// <summary>新的 WAL 下界；null 表示不变。</summary>
    internal long? LogNumber { get; set; }

    /// <summary>新的最后 sequence；null 表示不变。</summary>
    internal long? LastSequence { get; set; }

    /// <summary>新的下一个文件号；null 表示不变。</summary>
    internal long? NextFileNumber { get; set; }

    /// <summary>删除的文件 (level, fileNumber)。</summary>
    internal List<(int Level, long FileNumber)> DeletedFiles { get; } = [];

    /// <summary>新增的文件。</summary>
    internal List<KvLsmFileMetadata> AddedFiles { get; } = [];

    /// <summary>是否包含全部四个实例级计数器（manifest 首条快照记录的要求）。</summary>
    internal bool IsComplete => Generation.HasValue && LogNumber.HasValue && LastSequence.HasValue && NextFileNumber.HasValue;

    /// <summary>编码为 manifest 记录载荷。</summary>
    internal byte[] Encode()
    {
        var buffer = new GrowableBuffer();
        WriteOptional(buffer, TagGeneration, Generation);
        WriteOptional(buffer, TagLogNumber, LogNumber);
        WriteOptional(buffer, TagLastSequence, LastSequence);
        WriteOptional(buffer, TagNextFileNumber, NextFileNumber);
        foreach ((int level, long fileNumber) in DeletedFiles)
        {
            buffer.WriteVarint(TagDeletedFile);
            buffer.WriteVarint((ulong)level);
            buffer.WriteVarint((ulong)fileNumber);
        }
        foreach (KvLsmFileMetadata file in AddedFiles)
        {
            buffer.WriteVarint(TagAddedFile);
            buffer.WriteVarint((ulong)file.Level);
            buffer.WriteVarint((ulong)file.FileNumber);
            buffer.WriteVarint((ulong)file.FileSize);
            buffer.WriteBytes(file.SmallestKey);
            buffer.WriteBytes(file.LargestKey);
            buffer.WriteVarint((ulong)file.MinSequence);
            buffer.WriteVarint((ulong)file.MaxSequence);
        }
        return buffer.ToArray();
    }

    /// <summary>解析 manifest 记录载荷。</summary>
    /// <exception cref="InvalidDataException">tag 未知、字段重复、取值越界或存在截断。</exception>
    internal static KvVersionEdit Decode(ReadOnlySpan<byte> payload)
    {
        var edit = new KvVersionEdit();
        int position = 0;
        while (position < payload.Length)
        {
            ulong tag = KvLsmVarint.Read(payload, ref position);
            switch (tag)
            {
                case TagGeneration:
                    edit.Generation = ReadOnce(edit.Generation, payload, ref position, "Generation");
                    break;
                case TagLogNumber:
                    edit.LogNumber = ReadOnce(edit.LogNumber, payload, ref position, "LogNumber");
                    break;
                case TagLastSequence:
                    edit.LastSequence = ReadOnce(edit.LastSequence, payload, ref position, "LastSequence");
                    break;
                case TagNextFileNumber:
                    edit.NextFileNumber = ReadOnce(edit.NextFileNumber, payload, ref position, "NextFileNumber");
                    break;
                case TagDeletedFile:
                    edit.DeletedFiles.Add((
                        KvLsmVarint.ReadInt32(payload, ref position),
                        KvLsmVarint.ReadInt64(payload, ref position)));
                    break;
                case TagAddedFile:
                    var file = new KvLsmFileMetadata(
                        Level: KvLsmVarint.ReadInt32(payload, ref position),
                        FileNumber: KvLsmVarint.ReadInt64(payload, ref position),
                        FileSize: KvLsmVarint.ReadInt64(payload, ref position),
                        SmallestKey: ReadBytes(payload, ref position),
                        LargestKey: ReadBytes(payload, ref position),
                        MinSequence: KvLsmVarint.ReadInt64(payload, ref position),
                        MaxSequence: KvLsmVarint.ReadInt64(payload, ref position));
                    file.Validate();
                    edit.AddedFiles.Add(file);
                    break;
                default:
                    throw new InvalidDataException($"KV-LSM version edit has unknown tag {tag}.");
            }
        }
        return edit;
    }

    private static long ReadOnce(long? existing, ReadOnlySpan<byte> payload, ref int position, string name)
    {
        if (existing.HasValue)
            throw new InvalidDataException($"KV-LSM version edit repeats {name}.");
        return KvLsmVarint.ReadInt64(payload, ref position);
    }

    private static byte[] ReadBytes(ReadOnlySpan<byte> payload, ref int position)
    {
        int length = KvLsmVarint.ReadInt32(payload, ref position);
        if (length > payload.Length - position)
            throw new InvalidDataException("KV-LSM version edit byte field is truncated.");
        byte[] result = payload.Slice(position, length).ToArray();
        position += length;
        return result;
    }

    private static void WriteOptional(GrowableBuffer buffer, ulong tag, long? value)
    {
        if (!value.HasValue)
            return;
        ArgumentOutOfRangeException.ThrowIfNegative(value.Value);
        buffer.WriteVarint(tag);
        buffer.WriteVarint((ulong)value.Value);
    }

    private sealed class GrowableBuffer
    {
        private byte[] _buffer = new byte[256];
        private int _length;

        internal void WriteVarint(ulong value)
        {
            Ensure(KvLsmVarint.MaxLength);
            _length += KvLsmVarint.Write(_buffer.AsSpan(_length), value);
        }

        internal void WriteBytes(ReadOnlySpan<byte> value)
        {
            WriteVarint((ulong)value.Length);
            Ensure(value.Length);
            value.CopyTo(_buffer.AsSpan(_length));
            _length += value.Length;
        }

        internal byte[] ToArray() => _buffer.AsSpan(0, _length).ToArray();

        private void Ensure(int additional)
        {
            int required = checked(_length + additional);
            if (required > _buffer.Length)
                Array.Resize(ref _buffer, Math.Max(required, _buffer.Length * 2));
        }
    }
}
