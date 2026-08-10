namespace SonnetDB.Graphs.Storage;

/// <summary>原生图 V1 持久化格式常量。</summary>
internal static class GraphStorageFormat
{
    // Catalog/marker 持久化的是这一组合版本。Key 与 record 不允许独立升级，避免新前缀把旧 key
    // 静默隐藏；任一布局变化都必须先升级此版本并提供显式迁移或拒绝路径。
    public const int StorageFormatVersion = 1;
    public const byte KeyFormatVersion = (byte)StorageFormatVersion;
    public const int RecordFormatVersion = StorageFormatVersion;
    public const int KeyHeaderSize = 6;
    public const int KeyCrcSize = sizeof(uint);

    public static ReadOnlySpan<byte> KeyMagic => "SDBG"u8;
    public static ReadOnlySpan<byte> RecordMagic => "SDBGREC1"u8;
}

/// <summary>图元素类别。</summary>
internal enum GraphElementKind : byte
{
    Vertex = 1,
    Edge = 2,
}

/// <summary>图 key family。</summary>
internal enum GraphKeyKind : byte
{
    VertexRecord = 0x10,
    EdgeRecord = 0x11,
    OutgoingAdjacency = 0x20,
    IncomingAdjacency = 0x21,
    VertexLabel = 0x30,
    EdgeLabel = 0x31,
    VertexPropertyIndex = 0x40,
    EdgePropertyIndex = 0x41,
    VertexUniqueProperty = 0x42,
    EdgeUniqueProperty = 0x43,
    Metadata = 0x50,
    TransactionRequest = 0x51,
}

/// <summary>Graph metadata high-water 类别。</summary>
internal enum GraphHighWaterKind : byte
{
    VertexId = 1,
    EdgeId = 2,
    LabelId = 3,
    PropertyId = 4,
}
