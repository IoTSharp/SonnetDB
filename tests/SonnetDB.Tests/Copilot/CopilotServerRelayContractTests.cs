using System.Reflection;
using System.Text.Json;
using SonnetDB.Contracts;
using SonnetDB.Json;
using Xunit;

namespace SonnetDB.Tests.Copilot;

/// <summary>
/// ServerRelay 公共 DTO 的源码、二进制和 JSON wire 兼容性测试。
/// </summary>
public sealed class CopilotServerRelayContractTests
{
    [Fact]
    public void LegacyPositionalMembers_KeepExactRuntimeSignatures()
    {
        Type[] requestParameters =
        [
            typeof(string),
            typeof(string),
            typeof(List<AiMessage>),
            typeof(int?),
            typeof(int?),
            typeof(string),
            typeof(string),
            typeof(string),
        ];
        Type[] eventParameters =
        [
            typeof(string),
            typeof(string),
            typeof(string),
            typeof(string),
            typeof(string),
            typeof(string),
            typeof(IReadOnlyList<string>),
            typeof(IReadOnlyList<string>),
            typeof(IReadOnlyList<CopilotCitation>),
            typeof(int?),
        ];

        AssertLegacyPositionalMembers(typeof(CopilotChatRequest), requestParameters);
        AssertLegacyPositionalMembers(typeof(CopilotChatEvent), eventParameters);
    }

    [Fact]
    public void LegacyPositionalDeconstruction_CompilesAndPreservesValues()
    {
        var messages = new List<AiMessage> { new("user", "show cpu") };
        var request = new CopilotChatRequest(
            "factory",
            "show cpu",
            messages,
            DocsK: 3,
            SkillsK: 2,
            Mode: "read-only",
            CloudMode: "sql_assist",
            ConversationId: "conversation-1");
        var (db, message, actualMessages, docsK, skillsK, mode, cloudMode, conversationId) = request;

        Assert.Equal("factory", db);
        Assert.Equal("show cpu", message);
        Assert.Same(messages, actualMessages);
        Assert.Equal(3, docsK);
        Assert.Equal(2, skillsK);
        Assert.Equal("read-only", mode);
        Assert.Equal("sql_assist", cloudMode);
        Assert.Equal("conversation-1", conversationId);
        Assert.Null(request.RunId);
        Assert.Null(request.Cursor);

        var citations = new List<CopilotCitation>
        {
            new("C1", "doc", "title", "source", "snippet"),
        };
        var chatEvent = new CopilotChatEvent(
            "final",
            "ready",
            "answer",
            "query_sql",
            "{}",
            "{}",
            ["skill"],
            ["query_sql"],
            citations,
            Attempt: 1);
        var (type, eventMessage, answer, toolName, toolArguments, toolResult, skillNames, toolNames, actualCitations, attempt) = chatEvent;

        Assert.Equal("final", type);
        Assert.Equal("ready", eventMessage);
        Assert.Equal("answer", answer);
        Assert.Equal("query_sql", toolName);
        Assert.Equal("{}", toolArguments);
        Assert.Equal("{}", toolResult);
        Assert.Equal(["skill"], skillNames);
        Assert.Equal(["query_sql"], toolNames);
        Assert.Same(citations, actualCitations);
        Assert.Equal(1, attempt);
        Assert.Null(chatEvent.RunId);
        Assert.Null(chatEvent.Sequence);
        Assert.Null(chatEvent.Cursor);
        Assert.Null(chatEvent.ToolCallId);
    }

    [Fact]
    public void LegacyJsonPayloads_OmitUnsetRelayMembers()
    {
        var request = new CopilotChatRequest(
            "factory",
            "show cpu",
            Messages: [new AiMessage("user", "show cpu")],
            DocsK: 3,
            SkillsK: 2,
            Mode: "read-only",
            CloudMode: "sql_assist",
            ConversationId: "conversation-1")
        {
            Model = "model-1",
        };
        var requestJson = JsonSerializer.Serialize(
            request,
            ServerJsonContext.Default.CopilotChatRequest);
        var requestRoundTrip = JsonSerializer.Deserialize(
            requestJson,
            ServerJsonContext.Default.CopilotChatRequest);

        Assert.Equal(
            """{"db":"factory","message":"show cpu","messages":[{"role":"user","content":"show cpu"}],"docsK":3,"skillsK":2,"mode":"read-only","cloudMode":"sql_assist","conversationId":"conversation-1","model":"model-1"}""",
            requestJson);
        var legacyRequest = Assert.IsType<CopilotChatRequest>(requestRoundTrip);
        Assert.Null(legacyRequest.RunId);
        Assert.Null(legacyRequest.Cursor);

        var chatEvent = new CopilotChatEvent(
            "final",
            Message: "ready",
            Answer: "answer",
            Attempt: 1);
        var eventJson = JsonSerializer.Serialize(
            chatEvent,
            ServerJsonContext.Default.CopilotChatEvent);
        var eventRoundTrip = JsonSerializer.Deserialize(
            eventJson,
            ServerJsonContext.Default.CopilotChatEvent);

        Assert.Equal(
            """{"type":"final","message":"ready","answer":"answer","attempt":1}""",
            eventJson);
        var legacyEvent = Assert.IsType<CopilotChatEvent>(eventRoundTrip);
        Assert.Null(legacyEvent.RunId);
        Assert.Null(legacyEvent.Sequence);
        Assert.Null(legacyEvent.Cursor);
        Assert.Null(legacyEvent.ToolCallId);
    }

    [Fact]
    public void RelayJsonMembers_SourceGeneratedContextRoundTripsCamelCaseNames()
    {
        var request = new CopilotChatRequest("factory", "resume")
        {
            RunId = "run-1",
            Cursor = "run-1:3",
        };
        var requestJson = JsonSerializer.Serialize(
            request,
            ServerJsonContext.Default.CopilotChatRequest);
        var requestRoundTrip = JsonSerializer.Deserialize(
            requestJson,
            ServerJsonContext.Default.CopilotChatRequest);

        Assert.Equal(
            """{"db":"factory","message":"resume","runId":"run-1","cursor":"run-1:3"}""",
            requestJson);
        var actualRequest = Assert.IsType<CopilotChatRequest>(requestRoundTrip);
        Assert.Equal("run-1", actualRequest.RunId);
        Assert.Equal("run-1:3", actualRequest.Cursor);

        var chatEvent = new CopilotChatEvent(
            "tool_result",
            ToolName: "query_sql",
            ToolResult: "{\"rows\":[]}")
        {
            RunId = "run-1",
            Sequence = 4,
            Cursor = "run-1:4",
            ToolCallId = "tool-1",
        };
        var eventJson = JsonSerializer.Serialize(
            chatEvent,
            ServerJsonContext.Default.CopilotChatEvent);
        var eventRoundTrip = JsonSerializer.Deserialize(
            eventJson,
            ServerJsonContext.Default.CopilotChatEvent);

        using var document = JsonDocument.Parse(eventJson);
        var root = document.RootElement;
        Assert.Equal("run-1", root.GetProperty("runId").GetString());
        Assert.Equal(4, root.GetProperty("sequence").GetInt64());
        Assert.Equal("run-1:4", root.GetProperty("cursor").GetString());
        Assert.Equal("tool-1", root.GetProperty("toolCallId").GetString());
        var actualEvent = Assert.IsType<CopilotChatEvent>(eventRoundTrip);
        Assert.Equal("tool_result", actualEvent.Type);
        Assert.Equal("{\"rows\":[]}", actualEvent.ToolResult);
        Assert.Equal("run-1", actualEvent.RunId);
        Assert.Equal(4, actualEvent.Sequence);
        Assert.Equal("run-1:4", actualEvent.Cursor);
        Assert.Equal("tool-1", actualEvent.ToolCallId);
    }

    private static void AssertLegacyPositionalMembers(Type recordType, Type[] parameterTypes)
    {
        var constructor = recordType.GetConstructor(parameterTypes);
        Assert.NotNull(constructor);
        var parameters = constructor!.GetParameters();
        Assert.False(parameters[0].HasDefaultValue);
        Assert.All(parameters[1..], static parameter =>
        {
            Assert.True(parameter.HasDefaultValue);
            Assert.Null(parameter.DefaultValue);
        });

        var deconstructParameterTypes = parameterTypes
            .Select(static parameterType => parameterType.MakeByRefType())
            .ToArray();
        Assert.NotNull(recordType.GetMethod(
            "Deconstruct",
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types: deconstructParameterTypes,
            modifiers: null));
    }
}
