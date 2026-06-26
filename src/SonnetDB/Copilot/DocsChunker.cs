using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SonnetDB.Configuration;

namespace SonnetDB.Copilot;

/// <summary>
/// 单个可嵌入文档块。
/// </summary>
internal sealed record DocsChunk(
    string Source,
    string Title,
    string Section,
    string Content,
    int ChunkIndex,
    int TotalChunks);

/// <summary>
/// 将 markdown / html 文档切成适合 embedding 的块。
/// </summary>
internal sealed partial class DocsChunker
{
    private readonly int _chunkSize;
    private readonly int _chunkOverlap;

    public DocsChunker(IOptions<ServerOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var docs = options.Value.Copilot.Docs;
        _chunkSize = docs.ChunkSize <= 0 ? 800 : docs.ChunkSize;
        _chunkOverlap = docs.ChunkOverlap < 0 ? 0 : docs.ChunkOverlap;
    }

    public IReadOnlyList<DocsChunk> Chunk(DocsSourceFile sourceFile)
    {
        ArgumentNullException.ThrowIfNull(sourceFile);
        var raw = File.ReadAllText(sourceFile.FullPath);
        var extension = Path.GetExtension(sourceFile.FullPath);
        var text = string.Equals(extension, ".md", StringComparison.OrdinalIgnoreCase)
            ? raw.Replace("\r\n", "\n")
            : HtmlToText(raw);

        var sections = string.Equals(extension, ".md", StringComparison.OrdinalIgnoreCase)
            ? SplitMarkdownSections(text, sourceFile.Title)
            : [new Section(sourceFile.Title, sourceFile.Title, NormalizeWhitespace(text))];

        var chunks = new List<DocsChunk>();
        foreach (var section in sections)
        {
            if (string.IsNullOrWhiteSpace(section.Content))
                continue;

            var pieces = SplitOversizedSection(section.Content);
            for (var index = 0; index < pieces.Count; index++)
            {
                chunks.Add(new DocsChunk(
                    Source: sourceFile.Source,
                    Title: section.Title,
                    Section: section.Heading,
                    Content: pieces[index],
                    ChunkIndex: chunks.Count,
                    TotalChunks: 0));
            }
        }

        if (chunks.Count == 0)
        {
            var fallback = NormalizeWhitespace(text);
            if (!string.IsNullOrWhiteSpace(fallback))
            {
                chunks.Add(new DocsChunk(
                    Source: sourceFile.Source,
                    Title: sourceFile.Title,
                    Section: sourceFile.Title,
                    Content: fallback,
                    ChunkIndex: 0,
                    TotalChunks: 0));
            }
        }

        for (var i = 0; i < chunks.Count; i++)
            chunks[i] = chunks[i] with { ChunkIndex = i, TotalChunks = chunks.Count };

        return chunks;
    }

    private IReadOnlyList<Section> SplitMarkdownSections(string markdown, string fallbackTitle)
    {
        var sections = new List<Section>();
        string title = fallbackTitle;
        string currentSection = fallbackTitle;
        var builder = new StringBuilder();

        foreach (var rawLine in markdown.Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                Flush();
                title = ExtractHeading(line, 1);
                currentSection = title;
                continue;
            }

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                currentSection = ExtractHeading(line, 2);
                continue;
            }

            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                Flush();
                currentSection = $"{title} / {ExtractHeading(line, 3)}";
                continue;
            }

            builder.AppendLine(line);
        }

        Flush();
        return sections;

        void Flush()
        {
            var content = NormalizeWhitespace(builder.ToString());
            builder.Clear();
            if (string.IsNullOrWhiteSpace(content))
                return;

            sections.Add(new Section(title, currentSection, content));
        }
    }

    private List<string> SplitOversizedSection(string content)
    {
        var normalized = NormalizeWhitespace(content);
        if (normalized.Length <= _chunkSize)
            return [normalized];

        var result = new List<string>();
        var start = 0;
        while (start < normalized.Length)
        {
            var remaining = normalized.Length - start;
            var length = Math.Min(_chunkSize, remaining);
            var end = start + length;
            if (end < normalized.Length)
            {
                var split = normalized.LastIndexOf(' ', end - 1, length);
                if (split > start + (_chunkSize / 2))
                    end = split;
            }

            var piece = normalized[start..end].Trim();
            if (!string.IsNullOrWhiteSpace(piece))
                result.Add(piece);

            if (end >= normalized.Length)
                break;

            start = Math.Max(end - _chunkOverlap, start + 1);
        }

        return result;
    }

    private static string ExtractHeading(string line, int level)
        => NormalizeWhitespace(line[(level + 1)..]);

    private static string HtmlToText(string html)
    {
        var withoutScripts = ScriptRegex().Replace(html, " ");
        var withoutTags = TagRegex().Replace(withoutScripts, " ");
        return WebUtility.HtmlDecode(withoutTags);
    }

    private static string NormalizeWhitespace(string text)
        => WhitespaceRegex().Replace(text ?? string.Empty, " ").Trim();

    private sealed record Section(string Title, string Heading, string Content);

    [GeneratedRegex("<script[\\s\\S]*?</script>|<style[\\s\\S]*?</style>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ScriptRegex();

    [GeneratedRegex("<[^>]+>", RegexOptions.Compiled)]
    private static partial Regex TagRegex();

    [GeneratedRegex("\\s+", RegexOptions.Compiled)]
    private static partial Regex WhitespaceRegex();
}
