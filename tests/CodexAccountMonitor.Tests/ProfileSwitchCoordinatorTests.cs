using CodexAccountMonitor.Core.Models;
using CodexAccountMonitor.Core.Services;

namespace CodexAccountMonitor.Tests;

public sealed class ProfileSwitchCoordinatorTests
{
    [Fact]
    public async Task SwitchAsync_FirstIdleProfileBecomesActiveAfterIdentityVerification()
    {
        var profile = Profile(1);
        var session = new FakeSession(profile.Id);
        var factory = new FakeSessionFactory();
        factory.QueueSession(profile.Id, session);
        await using var coordinator = Coordinator(factory, profile);
        var statuses = CaptureStatuses(coordinator);

        var result = await coordinator.SwitchAsync(profile.Id);

        Assert.Equal(ProfileSwitchPhase.Active, result.Phase);
        Assert.Equal(profile.Id, result.ActiveProfileId);
        Assert.Null(result.PendingProfileId);
        Assert.Equal(1, session.VerifyCount);
        Assert.Equal(new[] { profile.Id }, factory.StartedProfileIds);
        Assert.Collection(
            statuses,
            status => Assert.Equal(ProfileSwitchPhase.Switching, status.Phase),
            status => Assert.Equal(ProfileSwitchPhase.Active, status.Phase));
    }

    [Fact]
    public async Task SwitchAsync_AlreadyActiveProfileDoesNotRestartSession()
    {
        var profile = Profile(1);
        var session = new FakeSession(profile.Id);
        var factory = new FakeSessionFactory();
        factory.QueueSession(profile.Id, session);
        await using var coordinator = Coordinator(factory, profile);
        await coordinator.SwitchAsync(profile.Id);

        var result = await coordinator.SwitchAsync(profile.Id);

        Assert.Equal(ProfileSwitchPhase.Active, result.Phase);
        Assert.Equal(profile.Id, result.ActiveProfileId);
        Assert.Single(factory.StartedProfileIds);
        Assert.Equal(0, session.StopCount);
        Assert.Equal(0, session.DisposeCount);
    }

    [Fact]
    public async Task SwitchAsync_IdleSwitchStopsOldSessionBeforeActivatingVerifiedCandidate()
    {
        var first = Profile(1);
        var second = Profile(2);
        var firstSession = new FakeSession(first.Id);
        var secondSession = new FakeSession(second.Id);
        var factory = new FakeSessionFactory();
        factory.QueueSession(first.Id, firstSession);
        factory.QueueSession(second.Id, secondSession);
        await using var coordinator = Coordinator(factory, first, second);
        await coordinator.SwitchAsync(first.Id);
        var statuses = CaptureStatuses(coordinator);

        var result = await coordinator.SwitchAsync(second.Id);

        Assert.Equal(ProfileSwitchPhase.Active, result.Phase);
        Assert.Equal(second.Id, result.ActiveProfileId);
        Assert.Equal(1, firstSession.StopCount);
        Assert.Equal(1, firstSession.DisposeCount);
        Assert.Equal(1, secondSession.VerifyCount);
        Assert.Collection(
            statuses,
            status =>
            {
                Assert.Equal(ProfileSwitchPhase.Switching, status.Phase);
                Assert.Equal(first.Id, status.ActiveProfileId);
            },
            status =>
            {
                Assert.Equal(ProfileSwitchPhase.Active, status.Phase);
                Assert.Equal(second.Id, status.ActiveProfileId);
            });
    }

    [Fact]
    public async Task SwitchAsync_RunningTaskQueuesTargetWithoutStoppingOrStartingAnything()
    {
        var first = Profile(1);
        var second = Profile(2);
        var firstSession = new FakeSession(first.Id) { IsTaskRunning = true };
        var secondSession = new FakeSession(second.Id);
        var factory = new FakeSessionFactory();
        factory.QueueSession(first.Id, firstSession);
        factory.QueueSession(second.Id, secondSession);
        await using var coordinator = Coordinator(factory, first, second);
        await coordinator.SwitchAsync(first.Id);

        var result = await coordinator.SwitchAsync(second.Id);

        Assert.Equal(ProfileSwitchPhase.QueuedAfterCurrentTask, result.Phase);
        Assert.Equal(first.Id, result.ActiveProfileId);
        Assert.Equal(second.Id, result.PendingProfileId);
        Assert.True(coordinator.HasRunningTask);
        Assert.Equal(0, firstSession.StopCount);
        Assert.Equal(0, firstSession.DisposeCount);
        Assert.Equal(new[] { first.Id }, factory.StartedProfileIds);

        // The coordinator now correctly refuses disposal while managed work is
        // running, so leave the fake session idle for await-using teardown.
        firstSession.IsTaskRunning = false;
    }

    [Fact]
    public async Task ApplyPendingIfIdleAsync_WaitsWhileTaskRunsThenSwitchesBeforeNextTask()
    {
        var first = Profile(1);
        var second = Profile(2);
        var firstSession = new FakeSession(first.Id) { IsTaskRunning = true };
        var secondSession = new FakeSession(second.Id);
        var factory = new FakeSessionFactory();
        factory.QueueSession(first.Id, firstSession);
        factory.QueueSession(second.Id, secondSession);
        await using var coordinator = Coordinator(factory, first, second);
        await coordinator.SwitchAsync(first.Id);
        var queued = await coordinator.SwitchAsync(second.Id);

        var whileBusy = await coordinator.ApplyPendingIfIdleAsync();

        Assert.Equal(queued, whileBusy);
        Assert.Equal(0, firstSession.StopCount);
        Assert.Equal(new[] { first.Id }, factory.StartedProfileIds);

        firstSession.IsTaskRunning = false;
        var applied = await coordinator.ApplyPendingIfIdleAsync();

        Assert.Equal(ProfileSwitchPhase.Active, applied.Phase);
        Assert.Equal(second.Id, applied.ActiveProfileId);
        Assert.Null(applied.PendingProfileId);
        Assert.Equal(1, firstSession.StopCount);
        Assert.Equal(1, firstSession.DisposeCount);
        Assert.Equal(new[] { first.Id, second.Id }, factory.StartedProfileIds);
    }

    [Fact]
    public async Task ApplyPendingIfIdleAsync_MissingQueuedProfileKeepsCurrentProfileAndReportsSafeError()
    {
        var first = Profile(1);
        var second = Profile(2);
        var firstSession = new FakeSession(first.Id) { IsTaskRunning = true };
        var factory = new FakeSessionFactory();
        factory.QueueSession(first.Id, firstSession);
        await using var coordinator = Coordinator(factory, first, second);
        await coordinator.SwitchAsync(first.Id);
        await coordinator.SwitchAsync(second.Id);
        coordinator.RegisterProfiles(new[] { first });
        firstSession.IsTaskRunning = false;

        var result = await coordinator.ApplyPendingIfIdleAsync();

        Assert.Equal(ProfileSwitchPhase.Active, result.Phase);
        Assert.Equal(first.Id, result.ActiveProfileId);
        Assert.Null(result.PendingProfileId);
        Assert.Equal("pending_profile_missing", result.SafeErrorCode);
        Assert.Equal(0, firstSession.StopCount);
    }

    [Fact]
    public async Task SwitchAsync_CandidateStartFailureRestartsAndVerifiesPreviousProfile()
    {
        var first = Profile(1);
        var second = Profile(2);
        var original = new FakeSession(first.Id);
        var rollback = new FakeSession(first.Id);
        var factory = new FakeSessionFactory();
        factory.QueueSession(first.Id, original);
        factory.QueueFailure(second.Id, new InvalidOperationException("candidate failed"));
        factory.QueueSession(first.Id, rollback);
        await using var coordinator = Coordinator(factory, first, second);
        await coordinator.SwitchAsync(first.Id);

        var result = await coordinator.SwitchAsync(second.Id);

        Assert.Equal(ProfileSwitchPhase.RollbackSucceeded, result.Phase);
        Assert.Equal(first.Id, result.ActiveProfileId);
        Assert.Equal("profile_start_failed", result.SafeErrorCode);
        Assert.Equal(1, original.StopCount);
        Assert.Equal(1, original.DisposeCount);
        Assert.Equal(1, rollback.VerifyCount);
        Assert.Equal(new[] { first.Id, second.Id, first.Id }, factory.StartedProfileIds);
    }

    [Fact]
    public async Task SwitchAsync_IdentityMismatchCleansCandidateAndRollsBack()
    {
        var first = Profile(1);
        var second = Profile(2);
        var original = new FakeSession(first.Id);
        var mismatchedCandidate = new FakeSession(second.Id) { IdentityMatches = false };
        var rollback = new FakeSession(first.Id);
        var factory = new FakeSessionFactory();
        factory.QueueSession(first.Id, original);
        factory.QueueSession(second.Id, mismatchedCandidate);
        factory.QueueSession(first.Id, rollback);
        await using var coordinator = Coordinator(factory, first, second);
        await coordinator.SwitchAsync(first.Id);

        var result = await coordinator.SwitchAsync(second.Id);

        Assert.Equal(ProfileSwitchPhase.RollbackSucceeded, result.Phase);
        Assert.Equal(first.Id, result.ActiveProfileId);
        Assert.Equal("profile_start_failed", result.SafeErrorCode);
        Assert.Equal(1, mismatchedCandidate.StopCount);
        Assert.Equal(1, mismatchedCandidate.DisposeCount);
        Assert.Equal(1, rollback.VerifyCount);
    }

    [Fact]
    public async Task SwitchAsync_RollbackIdentityMismatchLeavesNoProfileActive()
    {
        var first = Profile(1);
        var second = Profile(2);
        var original = new FakeSession(first.Id);
        var factory = new FakeSessionFactory();
        var rollback = new FakeSession(first.Id) { IdentityMatches = false };
        factory.QueueSession(first.Id, original);
        factory.QueueFailure(second.Id, new InvalidOperationException("candidate failed"));
        factory.QueueSession(first.Id, rollback);
        await using var coordinator = Coordinator(factory, first, second);
        await coordinator.SwitchAsync(first.Id);

        var result = await coordinator.SwitchAsync(second.Id);

        Assert.Equal(ProfileSwitchPhase.RollbackFailed, result.Phase);
        Assert.Null(result.ActiveProfileId);
        Assert.Equal("rollback_identity_mismatch", result.SafeErrorCode);
        Assert.Equal(1, rollback.StopCount);
        Assert.Equal(1, rollback.DisposeCount);
    }

    [Fact]
    public async Task SwitchAsync_RollbackStartFailureLeavesNoProfileActive()
    {
        var first = Profile(1);
        var second = Profile(2);
        var original = new FakeSession(first.Id);
        var factory = new FakeSessionFactory();
        factory.QueueSession(first.Id, original);
        factory.QueueFailure(second.Id, new InvalidOperationException("candidate failed"));
        factory.QueueFailure(first.Id, new InvalidOperationException("rollback failed"));
        await using var coordinator = Coordinator(factory, first, second);
        await coordinator.SwitchAsync(first.Id);

        var result = await coordinator.SwitchAsync(second.Id);

        Assert.Equal(ProfileSwitchPhase.RollbackFailed, result.Phase);
        Assert.Null(result.ActiveProfileId);
        Assert.Equal("rollback_start_failed", result.SafeErrorCode);
    }

    [Fact]
    public async Task SwitchAsync_FirstProfileFailureHasNoRollbackCandidate()
    {
        var profile = Profile(1);
        var factory = new FakeSessionFactory();
        factory.QueueFailure(profile.Id, new InvalidOperationException("candidate failed"));
        await using var coordinator = Coordinator(factory, profile);

        var result = await coordinator.SwitchAsync(profile.Id);

        Assert.Equal(ProfileSwitchPhase.RollbackFailed, result.Phase);
        Assert.Null(result.ActiveProfileId);
        Assert.Equal("profile_start_failed", result.SafeErrorCode);
    }

    [Fact]
    public async Task SwitchAsync_UnregisteredProfileThrowsWithoutChangingStatus()
    {
        var profile = Profile(1);
        var factory = new FakeSessionFactory();
        await using var coordinator = Coordinator(factory, profile);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => coordinator.SwitchAsync(Guid.NewGuid()));

        Assert.Equal(ProfileSwitchPhase.Inactive, coordinator.Status.Phase);
        Assert.Empty(factory.StartedProfileIds);
    }

    [Fact]
    public async Task DisposeAsync_GracefullyStopsAndDisposesActiveSession()
    {
        var profile = Profile(1);
        var session = new FakeSession(profile.Id);
        var factory = new FakeSessionFactory();
        factory.QueueSession(profile.Id, session);
        var coordinator = Coordinator(factory, profile);
        await coordinator.SwitchAsync(profile.Id);

        await coordinator.DisposeAsync();

        Assert.Equal(1, session.StopCount);
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task DisposeAsync_RunningTaskIsPreservedAndDisposalCanBeRetriedAfterIdle()
    {
        var profile = Profile(1);
        var session = new FakeSession(profile.Id) { IsTaskRunning = true };
        var factory = new FakeSessionFactory();
        factory.QueueSession(profile.Id, session);
        var coordinator = Coordinator(factory, profile);
        await coordinator.SwitchAsync(profile.Id);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.DisposeAsync().AsTask());

        Assert.Equal("managed_task_running", exception.Message);
        Assert.True(coordinator.OwnsProfile(profile.Id));
        Assert.True(coordinator.HasRunningTask);
        Assert.Equal(ProfileSwitchPhase.Active, coordinator.Status.Phase);
        Assert.Equal(0, session.StopCount);
        Assert.Equal(0, session.DisposeCount);

        session.IsTaskRunning = false;
        await coordinator.DisposeAsync();

        Assert.Equal(1, session.StopCount);
        Assert.Equal(1, session.DisposeCount);
        Assert.Equal(ProfileSwitchPhase.Inactive, coordinator.Status.Phase);
    }

    [Fact]
    public async Task QuarantineActiveSessionAsync_IdleSessionIsClearedStoppedAndDisposed()
    {
        var profile = Profile(1);
        var session = new FakeSession(profile.Id);
        var factory = new FakeSessionFactory();
        factory.QueueSession(profile.Id, session);
        await using var coordinator = Coordinator(factory, profile);
        await coordinator.SwitchAsync(profile.Id);

        var result = await coordinator.QuarantineActiveSessionAsync(
            profile.Id,
            "profile_identity_mismatch");

        Assert.Equal(ProfileSwitchPhase.Inactive, result.Phase);
        Assert.Null(result.ActiveProfileId);
        Assert.Null(result.PendingProfileId);
        Assert.Equal("profile_identity_mismatch", result.SafeErrorCode);
        Assert.False(coordinator.OwnsProfile(profile.Id));
        Assert.False(coordinator.HasRunningTask);
        Assert.Equal(1, session.StopCount);
        Assert.Equal(1, session.DisposeCount);
    }

    [Fact]
    public async Task QuarantineActiveSessionAsync_RunningSessionIsPreservedAndSameProfileIsReverifiedWhenIdle()
    {
        var profile = Profile(1);
        var runningSession = new FakeSession(profile.Id) { IsTaskRunning = true };
        var replacementSession = new FakeSession(profile.Id);
        var factory = new FakeSessionFactory();
        factory.QueueSession(profile.Id, runningSession);
        factory.QueueSession(profile.Id, replacementSession);
        await using var coordinator = Coordinator(factory, profile);
        await coordinator.SwitchAsync(profile.Id);

        var quarantined = await coordinator.QuarantineActiveSessionAsync(
            profile.Id,
            "profile_identity_mismatch");

        Assert.Equal(ProfileSwitchPhase.QueuedAfterCurrentTask, quarantined.Phase);
        Assert.Null(quarantined.ActiveProfileId);
        Assert.Equal(profile.Id, quarantined.PendingProfileId);
        Assert.Equal("profile_identity_mismatch", quarantined.SafeErrorCode);
        Assert.True(coordinator.OwnsProfile(profile.Id));
        Assert.True(coordinator.HasRunningTask);
        Assert.Equal(0, runningSession.StopCount);
        Assert.Equal(0, runningSession.DisposeCount);
        Assert.Equal(1, runningSession.VerifyCount);

        runningSession.IsTaskRunning = false;
        var recovered = await coordinator.ApplyPendingIfIdleAsync();

        Assert.Equal(ProfileSwitchPhase.Active, recovered.Phase);
        Assert.Equal(profile.Id, recovered.ActiveProfileId);
        Assert.Null(recovered.PendingProfileId);
        Assert.Equal(1, runningSession.StopCount);
        Assert.Equal(1, runningSession.DisposeCount);
        Assert.Equal(1, replacementSession.VerifyCount);
        Assert.Equal(new[] { profile.Id, profile.Id }, factory.StartedProfileIds);
    }

    [Fact]
    public async Task RecoverTerminatedSessionAsync_ObservesCallerCancellationDuringRestart()
    {
        var profile = Profile(1);
        var terminatedSession = new FakeSession(profile.Id);
        var factory = new FakeSessionFactory();
        factory.QueueSession(profile.Id, terminatedSession);
        var restartEntered = factory.QueueCancellationWait(profile.Id);
        await using var coordinator = Coordinator(factory, profile);
        await coordinator.SwitchAsync(profile.Id);
        terminatedSession.IsHealthy = false;
        using var cancellation = new CancellationTokenSource();

        var recovery = coordinator.RecoverTerminatedSessionAsync(profile.Id, cancellation.Token);
        await restartEntered.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recovery);
        Assert.Equal(ProfileSwitchPhase.RollbackFailed, coordinator.Status.Phase);
        Assert.Equal("managed_session_recovery_cancelled", coordinator.Status.SafeErrorCode);
        Assert.Null(coordinator.Status.ActiveProfileId);
        Assert.False(coordinator.OwnsProfile(profile.Id));
        Assert.Equal(1, terminatedSession.StopCount);
        Assert.Equal(1, terminatedSession.DisposeCount);
    }

    private static ProfileSwitchCoordinator Coordinator(
        FakeSessionFactory factory,
        params AccountProfile[] profiles)
    {
        var coordinator = new ProfileSwitchCoordinator(factory);
        coordinator.RegisterProfiles(profiles);
        return coordinator;
    }

    private static List<ProfileSwitchStatus> CaptureStatuses(ProfileSwitchCoordinator coordinator)
    {
        var statuses = new List<ProfileSwitchStatus>();
        coordinator.StatusChanged += (_, status) => statuses.Add(status);
        return statuses;
    }

    private static AccountProfile Profile(int slot) => new()
    {
        Id = Guid.NewGuid(),
        Slot = slot,
        Alias = $"Account {slot}",
        IdentityFingerprint = $"fingerprint-{slot}"
    };

    private sealed class FakeSessionFactory : IManagedProfileSessionFactory
    {
        private readonly Queue<StartPlan> _plans = new();

        public List<Guid> StartedProfileIds { get; } = new();

        public void QueueSession(Guid expectedProfileId, FakeSession session) =>
            _plans.Enqueue(new StartPlan(expectedProfileId, session, null, null));

        public void QueueFailure(Guid expectedProfileId, Exception exception) =>
            _plans.Enqueue(new StartPlan(expectedProfileId, null, exception, null));

        public Task QueueCancellationWait(Guid expectedProfileId)
        {
            var entered = new TaskCompletionSource<object?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _plans.Enqueue(new StartPlan(expectedProfileId, null, null, entered));
            return entered.Task;
        }

        public async Task<IManagedProfileSession> StartAsync(
            AccountProfile profile,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartedProfileIds.Add(profile.Id);
            if (_plans.Count == 0)
            {
                throw new InvalidOperationException("The test did not configure this session start.");
            }

            var plan = _plans.Dequeue();
            if (plan.ExpectedProfileId != profile.Id)
            {
                throw new InvalidOperationException(
                    $"Expected profile {plan.ExpectedProfileId}, but received {profile.Id}.");
            }

            if (plan.Exception is not null)
            {
                throw plan.Exception;
            }

            if (plan.CancellationWaitEntered is not null)
            {
                plan.CancellationWaitEntered.TrySetResult(null);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return plan.Session!;
        }

        private sealed record StartPlan(
            Guid ExpectedProfileId,
            FakeSession? Session,
            Exception? Exception,
            TaskCompletionSource<object?>? CancellationWaitEntered);
    }

    private sealed class FakeSession : IManagedProfileSession
    {
        public FakeSession(Guid profileId)
        {
            ProfileId = profileId;
        }

        public Guid ProfileId { get; }
        public bool IsTaskRunning { get; set; }
        public bool IsHealthy { get; set; } = true;
        public bool IdentityMatches { get; set; } = true;
        public int VerifyCount { get; private set; }
        public int StopCount { get; private set; }
        public int DisposeCount { get; private set; }

        public Task<bool> VerifyIdentityAsync(
            AccountProfile expectedProfile,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VerifyCount++;
            return Task.FromResult(IdentityMatches && expectedProfile.Id == ProfileId);
        }

        public Task StopGracefullyAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
