using System.Data;
using SonnetDB.Protocol;
using Xunit;

namespace SonnetDB.Core.Tests.Ado;

public sealed class SqlProcedureFrameCodecTests
{
    [Fact]
    public void Request_DirectionsDeclarationsAndQuotedText_RoundTripsWithoutSqlInterpolation()
    {
        SqlProcedureFrameParameter[] parameters =
        [
            new(SqlExecuteFrameCodec.FromClr("text", "'; DROP TABLE items; --"), SqlProcedureFrameDirection.Input,
                (int)DbType.String, 100, 0, 0),
            new(SqlExecuteFrameCodec.FromClr("output", null), SqlProcedureFrameDirection.Output,
                (int)DbType.String, 64, 0, 0),
            new(SqlExecuteFrameCodec.FromClr("status", null), SqlProcedureFrameDirection.ReturnValue,
                (int)DbType.Int32, 0, 0, 0),
        ];
        var request = SqlProcedureFrameCodec.DecodeRequest(SqlProcedureFrameCodec.EncodeRequestPayload("db", "procedure", "caller-transaction", parameters));
        Assert.Equal("procedure", request.Name);
        Assert.Equal("caller-transaction", request.SessionId);
        Assert.Equal(parameters, request.Parameters);
    }

    [Fact]
    public void Response_MissingOrInvalidCompletion_IsRejected()
    {
        byte[] encoded = SqlProcedureFrameCodec.EncodeResponsePayload(new([], 1, 7, [SqlExecuteFrameCodec.FromClr("output", null)]));
        var decoded = SqlProcedureFrameCodec.DecodeResponse(encoded);
        Assert.Equal(1, decoded.RecordsAffected);
        Assert.Equal(7L, decoded.ReturnValue);
        Assert.Null(decoded.Outputs.Single().Value);
        Assert.Throws<FrameFormatException>(() => SqlProcedureFrameCodec.DecodeResponse(encoded.AsSpan(0, encoded.Length - 1)));
        encoded[1] = 0;
        Assert.Throws<FrameFormatException>(() => SqlProcedureFrameCodec.DecodeResponse(encoded));
    }

    [Fact]
    public void Request_UnknownDirectionOrType_IsRejected()
    {
        var parameter = new SqlProcedureFrameParameter(SqlExecuteFrameCodec.FromClr("input", 1L),
            (SqlProcedureFrameDirection)99, (int)DbType.Int64, 0, 0, 0);
        Assert.Throws<FrameFormatException>(() => SqlProcedureFrameCodec.EncodeRequestPayload("db", "procedure", "", [parameter]));
        Assert.Throws<FrameFormatException>(() => SqlProcedureFrameCodec.EncodeRequestPayload("db", "procedure", "", [parameter with
        { Direction = SqlProcedureFrameDirection.Input, DbType = (int)DbType.DateTime }]));
    }
}
