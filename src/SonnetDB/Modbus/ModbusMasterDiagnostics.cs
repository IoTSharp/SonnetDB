using System.Diagnostics.Metrics;

namespace SonnetDB.Modbus;

internal static class ModbusMasterDiagnostics
{
    private static readonly Meter Meter = new("SonnetDB.Server", "1.0.0");
    private static readonly Counter<long> Polls = Meter.CreateCounter<long>(
        "sonnetdb.modbus.master.polls",
        unit: "{poll}",
        description: "Completed Modbus master poll rounds.");
    private static readonly Counter<long> ReadRequests = Meter.CreateCounter<long>(
        "sonnetdb.modbus.master.read.requests",
        unit: "{request}",
        description: "Modbus master batched read requests.");
    private static readonly Counter<long> RowsWritten = Meter.CreateCounter<long>(
        "sonnetdb.modbus.master.rows",
        unit: "{row}",
        description: "Local table rows written by successful Modbus polls.");
    private static readonly Counter<long> Reconnects = Meter.CreateCounter<long>(
        "sonnetdb.modbus.master.reconnects",
        unit: "{reconnect}",
        description: "Modbus master reconnect attempts after failures.");
    private static readonly Histogram<double> PollDuration = Meter.CreateHistogram<double>(
        "sonnetdb.modbus.master.poll.duration",
        unit: "ms",
        description: "End-to-end Modbus master poll duration.");

    private static readonly KeyValuePair<string, object?> OutcomeOk = new("outcome", "ok");
    private static readonly KeyValuePair<string, object?> OutcomeError = new("outcome", "error");
    private static readonly KeyValuePair<string, object?> AreaCoil = new("modbus.area", "coil");
    private static readonly KeyValuePair<string, object?> AreaDiscreteInput = new("modbus.area", "discrete_input");
    private static readonly KeyValuePair<string, object?> AreaInputRegister = new("modbus.area", "input_register");
    private static readonly KeyValuePair<string, object?> AreaHoldingRegister = new("modbus.area", "holding_register");

    internal static void RecordPoll(bool succeeded, double elapsedMilliseconds, int rowsWritten)
    {
        KeyValuePair<string, object?> outcome = succeeded ? OutcomeOk : OutcomeError;
        Polls.Add(1, outcome);
        PollDuration.Record(elapsedMilliseconds, outcome);
        if (rowsWritten > 0)
            RowsWritten.Add(rowsWritten);
    }

    internal static void RecordRead(ModbusRegisterArea area)
        => ReadRequests.Add(1, AreaTag(area));

    internal static void RecordReconnect()
        => Reconnects.Add(1);

    private static KeyValuePair<string, object?> AreaTag(ModbusRegisterArea area) => area switch
    {
        ModbusRegisterArea.Coil => AreaCoil,
        ModbusRegisterArea.DiscreteInput => AreaDiscreteInput,
        ModbusRegisterArea.InputRegister => AreaInputRegister,
        ModbusRegisterArea.HoldingRegister => AreaHoldingRegister,
        _ => throw new ArgumentOutOfRangeException(nameof(area), area, "未知的 Modbus 地址空间。"),
    };
}
