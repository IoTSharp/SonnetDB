namespace SonnetDB.Data.Internal;

internal static class BoundedContentReader
{
    public static async Task<byte[]> ReadAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        byte[] block = new byte[8192];
        int maximumReads = checked(maximumBytes + 1);
        for (int index = 0; index < maximumReads; index++)
        {
            int count = await stream.ReadAsync(block, deadline.Token).ConfigureAwait(false);
            if (count == 0) return buffer.ToArray();
            if (buffer.Length + count > maximumBytes)
                throw new InvalidDataException("过程响应超过有界字节预算；提交状态可能未知，未自动重试。");
            buffer.Write(block, 0, count);
        }
        throw new InvalidDataException("过程响应读取次数超过上限。");
    }
}
