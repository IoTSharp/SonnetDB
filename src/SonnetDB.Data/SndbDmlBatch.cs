namespace SonnetDB.Data;

internal sealed record SndbDmlBatchStatement(
    string Sql, SndbParameterCollection Parameters, int? ExpectedRecordsAffected, int CommandTimeout);

internal sealed record SndbDmlBatchResult(SndbDataReader? Reader, SndbServerException? Error);
