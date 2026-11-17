using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using SonnetDB.FullText.Index;

namespace SonnetDB.FullText.Storage;

internal static class SegmentFile
{
    private static readonly byte[] _magicV1 = "DSSEG001"u8.ToArray();
    private static readonly byte[] _magicV2 = "DSSEG002"u8.ToArray();
    private static readonly byte[] _magicV3 = "DSSEG003"u8.ToArray();
    private static readonly byte[] _magicV4 = "DSSEG004"u8.ToArray();
    private const int PostingBlockSize = 128;
    private const int MaxPostingBlockBytes = 64 * 1024 * 1024;

    public static SegmentReader Write(
        string segmentsDirectory,
        SegmentData data,
        FullTextBlockCache? blockCache = null,
        bool lazyPostingBlocks = false)
    {
        Directory.CreateDirectory(segmentsDirectory);
        string path = GetPath(segmentsDirectory, data.Id);
        string tempPath = path + ".tmp";

        using (FileStream stream = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(_magicV4);
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

                KeyValuePair<int, int>[] orderedPostings = postingList.Postings.OrderBy(static x => x.Key).ToArray();
                int blockCount = orderedPostings.Length == 0
                    ? 0
                    : checked((orderedPostings.Length + PostingBlockSize - 1) / PostingBlockSize);
                VarInt.Write(stream, blockCount);
                for (int blockStart = 0; blockStart < orderedPostings.Length; blockStart += PostingBlockSize)
                {
                    int blockEnd = Math.Min(orderedPostings.Length, blockStart + PostingBlockSize);
                    using var payload = new MemoryStream();
                    int previousDocId = 0;
                    int maxTermFrequency = 0;
                    for (int index = blockStart; index < blockEnd; index++)
                    {
                        KeyValuePair<int, int> posting = orderedPostings[index];
                        VarInt.Write(payload, checked(posting.Key - previousDocId));
                        VarInt.Write(payload, posting.Value);
                        maxTermFrequency = Math.Max(maxTermFrequency, posting.Value);
                        if (postingList.Positions.TryGetValue(posting.Key, out List<int>? positions))
                        {
                            VarInt.Write(payload, positions.Count);
                            int previousPosition = 0;
                            foreach (int position in positions.Order())
                            {
                                VarInt.Write(payload, checked(position - previousPosition));
                                previousPosition = position;
                            }
                        }
                        else
                        {
                            VarInt.Write(payload, 0);
                        }

                        previousDocId = posting.Key;
                    }

                    if (payload.Length > MaxPostingBlockBytes)
                    {
                        throw new InvalidDataException($"Posting block exceeds {MaxPostingBlockBytes} bytes.");
                    }

                    byte[] payloadBytes = payload.ToArray();
                    VarInt.Write(stream, payloadBytes.Length);
                    WriteInt32(stream, orderedPostings[blockStart].Key);
                    WriteInt32(stream, orderedPostings[blockEnd - 1].Key);
                    WriteInt32(stream, maxTermFrequency);
                    VarInt.Write(stream, blockEnd - blockStart);
                    WriteUInt32(stream, Crc32.HashToUInt32(payloadBytes));
                    stream.Write(payloadBytes);
                }
            }

            stream.Flush();
            stream.Flush(flushToDisk: true); // fsync 段内容，确保原子改名后段文件完整可读
        }

        // 原子改名：绝不 delete-then-move（中途崩溃会丢失段文件）。#192
        File.Move(tempPath, path, overwrite: true);
        SonnetDB.Wal.DirectoryFsync.FlushBestEffort(segmentsDirectory);

        return Read(path, blockCache, lazyPostingBlocks);
    }

    public static SegmentReader Read(
        string path,
        FullTextBlockCache? blockCache = null,
        bool lazyPostingBlocks = false)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> magic = stackalloc byte[_magicV2.Length];
        ReadExactly(stream, magic);
        bool hasPositions;
        bool hasPostingBlocks;
        bool hasPostingCounts;
        if (magic.SequenceEqual(_magicV2))
        {
            hasPositions = true;
            hasPostingBlocks = false;
            hasPostingCounts = false;
        }
        else if (magic.SequenceEqual(_magicV3))
        {
            hasPositions = true;
            hasPostingBlocks = true;
            hasPostingCounts = false;
        }
        else if (magic.SequenceEqual(_magicV4))
        {
            hasPositions = true;
            hasPostingBlocks = true;
            hasPostingCounts = true;
        }
        else if (magic.SequenceEqual(_magicV1))
        {
            hasPositions = false;
            hasPostingBlocks = false;
            hasPostingCounts = false;
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
            var postingList = new SegmentPostingList(field, term, postings, positions);
            if (hasPostingBlocks)
            {
                int blockCount = VarInt.Read(stream);
                if (blockCount < 0 || blockCount > 1_000_000 || count == 0 && blockCount != 0 || count > 0 && (blockCount <= 0 || blockCount > count))
                {
                    throw new FormatException($"Invalid posting block count for term '{term}'.");
                }

                int decodedCount = 0;
                for (int block = 0; block < blockCount; block++)
                {
                    int blockLength = VarInt.Read(stream);
                    if (blockLength <= 0 || blockLength > MaxPostingBlockBytes)
                    {
                        throw new FormatException($"Invalid posting block length {blockLength} for term '{term}'.");
                    }
                    int firstDocumentId = ReadInt32(stream);
                    int lastDocumentId = ReadInt32(stream);
                    int maxTermFrequency = ReadInt32(stream);
                    int postingCount = hasPostingCounts ? VarInt.Read(stream) : -1;
                    uint expectedCrc = ReadUInt32(stream);
                    long payloadOffset = stream.Position;
                    if (payloadOffset > stream.Length - blockLength)
                    {
                        throw new InvalidDataException($"Posting block exceeds segment boundary for term '{term}'.");
                    }

                    if (firstDocumentId > lastDocumentId || maxTermFrequency <= 0)
                    {
                        throw new FormatException($"Invalid posting block directory for term '{term}'.");
                    }

                    if (hasPostingCounts
                        && (postingCount <= 0 || postingCount > PostingBlockSize))
                    {
                        throw new FormatException($"Invalid posting count for term '{term}'.");
                    }

                    if (lazyPostingBlocks)
                    {
                        stream.Position += blockLength;
                    }
                    else
                    {
                        byte[] payload = new byte[blockLength];
                        ReadExactly(stream, payload);
                        uint actualCrc = Crc32.HashToUInt32(payload);
                        if (actualCrc != expectedCrc)
                        {
                            throw new InvalidDataException($"Posting block CRC32 mismatch for term '{term}'.");
                        }

                        DecodePostingBlock(
                            payload,
                            term,
                            firstDocumentId,
                            lastDocumentId,
                            maxTermFrequency,
                            postingCount,
                            postings,
                            positions,
                            ref decodedCount);
                    }

                    postingList.Blocks.Add(new PostingBlockInfo(
                        payloadOffset,
                        blockLength,
                        firstDocumentId,
                        lastDocumentId,
                        maxTermFrequency,
                        postingCount,
                        expectedCrc));
                }

                if (!lazyPostingBlocks && decodedCount != count)
                {
                    throw new FormatException($"Posting block count mismatch for term '{term}'.");
                }
            }
            else
            {
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
            }
            data.PostingLists.Add(postingList);
        }

        FileInfo file = new(path);
        return new SegmentReader(data, path, file.Length, blockCache, lazyPostingBlocks);
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

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static int ReadInt32(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        ReadExactly(stream, buffer);
        return BinaryPrimitives.ReadInt32LittleEndian(buffer);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void DecodePostingBlock(
        byte[] payload,
        string term,
        int firstDocumentId,
        int lastDocumentId,
        int maxTermFrequency,
        int postingCount,
        Dictionary<int, int> postings,
        Dictionary<int, List<int>> positions,
        ref int decodedCount)
    {
        using var payloadStream = new MemoryStream(payload, writable: false);
        int previousDocId = 0;
        int firstDecodedDocId = int.MaxValue;
        int lastDecodedDocId = int.MinValue;
        int actualMaxTermFrequency = 0;
        int blockDecoded = 0;
        while (payloadStream.Position < payloadStream.Length)
        {
            int docId = checked(previousDocId + VarInt.Read(payloadStream));
            int termFrequency = VarInt.Read(payloadStream);
            if (termFrequency <= 0)
            {
                throw new FormatException($"Invalid term frequency for term '{term}'.");
            }

            postings[docId] = termFrequency;
            int positionCount = VarInt.Read(payloadStream);
            if (positionCount < 0 || positionCount > MaxPostingBlockBytes)
            {
                throw new FormatException($"Invalid position count for term '{term}'.");
            }
            List<int> values = new(positionCount);
            int position = 0;
            for (int k = 0; k < positionCount; k++)
            {
                position = checked(position + VarInt.Read(payloadStream));
                values.Add(position);
            }
            positions[docId] = values;
            previousDocId = docId;
            blockDecoded++;
            decodedCount++;
            firstDecodedDocId = Math.Min(firstDecodedDocId, docId);
            lastDecodedDocId = Math.Max(lastDecodedDocId, docId);
            actualMaxTermFrequency = Math.Max(actualMaxTermFrequency, termFrequency);
        }

        if (blockDecoded == 0
            || firstDocumentId != firstDecodedDocId
            || lastDocumentId != lastDecodedDocId
            || maxTermFrequency < actualMaxTermFrequency
            || (postingCount > 0 && postingCount != blockDecoded))
        {
            throw new FormatException($"Invalid posting block payload for term '{term}'.");
        }
    }

    private static uint ReadUInt32(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        ReadExactly(stream, buffer);
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
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
