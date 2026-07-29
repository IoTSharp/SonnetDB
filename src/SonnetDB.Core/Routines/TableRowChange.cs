using SonnetDB.Tables;

namespace SonnetDB.Routines;

internal sealed record TableRowChange(
    TableSchema Schema,
    IReadOnlyList<object?>? OldValues,
    IReadOnlyList<object?>? NewValues);
