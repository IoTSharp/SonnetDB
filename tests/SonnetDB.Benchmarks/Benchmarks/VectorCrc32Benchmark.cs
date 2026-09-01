using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using SonnetDB.Vector.IO;

namespace SonnetDB.Benchmarks.Benchmarks;

/// <summary>比较向量文件 IEEE CRC32 旧查表实现与生产实现。</summary>
[Config(typeof(VectorCrc32BenchmarkConfig))]
[BenchmarkCategory("Vector", "Storage", "CRC32")]
// BenchmarkDotNet 会为 benchmark 生成派生类型，因此该类不能密封。
public class VectorCrc32Benchmark
{
    private static readonly uint[] LegacyTable = BuildLegacyTable();
    private byte[] _payload = [];

    /// <summary>覆盖小块、页级块和大块向量文件 payload。</summary>
    [Params(64, 4_096, 1_048_576)]
    public int Length { get; set; }

    /// <summary>创建固定随机数据，并保留三字节偏移以覆盖非对齐输入。</summary>
    [GlobalSetup]
    public void Setup()
    {
        _payload = new byte[checked(Length + 3)];
        new Random(0x43524333).NextBytes(_payload);
    }

    /// <summary>运行持久格式原有的逐字节 IEEE CRC32 参考实现。</summary>
    [Benchmark(Baseline = true)]
    public uint LegacyReference()
        => ComputeLegacy(_payload.AsSpan(3, Length));

    /// <summary>运行当前生产向量文件 CRC32 实现。</summary>
    [Benchmark]
    public uint Production()
        => Crc32.Compute(_payload.AsSpan(3, Length));

    /// <summary>构造旧实现使用的 IEEE 802.3 查找表。</summary>
    private static uint[] BuildLegacyTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            uint current = index;
            for (int bit = 0; bit < 8; bit++)
                current = (current & 1) != 0 ? 0xEDB88320u ^ (current >> 1) : current >> 1;
            table[index] = current;
        }

        return table;
    }

    /// <summary>按冻结的旧算法计算 CRC32，作为性能与格式参考。</summary>
    private static uint ComputeLegacy(ReadOnlySpan<byte> data)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in data)
            crc = LegacyTable[(crc ^ value) & byte.MaxValue] ^ (crc >> 8);
        return crc ^ uint.MaxValue;
    }
}

/// <summary>CRC32 基准的固定短作业配置。</summary>
internal sealed class VectorCrc32BenchmarkConfig : ManualConfig
{
    /// <summary>限制预热和正式迭代，同时记录延迟分位与托管分配。</summary>
    public VectorCrc32BenchmarkConfig()
    {
        BuildTimeout = TimeSpan.FromMinutes(5);
        AddJob(Job.Default.WithWarmupCount(2).WithIterationCount(5));
        AddColumn(StatisticColumn.Median, StatisticColumn.P90);
        AddDiagnoser(MemoryDiagnoser.Default);
    }
}
