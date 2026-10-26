namespace CodexAccountMonitor.Core.Services;

public sealed record ManagedSessionRecoveryDecision(
    bool IsAllowed,
    TimeSpan Delay,
    int Attempt,
    TimeSpan? CircuitRemaining = null);

/// <summary>
/// Bounds automatic resident-session restarts so a persistently crashing or
/// incompatible Codex binary cannot create an unbounded rapid restart loop.
/// </summary>
public sealed class ManagedSessionRecoveryPolicy
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, RecoveryState> _states = new();
    private readonly TimeSpan _stabilityWindow;
    private readonly TimeSpan _circuitDuration;
    private readonly TimeSpan _baseDelay;
    private readonly int _maximumAttempts;

    public ManagedSessionRecoveryPolicy(
        TimeSpan? stabilityWindow = null,
        TimeSpan? circuitDuration = null,
        TimeSpan? baseDelay = null,
        int maximumAttempts = 5)
    {
        if (maximumAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        }

        _stabilityWindow = stabilityWindow ?? TimeSpan.FromMinutes(2);
        _circuitDuration = circuitDuration ?? TimeSpan.FromMinutes(5);
        _baseDelay = baseDelay ?? TimeSpan.FromSeconds(1);
        _maximumAttempts = maximumAttempts;
    }

    public ManagedSessionRecoveryDecision Evaluate(Guid profileId, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_states.TryGetValue(profileId, out var state))
            {
                state = new RecoveryState();
                _states[profileId] = state;
            }

            if (state.CircuitUntil is DateTimeOffset circuitUntil)
            {
                if (now < circuitUntil)
                {
                    return new ManagedSessionRecoveryDecision(
                        false,
                        TimeSpan.Zero,
                        state.Attempts,
                        circuitUntil - now);
                }

                state.Attempts = 0;
                state.CircuitUntil = null;
                state.LastAttempt = null;
            }

            if (state.LastAttempt is DateTimeOffset lastAttempt
                && now - lastAttempt >= _stabilityWindow)
            {
                state.Attempts = 0;
            }

            state.Attempts++;
            state.LastAttempt = now;
            if (state.Attempts > _maximumAttempts)
            {
                state.CircuitUntil = now + _circuitDuration;
                return new ManagedSessionRecoveryDecision(
                    false,
                    TimeSpan.Zero,
                    state.Attempts,
                    _circuitDuration);
            }

            var exponent = Math.Clamp(state.Attempts - 2, 0, 20);
            var multiplier = state.Attempts == 1 ? 0d : Math.Pow(2, exponent);
            var delayMilliseconds = Math.Min(
                _baseDelay.TotalMilliseconds * multiplier,
                TimeSpan.FromMinutes(1).TotalMilliseconds);
            return new ManagedSessionRecoveryDecision(
                true,
                TimeSpan.FromMilliseconds(delayMilliseconds),
                state.Attempts);
        }
    }

    public void Reset(Guid profileId)
    {
        lock (_gate)
        {
            _states.Remove(profileId);
        }
    }

    private sealed class RecoveryState
    {
        public int Attempts { get; set; }
        public DateTimeOffset? LastAttempt { get; set; }
        public DateTimeOffset? CircuitUntil { get; set; }
    }
}
