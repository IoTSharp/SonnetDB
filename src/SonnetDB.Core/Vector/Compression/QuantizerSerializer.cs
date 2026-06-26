using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace SonnetDB.Vector.Compression;

/// <summary>
/// 自描述量化器二进制读写器，对应每个 Segment 目录下的 <c>quantizer.bin</c> 文件。
/// </summary>
/// <remarks>
/// <para>
/// 文件布局（little-endian，紧凑无填充）：
/// </para>
/// <code>
/// byte  kind        // QuantizerKind: None=0 / Sq8=1 / Pq=2 / Opq=3 / Rq=4
/// 各 kind 自带 payload：
///   SQ8 : i32 dim, f32[dim] min, f32[dim] scale
///   PQ  : i32 dim, i32 m, i32 ksub, f32[m*ksub*subDim] centroids
///   OPQ : i32 dim, i32 m, i32 ksub, f32[d*d] rotation, f32[m*ksub*subDim] pqCentroids
///   RQ  : i32 dim, i32 levels, i32 ksub, f32[levels*ksub*dim] centroids
///   None: 无 payload
/// </code>
/// <para>
/// 浮点数组通过 <see cref="MemoryMarshal.Cast{TFrom, TTo}(ReadOnlySpan{TFrom})"/>
/// 直接以小端字节序写入，要求宿主架构本身为 little-endian（SonnetDB 向量引擎的整体约定）。
/// </para>
/// </remarks>
public static class QuantizerSerializer
{
    private const int KsubExpected = 256;

    /// <summary>
    /// 将量化器序列化到目标字节流。
    /// </summary>
    /// <param name="quantizer">已训练的量化器；为 <see langword="null"/> 时仅写入 <see cref="QuantizerKind.None"/>。</param>
    /// <param name="destination">输出流。</param>
    public static void Write(IVectorQuantizer? quantizer, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        if (quantizer is null)
        {
            destination.WriteByte((byte)QuantizerKind.None);
            return;
        }
        if (!quantizer.IsTrained)
        {
            throw new InvalidOperationException("无法序列化未训练的 IVectorQuantizer。");
        }

        switch (quantizer)
        {
            case ScalarQuantizer8 sq8:
                WriteSq8(sq8, destination);
                return;
            case ProductQuantizer pq:
                WritePq(pq, destination);
                return;
            case OptimizedProductQuantizer opq:
                WriteOpq(opq, destination);
                return;
            case ResidualQuantizer rq:
                WriteRq(rq, destination);
                return;
            default:
                throw new NotSupportedException(
                    $"未支持的量化器类型 {quantizer.GetType().FullName}（kind={quantizer.Kind}）。");
        }
    }

    /// <summary>
    /// 从字节流读取并构造已训练状态的量化器。
    /// </summary>
    /// <param name="source">输入流。</param>
    /// <returns>已训练的量化器；若文件首字节为 <see cref="QuantizerKind.None"/> 返回 <see langword="null"/>。</returns>
    public static IVectorQuantizer? Read(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        int kindByte = source.ReadByte();
        if (kindByte < 0)
        {
            throw new EndOfStreamException("无法读取 QuantizerKind 前缀。");
        }
        QuantizerKind kind = (QuantizerKind)(byte)kindByte;
        return kind switch
        {
            QuantizerKind.None => null,
            QuantizerKind.Sq8 => ReadSq8(source),
            QuantizerKind.Pq => ReadPq(source),
            QuantizerKind.Opq => ReadOpq(source),
            QuantizerKind.Rq => ReadRq(source),
            _ => throw new InvalidDataException($"未知 QuantizerKind 字节值：{(byte)kind}。"),
        };
    }

    // --------------------------------------------------------------- SQ8

    private static void WriteSq8(ScalarQuantizer8 q, Stream s)
    {
        s.WriteByte((byte)QuantizerKind.Sq8);
        WriteInt32(s, q.Dimensions);
        WriteFloats(s, q.Min);
        WriteFloats(s, q.Scale);
    }

    private static ScalarQuantizer8 ReadSq8(Stream s)
    {
        int dim = ReadInt32(s);
        ValidatePositive(dim, nameof(dim));
        float[] min = ReadFloats(s, dim);
        float[] scale = ReadFloats(s, dim);
        var q = new ScalarQuantizer8(dim);
        q.LoadState(min, scale);
        return q;
    }

    // ---------------------------------------------------------------- PQ

    private static void WritePq(ProductQuantizer q, Stream s)
    {
        s.WriteByte((byte)QuantizerKind.Pq);
        WriteInt32(s, q.Dimensions);
        WriteInt32(s, q.M);
        WriteInt32(s, PqCodebook.Ksub);
        WriteFloats(s, q.Codebook.Centroids);
    }

    private static ProductQuantizer ReadPq(Stream s)
    {
        int dim = ReadInt32(s);
        int m = ReadInt32(s);
        int ksub = ReadInt32(s);
        ValidatePositive(dim, nameof(dim));
        ValidatePositive(m, nameof(m));
        if (ksub != KsubExpected)
        {
            throw new InvalidDataException($"PQ ksub 必须为 {KsubExpected}，实际 {ksub}。");
        }
        int subDim = dim / m;
        int total = m * ksub * subDim;
        float[] centroids = ReadFloats(s, total);
        var q = new ProductQuantizer(dim, m);
        q.LoadState(centroids);
        return q;
    }

    // --------------------------------------------------------------- OPQ

    private static void WriteOpq(OptimizedProductQuantizer q, Stream s)
    {
        s.WriteByte((byte)QuantizerKind.Opq);
        WriteInt32(s, q.Dimensions);
        WriteInt32(s, q.M);
        WriteInt32(s, PqCodebook.Ksub);
        WriteFloats(s, q.Rotation);
        WriteFloats(s, q.InnerPq.Codebook.Centroids);
    }

    private static OptimizedProductQuantizer ReadOpq(Stream s)
    {
        int dim = ReadInt32(s);
        int m = ReadInt32(s);
        int ksub = ReadInt32(s);
        ValidatePositive(dim, nameof(dim));
        ValidatePositive(m, nameof(m));
        if (ksub != KsubExpected)
        {
            throw new InvalidDataException($"OPQ ksub 必须为 {KsubExpected}，实际 {ksub}。");
        }
        int subDim = dim / m;
        float[] rotation = ReadFloats(s, dim * dim);
        float[] pqCentroids = ReadFloats(s, m * ksub * subDim);
        var q = new OptimizedProductQuantizer(dim, m);
        q.LoadState(rotation, pqCentroids);
        return q;
    }

    // ---------------------------------------------------------------- RQ

    private static void WriteRq(ResidualQuantizer q, Stream s)
    {
        s.WriteByte((byte)QuantizerKind.Rq);
        WriteInt32(s, q.Dimensions);
        WriteInt32(s, q.Levels);
        WriteInt32(s, ResidualQuantizer.Ksub);
        WriteFloats(s, q.Centroids);
    }

    private static ResidualQuantizer ReadRq(Stream s)
    {
        int dim = ReadInt32(s);
        int levels = ReadInt32(s);
        int ksub = ReadInt32(s);
        ValidatePositive(dim, nameof(dim));
        ValidatePositive(levels, nameof(levels));
        if (ksub != KsubExpected)
        {
            throw new InvalidDataException($"RQ ksub 必须为 {KsubExpected}，实际 {ksub}。");
        }
        int total = levels * ksub * dim;
        float[] centroids = ReadFloats(s, total);
        var q = new ResidualQuantizer(dim, levels);
        q.LoadState(centroids);
        return q;
    }

    // ----------------------------------------------------------- helpers

    private static void WriteInt32(Stream s, int value)
    {
        Span<byte> buf = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(buf, value);
        s.Write(buf);
    }

    private static int ReadInt32(Stream s)
    {
        Span<byte> buf = stackalloc byte[sizeof(int)];
        ReadExact(s, buf);
        return BinaryPrimitives.ReadInt32LittleEndian(buf);
    }

    private static void WriteFloats(Stream s, ReadOnlySpan<float> data)
    {
        if (data.IsEmpty)
        {
            return;
        }
        // little-endian 主机假设：直接以字节复用写入。AGENTS.md 约定 little-endian。
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(data);
        s.Write(bytes);
    }

    private static float[] ReadFloats(Stream s, int count)
    {
        if (count < 0)
        {
            throw new InvalidDataException($"非法浮点数组长度：{count}。");
        }
        float[] buf = new float[count];
        if (count == 0)
        {
            return buf;
        }
        Span<byte> bytes = MemoryMarshal.AsBytes(buf.AsSpan());
        ReadExact(s, bytes);
        return buf;
    }

    private static void ReadExact(Stream s, Span<byte> buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = s.Read(buffer[read..]);
            if (n <= 0)
            {
                throw new EndOfStreamException(
                    $"流提前结束：期望 {buffer.Length} 字节，已读 {read} 字节。");
            }
            read += n;
        }
    }

    private static void ValidatePositive(int value, string name)
    {
        if (value <= 0)
        {
            throw new InvalidDataException($"{name} 必须为正：{value}。");
        }
    }
}
