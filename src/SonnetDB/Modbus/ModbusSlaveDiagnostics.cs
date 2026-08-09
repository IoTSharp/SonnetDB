using System.Diagnostics.Metrics;

namespace SonnetDB.Modbus;

internal static class ModbusSlaveDiagnostics
{
    private static readonly Meter Meter = new("SonnetDB.Server", "1.0.0");
    private static readonly Counter<long> Connections = Meter.CreateCounter<long>(
        "sonnetdb.modbus.slave.connections",
        unit: "{connection}",
        description: "Accepted Modbus slave TCP connections.");
    private static readonly Counter<long> RejectedConnections = Meter.CreateCounter<long>(
        "sonnetdb.modbus.slave.connection.rejections",
        unit: "{connection}",
        description: "Modbus slave TCP connections rejected by allowlist or connection limit.");
    private static readonly UpDownCounter<long> ActiveConnections = Meter.CreateUpDownCounter<long>(
        "sonnetdb.modbus.slave.connections.active",
        unit: "{connection}",
        description: "Active Modbus slave TCP connections.");
    private static readonly Counter<long> ReadRequests = Meter.CreateCounter<long>(
        "sonnetdb.modbus.slave.read.requests",
        unit: "{request}",
        description: "Modbus slave read requests.");

    private static readonly KeyValuePair<string, object?> OutcomeOk = new("outcome", "ok");
    private static readonly KeyValuePair<string, object?> OutcomeError = new("outcome", "error");
    private static readonly KeyValuePair<string, object?> ReasonAllowlist = new("reason", "allowlist");
    private static readonly KeyValuePair<string, object?> ReasonConnectionLimit = new("reason", "connection_limit");
    private static readonly KeyValuePair<string, object?> AreaCoil = new("modbus.area", "coil");
    private static readonly KeyValuePair<string, object?> AreaDiscreteInput = new("modbus.area", "discrete_input");
    private static readonly KeyValuePair<string, object?> AreaInputRegister = new("modbus.area", "input_register");
    private static readonly KeyValuePair<string, object?> AreaHoldingRegister = new("modbus.area", "holding_register");

    internal static void RecordConnectionOpened()
    {
        Connections.Add(1);
        ActiveConnections.Add(1);
    }

    internal static void RecordConnectionClosed() => ActiveConnections.Add(-1);

    internal static void RecordConnectionRejected(bool allowlist)
        => RejectedConnections.Add(1, allowlist ? ReasonAllowlist : ReasonConnectionLimit);

    internal static void RecordRead(ModbusRegisterArea area, bool succeeded)
        => ReadRequests.Add(1, AreaTag(area), succeeded ? OutcomeOk : OutcomeError);

    private static KeyValuePair<string, object?> AreaTag(ModbusRegisterArea area) => area switch
    {
        ModbusRegisterArea.Coil => AreaCoil,
        ModbusRegisterArea.DiscreteInput => AreaDiscreteInput,
        ModbusRegisterArea.InputRegister => AreaInputRegister,
        ModbusRegisterArea.HoldingRegister => AreaHoldingRegister,
        _ => throw new ArgumentOutOfRangeException(nameof(area), area, "未知的 Modbus 地址空间。"),
    };
}
