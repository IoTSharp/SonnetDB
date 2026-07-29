using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;

namespace SonnetDB.Routines;

internal sealed record RoutineCatalogSnapshot(
    IReadOnlyList<ProcedureDefinition> Procedures,
    IReadOnlyList<TriggerDefinition> Triggers);

internal static class RoutineCatalogCodec
{
    public const string FileName = "routines.sdbrtn";

    private const int FormatVersion = 1;
    private const int HeaderSize = 32;
    private const int FooterSize = 16;
    private const int MaxDefinitions = 100_000;
    private const int MaxParameters = 1_024;
    private const int MaxNameBytes = 1_024;
    private const int MaxSqlBytes = 4 * 1024 * 1024;
    private static readonly byte[] Magic = "SDBRTN01"u8.ToArray();
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    public static RoutineCatalogSnapshot Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path))
            return new RoutineCatalogSnapshot([], []);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Load(stream);
    }

    public static void Save(
        string path,
        IReadOnlyList<ProcedureDefinition> procedures,
        IReadOnlyList<TriggerDefinition> triggers)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(procedures);
        ArgumentNullException.ThrowIfNull(triggers);
        if (procedures.Count > MaxDefinitions || triggers.Count > MaxDefinitions)
            throw new InvalidDataException($"过程或触发器数量超过上限 {MaxDefinitions}。");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporaryPath = path + ".tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                Save(stream, procedures, triggers);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch
        {
            File.Delete(temporaryPath);
            throw;
        }
    }

    private static RoutineCatalogSnapshot Load(Stream source)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        ReadExact(source, header, "header");
        if (!header[..Magic.Length].SequenceEqual(Magic))
            throw new InvalidDataException("RoutineCatalog: invalid header magic.");
        int version = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(8, 4));
        if (version != FormatVersion)
            throw new InvalidDataException($"RoutineCatalog: unsupported format version {version}.");
        if (BinaryPrimitives.ReadInt32LittleEndian(header.Slice(12, 4)) != HeaderSize)
            throw new InvalidDataException("RoutineCatalog: unexpected header size.");
        int procedureCount = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(16, 4));
        int triggerCount = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(20, 4));
        ValidateCount(procedureCount, "procedure");
        ValidateCount(triggerCount, "trigger");

        var crc = new Crc32();
        var procedures = new List<ProcedureDefinition>(procedureCount);
        var procedureNames = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < procedureCount; index++)
        {
            string name = ReadString(source, crc, MaxNameBytes, $"procedure {index} name")!;
            long createdAt = ReadInt64(source, crc, $"procedure {index} created at");
            int parameterCount = ReadInt32(source, crc, $"procedure {index} parameter count");
            if (parameterCount is < 0 or > MaxParameters)
                throw new InvalidDataException($"RoutineCatalog: invalid parameter count {parameterCount}.");
            var parameters = new SqlProcedureParameter[parameterCount];
            for (int parameterIndex = 0; parameterIndex < parameterCount; parameterIndex++)
            {
                string parameterName = ReadString(
                    source,
                    crc,
                    MaxNameBytes,
                    $"procedure {index} parameter {parameterIndex} name")!;
                byte type = ReadByte(source, crc, $"procedure {index} parameter {parameterIndex} type");
                if (!Enum.IsDefined((SqlProcedureParameterType)type))
                    throw new InvalidDataException($"RoutineCatalog: invalid parameter type {type}.");
                parameters[parameterIndex] = new SqlProcedureParameter(
                    parameterName,
                    (SqlProcedureParameterType)type);
            }
            string bodySql = ReadString(source, crc, MaxSqlBytes, $"procedure {index} SQL body")!;
            if (!procedureNames.Add(name))
                throw new InvalidDataException($"RoutineCatalog: duplicate procedure '{name}'.");
            try
            {
                procedures.Add(ProcedureDefinition.Restore(name, parameters, bodySql, createdAt));
            }
            catch (Exception exception) when (exception is ArgumentException or SqlParseException)
            {
                throw new InvalidDataException($"RoutineCatalog: procedure '{name}' is invalid.", exception);
            }
        }

        var triggers = new List<TriggerDefinition>(triggerCount);
        var triggerNames = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < triggerCount; index++)
        {
            string name = ReadString(source, crc, MaxNameBytes, $"trigger {index} name")!;
            string tableName = ReadString(source, crc, MaxNameBytes, $"trigger {index} table")!;
            byte eventValue = ReadByte(source, crc, $"trigger {index} event");
            if (!Enum.IsDefined((SqlTriggerEvent)eventValue))
                throw new InvalidDataException($"RoutineCatalog: invalid trigger event {eventValue}.");
            long createdAt = ReadInt64(source, crc, $"trigger {index} created at");
            string? whenSql = ReadString(source, crc, MaxSqlBytes, $"trigger {index} WHEN", nullable: true);
            string bodySql = ReadString(source, crc, MaxSqlBytes, $"trigger {index} SQL body")!;
            if (!triggerNames.Add(name))
                throw new InvalidDataException($"RoutineCatalog: duplicate trigger '{name}'.");
            try
            {
                triggers.Add(TriggerDefinition.Restore(
                    name,
                    tableName,
                    (SqlTriggerEvent)eventValue,
                    whenSql,
                    bodySql,
                    createdAt));
            }
            catch (Exception exception) when (exception is ArgumentException or SqlParseException)
            {
                throw new InvalidDataException($"RoutineCatalog: trigger '{name}' is invalid.", exception);
            }
        }

        Span<byte> footer = stackalloc byte[FooterSize];
        ReadExact(source, footer, "footer");
        uint storedCrc = BinaryPrimitives.ReadUInt32LittleEndian(footer[..4]);
        if (storedCrc != crc.GetCurrentHashAsUInt32())
            throw new InvalidDataException("RoutineCatalog: payload CRC mismatch.");
        if (!footer.Slice(4, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("RoutineCatalog: invalid footer magic.");
        if (source.ReadByte() != -1)
            throw new InvalidDataException("RoutineCatalog: trailing bytes detected.");

        return new RoutineCatalogSnapshot(procedures, triggers);
    }

    private static void Save(
        Stream destination,
        IReadOnlyList<ProcedureDefinition> procedures,
        IReadOnlyList<TriggerDefinition> triggers)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        header.Clear();
        Magic.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(8, 4), FormatVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(12, 4), HeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(16, 4), procedures.Count);
        BinaryPrimitives.WriteInt32LittleEndian(header.Slice(20, 4), triggers.Count);
        destination.Write(header);

        var crc = new Crc32();
        foreach (var procedure in procedures.OrderBy(static value => value.Name, StringComparer.Ordinal))
        {
            WriteString(destination, crc, procedure.Name, MaxNameBytes, nullable: false);
            WriteInt64(destination, crc, procedure.CreatedAtUtcTicks);
            WriteInt32(destination, crc, procedure.Parameters.Count);
            foreach (var parameter in procedure.Parameters)
            {
                WriteString(destination, crc, parameter.Name, MaxNameBytes, nullable: false);
                WriteByte(destination, crc, (byte)parameter.DataType);
            }
            WriteString(destination, crc, procedure.BodySql, MaxSqlBytes, nullable: false);
        }

        foreach (var trigger in triggers.OrderBy(static value => value.Name, StringComparer.Ordinal))
        {
            WriteString(destination, crc, trigger.Name, MaxNameBytes, nullable: false);
            WriteString(destination, crc, trigger.TableName, MaxNameBytes, nullable: false);
            WriteByte(destination, crc, (byte)trigger.Event);
            WriteInt64(destination, crc, trigger.CreatedAtUtcTicks);
            WriteString(destination, crc, trigger.WhenSql, MaxSqlBytes, nullable: true);
            WriteString(destination, crc, trigger.BodySql, MaxSqlBytes, nullable: false);
        }

        Span<byte> footer = stackalloc byte[FooterSize];
        footer.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(footer[..4], crc.GetCurrentHashAsUInt32());
        Magic.CopyTo(footer.Slice(4, Magic.Length));
        destination.Write(footer);
    }

    private static void ValidateCount(int count, string kind)
    {
        if (count is < 0 or > MaxDefinitions)
            throw new InvalidDataException($"RoutineCatalog: invalid {kind} count {count}.");
    }

    private static string? ReadString(
        Stream source,
        Crc32 crc,
        int maximumBytes,
        string description,
        bool nullable = false)
    {
        int length = ReadInt32(source, crc, description + " length");
        if (length == -1 && nullable)
            return null;
        if (length < 0 || length > maximumBytes)
            throw new InvalidDataException($"RoutineCatalog: invalid {description} length {length}.");
        byte[] buffer = new byte[length];
        ReadExact(source, buffer, description);
        crc.Append(buffer);
        try
        {
            return Utf8.GetString(buffer);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException($"RoutineCatalog: {description} is not valid UTF-8.", exception);
        }
    }

    private static void WriteString(
        Stream destination,
        Crc32 crc,
        string? value,
        int maximumBytes,
        bool nullable)
    {
        if (value is null)
        {
            if (!nullable)
                throw new InvalidDataException("RoutineCatalog: required string is null.");
            WriteInt32(destination, crc, -1);
            return;
        }

        int length = Utf8.GetByteCount(value);
        if (length > maximumBytes)
            throw new InvalidDataException($"RoutineCatalog: string exceeds {maximumBytes} UTF-8 bytes.");
        WriteInt32(destination, crc, length);
        if (length == 0)
            return;
        byte[] bytes = Utf8.GetBytes(value);
        crc.Append(bytes);
        destination.Write(bytes);
    }

    private static int ReadInt32(Stream source, Crc32 crc, string description)
    {
        Span<byte> buffer = stackalloc byte[4];
        ReadExact(source, buffer, description);
        crc.Append(buffer);
        return BinaryPrimitives.ReadInt32LittleEndian(buffer);
    }

    private static long ReadInt64(Stream source, Crc32 crc, string description)
    {
        Span<byte> buffer = stackalloc byte[8];
        ReadExact(source, buffer, description);
        crc.Append(buffer);
        return BinaryPrimitives.ReadInt64LittleEndian(buffer);
    }

    private static byte ReadByte(Stream source, Crc32 crc, string description)
    {
        int value = source.ReadByte();
        if (value < 0)
            throw new InvalidDataException($"RoutineCatalog: {description} is truncated.");
        Span<byte> buffer = stackalloc byte[1] { (byte)value };
        crc.Append(buffer);
        return (byte)value;
    }

    private static void WriteInt32(Stream destination, Crc32 crc, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        crc.Append(buffer);
        destination.Write(buffer);
    }

    private static void WriteInt64(Stream destination, Crc32 crc, long value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
        crc.Append(buffer);
        destination.Write(buffer);
    }

    private static void WriteByte(Stream destination, Crc32 crc, byte value)
    {
        Span<byte> buffer = stackalloc byte[1] { value };
        crc.Append(buffer);
        destination.Write(buffer);
    }

    private static void ReadExact(Stream source, Span<byte> destination, string description)
    {
        int read = 0;
        while (read < destination.Length)
        {
            int current = source.Read(destination[read..]);
            if (current == 0)
                throw new InvalidDataException($"RoutineCatalog: {description} is truncated.");
            read += current;
        }
    }
}
