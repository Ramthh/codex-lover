using CodexAccountMonitor.Core.Services;

namespace CodexAccountMonitor.Tests;

public sealed class ManagedSessionRecoveryPolicyTests
{
    [Fact]
    public void Evaluate_UsesExponentialDelaysThenOpensAndExpiresCircuit()
    {
        var policy = new ManagedSessionRecoveryPolicy(
            stabilityWindow: TimeSpan.FromSeconds(10),
            circuitDuration: TimeSpan.FromSeconds(30),
            baseDelay: TimeSpan.FromSeconds(2),
            maximumAttempts: 3);
        var profileId = Guid.NewGuid();
        var start = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);

        var first = policy.Evaluate(profileId, start);
        var second = policy.Evaluate(profileId, start.AddSeconds(1));
        var third = policy.Evaluate(profileId, start.AddSeconds(2));
        var circuitOpened = policy.Evaluate(profileId, start.AddSeconds(3));
        var circuitStillOpen = policy.Evaluate(profileId, start.AddSeconds(10));
        var afterCircuit = policy.Evaluate(profileId, start.AddSeconds(34));

        Assert.Equal(new ManagedSessionRecoveryDecision(true, TimeSpan.Zero, 1), first);
        Assert.Equal(new ManagedSessionRecoveryDecision(true, TimeSpan.FromSeconds(2), 2), second);
        Assert.Equal(new ManagedSessionRecoveryDecision(true, TimeSpan.FromSeconds(4), 3), third);
        Assert.Equal(
            new ManagedSessionRecoveryDecision(false, TimeSpan.Zero, 4, TimeSpan.FromSeconds(30)),
            circuitOpened);
        Assert.Equal(
            new ManagedSessionRecoveryDecision(false, TimeSpan.Zero, 4, TimeSpan.FromSeconds(23)),
            circuitStillOpen);
        Assert.Equal(new ManagedSessionRecoveryDecision(true, TimeSpan.Zero, 1), afterCircuit);
    }

    [Fact]
    public void Evaluate_StableWindowResetsAttemptSequence()
    {
        var policy = new ManagedSessionRecoveryPolicy(
            stabilityWindow: TimeSpan.FromSeconds(10),
            circuitDuration: TimeSpan.FromSeconds(30),
            baseDelay: TimeSpan.FromSeconds(2),
            maximumAttempts: 3);
        var profileId = Guid.NewGuid();
        var start = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);

        var first = policy.Evaluate(profileId, start);
        var second = policy.Evaluate(profileId, start.AddSeconds(1));
        var afterStability = policy.Evaluate(profileId, start.AddSeconds(12));

        Assert.Equal(1, first.Attempt);
        Assert.Equal(2, second.Attempt);
        Assert.Equal(TimeSpan.FromSeconds(2), second.Delay);
        Assert.True(afterStability.IsAllowed);
        Assert.Equal(1, afterStability.Attempt);
        Assert.Equal(TimeSpan.Zero, afterStability.Delay);
    }

    [Fact]
    public void Reset_RemovesFailureHistoryForOnlyTheRequestedProfile()
    {
        var policy = new ManagedSessionRecoveryPolicy(baseDelay: TimeSpan.FromSeconds(1));
        var resetProfile = Guid.NewGuid();
        var otherProfile = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);
        _ = policy.Evaluate(resetProfile, now);
        _ = policy.Evaluate(otherProfile, now);
        _ = policy.Evaluate(resetProfile, now.AddMilliseconds(1));
        _ = policy.Evaluate(otherProfile, now.AddMilliseconds(1));

        policy.Reset(resetProfile);

        Assert.Equal(1, policy.Evaluate(resetProfile, now.AddMilliseconds(2)).Attempt);
        Assert.Equal(3, policy.Evaluate(otherProfile, now.AddMilliseconds(2)).Attempt);
    }
}
