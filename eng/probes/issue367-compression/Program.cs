using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using SonnetDB.Buffers;
using SonnetDB.Memory;
using SonnetDB.Model;
using SonnetDB.Storage.Format;
using SonnetDB.Storage.Segments;

if (args.Length < 2)
    throw new ArgumentException("用法：write <独立输出目录>，read <段文件> [reject]。");

Console.WriteLine($"Core={typeof(SegmentReader).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion}; SegmentVersion={TsdbMagic.SegmentFormatVersion}");
using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(90));

if (args[0] == "read")
{
    bool reject = args.Length > 2 && args[2] == "reject";
    try
    {
        using var reader = SegmentReader.Open(Path.GetFullPath(args[1]));
        int points = 0;
        foreach (var block in reader.Blocks)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            points += reader.DecodeBlock(block).Length;
        }
        if (reject)
            throw new InvalidOperationException("旧 reader 错误接受了新版本段。");
        Console.WriteLine($"READ_PASS blocks={reader.BlockCount} points={points}");
    }
    catch (SegmentCorruptedException exception) when (reject && exception.Message.Contains("FormatVersion", StringComparison.Ordinal))
    {
        Console.WriteLine($"REJECT_PASS {exception.Message}");
    }
    return;
}
if (args[0] != "write")
    throw new ArgumentException("未知模式。");

string output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(output);
const int rows = 8192;
string[] datasets = ["constant", "runs256", "alternating", "counter", "extremes256"];
var report = new StringBuilder("dataset,rows,segment_version,file_bytes,quality_value_bytes,quality_timestamp_bytes,value_value_bytes,value_timestamp_bytes,write_ms,decode_ms\n");
foreach (string dataset in datasets)
{
    cancellation.Token.ThrowIfCancellationRequested();
    var table = new MemTable();
    for (int index = 0; index < rows; index++)
    {
        if ((index & 1023) == 0)
            cancellation.Token.ThrowIfCancellationRequested();
        long timestamp = 1_700_000_000_000L + index * 1000L;
        table.Append(1, timestamp, "value", FieldValue.FromDouble(Value(index)), index * 2L + 1);
        table.Append(1, timestamp, "quality", FieldValue.FromLong(Quality(dataset, index)), index * 2L + 2);
    }

    string path = Path.Combine(output, dataset + ".SDBSEG");
    var watch = Stopwatch.StartNew();
    new SegmentWriter(new SegmentWriterOptions
    {
        FsyncOnCommit = true,
        TimestampEncoding = BlockEncoding.DeltaTimestamp,
        ValueEncoding = BlockEncoding.DeltaValue,
    }).WriteFrom(table, 1, path);
    double writeMilliseconds = watch.Elapsed.TotalMilliseconds;
    watch.Restart();
    byte[] bytes = File.ReadAllBytes(path);
    using var reader = SegmentReader.Open(path, new SegmentReaderOptions { DecodeBlockCacheMaxBytes = 0 });
    int qualityBytes = 0, qualityTimestamps = 0, valueBytes = 0, valueTimestamps = 0;
    foreach (var block in reader.Blocks)
    {
        cancellation.Token.ThrowIfCancellationRequested();
        var header = MemoryMarshal.Read<BlockHeader>(bytes.AsSpan(checked((int)block.FileOffset)));
        if (block.FieldName == "quality")
        {
            qualityBytes = header.ValuePayloadLength;
            qualityTimestamps = header.TimestampPayloadLength;
        }
        else
        {
            valueBytes = header.ValuePayloadLength;
            valueTimestamps = header.TimestampPayloadLength;
        }
        var decoded = reader.DecodeBlock(block);
        if (decoded.Length != rows)
            throw new InvalidOperationException("点数不匹配。");
        for (int index = 0; index < decoded.Length; index++)
        {
            if ((index & 1023) == 0)
                cancellation.Token.ThrowIfCancellationRequested();
            long timestamp = 1_700_000_000_000L + index * 1000L;
            if (decoded[index].Timestamp != timestamp
                || (block.FieldName == "quality"
                    ? decoded[index].Value.AsLong() != Quality(dataset, index)
                    : BitConverter.DoubleToInt64Bits(decoded[index].Value.AsDouble()) != BitConverter.DoubleToInt64Bits(Value(index))))
                throw new InvalidOperationException($"完整回读不匹配：{dataset}/{block.FieldName}/{index}");
        }
        var range = reader.DecodeBlockRange(block, 1_700_000_255_000L, 1_700_000_257_000L);
        if (range.Length != 3)
            throw new InvalidOperationException("窄范围点数不匹配。");
        for (int index = 0; index < range.Length; index++)
        {
            if (range[index] != decoded[255 + index])
                throw new InvalidOperationException("跨游程范围回读不匹配。");
        }
    }
    double decodeMilliseconds = watch.Elapsed.TotalMilliseconds;
    string line = FormattableString.Invariant($"{dataset},{rows},{TsdbMagic.SegmentFormatVersion},{bytes.Length},{qualityBytes},{qualityTimestamps},{valueBytes},{valueTimestamps},{writeMilliseconds:F3},{decodeMilliseconds:F3}");
    report.AppendLine(line);
    Console.WriteLine(line);
}
File.WriteAllText(Path.Combine(output, "measurements.csv"), report.ToString(), new UTF8Encoding(false));

static double Value(int index) => 100.0 + index % 100 * 0.01;

static long Quality(string dataset, int index) => dataset switch
{
    "constant" => 192,
    "runs256" => index / 256 % 4,
    "alternating" => index % 2,
    "counter" => index,
    "extremes256" => index / 256 % 2 == 0 ? long.MinValue : long.MaxValue,
    _ => throw new ArgumentException("未知语料。", nameof(dataset)),
};
