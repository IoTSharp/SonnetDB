using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using SonnetDB.Data.Embedded;
using SonnetDB.Data.Internal;
using SonnetDB.Protocol;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;

namespace SonnetDB.Data.Remote;

/// <summary>按帧读取单个 SQL 查询响应。任意时刻只持有当前行块和底层 HTTP 响应。</summary>
internal sealed class FrameSqlExecutionResult(
    HttpResponseMessage response,
    Stream stream,
    MaterializedExecutionResult metadata,
    SelectColumnInfo[] columnInfo,
    bool nativeDateTime,
    bool nativeDuration,
    bool nativeDateTimeOffset) : IExecutionResult
{
    private readonly byte[] _header = new byte[FrameHeader.Size];
    private IReadOnlyList<object?[]> _rows = [];
    private int _rowIndex = -1;
    private long _rowsRead;
    private bool _disposed;

    public int RecordsAffected => -1;

    public IReadOnlyList<string> Columns => metadata.Columns;

    public bool DisposeOnReadFailure => true;

    internal static async Task<FrameSqlExecutionResult?> TryCreateAsync(
        FrameChannel channel, ReadOnlyMemory<byte> request, string version,
        bool retryUnsupportedVersion, CancellationToken cancellationToken)
    {
        var response = await channel.SendSqlQueryStreamAsync(request, version, cancellationToken).ConfigureAwait(false);
        bool transferred = false;
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var first = await ReadFrameAsync(stream, new byte[FrameHeader.Size], cancellationToken).ConfigureAwait(false);
            if (first.Header.IsError)
            {
                if (retryUnsupportedVersion && FrameCodec.ReadErrorPayload(first.Payload).Code == "unsupported_result_version")
                {
                    await EnsureEndOfResponseAsync(stream, cancellationToken).ConfigureAwait(false);
                    return null;
                }
                FrameChannel.ThrowIfError(first.Header, first.Payload);
            }
            if (SqlFrameCodec.PeekChunkKind(first.Payload) != SqlQueryChunkKind.Meta)
                throw new InvalidDataException("SQL Frame 响应缺少列声明。");

            var (columns, columnInfo) = SqlFrameCodec.DecodeQueryMetaFrameWithInfo(first.Payload,
                out bool nativeDateTime, out bool nativeDuration, out bool nativeDateTimeOffset, out bool nativeDateTime2);
            if (columnInfo is null
                || nativeDateTime != (version is SqlFrameCodec.NativeDateTimeResultVersion or SqlFrameCodec.NativeDurationResultVersion
                    or SqlFrameCodec.NativeDateTimeOffsetResultVersion or SqlFrameCodec.NativeDateTime2ResultVersion)
                || nativeDuration != (version is SqlFrameCodec.NativeDurationResultVersion
                    or SqlFrameCodec.NativeDateTimeOffsetResultVersion or SqlFrameCodec.NativeDateTime2ResultVersion)
                || nativeDateTimeOffset != (version is SqlFrameCodec.NativeDateTimeOffsetResultVersion or SqlFrameCodec.NativeDateTime2ResultVersion)
                || nativeDateTime2 != (version == SqlFrameCodec.NativeDateTime2ResultVersion))
                throw new SndbServerException("frame_sql_result_version_unsupported",
                    "服务端未协商 SQL Frame 声明结果格式；SQL 已执行，不能自动重发。请升级服务端或在新请求中显式选择 Protocol=rest。",
                    HttpStatusCode.OK);

            var metadata = MaterializedExecutionResult.FromSelect(new SelectExecutionResult(columns, [])
            {
                ColumnInfo = columnInfo,
            });
            var result = new FrameSqlExecutionResult(response, stream, metadata, columnInfo,
                nativeDateTime, nativeDuration, nativeDateTimeOffset);
            transferred = true;
            return result;
        }
        finally
        {
            if (!transferred)
                response.Dispose();
        }
    }

    public bool ReadNextRow() => ReadNextRowAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public async ValueTask<bool> ReadNextRowAsync(CancellationToken cancellationToken)
    {
        if (_disposed)
            return false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_rowIndex + 1 >= _rows.Count)
            {
                _rows = [];
                _rowIndex = -1;
                var next = await ReadFrameAsync(stream, _header, cancellationToken).ConfigureAwait(false);
                FrameChannel.ThrowIfError(next.Header, next.Payload);
                switch (SqlFrameCodec.PeekChunkKind(next.Payload))
                {
                    case SqlQueryChunkKind.End:
                        if (SqlFrameCodec.DecodeQueryEndFrame(next.Payload).RowCount != _rowsRead)
                            throw new InvalidDataException("SQL Frame 终态行数与实际结果不一致。");
                        await EnsureEndOfResponseAsync(stream, cancellationToken).ConfigureAwait(false);
                        Dispose();
                        return false;
                    case SqlQueryChunkKind.Rows:
                        _rows = SqlFrameCodec.DecodeQueryRowsFrame(next.Payload,
                            nativeDateTime, nativeDuration, nativeDateTimeOffset);
                        if (_rows.Count == 0)
                            throw new InvalidDataException("SQL Frame 响应包含空行块。");
                        break;
                    default:
                        throw new InvalidDataException("SQL Frame 响应包含重复列声明。");
                }
            }

            var row = _rows[++_rowIndex];
            if (row.Length != Columns.Count)
                throw new InvalidDataException("SQL Frame 行列数与声明不一致。");
            NormalizeDeclaredValues(row, columnInfo);
            cancellationToken.ThrowIfCancellationRequested();
            _rowsRead++;
            return true;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public ValueTask<bool> NextResultAsync(CancellationToken cancellationToken)
    {
        Dispose();
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(false);
    }

    public object? GetValue(int ordinal) => _rows[_rowIndex][ordinal];

    [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicFields)]
    public Type GetFieldType(int ordinal) => metadata.GetFieldType(ordinal);

    public ExecutionColumnMetadata? GetColumnMetadata(int ordinal) => metadata.GetColumnMetadata(ordinal);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _rows = [];
        response.Dispose();
    }

    internal static void NormalizeDeclaredValues(object?[] row, SelectColumnInfo[]? declarations)
    {
        if (declarations is null)
            return;
        for (int column = 0; column < row.Length; column++)
        {
            if (declarations[column].DataType == TableColumnType.DateTime2 && row[column] is not (null or DateTime))
                throw new InvalidDataException("SQL Frame DATETIME2 声明与原生日期值不一致；结果状态未知。");
            if (declarations[column].DataType != TableColumnType.Time || row[column] is not string text)
                continue;
            if (!TimeOnly.TryParseExact(text, "HH:mm:ss.fffffff", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var time))
                throw new InvalidDataException("SQL Frame TIME 值不是有效的时间值。");
            row[column] = time;
        }
    }

    private static async Task<FrameMessage> ReadFrameAsync(Stream stream, byte[] headerBytes, CancellationToken cancellationToken)
    {
        try
        {
            await stream.ReadExactlyAsync(headerBytes, cancellationToken).ConfigureAwait(false);
            FrameHeader.TryRead(headerBytes, out var header);
            FrameCodec.ValidateResponse(header, (byte)FrameService.Sql, (byte)SqlFrameOp.Query, streamId: 1);
            if (header.PayloadLength > FrameHeader.MaxFramePayloadBytes)
                throw new InvalidDataException("SQL Frame 帧体超过协议大小限制。");
            var payload = new byte[(int)header.PayloadLength];
            await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
            return new FrameMessage(header, payload);
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException("SQL Frame 缺少完整的响应帧或终态标记。", exception);
        }
    }

    private static async Task EnsureEndOfResponseAsync(Stream stream, CancellationToken cancellationToken)
    {
        if (await stream.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0)
            throw new InvalidDataException("SQL Frame 终态之后仍包含响应数据。");
    }
}
