using SonnetDB.Catalog;

namespace SonnetDB.Engine;

/// <summary>同一线性化点上的 measurement schema 集合及其 revision。</summary>
/// <param name="Measurements">按名称排序的 measurement schema 快照。</param>
/// <param name="Revision">与该 schema 集合匹配的稳定内容 revision。</param>
public sealed record MeasurementSchemaSnapshot(
    IReadOnlyList<MeasurementSchema> Measurements,
    string Revision);
