using System.Buffers.Binary;
using System.Text;
using SonnetDB.FullText.Index;

namespace SonnetDB.FullText.Storage;

internal static class SegmentFile
{
    private static readonly byte[] _magicV1 = "DSSEG001"u8.ToArray();
    private static readonly byte[] _magicV2 = "DSSEG002"u8.ToArray();

    public static SegmentReader Write(string segmentsDirectory, SegmentData data)
    {
        Directory.CreateDirectory(segmentsDirectory);
        string path = GetPath(segmentsDirectory, data.Id);
        string tempPath = path + ".tmp";

        using (FileStream stream = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(_magicV2);
            WriteInt64(stream, data.Id);
            VarInt.Write(stream, data.Documents.Count);

            foreach (SegmentDocument document in data.Documents)
            {
                VarInt.Write(stream, document.LocalId);
                WriteString(stream, document.Id.Value);
                VarInt.Write(stream, document.Fields.Count);
                foreach (KeyValuePair<string, string> field in document.Fields)
                {
                    WriteString(stream, field.Key);
                    WriteString(stream, field.Value);
                }
            }

            VarInt.Write(stream, data.FieldLengths.Count);
            foreach (KeyValuePair<string, Dictionary<int, int>> field in data.FieldLengths.OrderBy(static x => x.Key, StringComparer.Ordinal))
            {
                WriteString(stream, field.Key);
                VarInt.Write(stream, field.Value.Count);
                foreach (KeyValuePair<int, int> length in field.Value.OrderBy(static x => x.Key))
                {
                    VarInt.Write(stream, length.Key);
                    VarInt.Write(stream, length.Value);
                }
            }

            VarInt.Write(stream, data.PostingLists.Count);
            foreach (SegmentPostingList postingList in data.PostingLists
                .OrderBy(static x => x.Field, StringComparer.Ordinal)
                .ThenBy(static x => x.Term, StringComparer.Ordinal))
            {
                WriteString(stream, postingList.Field);
                WriteString(stream, postingList.Term);
                VarInt.Write(stream, postingList.Postings.Count);

                int previousDocId = 0;
                foreach (KeyValuePair<int, int> posting in postingList.Postings.OrderBy(static x => x.Key))
                {
                    VarInt.Write(stream, posting.Key - previousDocId);
                    VarInt.Write(stream, posting.Value);
                    if (postingList.Positions.TryGetValue(posting.Key, out List<int>? positions))
                    {
                        VarInt.Write(stream, positions.Count);
                        int previousPosition = 0;
                        foreach (int position in positions.Order())
                        {
                            VarInt.Write(stream, position - previousPosition);
                            previousPosition = position;
                        }
                    }
                    else
                    {
                        VarInt.Write(stream, 0);
                    }
                    previousDocId = posting.Key;
                }
            }

            stream.Flush();
            stream.Flush(flushToDisk: true); // fsync 段内容，确保原子改名后段文件完整可读
        }

        // 原子改名：绝不 delete-then-move（中途崩溃会丢失段文件）。#192
        File.Move(tempPath, path, overwrite: true);
        SonnetDB.Wal.DirectoryFsync.FlushBestEffort(segmentsDirectory);

        return Read(path);
    }

    public static SegmentReader Read(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> magic = stackalloc byte[_magicV2.Length];
        ReadExactly(stream, magic);
        bool hasPositions;
        if (magic.SequenceEqual(_magicV2))
        {
            hasPositions = true;
        }
        else if (magic.SequenceEqual(_magicV1))
        {
            hasPositions = false;
        }
        else
        {
            throw new FormatException($"Invalid segment file: {path}");
        }

        long id = ReadInt64(stream);
        SegmentData data = new(id);

        int documentCount = VarInt.Read(stream);
        for (int i = 0; i < documentCount; i++)
        {
            int localId = VarInt.Read(stream);
            SegmentDocument document = new(localId, new DocumentId(ReadString(stream)));
            int fieldCount = VarInt.Read(stream);
            for (int j = 0; j < fieldCount; j++)
            {
                document.Fields[ReadString(stream)] = ReadString(stream);
            }
            data.Documents.Add(document);
        }

        int fieldLengthCount = VarInt.Read(stream);
        for (int i = 0; i < fieldLengthCount; i++)
        {
            string field = ReadString(stream);
            int count = VarInt.Read(stream);
            Dictionary<int, int> lengths = new();
            for (int j = 0; j < count; j++)
            {
                lengths[VarInt.Read(stream)] = VarInt.Read(stream);
            }
            data.FieldLengths[field] = lengths;
        }

        int postingListCount = VarInt.Read(stream);
        for (int i = 0; i < postingListCount; i++)
        {
            string field = ReadString(stream);
            string term = ReadString(stream);
            int count = VarInt.Read(stream);
            Dictionary<int, int> postings = new();
            Dictionary<int, List<int>> positions = new();
            int docId = 0;
            for (int j = 0; j < count; j++)
            {
                docId += VarInt.Read(stream);
                int termFrequency = VarInt.Read(stream);
                postings[docId] = termFrequency;
                if (hasPositions)
                {
                    int positionCount = VarInt.Read(stream);
                    List<int> values = new(positionCount);
                    int position = 0;
                    for (int k = 0; k < positionCount; k++)
                    {
                        position += VarInt.Read(stream);
                        values.Add(position);
                    }
                    positions[docId] = values;
                }
                else
                {
                    List<int> values = new(termFrequency);
                    for (int k = 0; k < termFrequency; k++)
                    {
                        values.Add(k);
                    }
                    positions[docId] = values;
                }
            }
            data.PostingLists.Add(new SegmentPostingList(field, term, postings, positions));
        }

        FileInfo file = new(path);
        return new SegmentReader(data, path, file.Length);
    }

    public static string GetPath(string segmentsDirectory, long segmentId)
    {
        return Path.Combine(segmentsDirectory, $"{segmentId:0000000000}.seg");
    }

    private static void WriteString(Stream stream, string value)
    {
        int byteCount = Encoding.UTF8.GetByteCount(value);
        VarInt.Write(stream, byteCount);
        if (byteCount <= 256)
        {
            Span<byte> buffer = stackalloc byte[256];
            int written = Encoding.UTF8.GetBytes(value.AsSpan(), buffer);
            stream.Write(buffer[..written]);
            return;
        }

        byte[] bytes = Encoding.UTF8.GetBytes(value);
        stream.Write(bytes);
    }

    private static string ReadString(Stream stream)
    {
        int byteCount = VarInt.Read(stream);
        if (byteCount == 0)
        {
            return string.Empty;
        }

        byte[] bytes = new byte[byteCount];
        ReadExactly(stream, bytes);
        return Encoding.UTF8.GetString(bytes);
    }

    private static void WriteInt64(Stream stream, long value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static long ReadInt64(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[sizeof(long)];
        ReadExactly(stream, buffer);
        return BinaryPrimitives.ReadInt64LittleEndian(buffer);
    }

    private static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = stream.Read(buffer[offset..]);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }
            offset += read;
        }
    }
}
