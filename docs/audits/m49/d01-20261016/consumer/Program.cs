using System.Buffers;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SonnetDB.Client;
using SonnetDB.IO;
using SonnetDB.Protocol;

byte[] expected = [0, 255, 42, 128];
string? observedProtocol = null;
string? observedAuthorization = null;
var builder = WebApplication.CreateSlimBuilder();
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(options =>
    options.Listen(IPAddress.Loopback, 0, endpoint => endpoint.Protocols = HttpProtocols.Http2));
await using var app = builder.Build();
app.MapPost("/v1/frame", async context =>
{
    observedProtocol = context.Request.Protocol;
    observedAuthorization = context.Request.Headers.Authorization;
    byte[] request = new byte[FrameHeader.Size + expected.Length];
    await context.Request.Body.ReadExactlyAsync(request, context.RequestAborted);
    var sequence = new ReadOnlySequence<byte>(request);
    if (!FrameCodec.TryReadFrame(ref sequence, out var header, out var payload)
        || header.Version != 1 || header.Service != 5 || header.Op != 1 || header.StreamId != 42
        || !payload.ToArray().AsSpan().SequenceEqual(expected) || !sequence.IsEmpty)
        throw new InvalidDataException("Consumer request differs from the shared binary contract.");
    var response = new ArrayBufferWriter<byte>();
    FrameCodec.WriteFrame(response, new FrameHeader((uint)expected.Length, 1, 5, 1,
        (byte)FrameFlags.Response, 42), expected);
    context.Response.ContentType = "application/x-sonnetdb-frame";
    await context.Response.Body.WriteAsync(response.WrittenMemory, context.RequestAborted);
});
using var start = new CancellationTokenSource(TimeSpan.FromSeconds(5));
await app.StartAsync(start.Token);
try
{
    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    using var client = new SndbFrameClient(new Uri(address + "/"), "consumer-token", timeout: TimeSpan.FromSeconds(5));
    var reply = await client.SendUnaryAsync(5, 1, 42, expected);
    if (!reply.Span.SequenceEqual(expected) || observedProtocol != "HTTP/2"
        || observedAuthorization != "Bearer consumer-token")
        throw new InvalidDataException("Consumer did not complete the expected HTTP/2 exchange.");
    if (typeof(FrameHeader).Assembly.GetName().Name != "SonnetDB.Protocol"
        || typeof(SpanReader).Assembly.GetName().Name != "SonnetDB.Protocol")
        throw new InvalidDataException("Protocol primitives were not loaded from the standalone package.");
    if (Directory.EnumerateFiles(AppContext.BaseDirectory, "SonnetDB.*.dll").Take(16)
        .Any(path => Path.GetFileName(path) is "SonnetDB.Core.dll" or "SonnetDB.Data.dll" or "SonnetDB.Native.dll"))
        throw new InvalidDataException("The clean remote consumer unexpectedly includes an engine/provider runtime.");
    Console.WriteLine("PASS: package-only HTTP/2 Frame exchange; shared protocol; exact binary/auth; no Core/Data/Native files.");
}
finally
{
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await app.StopAsync(stop.Token);
}
