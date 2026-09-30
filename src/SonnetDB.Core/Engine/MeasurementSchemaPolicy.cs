using System.Collections.ObjectModel;

namespace SonnetDB.Engine;

/// <summary>measurement 自动 schema 变更的许可模式。</summary>
public enum MeasurementSchemaMode
{
    /// <summary>拒绝自动创建 measurement 与自动扩列、类型提升。</summary>
    Disabled,

    /// <summary>允许自动创建 measurement，拒绝已有 measurement 的自动演进。</summary>
    CreateOnly,

    /// <summary>允许自动创建、扩列与兼容的 INT64 到 FLOAT64 类型提升。</summary>
    CreateAndEvolve,
}

/// <summary>measurement schema 增长额度与自动变更策略。</summary>
public sealed record MeasurementSchemaPolicy
{
    /// <summary>未指定 measurement 覆盖时使用的模式；默认保持现有自动演进行为。</summary>
    public MeasurementSchemaMode Mode { get; init; } = MeasurementSchemaMode.CreateAndEvolve;

    /// <summary>按 measurement 名称覆盖默认模式，名称区分大小写。</summary>
    public IReadOnlyDictionary<string, MeasurementSchemaMode> MeasurementModes { get; init; }
        = new ReadOnlyDictionary<string, MeasurementSchemaMode>(
            new Dictionary<string, MeasurementSchemaMode>(StringComparer.Ordinal));

    /// <summary>一个数据库允许的 measurement 总数。</summary>
    public int MaxMeasurements { get; init; } = 16_384;

    /// <summary>单个 measurement 允许的列总数，包括 TAG 与 FIELD。</summary>
    public int MaxColumnsPerMeasurement { get; init; } = 256;

    /// <summary>一次写入规划允许新增的列总数；批量写入按内部有界块规划。</summary>
    public int MaxNewColumnsPerWrite { get; init; } = 32;

    /// <summary>默认策略。</summary>
    public static MeasurementSchemaPolicy Default { get; } = new();

    /// <summary>返回指定 measurement 的有效模式。</summary>
    /// <param name="measurement">measurement 名称。</param>
    /// <returns>覆盖模式或默认模式。</returns>
    public MeasurementSchemaMode GetMode(string measurement)
    {
        ArgumentNullException.ThrowIfNull(measurement);
        return MeasurementModes.TryGetValue(measurement, out var mode) ? mode : Mode;
    }

    /// <summary>校验额度与模式，并复制 measurement 覆盖映射以防止调用方后续修改。</summary>
    /// <returns>持有只读覆盖映射的独立策略实例。</returns>
    public MeasurementSchemaPolicy ValidateAndCopy()
    {
        if (!Enum.IsDefined(Mode))
            throw new ArgumentOutOfRangeException(nameof(Mode), Mode, "无效的 measurement schema 模式。");
        ArgumentNullException.ThrowIfNull(MeasurementModes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxMeasurements);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxColumnsPerMeasurement);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxNewColumnsPerWrite);

        var modes = new Dictionary<string, MeasurementSchemaMode>(StringComparer.Ordinal);
        foreach (var (measurement, mode) in MeasurementModes)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(measurement);
            if (!Enum.IsDefined(mode))
                throw new ArgumentOutOfRangeException(nameof(MeasurementModes), mode, "无效的 measurement schema 覆盖模式。");
            modes.Add(measurement, mode);
        }

        return this with
        {
            MeasurementModes = new ReadOnlyDictionary<string, MeasurementSchemaMode>(modes),
        };
    }
}
