using Microsoft.Extensions.Logging;
using SonnetDB.Diagnostics;
using SonnetDB.Hosting;
using Xunit;

namespace SonnetDB.Tests.Diagnostics;

/// <summary>
/// M17 #93：Server 源生成日志事件与 formatter 选择测试。
/// </summary>
public sealed class ServerLoggingTests
{
    [Theory]
    [InlineData("Production", true)]
    [InlineData("Staging", true)]
    [InlineData("Development", false)]
    [InlineData("development", false)]
    public void UsesJsonConsole_ForEnvironment_SelectsExpectedFormatter(string environmentName, bool expected)
    {
        Assert.Equal(expected, ServerLogging.UsesJsonConsole(environmentName));
    }

    [Fact]
    public void GeneratedEvents_WhenLogged_PreserveEventClassificationAndStructuredState()
    {
        var logger = new RecordingLogger();
        var exception = new InvalidOperationException("planner failed");

        logger.ExternalMqttConfigurationInvalid("Host 不可为空。");
        logger.CopilotPlannerFailed(exception);

        Assert.Collection(
            logger.Entries,
            writeEntry =>
            {
                Assert.Equal(LogLevel.Error, writeEntry.Level);
                Assert.Equal(1001, writeEntry.EventId.Id);
                Assert.Equal("Write.ExternalMqttConfigurationInvalid", writeEntry.EventId.Name);
                Assert.Contains(writeEntry.State, pair => pair.Key == "Error" && Equals(pair.Value, "Host 不可为空。"));
            },
            copilotEntry =>
            {
                Assert.Equal(LogLevel.Warning, copilotEntry.Level);
                Assert.Equal(6002, copilotEntry.EventId.Id);
                Assert.Equal("Copilot.PlannerFailed", copilotEntry.EventId.Name);
                Assert.Same(exception, copilotEntry.Exception);
            });
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var values = state as IEnumerable<KeyValuePair<string, object?>>
                ?? [];
            Entries.Add(new LogEntry(logLevel, eventId, values.ToList(), exception, formatter(state, exception)));
        }
    }

    private sealed record LogEntry(
        LogLevel Level,
        EventId EventId,
        IReadOnlyList<KeyValuePair<string, object?>> State,
        Exception? Exception,
        string Message);
}
