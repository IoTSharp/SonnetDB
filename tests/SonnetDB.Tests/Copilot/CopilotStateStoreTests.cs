using SonnetDB.Contracts;
using SonnetDB.Copilot;
using SonnetDB.Hosting;
using Xunit;

namespace SonnetDB.Tests.Copilot;

/// <summary>
/// Copilot 系统库持久化测试。
/// </summary>
public sealed class CopilotStateStoreTests
{
    [Fact]
    public void Reopen_WithStoredConversationAndUsage_RestoresState()
    {
        var root = Path.Combine(Path.GetTempPath(), "sndb-copilot-state-" + Guid.NewGuid().ToString("N"));
        var owner = "user:restart-test";
        var now = DateTimeOffset.UtcNow;
        try
        {
            using (var firstRegistry = new TsdbRegistry(root))
            {
                var first = new CopilotStateStore(firstRegistry);
                first.UpsertConversation(owner, "restart-session", "重启恢复", "alpha", now);
                first.AppendMessage(owner, "restart-session", "user", "请保存", now: now);
                first.AppendMessage(
                    owner,
                    "restart-session",
                    "assistant",
                    "已经保存",
                    [new CopilotCitation("C1", "tool", "保存", "system", "持久化完成")],
                    "gpt-test",
                    10,
                    5,
                    now);
                first.RecordUsage(owner, new CopilotUsageRecord(
                    "restart-session",
                    "gpt-test",
                    "sql_assist",
                    10,
                    5,
                    15,
                    false,
                    1,
                    25,
                    true,
                    now));
            }

            using var secondRegistry = new TsdbRegistry(root);
            secondRegistry.LoadExisting();
            var second = new CopilotStateStore(secondRegistry);

            var conversation = Assert.Single(second.ListConversations(owner));
            Assert.Equal("restart-session", conversation.Id);
            Assert.Equal(2, conversation.MessageCount);
            var messages = second.ListMessages(owner, conversation.Id);
            Assert.Equal(["user", "assistant"], messages.Select(static message => message.Role));
            Assert.True(messages[0].CreatedAtUtc < messages[1].CreatedAtUtc);

            var metrics = second.GetMetrics(owner, TimeSpan.FromHours(1), now.AddMinutes(1));
            Assert.Equal(1, metrics.RequestCount);
            Assert.Equal(15, metrics.TotalTokens);
            Assert.False(metrics.IncludesEstimatedTokens);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
