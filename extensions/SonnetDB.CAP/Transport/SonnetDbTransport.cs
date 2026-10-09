using System.Text.Json;
using DotNetCore.CAP;
using DotNetCore.CAP.Messages;
using DotNetCore.CAP.Transport;

namespace SonnetDB.CAP.Transport;

internal sealed class SonnetDbTransport(CapMqConnection connection) : ITransport
{
    public BrokerAddress BrokerAddress => connection.BrokerAddress;

    public async Task<OperateResult> SendAsync(TransportMessage message)
    {
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(message.GetId());
            var envelope = new CapTransportEnvelope(1, new Dictionary<string, string?>(message.Headers, StringComparer.Ordinal), message.Body.ToArray());
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(envelope, CapTransportJsonContext.Default.CapTransportEnvelope);
            if (payload.Length > connection.Options.MaxEnvelopeBytes)
                throw new ArgumentOutOfRangeException(nameof(message), "CAP MQ 信封超过字节预算。");
            await connection.Client.PublishAsync(connection.Topic(message.GetName()), payload,
                new Dictionary<string, string> { ["message-id"] = message.GetId() }).ConfigureAwait(false);
            return OperateResult.Success;
        }
        catch (Exception exception)
        {
            return OperateResult.Failed(exception);
        }
    }
}
