using SonnetDB.SemanticContent;

namespace SonnetDB.Core.Tests.SemanticContent;

/// <summary>
/// Semantic Content 派生索引状态机测试。
/// </summary>
public sealed class SemanticIndexStateMachineTests
{
    [Fact]
    public void Transition_FromPendingToRunning_IncrementsAttemptOnce()
    {
        var timestamp = DateTimeOffset.Parse("2026-08-03T00:00:00Z");
        var pending = new SemanticIndexStateInfo(
            SemanticIndexState.Pending,
            attempt: 3,
            updatedUtc: timestamp);

        var running = SemanticIndexStateMachine.Transition(
            pending,
            SemanticIndexState.Running,
            timestamp.AddMinutes(1));
        var sameRunning = SemanticIndexStateMachine.Transition(
            running,
            SemanticIndexState.Running,
            timestamp.AddMinutes(2));

        Assert.Equal(SemanticIndexState.Running, running.State);
        Assert.Equal(4, running.Attempt);
        Assert.Equal(4, sameRunning.Attempt);
        Assert.Equal(timestamp.AddMinutes(2), sameRunning.UpdatedUtc);
    }

    [Fact]
    public void Transition_ToFailedRequiresError_AndRetryIncrementsAttempt()
    {
        var running = new SemanticIndexStateInfo(
            SemanticIndexState.Running,
            attempt: 1,
            updatedUtc: DateTimeOffset.Parse("2026-08-03T00:00:00Z"));

        Assert.Throws<ArgumentException>(() => SemanticIndexStateMachine.Transition(
            running,
            SemanticIndexState.Failed));

        var failed = SemanticIndexStateMachine.Transition(
            running,
            SemanticIndexState.Failed,
            lastError: "provider timeout");
        var retry = SemanticIndexStateMachine.Transition(
            failed,
            SemanticIndexState.Running,
            lastError: "must be cleared");

        Assert.Equal(SemanticIndexState.Failed, failed.State);
        Assert.Equal("provider timeout", failed.LastError);
        Assert.Equal(1, failed.Attempt);
        Assert.Equal(SemanticIndexState.Running, retry.State);
        Assert.Equal(2, retry.Attempt);
        Assert.Null(retry.LastError);
    }

    [Fact]
    public void CanTransition_RejectsInvalidEdges_AndTryTransitionReturnsFalse()
    {
        Assert.True(SemanticIndexStateMachine.CanTransition(
            SemanticIndexState.Pending,
            SemanticIndexState.Running));
        Assert.True(SemanticIndexStateMachine.CanTransition(
            SemanticIndexState.Ready,
            SemanticIndexState.Stale));
        Assert.False(SemanticIndexStateMachine.CanTransition(
            SemanticIndexState.Ready,
            SemanticIndexState.Pending));
        Assert.False(SemanticIndexStateMachine.CanTransition(
            (SemanticIndexState)255,
            SemanticIndexState.Pending));

        var ready = new SemanticIndexStateInfo(SemanticIndexState.Ready);
        bool transitioned = SemanticIndexStateMachine.TryTransition(
            ready,
            SemanticIndexState.Pending,
            out var next);

        Assert.False(transitioned);
        Assert.Null(next);
    }

    [Fact]
    public void Transition_WithOverflowingAttemptFailsDeterministically()
    {
        var current = new SemanticIndexStateInfo(
            SemanticIndexState.Pending,
            attempt: int.MaxValue);

        Assert.Throws<OverflowException>(() => SemanticIndexStateMachine.Transition(
            current,
            SemanticIndexState.Running));
    }
}
