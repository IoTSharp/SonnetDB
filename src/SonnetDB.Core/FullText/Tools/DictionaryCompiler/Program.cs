using System.Buffers.Binary;
using System.Text;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: DotSearch.DictionaryCompiler <input-dict.txt> <output-dict.dat>");
    return 2;
}

string inputPath = Path.GetFullPath(args[0]);
string outputPath = Path.GetFullPath(args[1]);

Dictionary<string, int> terms = LoadTerms(inputPath);
TrieNode root = new();
long totalFrequency = 0;
int maxTermLength = 0;
foreach (KeyValuePair<string, int> term in terms.OrderBy(static x => x.Key, StringComparer.Ordinal))
{
    Add(root, term.Key, term.Value);
    totalFrequency += term.Value;
    maxTermLength = Math.Max(maxTermLength, term.Key.Length);
}

List<int> bases = [0];
List<int> checks = [-1];
List<int> frequencies = [root.Frequency];
HashSet<int> used = [0];
Build(root, 0, bases, checks, frequencies, used);

Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
using FileStream stream = new(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
stream.Write("DSDAT001"u8);
WriteInt32(stream, bases.Count);
WriteInt32(stream, terms.Count);
WriteInt64(stream, totalFrequency);
WriteInt32(stream, maxTermLength);
for (int i = 0; i < bases.Count; i++)
{
    WriteInt32(stream, bases[i]);
    WriteInt32(stream, checks[i]);
    WriteInt32(stream, frequencies[i]);
}

return 0;

static Dictionary<string, int> LoadTerms(string path)
{
    Dictionary<string, int> terms = new(StringComparer.Ordinal);
    foreach (string line in File.ReadLines(path))
    {
        ReadOnlySpan<char> span = line.AsSpan().Trim();
        if (span.IsEmpty || span[0] == '#')
        {
            continue;
        }

        int tab = span.IndexOf('\t');
        if (tab <= 0)
        {
            continue;
        }

        string term = span[..tab].ToString();
        if (!int.TryParse(span[(tab + 1)..], out int frequency) || frequency <= 0)
        {
            frequency = 1;
        }
        terms[term] = frequency;
    }
    return terms;
}

static void Add(TrieNode root, string term, int frequency)
{
    TrieNode current = root;
    foreach (Rune rune in term.EnumerateRunes())
    {
        int code = rune.Value + 1;
        if (!current.Children.TryGetValue(code, out TrieNode? child))
        {
            child = new TrieNode();
            current.Children[code] = child;
        }
        current = child;
    }
    current.Frequency = frequency;
}

static void Build(TrieNode node, int index, List<int> bases, List<int> checks, List<int> frequencies, HashSet<int> used)
{
    if (node.Children.Count == 0)
    {
        return;
    }

    int baseValue = FindBase(node.Children.Keys, used);
    bases[index] = baseValue;

    foreach (KeyValuePair<int, TrieNode> child in node.Children.OrderBy(static x => x.Key))
    {
        int childIndex = baseValue + child.Key;
        EnsureSize(bases, checks, frequencies, childIndex + 1);
        checks[childIndex] = index;
        frequencies[childIndex] = child.Value.Frequency;
        used.Add(childIndex);
    }

    foreach (KeyValuePair<int, TrieNode> child in node.Children.OrderBy(static x => x.Key))
    {
        Build(child.Value, baseValue + child.Key, bases, checks, frequencies, used);
    }
}

static int FindBase(IEnumerable<int> codes, HashSet<int> used)
{
    int baseValue = 1;
    int[] snapshot = codes.ToArray();
    while (true)
    {
        bool free = true;
        foreach (int code in snapshot)
        {
            if (used.Contains(baseValue + code))
            {
                free = false;
                break;
            }
        }
        if (free)
        {
            return baseValue;
        }
        baseValue++;
    }
}

static void EnsureSize(List<int> bases, List<int> checks, List<int> frequencies, int size)
{
    while (bases.Count < size)
    {
        bases.Add(0);
        checks.Add(-1);
        frequencies.Add(0);
    }
}

static void WriteInt32(Stream stream, int value)
{
    Span<byte> buffer = stackalloc byte[sizeof(int)];
    BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
    stream.Write(buffer);
}

static void WriteInt64(Stream stream, long value)
{
    Span<byte> buffer = stackalloc byte[sizeof(long)];
    BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
    stream.Write(buffer);
}

internal sealed class TrieNode
{
    public SortedDictionary<int, TrieNode> Children { get; } = new();

    public int Frequency { get; set; }
}
