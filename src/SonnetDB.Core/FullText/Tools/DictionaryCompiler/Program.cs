using SonnetDB.FullText.Tokenizers.Jieba;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: SonnetDB.FullText.DictionaryCompiler <input-dict.txt> <output-dict.dat>");
    return 2;
}

string inputPath = Path.GetFullPath(args[0]);
string outputPath = Path.GetFullPath(args[1]);

ChineseDictionaryCompileResult result = ChineseDictionaryCompiler.Compile([inputPath], outputPath);
Console.WriteLine($"Compiled {result.TermCount} terms, {result.NodeCount} DAT nodes.");
return 0;
