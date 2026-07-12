using System.Net.Http.Headers;

namespace SonnetDB.Cli;

/// <summary>
/// 处理 <c>sndb diag dump</c> 远程诊断快照采集命令。
/// </summary>
internal sealed class DiagnosticCommandRunner
{
    private readonly TextWriter _output;
    private readonly TextWriter _error;
    private readonly Func<HttpMessageHandler>? _handlerFactory;

    /// <summary>
    /// 创建诊断命令运行器。
    /// </summary>
    /// <param name="output">标准输出。</param>
    /// <param name="error">标准错误。</param>
    /// <param name="handlerFactory">测试可选的 HTTP handler 工厂。</param>
    internal DiagnosticCommandRunner(
        TextWriter output,
        TextWriter error,
        Func<HttpMessageHandler>? handlerFactory = null)
    {
        _output = output;
        _error = error;
        _handlerFactory = handlerFactory;
    }

    /// <summary>
    /// 解析并执行 <c>diag</c> 子命令。
    /// </summary>
    /// <param name="args">完整 CLI 参数。</param>
    /// <returns>进程退出码。</returns>
    internal int Run(IReadOnlyList<string> args)
    {
        if (args.Count < 2)
            throw new CliUsageException(BuildHelp());

        return args[1].ToLowerInvariant() switch
        {
            "dump" => RunDump(args),
            "help" or "--help" or "-h" => Help(),
            _ => throw new CliUsageException($"未知 diag 子命令 '{args[1]}'。\n{BuildHelp()}"),
        };
    }

    private int RunDump(IReadOnlyList<string> args)
    {
        var endpoint = Environment.GetEnvironmentVariable("SONNETDB_DIAG_URL") ?? "http://127.0.0.1:5080";
        var token = Environment.GetEnvironmentVariable("SONNETDB_DIAG_TOKEN");
        var timeoutSeconds = 30;
        string? outputPath = null;

        for (var i = 2; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--endpoint" or "--url":
                    endpoint = RequireValue(args, ref i, "--endpoint");
                    break;
                case "--token" or "-t":
                    token = RequireValue(args, ref i, "--token");
                    break;
                case "--timeout":
                    if (!int.TryParse(RequireValue(args, ref i, "--timeout"), out timeoutSeconds) || timeoutSeconds <= 0)
                        throw new CliUsageException("--timeout 必须为正整数（秒）。");
                    break;
                case "--output" or "-o":
                    outputPath = RequireValue(args, ref i, "--output");
                    break;
                case "--help" or "-h":
                    return Help();
                default:
                    throw new CliUsageException($"未知参数 '{args[i]}'。\n{BuildHelp()}");
            }
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri)
            || endpointUri.Scheme is not ("http" or "https"))
        {
            throw new CliUsageException("--endpoint 必须是有效的 HTTP 或 HTTPS 绝对地址。");
        }

        var url = endpoint.TrimEnd('/') + "/v1/diagnostics/dump";
        using var client = CreateClient(timeoutSeconds, token);
        HttpResponseMessage response;
        try
        {
            response = client.GetAsync(url).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _error.WriteLine($"调用 {url} 失败: {ex.Message}");
            return ExitCodes.ExecutionFailed;
        }

        using (response)
        {
            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                _error.WriteLine($"服务端返回 {(int)response.StatusCode}: {body}");
                return ExitCodes.ExecutionFailed;
            }

            if (string.IsNullOrWhiteSpace(outputPath))
            {
                _output.WriteLine(body);
                return ExitCodes.Success;
            }

            var fullPath = Path.GetFullPath(outputPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(fullPath, body + Environment.NewLine);
            _output.WriteLine($"Diagnostic Dump 已写入 {fullPath}");
            return ExitCodes.Success;
        }
    }

    private HttpClient CreateClient(int timeoutSeconds, string? token)
    {
        var client = _handlerFactory is null
            ? new HttpClient()
            : new HttpClient(_handlerFactory(), disposeHandler: true);
        client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
        if (!string.IsNullOrWhiteSpace(token))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private int Help()
    {
        _output.WriteLine(BuildHelp());
        return ExitCodes.Success;
    }

    private static string RequireValue(IReadOnlyList<string> args, ref int index, string flag)
    {
        if (index + 1 >= args.Count)
            throw new CliUsageException($"{flag} 缺少参数值。");
        return args[++index];
    }

    private static string BuildHelp()
        => """
sndb diag - 采集 SonnetDB Server 运行时诊断快照

用法:
  sndb diag dump [--endpoint <url>] [--token <admin-bearer>] [--timeout <sec>] [--output <file>]

环境变量:
  SONNETDB_DIAG_URL    服务端地址，默认 http://127.0.0.1:5080
  SONNETDB_DIAG_TOKEN  admin Bearer token
""";
}
