using CodexAccountMonitor.Core.Models;

namespace CodexAccountMonitor.Core.Services;

public enum ProfileSwitchPhase
{
    Inactive,
    Active,
    QueuedAfterCurrentTask,
    Switching,
    RollbackSucceeded,
    RollbackFailed
}

public sealed record ProfileSwitchStatus(
    Guid? ActiveProfileId,
    Guid? PendingProfileId,
    ProfileSwitchPhase Phase,
    string? SafeErrorCode = null);

public sealed record ManagedProfileUsageReadResult(
    bool HasAccount,
    bool IdentityMatches,
    QuotaSnapshot? Snapshot);

public sealed record ManagedResetCreditResult(bool Handled, string? Outcome);

public interface IManagedProfileSession : IAsyncDisposable
{
    Guid ProfileId { get; }
    bool IsTaskRunning { get; }
    bool IsHealthy => true;
    Task<bool> VerifyIdentityAsync(AccountProfile expectedProfile, CancellationToken cancellationToken);
    Task StopGracefullyAsync(CancellationToken cancellationToken);

    Task<ManagedProfileUsageReadResult> ReadUsageAsync(
        AccountProfile expectedProfile,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("This managed session does not expose usage reads.");

    Task<string> ConsumeResetCreditAsync(
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("This managed session does not expose reset-credit operations.");
}

public interface IManagedProfileSessionFactory
{
    Task<IManagedProfileSession> StartAsync(AccountProfile profile, CancellationToken cancellationToken);
}

public sealed class ProfileSwitchCoordinator : IAsyncDisposable
{
    private static readonly TimeSpan RecoveryAttemptTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RollbackAttemptTimeout = TimeSpan.FromSeconds(10);
    private readonly IManagedProfileSessionFactory _sessionFactory;
    private readonly ProfileOperationCoordinator _profileOperations;
    private readonly SemaphoreSlim _switchGate = new(1, 1);
    private readonly object _profilesGate = new();
    private readonly object _disposeGate = new();
    private Dictionary<Guid, AccountProfile> _profiles = new();
    private IManagedProfileSession? _activeSession;
    private Guid? _pendingProfileId;
    private Task? _disposeTask;
    private int _disposeRequested;
    private int _disposed;

    public ProfileSwitchCoordinator(
        IManagedProfileSessionFactory sessionFactory,
        ProfileOperationCoordinator? profileOperations = null)
    {
        _sessionFactory = sessionFactory;
        _profileOperations = profileOperations ?? new ProfileOperationCoordinator();
    }

    public ProfileSwitchStatus Status { get; private set; } =
        new(null, null, ProfileSwitchPhase.Inactive);

    public bool HasRunningTask => _activeSession?.IsTaskRunning == true;

    public bool IsActiveProfile(Guid profileId) =>
        _activeSession?.ProfileId == profileId
        && Status.ActiveProfileId == profileId;

    public bool OwnsProfile(Guid profileId) => _activeSession?.ProfileId == profileId;

    public event EventHandler<ProfileSwitchStatus>? StatusChanged;

    public void RegisterProfiles(IEnumerable<AccountProfile> profiles)
    {
        var replacement = profiles.ToDictionary(static profile => profile.Id);
        lock (_profilesGate)
        {
            _profiles = replacement;
        }
    }

    public async Task<ProfileSwitchStatus> SwitchAsync(
        Guid targetProfileId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        await _switchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            if (!TryGetProfile(targetProfileId, out var target))
            {
                throw new KeyNotFoundException("The requested account profile is not registered.");
            }

            if (_activeSession?.ProfileId == targetProfileId
                && Status.Phase == ProfileSwitchPhase.Active
                && Status.ActiveProfileId == targetProfileId)
            {
                _pendingProfileId = null;
                return SetStatus(new(targetProfileId, null, ProfileSwitchPhase.Active));
            }

            if (_activeSession?.IsTaskRunning == true)
            {
                _pendingProfileId = targetProfileId;
                return SetStatus(new(
                    Status.ActiveProfileId,
                    targetProfileId,
                    ProfileSwitchPhase.QueuedAfterCurrentTask));
            }

            return await SwitchNowAsync(target, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _switchGate.Release();
        }
    }

    public async Task<ProfileSwitchStatus> ApplyPendingIfIdleAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        await _switchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            if (_pendingProfileId is null || _activeSession?.IsTaskRunning == true)
            {
                return Status;
            }

            var pending = _pendingProfileId.Value;
            _pendingProfileId = null;
            if (!TryGetProfile(pending, out var target))
            {
                var verifiedActive = Status.ActiveProfileId;
                return SetStatus(new(
                    verifiedActive,
                    null,
                    verifiedActive is null ? ProfileSwitchPhase.Inactive : ProfileSwitchPhase.Active,
                    "pending_profile_missing"));
            }

            return await SwitchNowAsync(target, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _switchGate.Release();
        }
    }

    public async Task<ManagedProfileUsageReadResult?> ReadActiveUsageAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        await _switchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            if (_activeSession?.ProfileId != profileId || !TryGetProfile(profileId, out var profile))
            {
                return null;
            }

            return await _activeSession.ReadUsageAsync(profile, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _switchGate.Release();
        }
    }

    public async Task<ManagedResetCreditResult> ConsumeActiveResetCreditAsync(
        Guid profileId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        await _switchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            if (_activeSession?.ProfileId != profileId || Status.ActiveProfileId != profileId)
            {
                return new ManagedResetCreditResult(false, null);
            }

            if (!TryGetProfile(profileId, out var profile)
                || !await _activeSession.VerifyIdentityAsync(profile, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("identity_mismatch");
            }

            var outcome = await _activeSession.ConsumeResetCreditAsync(idempotencyKey, cancellationToken)
                .ConfigureAwait(false);
            return new ManagedResetCreditResult(true, outcome);
        }
        finally
        {
            _switchGate.Release();
        }
    }

    public async Task<ProfileSwitchStatus> DeactivateAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        await _switchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            if (_activeSession?.ProfileId != profileId)
            {
                if (_pendingProfileId == profileId)
                {
                    _pendingProfileId = null;
                    return SetStatus(Status with { PendingProfileId = null,
                        Phase = Status.ActiveProfileId is null ? ProfileSwitchPhase.Inactive : ProfileSwitchPhase.Active });
                }
                return Status;
            }
            if (_activeSession.IsTaskRunning)
            {
                throw new InvalidOperationException("managed_task_running");
            }
            await _activeSession.StopGracefullyAsync(cancellationToken).ConfigureAwait(false);
            await _activeSession.DisposeAsync().ConfigureAwait(false);
            _activeSession = null;
            _pendingProfileId = null;
            return SetStatus(new(null, null, ProfileSwitchPhase.Inactive));
        }
        finally
        {
            _switchGate.Release();
        }
    }

    public async Task<ProfileSwitchStatus> RecoverTerminatedSessionAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        await _switchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            if (_activeSession?.ProfileId != profileId || _activeSession.IsHealthy)
            {
                return Status;
            }

            var failedSession = _activeSession;
            _activeSession = null;
            await CleanupSessionWithoutThrowingAsync(failedSession).ConfigureAwait(false);
            if (!TryGetProfile(profileId, out var failedProfile))
            {
                _pendingProfileId = null;
                return SetStatus(new(null, null, ProfileSwitchPhase.Inactive, "terminated_profile_missing"));
            }

            var applyQueued = false;
            var target = failedProfile;
            if (_pendingProfileId is Guid pendingId && TryGetProfile(pendingId, out var queuedProfile))
            {
                applyQueued = true;
                target = queuedProfile;
            }
            _pendingProfileId = null;
            SetStatus(new(null, target.Id, ProfileSwitchPhase.Switching, "managed_session_terminated"));
            try
            {
                using var attempt = CreateBoundedToken(cancellationToken, RecoveryAttemptTimeout);
                _activeSession = await StartVerifiedSessionAsync(target, attempt.Token)
                    .ConfigureAwait(false);
                return SetStatus(new(
                    target.Id,
                    null,
                    ProfileSwitchPhase.Active,
                    applyQueued ? "queued_switch_applied_after_termination" : "managed_session_restarted"));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _activeSession = null;
                SetStatus(new(
                    null,
                    null,
                    ProfileSwitchPhase.RollbackFailed,
                    "managed_session_recovery_cancelled"));
                throw;
            }
            catch
            {
                _activeSession = null;
                if (applyQueued)
                {
                    try
                    {
                        using var rollback = CreateBoundedToken(cancellationToken, RollbackAttemptTimeout);
                        _activeSession = await StartVerifiedSessionAsync(failedProfile, rollback.Token)
                            .ConfigureAwait(false);
                        return SetStatus(new(
                            failedProfile.Id,
                            null,
                            ProfileSwitchPhase.RollbackSucceeded,
                            "queued_switch_failed_after_termination"));
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        _activeSession = null;
                        SetStatus(new(
                            null,
                            null,
                            ProfileSwitchPhase.RollbackFailed,
                            "managed_session_recovery_cancelled"));
                        throw;
                    }
                    catch
                    {
                        _activeSession = null;
                    }
                }

                return SetStatus(new(
                    null,
                    null,
                    ProfileSwitchPhase.RollbackFailed,
                    "managed_session_restart_failed"));
            }
        }
        finally
        {
            _switchGate.Release();
        }
    }

    public async Task<ProfileSwitchStatus> QuarantineActiveSessionAsync(
        Guid profileId,
        string safeErrorCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(safeErrorCode);
        ThrowIfUnavailable();
        await _switchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            if (_activeSession?.ProfileId != profileId)
            {
                return Status;
            }

            if (_activeSession.IsTaskRunning)
            {
                // Do not terminate work that this monitor owns. The same profile is
                // re-verified as soon as the turn becomes idle, while the UI fails
                // closed and no longer presents the session as active.
                _pendingProfileId = profileId;
                return SetStatus(new(
                    null,
                    profileId,
                    ProfileSwitchPhase.QueuedAfterCurrentTask,
                    safeErrorCode));
            }

            var quarantined = _activeSession;
            _activeSession = null;
            _pendingProfileId = null;
            var status = SetStatus(new(null, null, ProfileSwitchPhase.Inactive, safeErrorCode));
            await CleanupSessionWithoutThrowingAsync(quarantined).ConfigureAwait(false);
            return status;
        }
        finally
        {
            _switchGate.Release();
        }
    }

    private async Task<ProfileSwitchStatus> SwitchNowAsync(
        AccountProfile target,
        CancellationToken cancellationToken)
    {
        var previousSession = _activeSession;
        var previousProfile = previousSession is null
            ? null
            : TryGetProfile(previousSession.ProfileId, out var foundPrevious)
                ? foundPrevious
                : null;

        _pendingProfileId = null;
        SetStatus(new(previousSession?.ProfileId, target.Id, ProfileSwitchPhase.Switching));
        if (previousSession is not null)
        {
            try
            {
                await previousSession.StopGracefullyAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                SetStatus(new(previousSession.ProfileId, null, ProfileSwitchPhase.Active, "switch_cancelled"));
                throw;
            }
            catch
            {
                return SetStatus(new(
                    previousSession.ProfileId,
                    null,
                    ProfileSwitchPhase.Active,
                    "previous_profile_stop_failed"));
            }

            try
            {
                await previousSession.DisposeAsync().ConfigureAwait(false);
                _activeSession = null;
            }
            catch
            {
                _activeSession = null;
                return SetStatus(new(
                    null,
                    null,
                    ProfileSwitchPhase.RollbackFailed,
                    "previous_profile_dispose_failed"));
            }
        }

        try
        {
            _activeSession = await StartVerifiedSessionAsync(target, cancellationToken).ConfigureAwait(false);
            return SetStatus(new(target.Id, null, ProfileSwitchPhase.Active));
        }
        catch (OperationCanceledException)
        {
            using var rollback = new CancellationTokenSource(RollbackAttemptTimeout);
            await RestoreAfterFailedSwitchAsync(previousProfile, "switch_cancelled", rollback.Token)
                .ConfigureAwait(false);
            throw;
        }
        catch
        {
            using var rollback = new CancellationTokenSource(RollbackAttemptTimeout);
            return await RestoreAfterFailedSwitchAsync(previousProfile, "profile_start_failed", rollback.Token)
                .ConfigureAwait(false);
        }
    }

    private async Task<IManagedProfileSession> StartVerifiedSessionAsync(
        AccountProfile profile,
        CancellationToken cancellationToken)
    {
        using var lease = await _profileOperations.AcquireAsync(profile.Id, cancellationToken).ConfigureAwait(false);
        IManagedProfileSession? session = null;
        try
        {
            session = await _sessionFactory.StartAsync(profile, cancellationToken).ConfigureAwait(false);
            if (!await session.VerifyIdentityAsync(profile, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("profile_identity_mismatch");
            }

            return session;
        }
        catch
        {
            if (session is not null)
            {
                await CleanupSessionWithoutThrowingAsync(session).ConfigureAwait(false);
            }

            throw;
        }
    }

    private async Task<ProfileSwitchStatus> RestoreAfterFailedSwitchAsync(
        AccountProfile? previousProfile,
        string safeErrorCode,
        CancellationToken cancellationToken)
    {
        if (previousProfile is null)
        {
            _activeSession = null;
            return SetStatus(new(null, null, ProfileSwitchPhase.RollbackFailed, safeErrorCode));
        }

        try
        {
            _activeSession = await StartVerifiedSessionAsync(previousProfile, cancellationToken)
                .ConfigureAwait(false);
            return SetStatus(new(
                previousProfile.Id,
                null,
                ProfileSwitchPhase.RollbackSucceeded,
                safeErrorCode));
        }
        catch (InvalidOperationException exception)
            when (string.Equals(exception.Message, "profile_identity_mismatch", StringComparison.Ordinal))
        {
            _activeSession = null;
            return SetStatus(new(null, null, ProfileSwitchPhase.RollbackFailed, "rollback_identity_mismatch"));
        }
        catch
        {
            _activeSession = null;
            return SetStatus(new(null, null, ProfileSwitchPhase.RollbackFailed, "rollback_start_failed"));
        }
    }

    private static async Task CleanupSessionWithoutThrowingAsync(IManagedProfileSession session)
    {
        try
        {
            await session.StopGracefullyAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
        }

        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static CancellationTokenSource CreateBoundedToken(
        CancellationToken cancellationToken,
        TimeSpan timeout)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(timeout);
        return source;
    }

    private bool TryGetProfile(Guid profileId, out AccountProfile profile)
    {
        lock (_profilesGate)
        {
            return _profiles.TryGetValue(profileId, out profile!);
        }
    }

    private ProfileSwitchStatus SetStatus(ProfileSwitchStatus status)
    {
        Status = status;
        var handlers = StatusChanged?.GetInvocationList();
        if (handlers is not null)
        {
            foreach (var handler in handlers)
            {
                try
                {
                    ((EventHandler<ProfileSwitchStatus>)handler)(this, status);
                }
                catch
                {
                    // Observers cannot alter the switch transaction.
                }
            }
        }

        return status;
    }

    private void ThrowIfUnavailable() => ObjectDisposedException.ThrowIf(
        Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _disposeRequested) != 0,
        this);

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return ValueTask.CompletedTask;
            }

            if (_disposeTask is null || _disposeTask.IsFaulted || _disposeTask.IsCanceled)
            {
                Volatile.Write(ref _disposeRequested, 1);
                _disposeTask = DisposeCoreAsync();
            }

            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        var completed = false;
        await _switchGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_activeSession?.IsTaskRunning == true)
            {
                throw new InvalidOperationException("managed_task_running");
            }

            var session = _activeSession;
            _activeSession = null;
            _pendingProfileId = null;
            SetStatus(new(null, null, ProfileSwitchPhase.Inactive));
            if (session is not null)
            {
                await CleanupSessionWithoutThrowingAsync(session).ConfigureAwait(false);
            }

            Volatile.Write(ref _disposed, 1);
            completed = true;
        }
        finally
        {
            _switchGate.Release();
            if (!completed)
            {
                Volatile.Write(ref _disposeRequested, 0);
            }
        }
    }
}
