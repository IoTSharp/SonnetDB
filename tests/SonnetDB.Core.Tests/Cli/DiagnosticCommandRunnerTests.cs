using System.Net;
using System.Net.Http.Headers;
using SonnetDB.Cli;
using Xunit;

namespace SonnetDB.Core.Tests.Cli;

/// <summary>
/// M17 #96：<c>sndb diag dump</c> HTTP 调用测试。
/// </summary>
public sealed class DiagnosticCommandRunnerTests
{
    [Fact]
    public void Run_Dump_WithAdminToken_WritesServerJson()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "{\"timestampUtcMs\":123}");
        var output = new StringWriter();
        var error = new StringWriter();
        var runner = new DiagnosticCommandRunner(output, error, () => handler);

        var exitCode = runner.Run([
            "diag", "dump",
            "--endpoint", "https://sonnetdb.example/base/",
            "--token", "admin-token"]);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal("https://sonnetdb.example/base/v1/diagnostics/dump", handler.RequestUri?.ToString());
        Assert.Equal(new AuthenticationHeaderValue("Bearer", "admin-token"), handler.Authorization);
        Assert.Contains("\"timestampUtcMs\":123", output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void Run_Dump_WhenServerRejectsRequest_ReturnsExecutionFailure()
    {
        var handler = new RecordingHandler(HttpStatusCode.Forbidden, "{\"error\":\"forbidden\"}");
        var output = new StringWriter();
        var error = new StringWriter();
        var runner = new DiagnosticCommandRunner(output, error, () => handler);

        var exitCode = runner.Run(["diag", "dump", "--token", "readonly-token"]);

        Assert.Equal(ExitCodes.ExecutionFailed, exitCode);
        Assert.Equal(string.Empty, output.ToString());
        Assert.Contains("403", error.ToString());
        Assert.Contains("forbidden", error.ToString());
    }

    [Fact]
    public void Run_Dump_WithOutputFile_PersistsExactJson()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), "sndb-dump-" + Guid.NewGuid().ToString("N"), "dump.json");
        try
        {
            var handler = new RecordingHandler(HttpStatusCode.OK, "{\"timestampUtcMs\":456}");
            var output = new StringWriter();
            var runner = new DiagnosticCommandRunner(output, new StringWriter(), () => handler);

            var exitCode = runner.Run(["diag", "dump", "--output", outputPath]);

            Assert.Equal(ExitCodes.Success, exitCode);
            Assert.Equal("{\"timestampUtcMs\":456}" + Environment.NewLine, File.ReadAllText(outputPath));
            Assert.Contains(Path.GetFullPath(outputPath), output.ToString());
        }
        finally
        {
            var directory = Path.GetDirectoryName(outputPath);
            if (directory is not null && Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class RecordingHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        public AuthenticationHeaderValue? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Authorization = request.Headers.Authorization;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body),
            });
        }
    }
}
