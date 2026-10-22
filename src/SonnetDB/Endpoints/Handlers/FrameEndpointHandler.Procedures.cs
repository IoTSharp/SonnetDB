using System.Buffers;
using System.IO.Pipelines;
using Microsoft.AspNetCore.Http;
using SonnetDB.Auth;
using SonnetDB.Hosting;
using SonnetDB.Protocol;

namespace SonnetDB.Endpoints;

internal static partial class FrameEndpointHandler
{
    private static async Task ExecuteProcedureAsync(HttpContext context, TsdbRegistry registry, GrantsStore grants,
        ServerMetrics metrics, PipeWriter writer, FrameHeader header, ReadOnlySequence<byte> payload)
    {
        try
        {
            if (context.Request.Protocol != "HTTP/2")
            {
                FrameCodec.WriteErrorFrame(writer, header.Service, header.Op, header.StreamId,
                    "unsupported_transport", "过程 Frame 要求实际 HTTP/2。");
                return;
            }
            if (payload.Length > SqlProcedureFrameCodec.MaxRequestBytes)
                throw new FrameFormatException("过程请求超过字节预算。");
            var request = SqlProcedureFrameCodec.DecodeRequest(payload.ToArray());
            byte[] completed = await ProcedureEndpointHandler.ExecuteAsync(context, request, registry, grants, metrics).ConfigureAwait(false);
            writer.Write(SqlProcedureFrameCodec.WrapFrame(completed, response: true, header.StreamId));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var (_, code) = ProcedureEndpointHandler.MapError(exception);
            FrameCodec.WriteErrorFrame(writer, header.Service, header.Op, header.StreamId, code, exception.Message);
        }
    }
}
