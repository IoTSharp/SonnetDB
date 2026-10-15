namespace SonnetDB.Tables;

/// <summary>无显式主键关系表的内部行身份；仅存在于物理行键，不增加 SQL 列。</summary>
internal static class TableRowIdentity
{
    private const byte Marker = (byte)'H';
    private const byte Version = 1;
    private const int EncodedLength = 18;

    /// <summary>分配不依赖业务值的 v1/GUID128 身份；随行及索引的原子批次持久化。</summary>
    internal static byte[] Create()
    {
        var bytes = new byte[EncodedLength];
        bytes[0] = Marker;
        bytes[1] = Version;
        _ = Guid.NewGuid().TryWriteBytes(bytes.AsSpan(2));
        return bytes;
    }

    /// <summary>识别完整内部行身份；后续版本必须显式升级此检查。</summary>
    internal static bool IsEncoded(ReadOnlySpan<byte> bytes)
        => bytes.Length == EncodedLength && bytes[0] == Marker && bytes[1] == Version
            && new Guid(bytes[2..]) != Guid.Empty;

    /// <summary>拒绝缺失、损坏或未知版本身份，避免把业务行内容当成内部键。</summary>
    internal static byte[] Require(byte[]? bytes)
        => bytes is not null && IsEncoded(bytes)
            ? bytes
            : throw new InvalidOperationException("无主键关系表变更缺少有效的内部行身份。");

    /// <summary>在进入事务缓冲前为每个无主键 INSERT 分配独立身份，重复数据不会归并。</summary>
    internal static TableRowMutation PrepareInsert(TableSchema schema, TableRowMutation mutation)
        => schema.PrimaryKey.Count == 0 && mutation.PrimaryKeyValues is null && mutation.NewValues is not null
            && mutation.EncodedRowIdentity is null
            ? mutation with { EncodedRowIdentity = Create() }
            : mutation;

    /// <summary>取得变更原始行的物理身份，不根据无主键行内容猜测身份。</summary>
    internal static byte[] TargetKey(TableSchema schema, TableRowMutation mutation)
    {
        if (schema.PrimaryKey.Count == 0)
            return Require(mutation.EncodedRowIdentity);
        if (mutation.PrimaryKeyValues is not null)
            return TableKeyCodec.EncodePrimaryKeyValues(schema, mutation.PrimaryKeyValues);
        if (mutation.NewValues is not null)
            return TableKeyCodec.EncodePrimaryKey(schema, mutation.NewValues);
        throw new InvalidOperationException("关系表变更缺少主键和新行值。");
    }

    /// <summary>主键表可按新值迁移主键，无主键表更新始终保留原身份。</summary>
    internal static byte[] CurrentKey(TableSchema schema, TableRowMutation mutation)
        => schema.PrimaryKey.Count == 0 || mutation.NewValues is null
            ? TargetKey(schema, mutation)
            : TableKeyCodec.EncodePrimaryKey(schema, mutation.NewValues);

    /// <summary>普通原生 INSERT 分配物理键，不改变既有主键编码。</summary>
    internal static byte[] InsertKey(TableSchema schema, IReadOnlyList<object?> values)
        => schema.PrimaryKey.Count == 0 ? Create() : TableKeyCodec.EncodePrimaryKey(schema, values);
}
