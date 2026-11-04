using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol.Protocol;
using SonnetDB.Auth;
using SonnetDB.Endpoints;
using SonnetDB.Exceptions;
using SonnetDB.Kv;
using SonnetDB.Sql;

namespace SonnetDB.Mcp;

internal sealed partial class SonnetDbMcpTools
{
    internal const int MaxModelValueBytes = 64 * 1024;
    internal const int MaxModelResponseBytes = 256 * 1024;
    private const int MaxModelTextChars = 4096;

    private static Task<CallToolResult> RunModelTool<T>(
        SonnetDbMcpContextAccessor context, GrantsStore grants, bool write,
        Func<CancellationToken, T> operation, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken = default)
        => RunModelToolAsync(context, grants, write, token => Task.FromResult(operation(token)), typeInfo, cancellationToken);

    private static async Task<CallToolResult> RunModelToolAsync<T>(
        SonnetDbMcpContextAccessor context, GrantsStore grants, bool write,
        Func<CancellationToken, Task<T>> operation, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken = default)
    {
        var started = false;
        try
        {
            var http = context.GetHttpContext();
            var permission = DatabaseAccessEvaluator.GetEffectivePermission(http, grants, context.GetDatabaseName());
            if (!DatabaseAccessEvaluator.HasPermission(permission, write ? DatabasePermission.Write : DatabasePermission.Read))
                return SonnetDbMcpResults.Error("forbidden", "当前凭据没有此数据库所需的读写权限。");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            deadline.Token.ThrowIfCancellationRequested();
            started = true;
            var payload = await operation(deadline.Token).ConfigureAwait(false);
            var bytes = EncodeBoundedModel(payload, typeInfo, MaxModelResponseBytes);
            using var document = JsonDocument.Parse(bytes);
            return new CallToolResult
            {
                IsError = false,
                Content = [new TextContentBlock { Text = Encoding.UTF8.GetString(bytes.Span) }],
                StructuredContent = document.RootElement.Clone(),
            };
        }
        catch (PayloadLimitExceededException)
        {
            return SonnetDbMcpResults.Error("limit_exceeded", "内容超过读取或编码预算；请缩小参数或使用原生接口。");
        }
        catch (SqlParseException ex)
        {
            return SonnetDbMcpResults.Error(SonnetDbMcpErrorCodes.InvalidSql, ex.Message);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or JsonException)
        {
            return SonnetDbMcpResults.Error(SonnetDbMcpErrorCodes.InvalidArgument, ex.Message);
        }
        catch (OperationCanceledException)
        {
            return SonnetDbMcpResults.Error(write && started ? "write_outcome_unknown" : SonnetDbMcpErrorCodes.RequestCancelled,
                write && started ? "操作取消，写入结果可能未知；先回读状态，禁止盲目重试。" : "操作已取消。");
        }
        catch (IOException)
        {
            return SonnetDbMcpResults.Error(write && started ? "write_outcome_unknown" : SonnetDbMcpErrorCodes.OperationFailed,
                write && started ? "存储异常，写入结果可能未知；先回读状态，禁止盲目重试。" : "存储读取失败。");
        }
        catch (InvalidOperationException ex)
        {
            return SonnetDbMcpResults.Error(SonnetDbMcpErrorCodes.OperationFailed, ex.Message);
        }
        catch (Exception)
        {
            return SonnetDbMcpResults.Error(write && started ? "write_outcome_unknown" : SonnetDbMcpErrorCodes.OperationFailed,
                write && started ? "写入结果可能未知；先回读状态，禁止盲目重试。" : "模型操作失败。");
        }
    }

    private static void ValidateModelText(string? value, string name, bool allowEmpty = false)
    {
        if (!allowEmpty)
            ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        if (value is null || value.Length > MaxModelTextChars)
            throw new ArgumentException("文本参数不能超过 4096 个字符。", name);
    }

    private static void ValidateModelResource(string name)
    {
        if (!SonnetDbEndpoints.IsValidKeyspaceName(name))
            throw new ArgumentException("非法或保留的模型资源名称。", nameof(name));
    }

    private static byte[] DecodeModelBytes(string valueBase64)
    {
        ArgumentNullException.ThrowIfNull(valueBase64);
        if (valueBase64.Length > ((MaxModelValueBytes + 2) / 3) * 4)
            throw new ArgumentException("Base64 内容超过 64 KiB 输入预算。", nameof(valueBase64));
        byte[] bytes = Convert.FromBase64String(valueBase64);
        if (bytes.Length > MaxModelValueBytes)
            throw new ArgumentException("内容超过 64 KiB 输入预算。", nameof(valueBase64));
        return bytes;
    }

    private static byte[] EncodeModelBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaxModelValueBytes)
            throw new PayloadLimitExceededException(MaxModelValueBytes);
        return bytes.ToArray();
    }

    private static KvKeyspace OpenExistingKeyspace(SonnetDbMcpContextAccessor context, string keyspace)
    {
        ValidateModelResource(keyspace);
        var manager = context.GetDatabase().Keyspaces;
        // Open 会创建缺失目录；只读工具必须先确认该 keyspace 已存在。
        if (!manager.List().Contains(keyspace, StringComparer.Ordinal))
            throw new InvalidOperationException("keyspace 不存在。");
        return manager.Open(keyspace);
    }

    private static McpModelNamesResult ModelNames(
        SonnetDbMcpContextAccessor context, string model, IEnumerable<string> names, int? maxRows, string? after, string scope = "database")
    {
        var limit = SonnetDbMcpResults.NormalizeToolRowLimit(maxRows);
        if (after is not null)
            ValidateModelText(after, nameof(after), allowEmpty: true);
        var page = names.Where(name => !SonnetDB.SemanticSearch.SemanticEmbeddingAuditStore.IsReservedName(name)
                && !SonnetDB.SemanticSearch.RagManagementResourceNames.IsReserved(name)
                && !SonnetDB.SemanticContent.FaceReservedResourceNames.IsReserved(name))
            .Where(name => after is null || StringComparer.Ordinal.Compare(name, after) > 0)
            .Order(StringComparer.Ordinal).Take(limit + 1).ToArray();
        var truncated = page.Length > limit;
        var visible = page.Take(limit).ToArray();
        return new(context.GetDatabaseName(), model, visible, truncated, truncated ? visible[^1] : null, scope);
    }

    private static string QuotedIdentifier(string name)
    {
        ValidateModelText(name, nameof(name));
        return "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static string QuotedLiteral(string value)
    {
        ValidateModelText(value, nameof(value), allowEmpty: true);
        return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }
}
