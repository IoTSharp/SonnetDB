using System.Reflection;
using InventoryMigrationApp;
using Microsoft.EntityFrameworkCore;
using SonnetDB.Data;

if (args.Length != 2)
    throw new ArgumentException("Usage: InventoryMigrationApp <initialize|upgrade|downgrade|verify-old|verify-new|roundtrip> <connection-string>");

foreach (var assembly in new[] { typeof(SonnetDbDbContextOptionsBuilderExtensions).Assembly, typeof(SndbConnection).Assembly })
    Console.WriteLine($"ASSEMBLY {assembly.GetName().Name} | {assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion} | {assembly.Location}");

using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
await InventoryWorkflow.RunAsync(args[0], args[1], deadline.Token);
Console.WriteLine($"PASS {args[0]}");
