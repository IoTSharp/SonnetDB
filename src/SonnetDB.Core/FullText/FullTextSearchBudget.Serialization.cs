using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace SonnetDB.FullText;

public sealed partial class FullTextSearchBudget
{
    /// <summary>在共享期限及工作量预算内稳定排序，不包装预算或取消异常。</summary>
    /// <typeparam name="T">元素类型。</typeparam>
    /// <param name="items">待排序元素。</param>
    /// <param name="comparison">排序比较器。</param>
    /// <returns>完整排序后的独立数组。</returns>
    public T[] Sort<T>(IReadOnlyList<T> items, Comparison<T> comparison)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(comparison);
        Check();
        T[] source = new T[items.Count];
        for (int index = 0; index < source.Length; index++)
        {
            ChargeWork();
            source[index] = items[index];
        }
        if (source.Length < 2)
            return source;

        T[] target = new T[source.Length];
        // 每轮将宽度翻倍，最大轮数受 Int32 数组长度限制，不发生 width 溢出。
        for (int width = 1; width < source.Length; width = width > source.Length / 2 ? source.Length : width * 2)
        {
            Check();
            for (int left = 0; left < source.Length;)
            {
                int middle = left + Math.Min(width, source.Length - left);
                int right = middle + Math.Min(width, source.Length - middle);
                int first = left;
                int second = middle;
                for (int output = left; output < right; output++)
                {
                    ChargeWork();
                    if (first < middle && (second >= right || comparison(source[first], source[second]) <= 0))
                        target[output] = source[first++];
                    else
                        target[output] = source[second++];
                }
                left = right;
            }
            (source, target) = (target, source);
        }
        Check();
        return source;
    }

    /// <summary>
    /// 使用 source-generated JSON 信息，在同一预算内完成整个响应编码。
    /// 只有全部编码通过后才返回字节，因此预算错误不会返回部分 JSON。
    /// </summary>
    /// <typeparam name="T">响应类型。</typeparam>
    /// <param name="value">完整响应。</param>
    /// <param name="typeInfo">AOT 友好的 JSON 类型信息。</param>
    /// <returns>通过总响应字节上限检查的完整 UTF-8 JSON。</returns>
    public byte[] Serialize<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        Check();
        using var stream = new BudgetedResponseStream(this);
        JsonSerializer.Serialize(stream, value, typeInfo);
        Check();
        return stream.ToArray();
    }

    private sealed class BudgetedResponseStream(FullTextSearchBudget budget) : Stream
    {
        private readonly MemoryStream _buffer = new();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _buffer.Length;
        public override long Position { get => _buffer.Position; set => throw new NotSupportedException(); }

        public byte[] ToArray() => _buffer.ToArray();

        public override void Write(byte[] buffer, int offset, int count)
            => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            budget.ChargeWork(Math.Max(1, buffer.Length / 64));
            budget.ChargeResponseBytes(buffer.Length);
            _buffer.Write(buffer);
        }

        public override void Flush() => budget.Check();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _buffer.Dispose();
            base.Dispose(disposing);
        }
    }
}
