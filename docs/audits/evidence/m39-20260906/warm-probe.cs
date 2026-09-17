using System.Diagnostics;
using SonnetDB.Benchmarks.Benchmarks;

int iterations = args.Length == 1 ? int.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture) : 7;
if (iterations is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(args));
var timer = Stopwatch.StartNew();
Console.WriteLine("iteration,elapsedMilliseconds,allocatedBytes");
for (int index = 0; index < iterations; index++)
{
    if (timer.Elapsed > TimeSpan.FromSeconds(60)) throw new TimeoutException();
    var sample = new TriggerBaselineBenchmark
    {
        Rows = 10000,
        Operation = TriggerDmlOperation.Update,
        Path = TriggerPath.NoTrigger,
    }.RunSingleIteration();
    Console.WriteLine(FormattableString.Invariant($"{index},{sample.ElapsedMilliseconds},{sample.AllocatedBytes}"));
}
