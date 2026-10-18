using System.Buffers;
using System.Globalization;
using SonnetDB.IO;
using SonnetDB.Protocol;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;

namespace SonnetDB.Data.Remote;

internal static class ProcedureResultDecoder
{
    public static SelectExecutionResult DecodeResult(byte[] bytes, long maximumCells = 2_000_000)
    {
        var buffer = new ReadOnlySequence<byte>(bytes);
        string[]? columns = null;
        SelectColumnInfo[]? info = null;
        bool nativeDateTime = false, nativeDuration = false, nativeOffset = false, ended = false;
        var rows = new List<IReadOnlyList<object?>>();
        for (int index = 0; buffer.Length > 0 && index < 100_002; index++)
        {
            if (!FrameCodec.TryReadFrame(ref buffer, out var header, out var payload)
                || !header.IsResponse || header.IsError || header.Service != (byte)FrameService.Sql
                || header.Op != (byte)SqlFrameOp.Query || header.StreamId != 1 || ended)
                throw new InvalidDataException("过程结果帧边界、身份或顺序无效；提交状态可能未知。");
            byte[] frame = payload.ToArray();
            switch (SqlFrameCodec.PeekChunkKind(frame))
            {
                case SqlQueryChunkKind.Meta when columns is null:
                    (columns, info) = SqlFrameCodec.DecodeQueryMetaFrameWithInfo(frame,
                        out nativeDateTime, out nativeDuration, out nativeOffset);
                    break;
                case SqlQueryChunkKind.Rows when columns is not null:
                    var prefix = new SpanReader(frame);
                    _ = prefix.ReadByte();
                    uint chunkRows = prefix.ReadVarUInt32();
                    uint chunkColumns = prefix.ReadVarUInt32();
                    if (chunkColumns != columns.Length || ((long)rows.Count + chunkRows) * chunkColumns > maximumCells)
                        throw new InvalidDataException("过程结果超过客户端累计单元格预算。");
                    foreach (var row in SqlFrameCodec.DecodeQueryRowsFrame(frame, nativeDateTime, nativeDuration, nativeOffset))
                    {
                        if (row.Length != columns.Length) throw new InvalidDataException("过程结果列数不匹配。");
                        for (int column = 0; column < row.Length; column++)
                            if (info?[column].DataType == TableColumnType.Time && row[column] is string text)
                                row[column] = TimeOnly.ParseExact(text, "HH:mm:ss.fffffff", CultureInfo.InvariantCulture);
                        rows.Add(row);
                        if (rows.Count > 100_000) throw new InvalidDataException("过程结果行数超过客户端上限。");
                    }
                    break;
                case SqlQueryChunkKind.End when columns is not null:
                    if (SqlFrameCodec.DecodeQueryEndFrame(frame).RowCount != rows.Count)
                        throw new InvalidDataException("过程结果行数与完成标记不符。");
                    ended = true;
                    break;
                default:
                    throw new InvalidDataException("过程结果缺少元数据或包含重复帧。");
            }
        }
        if (!ended || buffer.Length != 0 || columns is null)
            throw new InvalidDataException("过程结果没有完整终态；提交状态可能未知。");
        return new SelectExecutionResult(columns, rows) { ColumnInfo = info };
    }
}
