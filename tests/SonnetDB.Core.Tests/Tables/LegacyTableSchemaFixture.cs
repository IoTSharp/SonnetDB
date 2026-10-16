using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using SonnetDB.Tables;

namespace SonnetDB.Core.Tests.Tables;

/// <summary>按冻结的旧目录布局生成无索引测试夹具，避免把新格式仅改版本号伪装成旧格式。</summary>
internal static class LegacyTableSchemaFixture
{
    internal static void Write(string path, TableSchema schema, int version)
    {
        if (version is < 5 or > 11 || schema.Indexes.Count != 0 || schema.ForeignKeys.Count != 0
            || schema.CheckConstraints.Count != 0 || schema.Columns.Any(column => column.DataType is TableColumnType.Duration or TableColumnType.DateTimeOffset))
            throw new ArgumentException("该旧格式夹具只支持 v5-v11 基础列结构。");
        using var payload = new MemoryStream();
        using (var writer = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
        {
            WriteString(writer, schema.Name);
            writer.Write(schema.CreatedAtUtcTicks);
            writer.Write(checked((ushort)schema.Columns.Count));
            foreach (var column in schema.Columns)
            {
                WriteString(writer, column.Name);
                writer.Write((byte)column.DataType);
                byte flags = (byte)((column.IsPrimaryKey ? 1 : 0) | (column.IsNullable ? 2 : 0)
                    | (column.IsRowVersion ? 4 : 0) | (version >= 8 && column.IsAutoIncrement ? 8 : 0)
                    | (version >= 11 && column.HasExplicitDecimalDeclaration ? 16 : 0));
                writer.Write(flags);
                if (version >= 9 && column.DataType == TableColumnType.Decimal)
                {
                    writer.Write(column.DecimalPrecision);
                    writer.Write(column.DecimalScale);
                }
                if (version >= 7) WriteString(writer, column.DefaultExpressionSql ?? string.Empty);
            }
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            if (version >= 6) writer.Write((ushort)0);
        }
        byte[] data = payload.ToArray();
        byte[] file = new byte[32 + data.Length + 16];
        "SDBTBLv1"u8.CopyTo(file);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(8), version);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(12), 32);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(16), 1);
        data.CopyTo(file, 32);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(32 + data.Length), Crc32.HashToUInt32(data));
        "SDBTBLv1"u8.CopyTo(file.AsSpan(36 + data.Length));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, file);
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(checked((ushort)bytes.Length));
        writer.Write(bytes);
    }
}
