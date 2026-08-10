using System.Text;

namespace SonnetDB.Graphs;

/// <summary>
/// 单个原生属性图的不可变目录定义。
/// </summary>
public sealed class GraphDefinition
{
    /// <summary>当前图记录与键布局的组合存储版本。</summary>
    public const int CurrentRecordFormatVersion = Storage.GraphStorageFormat.RecordFormatVersion;

    internal const int MaxNameBytes = 1_024;

    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    private GraphDefinition(
        string name,
        Guid storageId,
        long createdAtUtcTicks,
        int recordFormatVersion)
    {
        Name = name;
        StorageId = storageId;
        CreatedAtUtcTicks = createdAtUtcTicks;
        RecordFormatVersion = recordFormatVersion;
    }

    /// <summary>图名称，按序号比较并区分大小写。</summary>
    public string Name { get; }

    /// <summary>图物理存储的稳定标识符。</summary>
    public Guid StorageId { get; }

    /// <summary>图创建时间（UTC ticks）。</summary>
    public long CreatedAtUtcTicks { get; }

    /// <summary>图记录与键布局的组合存储版本；任一布局变化都必须升级该值。</summary>
    public int RecordFormatVersion { get; }

    /// <summary>
    /// 创建新的图目录定义。
    /// </summary>
    /// <param name="name">图名称。</param>
    /// <returns>带有新物理存储标识符的图定义。</returns>
    public static GraphDefinition Create(string name)
        => Restore(
            name,
            Guid.NewGuid(),
            DateTime.UtcNow.Ticks,
            CurrentRecordFormatVersion);

    internal static GraphDefinition Restore(
        string name,
        Guid storageId,
        long createdAtUtcTicks,
        int recordFormatVersion)
    {
        ValidateName(name);
        if (storageId == Guid.Empty)
            throw new ArgumentException("图物理存储标识符不能为空。", nameof(storageId));
        if (createdAtUtcTicks <= 0 || createdAtUtcTicks > DateTime.MaxValue.Ticks)
            throw new ArgumentOutOfRangeException(nameof(createdAtUtcTicks));
        if (recordFormatVersion != CurrentRecordFormatVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(recordFormatVersion),
                recordFormatVersion,
                $"不支持图记录格式版本 {recordFormatVersion}。");
        }

        return new GraphDefinition(name, storageId, createdAtUtcTicks, recordFormatVersion);
    }

    internal static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (Utf8.GetByteCount(name) > MaxNameBytes)
            throw new ArgumentException($"图名称的 UTF-8 长度不能超过 {MaxNameBytes} 字节。", nameof(name));

        foreach (char character in name)
        {
            if (char.IsControl(character))
                throw new ArgumentException("图名称不能包含控制字符。", nameof(name));
        }
    }
}
