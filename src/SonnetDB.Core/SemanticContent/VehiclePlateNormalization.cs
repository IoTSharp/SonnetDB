namespace SonnetDB.SemanticContent;

/// <summary>版本固定的车牌精确键规范化；不做模糊纠错或字形替换。</summary>
public static class VehiclePlateNormalization
{
    /// <summary>当前规范化版本；未来规则变化必须新建键空间或显式重建索引。</summary>
    public const string Revision = "plate-ascii-cjk-v1";

    /// <summary>将 ASCII/全角 ASCII 字母数字以及基本中日韩汉字规范化。</summary>
    /// <param name="text">1..128 个字符；空格、全角空格、连字符和间隔点是可忽略分隔符。</param>
    /// <returns>1..32 字符的号码；保留 O/0、I/1 等不同号码。</returns>
    public static string Normalize(string text)
    {
        VisualEmbeddingValidation.Text(text, nameof(text), 128);
        Span<char> buffer = stackalloc char[32];
        int length = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char value = text[i];
            if (value is >= '\uFF01' and <= '\uFF5E') value = (char)(value - 0xFEE0);
            if (value is ' ' or '\u3000' or '-' or '\u00B7') continue;
            if (value is >= 'a' and <= 'z') value = (char)(value - 32);
            if (value is not (>= 'A' and <= 'Z') and not (>= '0' and <= '9') and not (>= '\u4E00' and <= '\u9FFF'))
                throw new ArgumentException("车牌包含此规范化版本不支持的字符。", nameof(text));
            if (length == buffer.Length) throw new ArgumentException("规范化号码超过 32 字符。", nameof(text));
            buffer[length++] = value;
        }
        if (length == 0) throw new ArgumentException("规范化号码不能为空。", nameof(text));
        return new string(buffer[..length]);
    }

    /// <summary>建立包含签发地区和规范化版本的精确索引键。</summary>
    /// <param name="jurisdiction">显式签发地区，1..32 ASCII 字母/数字/连字符。</param>
    /// <param name="text">车牌文本。</param><returns>稳定精确键。</returns>
    public static string CreateKey(string jurisdiction, string text)
    {
        VisualEmbeddingValidation.Text(jurisdiction, nameof(jurisdiction), 32);
        for (int i = 0; i < jurisdiction.Length; i++)
            if (jurisdiction[i] is not (>= 'A' and <= 'Z') and not (>= 'a' and <= 'z')
                and not (>= '0' and <= '9') and not '-')
                throw new ArgumentException("签发地区只允许 ASCII 字母/数字/连字符。", nameof(jurisdiction));
        return Revision + ":" + jurisdiction.ToUpperInvariant() + ":" + Normalize(text);
    }
}
