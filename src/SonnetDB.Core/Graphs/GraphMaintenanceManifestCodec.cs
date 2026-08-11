using System.Buffers.Binary;
using System.IO.Hashing;

namespace SonnetDB.Graphs;

internal static class GraphMaintenanceManifestCodec
{
    internal const string FileName = "maintenance.sdbgraph";

    private const int FormatVersion = 1;
    private const int HeaderSize = 136;
    private const int DefinitionSize = 12;
    private const int FooterSize = 8;
    private const int MaximumManifestBytes = 16 * 1024 * 1024;
    private static readonly byte[] Magic = "SDBGMNT1"u8.ToArray();

    internal static GraphMaintenanceState? Load(string path, Guid expectedStorageId)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (expectedStorageId == Guid.Empty)
            throw new ArgumentException("Graph storage ID 不能为空。", nameof(expectedStorageId));
        if (!File.Exists(path))
            return null;

        byte[] encoded;
        using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (source.Length < HeaderSize + sizeof(uint) + FooterSize
                || source.Length > MaximumManifestBytes)
            {
                throw new InvalidDataException("Graph maintenance manifest 长度无效。");
            }
            encoded = new byte[checked((int)source.Length)];
            source.ReadExactly(encoded);
        }

        ReadOnlySpan<byte> span = encoded;
        if (!span[..Magic.Length].SequenceEqual(Magic)
            || BinaryPrimitives.ReadInt32LittleEndian(span[8..]) != FormatVersion
            || BinaryPrimitives.ReadInt32LittleEndian(span[12..]) != HeaderSize
            || !span[^FooterSize..].SequenceEqual(Magic))
        {
            throw new InvalidDataException("Graph maintenance manifest header 无效。");
        }

        uint expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(span.Length - FooterSize - sizeof(uint), sizeof(uint)));
        uint actualCrc = Crc32.HashToUInt32(span[..(span.Length - FooterSize - sizeof(uint))]);
        if (expectedCrc != actualCrc)
            throw new InvalidDataException("Graph maintenance manifest CRC32 不匹配。");

        Guid storageId = new(span[16..32]);
        Guid operationId = new(span[32..48]);
        var phase = (GraphMaintenancePhase)BinaryPrimitives.ReadInt32LittleEndian(span[48..]);
        int flags = BinaryPrimitives.ReadInt32LittleEndian(span[52..]);
        int definitionIndex = BinaryPrimitives.ReadInt32LittleEndian(span[112..]);
        int afterKeyLength = BinaryPrimitives.ReadInt32LittleEndian(span[116..]);
        int previousUniqueKeyLength = BinaryPrimitives.ReadInt32LittleEndian(span[120..]);
        int definitionCount = BinaryPrimitives.ReadInt32LittleEndian(span[124..]);
        int maximumDefinitions = BinaryPrimitives.ReadInt32LittleEndian(span[128..]);
        if (storageId != expectedStorageId
            || operationId == Guid.Empty
            || !Enum.IsDefined(phase)
            || phase == GraphMaintenancePhase.Completed
            || (flags & ~1) != 0
            || BinaryPrimitives.ReadInt32LittleEndian(span[132..]) != 0
            || definitionIndex < 0
            || afterKeyLength < 0
            || afterKeyLength > Graphs.Storage.GraphKeyCodec.MaxEncodedKeyBytes
            || previousUniqueKeyLength < 0
            || previousUniqueKeyLength > Graphs.Storage.GraphKeyCodec.MaxEncodedKeyBytes
            || definitionCount < 0
            || maximumDefinitions is <= 0 or > 1_000_000
            || definitionCount > maximumDefinitions)
        {
            throw new InvalidDataException("Graph maintenance manifest 字段无效。");
        }

        int expectedLength = checked(
            HeaderSize
            + afterKeyLength
            + previousUniqueKeyLength
            + definitionCount * DefinitionSize
            + sizeof(uint)
            + FooterSize);
        if (encoded.Length != expectedLength)
            throw new InvalidDataException("Graph maintenance manifest payload 长度无效。");

        int offset = HeaderSize;
        byte[] afterKey = span.Slice(offset, afterKeyLength).ToArray();
        offset += afterKeyLength;
        byte[] previousUniqueKey = span.Slice(offset, previousUniqueKeyLength).ToArray();
        offset += previousUniqueKeyLength;
        var definitions = new List<GraphUniqueIndexDefinition>(definitionCount);
        for (int index = 0; index < definitionCount; index++)
        {
            ReadOnlySpan<byte> definition = span.Slice(offset, DefinitionSize);
            var elementType = (GraphElementType)definition[0];
            if (definition[1] != 0 || definition[2] != 0 || definition[3] != 0)
                throw new InvalidDataException("Graph maintenance unique definition 保留字节无效。");
            try
            {
                definitions.Add(new GraphUniqueIndexDefinition(
                    elementType,
                    new LabelId(BinaryPrimitives.ReadInt32LittleEndian(definition[4..])),
                    BinaryPrimitives.ReadInt32LittleEndian(definition[8..])));
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException("Graph maintenance unique definition 无效。", exception);
            }
            offset += DefinitionSize;
        }
        if (definitions.Distinct().Count() != definitions.Count)
            throw new InvalidDataException("Graph maintenance unique definition 重复。");

        long sourceSequence = ReadNonNegativeInt64(span[56..], "source sequence");
        long lastSequence = ReadNonNegativeInt64(span[64..], "last sequence");
        if (lastSequence < sourceSequence
            || !IsContinuationStateValid(
                phase,
                definitionIndex,
                definitionCount,
                afterKeyLength,
                previousUniqueKeyLength))
        {
            throw new InvalidDataException("Graph maintenance manifest continuation 状态无效。");
        }

        return new GraphMaintenanceState
        {
            StorageId = storageId,
            OperationId = operationId,
            Phase = phase,
            CompactOnCompletion = (flags & 1) != 0,
            SourceSequence = sourceSequence,
            LastSequence = lastSequence,
            ScannedRecords = ReadNonNegativeInt64(span[72..], "scanned records"),
            RepairedEntries = ReadNonNegativeInt64(span[80..], "repaired entries"),
            RemovedEntries = ReadNonNegativeInt64(span[88..], "removed entries"),
            WorkUnits = ReadNonNegativeInt64(span[96..], "work units"),
            CheckpointCount = ReadNonNegativeInt64(span[104..], "checkpoint count"),
            UniqueDefinitionIndex = definitionIndex,
            AfterKey = afterKey,
            PreviousUniqueKey = previousUniqueKey,
            UniqueDefinitions = definitions,
            MaxUniqueIndexDefinitions = maximumDefinitions,
        };
    }

    internal static void Save(string path, GraphMaintenanceState state)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(state);
        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
            throw new ArgumentException("Graph maintenance manifest 必须包含父目录。", nameof(path));
        if (state.Phase == GraphMaintenancePhase.Completed)
            throw new ArgumentException("已完成的 Graph maintenance state 不应继续持久化。", nameof(state));

        SortDefinitions(state.UniqueDefinitions);
        int length = checked(
            HeaderSize
            + state.AfterKey.Length
            + state.PreviousUniqueKey.Length
            + state.UniqueDefinitions.Count * DefinitionSize
            + sizeof(uint)
            + FooterSize);
        if (length > MaximumManifestBytes)
            throw new InvalidOperationException("Graph maintenance manifest 超过持久化上限。");

        byte[] encoded = new byte[length];
        Span<byte> span = encoded;
        Magic.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], FormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(span[12..], HeaderSize);
        state.StorageId.TryWriteBytes(span[16..32]);
        state.OperationId.TryWriteBytes(span[32..48]);
        BinaryPrimitives.WriteInt32LittleEndian(span[48..], (int)state.Phase);
        BinaryPrimitives.WriteInt32LittleEndian(span[52..], state.CompactOnCompletion ? 1 : 0);
        BinaryPrimitives.WriteInt64LittleEndian(span[56..], state.SourceSequence);
        BinaryPrimitives.WriteInt64LittleEndian(span[64..], state.LastSequence);
        BinaryPrimitives.WriteInt64LittleEndian(span[72..], state.ScannedRecords);
        BinaryPrimitives.WriteInt64LittleEndian(span[80..], state.RepairedEntries);
        BinaryPrimitives.WriteInt64LittleEndian(span[88..], state.RemovedEntries);
        BinaryPrimitives.WriteInt64LittleEndian(span[96..], state.WorkUnits);
        BinaryPrimitives.WriteInt64LittleEndian(span[104..], state.CheckpointCount);
        BinaryPrimitives.WriteInt32LittleEndian(span[112..], state.UniqueDefinitionIndex);
        BinaryPrimitives.WriteInt32LittleEndian(span[116..], state.AfterKey.Length);
        BinaryPrimitives.WriteInt32LittleEndian(span[120..], state.PreviousUniqueKey.Length);
        BinaryPrimitives.WriteInt32LittleEndian(span[124..], state.UniqueDefinitions.Count);
        BinaryPrimitives.WriteInt32LittleEndian(span[128..], state.MaxUniqueIndexDefinitions);

        int offset = HeaderSize;
        state.AfterKey.CopyTo(span[offset..]);
        offset += state.AfterKey.Length;
        state.PreviousUniqueKey.CopyTo(span[offset..]);
        offset += state.PreviousUniqueKey.Length;
        foreach (GraphUniqueIndexDefinition item in state.UniqueDefinitions)
        {
            span[offset] = (byte)item.ElementType;
            BinaryPrimitives.WriteInt32LittleEndian(span[(offset + 4)..], item.LabelId.Value);
            BinaryPrimitives.WriteInt32LittleEndian(span[(offset + 8)..], item.PropertyId);
            offset += DefinitionSize;
        }

        uint crc = Crc32.HashToUInt32(span[..offset]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[offset..], crc);
        Magic.CopyTo(span[(offset + sizeof(uint))..]);

        Directory.CreateDirectory(directory);
        string temporaryPath = path + ".tmp";
        try
        {
            using (var destination = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                options: FileOptions.WriteThrough))
            {
                destination.Write(encoded);
                destination.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
            SonnetDB.Wal.DirectoryFsync.FlushRequired(directory);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    internal static void Delete(string path)
    {
        if (!File.Exists(path))
            return;
        File.Delete(path);
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            SonnetDB.Wal.DirectoryFsync.FlushRequired(directory);
    }

    internal static void SortDefinitions(List<GraphUniqueIndexDefinition> definitions)
        => definitions.Sort(static (left, right) =>
        {
            int comparison = left.ElementType.CompareTo(right.ElementType);
            if (comparison != 0)
                return comparison;
            comparison = left.LabelId.CompareTo(right.LabelId);
            return comparison != 0 ? comparison : left.PropertyId.CompareTo(right.PropertyId);
        });

    private static bool IsContinuationStateValid(
        GraphMaintenancePhase phase,
        int definitionIndex,
        int definitionCount,
        int afterKeyLength,
        int previousUniqueKeyLength)
    {
        bool usesDefinitionIndex = phase is GraphMaintenancePhase.ValidateUniqueIndexes
            or GraphMaintenancePhase.RepairUniqueIndexes;
        if (usesDefinitionIndex
            ? definitionCount == 0 || definitionIndex >= definitionCount
            : definitionIndex != 0)
        {
            return false;
        }

        if (previousUniqueKeyLength > 0
            && (phase != GraphMaintenancePhase.ValidateUniqueIndexes || afterKeyLength == 0))
        {
            return false;
        }

        return phase is not (GraphMaintenancePhase.Checkpoint or GraphMaintenancePhase.Compaction)
            || afterKeyLength == 0 && previousUniqueKeyLength == 0;
    }

    private static long ReadNonNegativeInt64(ReadOnlySpan<byte> source, string name)
    {
        long value = BinaryPrimitives.ReadInt64LittleEndian(source);
        return value >= 0
            ? value
            : throw new InvalidDataException($"Graph maintenance manifest {name} 无效。");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Preserve the original durable-save failure.
        }
    }
}
