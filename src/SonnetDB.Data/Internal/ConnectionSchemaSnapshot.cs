using SonnetDB.Tables;

namespace SonnetDB.Data.Internal;

/// <summary>
/// ADO.NET 元数据所需的跨嵌入式/远程 schema 投影。
/// 只承载服务端已经公开的目录摘要，不把文档动态字段伪装成关系列。
/// </summary>
internal sealed record ConnectionSchemaSnapshot(
    IReadOnlyList<TableSchema> Tables,
    IReadOnlyList<ConnectionViewSchema> Views,
    IReadOnlyList<ConnectionDocumentCollectionSchema> DocumentCollections)
{
    public static ConnectionSchemaSnapshot Empty { get; } = new([], [], []);
}

internal sealed record ConnectionViewSchema(
    string Name,
    string DefinitionSql,
    DateTimeOffset CreatedUtc,
    bool IsMaterialized);

internal sealed record ConnectionDocumentCollectionSchema(
    string Name,
    DateTimeOffset CreatedUtc,
    int JsonIndexCount,
    int FullTextIndexCount,
    bool HasValidator);
