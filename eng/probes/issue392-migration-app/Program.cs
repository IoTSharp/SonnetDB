using System.Diagnostics;
using System.Reflection;
using InventoryMigrationApp;
using Microsoft.EntityFrameworkCore;
using SonnetDB.Data;

bool waitForKill = args.Length == 3 && args[0] == "upgrade-await-kill";
if ((args.Length != 2 && !waitForKill) || (args.Length == 2 && args[0] == "upgrade-await-kill"))
    throw new ArgumentException("Usage: InventoryMigrationApp <initialize|upgrade|downgrade|verify-old|verify-new|roundtrip> <connection-string>; upgrade-await-kill <connection-string> <new-ready-file>");

var requested = new SndbConnectionStringBuilder(args[1]);
string protocol = requested.ResolveMode() == SndbProviderMode.Embedded ? "embedded" : requested.ResolveProtocol() switch
{
    SndbTransportProtocol.Rest => "rest",
    SndbTransportProtocol.FrameHttp2 => "frame-http2",
    _ => throw new ArgumentException("Remote acceptance requires an explicit Protocol=rest or Protocol=frame-http2."),
};
Console.WriteLine("REQUESTED_PROTOCOL " + protocol);
using var process = Process.GetCurrentProcess();
Console.WriteLine($"APP_PROCESS pid={process.Id} | startedUtc={process.StartTime.ToUniversalTime():O}");

foreach (var assembly in new[] { typeof(SonnetDbDbContextOptionsBuilderExtensions).Assembly, typeof(SndbConnection).Assembly })
    Console.WriteLine($"ASSEMBLY {assembly.GetName().Name} | {assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion} | {assembly.Location}");

using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
await InventoryWorkflow.RunAsync(waitForKill ? "upgrade" : args[0], args[1], deadline.Token);
if (waitForKill)
{
    string ready = Path.GetFullPath(args[2]);
    using (var marker = new FileStream(ready, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
    {
        byte[] contents = System.Text.Encoding.UTF8.GetBytes($"after-ack | pid={process.Id} | startedUtc={process.StartTime.ToUniversalTime():O}\n");
        marker.Write(contents);
        marker.Flush(flushToDisk: true);
    }
    Console.WriteLine("READY_FOR_EXTERNAL_KILL after-ack");
    await Task.Delay(TimeSpan.FromSeconds(120), deadline.Token);
    throw new TimeoutException("The parent did not kill this process within its acceptance deadline.");
}
Console.WriteLine($"PASS {args[0]}");
