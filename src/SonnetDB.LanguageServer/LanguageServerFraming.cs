using System.Globalization;
using System.Text;

namespace SonnetDB.LanguageServer;

internal static class LanguageServerFraming
{
    private const int MaxHeaderBytes = 16 * 1024;

    /// <summary>
    /// 按 Language Server Protocol 的 Content-Length 头读取一条 UTF-8 消息。
    /// </summary>
    /// <param name="input">语言服务标准输入流。</param>
    /// <returns>消息体；输入结束时返回 <see langword="null"/>。</returns>
    internal static byte[]? ReadMessage(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var header = new List<byte>(128);
        while (true)
        {
            int value = input.ReadByte();
            if (value < 0)
                return header.Count == 0 ? null : throw new EndOfStreamException("LSP 消息头未完整结束。");

            header.Add((byte)value);
            if (header.Count > MaxHeaderBytes)
                throw new InvalidDataException("LSP 消息头超过允许大小。");

            int count = header.Count;
            if (count >= 4
                && header[count - 4] == (byte)'\r'
                && header[count - 3] == (byte)'\n'
                && header[count - 2] == (byte)'\r'
                && header[count - 1] == (byte)'\n')
            {
                break;
            }
        }

        int contentLength = ParseContentLength(header.ToArray());
        var payload = GC.AllocateUninitializedArray<byte>(contentLength);
        input.ReadExactly(payload);
        return payload;
    }

    /// <summary>
    /// 写入一条带 Content-Length 头的 UTF-8 消息并立即刷新。
    /// </summary>
    /// <param name="output">语言服务标准输出流。</param>
    /// <param name="payload">UTF-8 JSON 消息体。</param>
    /// <returns>异步写入任务。</returns>
    internal static async ValueTask WriteMessageAsync(Stream output, ReadOnlyMemory<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(output);

        byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
        await output.WriteAsync(header);
        await output.WriteAsync(payload);
        await output.FlushAsync();
    }

    private static int ParseContentLength(ReadOnlySpan<byte> header)
    {
        string text = Encoding.ASCII.GetString(header);
        foreach (string line in text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            const string prefix = "Content-Length:";
            if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            if (int.TryParse(line[prefix.Length..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int length)
                && length >= 0)
            {
                return length;
            }
            break;
        }

        throw new InvalidDataException("LSP 消息缺少有效的 Content-Length。");
    }
}
