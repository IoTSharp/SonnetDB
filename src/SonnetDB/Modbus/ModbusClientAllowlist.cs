using System.Net;
using System.Net.Sockets;

namespace SonnetDB.Modbus;

internal sealed class ModbusClientAllowlist
{
    private readonly NetworkRule[] _rules;

    internal ModbusClientAllowlist(IReadOnlyList<string>? entries)
    {
        _rules = entries is null
            ? []
            : entries.Select(ParseRule).ToArray();
    }

    internal bool IsAllowed(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (_rules.Length == 0)
            return IPAddress.IsLoopback(NormalizeAddress(address));

        return _rules.Any(rule => rule.Contains(address));
    }

    private static NetworkRule ParseRule(string value)
    {
        int slashIndex = value.IndexOf('/');
        string addressText = slashIndex < 0 ? value : value[..slashIndex];
        IPAddress address = IPAddress.Parse(addressText);
        int prefixLength = slashIndex < 0
            ? address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128
            : int.Parse(value.AsSpan(slashIndex + 1), System.Globalization.CultureInfo.InvariantCulture);

        if (address.IsIPv4MappedToIPv6 && prefixLength >= 96)
            return new NetworkRule(address.MapToIPv4(), prefixLength - 96);
        return new NetworkRule(address, prefixLength);
    }

    private static IPAddress NormalizeAddress(IPAddress address)
        => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private sealed record NetworkRule(IPAddress Network, int PrefixLength)
    {
        internal bool Contains(IPAddress candidate)
        {
            IPAddress normalizedCandidate = NormalizeAddress(candidate);
            if (Network.AddressFamily != normalizedCandidate.AddressFamily)
                return false;

            byte[] networkBytes = Network.GetAddressBytes();
            byte[] candidateBytes = normalizedCandidate.GetAddressBytes();
            int fullBytes = PrefixLength / 8;
            int remainingBits = PrefixLength % 8;
            if (!networkBytes.AsSpan(0, fullBytes).SequenceEqual(candidateBytes.AsSpan(0, fullBytes)))
                return false;
            if (remainingBits == 0)
                return true;

            int mask = 0xFF << (8 - remainingBits);
            return (networkBytes[fullBytes] & mask) == (candidateBytes[fullBytes] & mask);
        }
    }
}
