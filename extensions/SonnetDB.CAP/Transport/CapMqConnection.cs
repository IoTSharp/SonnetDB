using System.Security.Cryptography;
using System.Text;
using DotNetCore.CAP.Transport;
using Microsoft.Extensions.Options;
using SonnetDB.Data;
using SonnetDB.Data.Mq;

namespace SonnetDB.CAP.Transport;

internal sealed class CapMqConnection : IDisposable
{
    private readonly bool _ownsClient;
    private readonly HashSet<string> _groups = new(StringComparer.Ordinal);

    public CapMqConnection(IOptions<SonnetDbCapTransportOptions> options)
    {
        Options = options.Value;
        _ownsClient = Options.Client is null;
        Client = Options.Client ?? new SndbMqClient(Options.ConnectionString);
        string endpoint = "embedded";
        if (!string.IsNullOrEmpty(Options.ConnectionString))
        {
            var builder = new SndbConnectionStringBuilder(Options.ConnectionString);
            if (builder.ResolveMode() == SndbProviderMode.Remote)
                endpoint = new Uri(builder.ResolveBaseUrl()).GetComponents(UriComponents.HostAndPort, UriFormat.Unescaped);
        }
        BrokerAddress = new BrokerAddress("SonnetDB.MQ", endpoint);
    }

    public SonnetDbCapTransportOptions Options { get; }
    public SndbMqClient Client { get; }
    public BrokerAddress BrokerAddress { get; }
    public string Topic(string name) => Map("t", name);
    public string Group(string name) => Map("g", name);

    private string Map(string kind, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        byte[] bytes = Encoding.UTF8.GetBytes(Options.Namespace + "\0" + kind + "\0" + value);
        return "cap-" + Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    public void AcquireGroup(string group)
    {
        lock (_groups)
            if (!_groups.Add(group))
                throw new InvalidOperationException("同一 CAP 消费组已有活动消费者。");
    }

    public void ReleaseGroup(string group)
    {
        lock (_groups)
            _groups.Remove(group);
    }

    public void Dispose()
    {
        if (_ownsClient)
            Client.Dispose();
    }
}
