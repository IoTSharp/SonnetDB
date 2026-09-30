using System.Text.Json.Serialization;

namespace SonnetDB.Cdc;

/// <summary>
/// 从固定源端视图读取一页行的委托。
/// </summary>
/// <param name="afterKey">排他的起始键；为空时从首行开始。</param>
/// <param name="maxRows">本页最多请求的行数。</param>
/// <param name="cancellationToken">取消令牌。</param>
/// <returns>按稳定键顺序返回的行；空页表示源端视图已结束。</returns>
public delegate ValueTask<IReadOnlyList<CdcSnapshotRow>> CdcSnapshotPageReader(
    string? afterKey,
    int maxRows,
    CancellationToken cancellationToken);

/// <summary>源端固定读视图文件中的不可变正文。</summary>
internal sealed record CdcSourceReadViewDocument(
    [property: JsonRequired] int FormatVersion,
    [property: JsonRequired] CdcSnapshotDescriptor Descriptor,
    [property: JsonRequired] IReadOnlyList<CdcSnapshotRow> Rows);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(CdcSourceReadViewDocument))]
[JsonSerializable(typeof(CdcSnapshotDescriptor))]
[JsonSerializable(typeof(CdcSnapshotRow))]
[JsonSerializable(typeof(List<CdcSnapshotRow>))]
internal sealed partial class CdcSourceReadViewJsonContext : JsonSerializerContext;
