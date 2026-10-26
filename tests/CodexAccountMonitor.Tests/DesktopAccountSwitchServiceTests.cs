using System.Text;
using System.Text.Json;
using CodexAccountMonitor.Core.Models;
using CodexAccountMonitor.Core.Services;
using CodexAccountMonitor.Infrastructure;
using CodexAccountMonitor.Infrastructure.AppServer;
using CodexAccountMonitor.Infrastructure.DesktopSwitching;
using CodexAccountMonitor.Infrastructure.Membership;

namespace CodexAccountMonitor.Tests;

public sealed class DesktopAccountSwitchServiceTests
{
    [Fact]
    public async Task AutomaticSwitchClosesBeforeRemoteValidationAndReportsCompletionAfterLaunch()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        fixture.Desktop.Closed = false;
        var progress = new RecordingProgress();
        fixture.Desktop.CloseHandler = _ =>
        {
            var checkpoint = fixture.SwitchEvents()[^1];
            Assert.Equal("desktop_switch_stage", checkpoint.GetProperty("event").GetString());
            Assert.Equal("WaitingForExit", checkpoint.GetProperty("stage").GetString());
            Assert.True(checkpoint.GetProperty("closeRequested").GetBoolean());
            Assert.False(checkpoint.GetProperty("desktopExitVerified").GetBoolean());
            Assert.False(checkpoint.GetProperty("installAttempted").GetBoolean());
            Assert.Equal(0, fixture.ValidationStarts);
            Assert.Equal(fixture.Original, File.ReadAllBytes(fixture.DesktopAuth));
            Assert.Null(fixture.Store.GetReceipt());
            fixture.Desktop.Closed = true;
            return Task.CompletedTask;
        };
        fixture.Desktop.OnLaunch = () =>
        {
            Assert.True(fixture.Client.Disposed);
            Assert.Equal(File.ReadAllBytes(fixture.ProfileAuth), File.ReadAllBytes(fixture.DesktopAuth));
            Assert.DoesNotContain(progress.Items, p => p.Stage == DesktopSwitchStage.Completed);
        };

        await fixture.Service.SwitchAndReopenAsync(fixture.Profile.Id, progress: progress);

        Assert.Equal(1, fixture.Desktop.CloseRequests);
        Assert.Equal(1, fixture.Desktop.Launches);
        Assert.Equal(1, fixture.ValidationStarts);
        Assert.Equal(DesktopSwitchStage.Completed, progress.Items[^1].Stage);
        Assert.Equal(new[] { DesktopSwitchStage.Checking, DesktopSwitchStage.ClosingCodex,
            DesktopSwitchStage.WaitingForExit, DesktopSwitchStage.Checking, DesktopSwitchStage.SwitchingSignIn,
            DesktopSwitchStage.ReopeningCodex, DesktopSwitchStage.Completed }, progress.Items.Select(p => p.Stage));
        var events = fixture.SwitchEvents();
        Assert.Equal("desktop_switch_started", events[0].GetProperty("event").GetString());
        Assert.Equal("desktop_switch_completed", events[^1].GetProperty("event").GetString());
        Assert.True(events[^1].GetProperty("desktopExitVerified").GetBoolean());
        Assert.True(events[^1].GetProperty("installAttempted").GetBoolean());
        Assert.Single(events.Select(item => item.GetProperty("attemptId").GetGuid()).Distinct());
        Assert.DoesNotContain(events, item => item.GetProperty("event").GetString() == "desktop_switch_failed");
    }

    [Theory]
    [InlineData("membership")]
    [InlineData("missing_auth")]
    [InlineData("wrong_auth")]
    [InlineData("unsupported_config")]
    public async Task AutomaticPreflightFailuresLeaveRunningDesktopUntouched(string failure)
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        fixture.Desktop.Closed = false;
        switch (failure)
        {
            case "membership": fixture.Monitor.MembershipAccessGuard = (_, _) => throw new MembershipRequiredException("expired"); break;
            case "missing_auth": File.Delete(fixture.ProfileAuth); break;
            case "wrong_auth": File.WriteAllBytes(fixture.ProfileAuth, Auth("wrong")); break;
            case "unsupported_config": File.WriteAllText(Path.Combine(fixture.DesktopHome, "config.toml"), "model_provider = \"other\""); break;
        }

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => fixture.Service.SwitchAndReopenAsync(fixture.Profile.Id));

        Assert.Equal(0, fixture.Desktop.CloseRequests);
        Assert.Equal(0, fixture.ValidationStarts);
        Assert.Equal(0, fixture.Desktop.Launches);
        Assert.Equal(fixture.Original, File.ReadAllBytes(fixture.DesktopAuth));
        Assert.Null(fixture.Store.GetReceipt());
        Assert.False(fixture.Service.IsBusy);
        var loggedFailure = fixture.SwitchEvents()[^1];
        Assert.Equal("desktop_switch_failed", loggedFailure.GetProperty("event").GetString());
        Assert.False(loggedFailure.GetProperty("closeRequested").GetBoolean());
        Assert.False(loggedFailure.GetProperty("installAttempted").GetBoolean());
        if (failure == "membership")
            Assert.Equal("Preflight", loggedFailure.GetProperty("failureStage").GetString());
    }

    [Fact]
    public async Task AutomaticCloseTimeoutDoesNotValidateOrWriteCredentials()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        fixture.Desktop.Closed = false;
        const string sensitiveFailure = "Codex did not exit: target@example.test private-refresh-value";
        fixture.Desktop.CloseHandler = _ => throw new TimeoutException(sensitiveFailure);

        await Assert.ThrowsAsync<TimeoutException>(() => fixture.Service.SwitchAndReopenAsync(fixture.Profile.Id));

        Assert.Equal(0, fixture.ValidationStarts);
        Assert.Equal(0, fixture.Desktop.Launches);
        Assert.Equal(fixture.Original, File.ReadAllBytes(fixture.DesktopAuth));
        Assert.Null(fixture.Store.GetReceipt());
        var events = fixture.SwitchEvents();
        var loggedFailure = events[^1];
        Assert.Equal("desktop_switch_failed", loggedFailure.GetProperty("event").GetString());
        Assert.Equal("WaitingForExit", loggedFailure.GetProperty("failureStage").GetString());
        Assert.Equal("timeout", loggedFailure.GetProperty("reason").GetString());
        Assert.Equal("TimeoutException", loggedFailure.GetProperty("exceptionType").GetString());
        Assert.False(loggedFailure.GetProperty("desktopExitVerified").GetBoolean());
        Assert.False(loggedFailure.GetProperty("installAttempted").GetBoolean());
        var diagnosticText = string.Join("\n", events.Select(item => item.GetRawText()));
        Assert.DoesNotContain("target@example.test", diagnosticText);
        Assert.DoesNotContain("private-refresh-value", diagnosticText);
        Assert.DoesNotContain(fixture.DesktopHome, diagnosticText);
        Assert.DoesNotContain(fixture.Profile.Id.ToString(), diagnosticText);
    }

    [Theory]
    [InlineData("desktop_closed_before_quit", "desktop_closed_before_quit")]
    [InlineData("desktop_identity_changed", "desktop_identity_changed")]
    [InlineData("desktop_signature_unverified", "desktop_signature_unverified")]
    [InlineData("codex_process_metadata_unavailable", "codex_process_metadata_unavailable")]
    [InlineData("another_codex_process_started", "another_codex_process_started")]
    [InlineData("unknown-private-code target@example.test private-refresh-value", "operation_rejected")]
    public async Task QuitValidationDiagnosticsAllowOnlyKnownReasonCodes(string reasonCode, string expectedReason)
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        fixture.Desktop.Closed = false;
        fixture.Desktop.CloseHandler = _ => throw new DesktopQuitValidationException(reasonCode,
            "Rejected: target@example.test private-refresh-value");

        await Assert.ThrowsAsync<DesktopQuitValidationException>(() => fixture.Service.SwitchAndReopenAsync(fixture.Profile.Id));

        var events = fixture.SwitchEvents();
        var failures = events.Where(item => item.GetProperty("event").GetString() is
            "desktop_switch_failure_detected" or "desktop_switch_failed").ToArray();
        Assert.Equal(2, failures.Length);
        Assert.All(failures, item =>
        {
            Assert.Equal(expectedReason, item.GetProperty("reason").GetString());
            Assert.Equal("WaitingForExit", item.GetProperty("failureStage").GetString());
            Assert.Equal("DesktopQuitValidationException", item.GetProperty("exceptionType").GetString());
            Assert.False(item.GetProperty("desktopExitVerified").GetBoolean());
            Assert.False(item.GetProperty("installAttempted").GetBoolean());
        });
        var diagnosticText = string.Join("\n", events.Select(item => item.GetRawText()));
        Assert.DoesNotContain("target@example.test", diagnosticText);
        Assert.DoesNotContain("private-refresh-value", diagnosticText);
        Assert.DoesNotContain("unknown-private-code", diagnosticText);
        Assert.Equal(0, fixture.ValidationStarts);
        Assert.Equal(0, fixture.Desktop.Launches);
        Assert.Equal(fixture.Original, File.ReadAllBytes(fixture.DesktopAuth));
    }

    [Fact]
    public async Task CancelledBeforeSwitchLogsAttemptWithoutQuitOrCredentialWrite()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        fixture.Desktop.Closed = false;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.SwitchAndReopenAsync(fixture.Profile.Id, cancellation.Token));

        var loggedFailure = fixture.SwitchEvents()[^1];
        Assert.Equal("cancelled", loggedFailure.GetProperty("reason").GetString());
        Assert.Equal("WaitingForSwitchLock", loggedFailure.GetProperty("failureStage").GetString());
        Assert.False(loggedFailure.GetProperty("closeRequested").GetBoolean());
        Assert.False(loggedFailure.GetProperty("desktopExitVerified").GetBoolean());
        Assert.False(loggedFailure.GetProperty("installAttempted").GetBoolean());
        Assert.Equal(0, fixture.Desktop.CloseRequests);
        Assert.Equal(0, fixture.ValidationStarts);
        Assert.Equal(fixture.Original, File.ReadAllBytes(fixture.DesktopAuth));
        Assert.False(fixture.Service.IsBusy);
        // A cancelled wait did not release a semaphore it never acquired.
        fixture.Desktop.Closed = true;
        await fixture.Service.SwitchAsync(fixture.Profile.Id);
        Assert.Equal("desktop_switch_completed", fixture.SwitchEvents()[^1].GetProperty("event").GetString());
    }

    [Fact]
    public async Task CancellationWhileWaitingForExitLeavesCredentialsUntouched()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        fixture.Desktop.Closed = false;
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Desktop.CloseHandler = token => { entered.SetResult(); return Task.Delay(Timeout.Infinite, token); };

        var pending = fixture.Service.SwitchAndReopenAsync(fixture.Profile.Id, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        Assert.Equal(0, fixture.ValidationStarts);
        Assert.Equal(0, fixture.Desktop.Launches);
        Assert.Equal(fixture.Original, File.ReadAllBytes(fixture.DesktopAuth));
        Assert.Null(fixture.Store.GetReceipt());
        var loggedFailure = fixture.SwitchEvents()[^1];
        Assert.Equal("cancelled", loggedFailure.GetProperty("reason").GetString());
        Assert.Equal("WaitingForExit", loggedFailure.GetProperty("failureStage").GetString());
        Assert.False(loggedFailure.GetProperty("desktopExitVerified").GetBoolean());
    }

    [Fact]
    public async Task CancelledExitAcknowledgementReopensUnchangedSignInWhenDesktopActuallyExited()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        fixture.Desktop.Closed = false;
        using var cancellation = new CancellationTokenSource();
        fixture.Desktop.CloseHandler = token =>
        {
            fixture.Desktop.Closed = true;
            cancellation.Cancel();
            return Task.FromCanceled(token);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.SwitchAndReopenAsync(fixture.Profile.Id, cancellation.Token));

        Assert.Equal(1, fixture.Desktop.Launches);
        Assert.Equal(0, fixture.ValidationStarts);
        Assert.Equal(fixture.Original, File.ReadAllBytes(fixture.DesktopAuth));
        Assert.Null(fixture.Store.GetReceipt());
    }

    [Fact]
    public async Task OpenOnlyNeverQuitsOrChangesCredentialsAndIsBusyUntilWindowIsConfirmed()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        fixture.Desktop.Closed = false;
        fixture.Desktop.OnLaunch = () => Assert.True(fixture.Service.IsBusy);
        await fixture.Service.OpenCodexAsync();
        Assert.Equal(1, fixture.Desktop.Launches);
        Assert.Equal(0, fixture.Desktop.CloseRequests);
        Assert.Equal(0, fixture.ValidationStarts);
        Assert.Equal(fixture.Original, File.ReadAllBytes(fixture.DesktopAuth));
        Assert.False(fixture.Service.IsBusy);
    }

    [Fact]
    public async Task OpenOnlyRefusesPendingCredentialRecovery()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        fixture.Store.Install(DesktopAuthFileStore.ParseSnapshot(Auth("interrupted")), Guid.NewGuid(), () => true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.OpenCodexAsync());
        Assert.Equal(0, fixture.Desktop.Launches);
        Assert.True(fixture.Store.GetReceipt()!.IsPending);
        Assert.False(fixture.Service.IsBusy);
    }

    [Fact]
    public async Task RejectedTargetAfterAutomaticCloseReopensUnchangedPreviousSignIn()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        fixture.Desktop.Closed = false;
        fixture.Client.Email = "wrong@example.test";
        var progress = new RecordingProgress();
        fixture.Desktop.OnLaunch = () => Assert.Equal(fixture.Original, File.ReadAllBytes(fixture.DesktopAuth));

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SwitchAndReopenAsync(fixture.Profile.Id, progress: progress));

        Assert.Equal(1, fixture.Desktop.CloseRequests);
        Assert.Equal(1, fixture.Desktop.Launches);
        Assert.Null(fixture.Store.GetReceipt());
        Assert.DoesNotContain(progress.Items, p => p.Stage == DesktopSwitchStage.Completed);
        Assert.Contains(progress.Items, p => p.Message.Contains("previous sign-in"));
    }

    [Fact]
    public async Task CancellationAfterAutomaticCloseReopensPreviousSignInWithoutCommitting()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        fixture.Desktop.Closed = false;
        using var cancellation = new CancellationTokenSource();
        fixture.Client.OnQuotaRead = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.SwitchAndReopenAsync(fixture.Profile.Id, cancellation.Token));

        Assert.Equal(1, fixture.Desktop.Launches);
        Assert.Equal(fixture.Original, File.ReadAllBytes(fixture.DesktopAuth));
        Assert.Null(fixture.Store.GetReceipt());
        var events = fixture.SwitchEvents();
        var loggedFailure = events[^1];
        Assert.Equal("cancelled", loggedFailure.GetProperty("reason").GetString());
        Assert.Equal("ValidatingSelectedSignIn", loggedFailure.GetProperty("failureStage").GetString());
        Assert.Equal("PreviousSignInReopened", loggedFailure.GetProperty("stage").GetString());
        Assert.True(loggedFailure.GetProperty("desktopExitVerified").GetBoolean());
        Assert.False(loggedFailure.GetProperty("installAttempted").GetBoolean());
        Assert.DoesNotContain(events, item => item.GetProperty("event").GetString() == "desktop_switch_completed");
    }

    [Fact]
    public async Task NewCodexProcessAfterCloseAcknowledgementPreventsWriteAndRecoveryLaunch()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        fixture.Desktop.Closed = false;
        fixture.Client.OnQuotaRead = () => fixture.Desktop.Closed = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SwitchAndReopenAsync(fixture.Profile.Id));

        Assert.Equal(0, fixture.Desktop.Launches);
        Assert.Equal(fixture.Original, File.ReadAllBytes(fixture.DesktopAuth));
        Assert.Null(fixture.Store.GetReceipt());
    }

    [Fact]
    public async Task CloseAcknowledgementWithoutActualExitCannotStartValidation()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        fixture.Desktop.Closed = false;
        fixture.Desktop.CloseHandler = _ => Task.CompletedTask;

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SwitchAndReopenAsync(fixture.Profile.Id));

        Assert.Equal(0, fixture.ValidationStarts);
        Assert.Equal(0, fixture.Desktop.Launches);
        Assert.Equal(fixture.Original, File.ReadAllBytes(fixture.DesktopAuth));
        Assert.Null(fixture.Store.GetReceipt());
    }

    [Fact]
    public async Task IndependentSignInChangeDuringPostExitValidationIsPreservedWithoutReopen()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        fixture.Desktop.Closed = false;
        var independent = Auth("independent");
        fixture.Client.OnQuotaRead = () => File.WriteAllBytes(fixture.DesktopAuth, independent);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SwitchAndReopenAsync(fixture.Profile.Id));

        Assert.Contains("sign-in changed", error.Message);
        Assert.Equal(0, fixture.Desktop.Launches);
        Assert.Equal(independent, File.ReadAllBytes(fixture.DesktopAuth));
        Assert.Null(fixture.Store.GetReceipt());
    }

    [Fact]
    public async Task IndependentSignInChangeImmediatelyBeforeInstallIsPreserved()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        fixture.Desktop.Closed = false;
        var independent = Auth("independent");
        var progress = new RecordingProgress
        {
            OnReport = value =>
            {
                if (value.Stage == DesktopSwitchStage.SwitchingSignIn)
                    File.WriteAllBytes(fixture.DesktopAuth, independent);
            }
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SwitchAndReopenAsync(fixture.Profile.Id, progress: progress));

        Assert.Equal(0, fixture.Desktop.Launches);
        Assert.Equal(independent, File.ReadAllBytes(fixture.DesktopAuth));
        Assert.Null(fixture.Store.GetReceipt());
    }

    [Fact]
    public async Task AutomaticSwitchSavesTokenRotationFlushedDuringDesktopExit()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        var previous = await fixture.AddSelectedPreviousProfileAsync();
        fixture.Desktop.Closed = false;
        var finalRotation = Auth("previous", "rotated-during-exit");
        fixture.Desktop.CloseHandler = _ =>
        {
            File.WriteAllBytes(fixture.DesktopAuth, finalRotation);
            fixture.Desktop.Closed = true;
            return Task.CompletedTask;
        };

        await fixture.Service.SwitchAndReopenAsync(fixture.Profile.Id);

        Assert.Equal(finalRotation, File.ReadAllBytes(previous.SourceAuthPath));
        Assert.Equal(fixture.Profile.Id, fixture.Service.SelectedProfileId);
    }

    [Fact]
    public async Task AutomaticLaunchFailureKeepsCommittedSelectionAndDoesNotReportFalseRecovery()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        fixture.Desktop.Closed = false;
        fixture.Desktop.OnLaunch = () => throw new InvalidOperationException("fixture_launch_failed");

        await Assert.ThrowsAsync<DesktopReopenException>(() => fixture.Service.SwitchAndReopenAsync(fixture.Profile.Id));

        Assert.Equal(1, fixture.Desktop.Launches);
        Assert.Equal(File.ReadAllBytes(fixture.ProfileAuth), File.ReadAllBytes(fixture.DesktopAuth));
        Assert.Equal(fixture.Profile.Id, fixture.Store.GetReceipt()!.SelectedProfileId);
        Assert.False(fixture.Store.GetReceipt()!.IsPending);
        var events = fixture.SwitchEvents();
        var loggedFailure = events[^1];
        Assert.Equal("ReopeningCodex", loggedFailure.GetProperty("failureStage").GetString());
        Assert.Equal("ReopeningCodex", loggedFailure.GetProperty("stage").GetString());
        Assert.True(loggedFailure.GetProperty("installAttempted").GetBoolean());
        Assert.DoesNotContain(events, item => item.GetProperty("stage").GetString() == "RollingBack");
        Assert.DoesNotContain(events, item => item.GetProperty("event").GetString() == "desktop_switch_completed");
    }

    [Fact]
    public async Task ExternalCredentialChangeAfterClosePreventsAutomaticRecoveryReopen()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        fixture.Desktop.Closed = false;
        var external = Auth("external");
        fixture.Desktop.OnLaunch = () =>
        {
            File.WriteAllBytes(fixture.DesktopAuth, external);
            throw new InvalidOperationException("fixture_launch_failed");
        };

        await Assert.ThrowsAsync<DesktopReopenException>(() => fixture.Service.SwitchAndReopenAsync(fixture.Profile.Id));

        Assert.Equal(1, fixture.Desktop.Launches);
        Assert.Equal(external, File.ReadAllBytes(fixture.DesktopAuth));
    }

    [Fact]
    public async Task AutomaticSwitchWaitsForProfileOperationLeaseBeforeClosingDesktop()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        fixture.Desktop.Closed = false;
        using var lease = await fixture.Operations.AcquireAsync(fixture.Profile.Id);
        var progress = new RecordingProgress();
        var pending = fixture.Service.SwitchAndReopenAsync(fixture.Profile.Id, progress: progress);
        await progress.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, fixture.Desktop.CloseRequests);
        lease.Dispose();
        await pending;
        Assert.Equal(1, fixture.Desktop.CloseRequests);
    }

    [Fact]
    public async Task ExpiredMembershipPreventsDesktopSwitchAndQuotaRead()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        fixture.Monitor.MembershipAccessGuard = (_, _) => throw new MembershipRequiredException("expired");
        await Assert.ThrowsAsync<MembershipRequiredException>(() => fixture.Service.SwitchAsync(fixture.Profile.Id));
        Assert.Equal(fixture.Original, File.ReadAllBytes(fixture.DesktopAuth));
        Assert.Equal(0, fixture.ValidationStarts);
        Assert.Equal(0, fixture.Desktop.Launches);
    }

    [Fact]
    public async Task RunningDesktopPreventsValidationAndCredentialChanges()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        fixture.Desktop.Closed = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SwitchAsync(fixture.Profile.Id));
        Assert.Equal(fixture.Original, File.ReadAllBytes(fixture.DesktopAuth));
        Assert.Equal(0, fixture.ValidationStarts);
        Assert.Equal(0, fixture.Desktop.Launches);
        Assert.Null(fixture.Store.GetReceipt());
        Assert.False(fixture.Service.IsBusy);
    }

    [Fact]
    public async Task IdentityMismatchPreservesDesktopAndDoesNotReadQuota()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        fixture.Client.Email = "wrong@example.test";
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SwitchAsync(fixture.Profile.Id));
        Assert.Equal(fixture.Original, File.ReadAllBytes(fixture.DesktopAuth));
        Assert.True(fixture.Client.Disposed);
        Assert.Equal(0, fixture.Client.QuotaReads);
        Assert.Equal(0, fixture.Desktop.Launches);
        Assert.Null(fixture.Store.GetReceipt());
    }

    [Fact]
    public async Task DesktopOpenedDuringValidationPreventsAuthWrite()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        fixture.Client.OnQuotaRead = () => fixture.Desktop.Closed = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SwitchAsync(fixture.Profile.Id));
        Assert.Equal(fixture.Original, File.ReadAllBytes(fixture.DesktopAuth));
        Assert.True(fixture.Client.Disposed);
        Assert.Equal(0, fixture.Desktop.Launches);
        Assert.Null(fixture.Store.GetReceipt());
    }

    [Fact]
    public async Task ValidationTokenRotationCopiesNewestVerifiedSourceThenLaunches()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        var rotated = Auth("target", "rotated");
        fixture.Client.OnQuotaRead = () => File.WriteAllBytes(fixture.ProfileAuth, rotated);
        fixture.Desktop.OnLaunch = () =>
        {
            Assert.True(fixture.Client.Disposed);
            Assert.Equal(rotated, File.ReadAllBytes(fixture.DesktopAuth));
            Assert.False(fixture.Store.GetReceipt()!.IsPending);
        };
        await fixture.Service.SwitchAsync(fixture.Profile.Id);
        Assert.Equal(1, fixture.Client.QuotaReads);
        Assert.True(fixture.Client.RefreshRequested);
        Assert.Equal(1, fixture.Desktop.Launches);
        Assert.Equal(fixture.Profile.Id, fixture.Store.GetReceipt()!.SelectedProfileId);
        Assert.False(fixture.Service.IsBusy);
    }

    [Fact]
    public async Task ChangedIdentityDuringValidationDoesNotReachDesktop()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        fixture.Client.OnQuotaRead = () => File.WriteAllBytes(fixture.ProfileAuth, Auth("other-account"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SwitchAsync(fixture.Profile.Id));
        Assert.Equal(fixture.Original, File.ReadAllBytes(fixture.DesktopAuth));
        Assert.Equal(0, fixture.Desktop.Launches);
    }

    [Fact]
    public async Task CancellationAfterValidationLeavesDesktopUntouched()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        using var cancellation = new CancellationTokenSource();
        fixture.Client.OnQuotaRead = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.SwitchAsync(fixture.Profile.Id, cancellation.Token));
        Assert.Equal(fixture.Original, File.ReadAllBytes(fixture.DesktopAuth));
        Assert.Null(fixture.Store.GetReceipt());
        Assert.Equal(0, fixture.Desktop.Launches);
    }

    [Fact]
    public async Task FailureBeforeNewJournalDoesNotRollbackPreviousSuccessfulSwitch()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        var previous = fixture.SeedReceipt("previous");
        var previousBytes = File.ReadAllBytes(fixture.DesktopAuth);
        // The first two calls guard initial entry and post-validation. The third is
        // Install's first guard, before it creates this attempt's recovery journal.
        fixture.Desktop.IsClosedAtInspection = inspection => inspection != 3;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SwitchAsync(fixture.Profile.Id));
        Assert.Equal(previousBytes, File.ReadAllBytes(fixture.DesktopAuth));
        Assert.Equal(previous.CreatedUtc, fixture.Store.GetReceipt()!.CreatedUtc);
        Assert.Equal(previous.SelectedProfileId, fixture.Store.GetReceipt()!.SelectedProfileId);
        Assert.False(fixture.Store.GetReceipt()!.IsPending);
        Assert.Equal(0, fixture.Desktop.Launches);
    }

    [Fact]
    public async Task FailureAfterJournalWriteRecoversOnlyPendingAttempt()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        var previous = fixture.SeedReceipt("previous");
        var previousBytes = File.ReadAllBytes(fixture.DesktopAuth);
        // Install's second guard runs after its durable journal exists, before auth replacement.
        fixture.Desktop.IsClosedAtInspection = inspection => inspection != 4;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SwitchAsync(fixture.Profile.Id));
        Assert.Equal(previousBytes, File.ReadAllBytes(fixture.DesktopAuth));
        Assert.Equal(previous.CreatedUtc, fixture.Store.GetReceipt()!.CreatedUtc);
        Assert.False(fixture.Store.GetReceipt()!.IsPending);
        Assert.Equal(0, fixture.Desktop.Launches);
    }

    [Fact]
    public async Task LaunchFailureLeavesSelectedLoginReadyForOpenOnlyRetry()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        fixture.Desktop.OnLaunch = () => throw new InvalidOperationException("fixture_launch_failed");
        var exception = await Assert.ThrowsAsync<DesktopReopenException>(() => fixture.Service.SwitchAsync(fixture.Profile.Id));
        Assert.Contains("Open Codex", exception.Message);
        Assert.Equal(File.ReadAllBytes(fixture.ProfileAuth), File.ReadAllBytes(fixture.DesktopAuth));
        Assert.False(fixture.Store.GetReceipt()!.IsPending);
        Assert.Equal(1, fixture.Desktop.Launches);
        fixture.Desktop.OnLaunch = null;
        await fixture.Service.OpenCodexAsync();
        Assert.Equal(2, fixture.Desktop.Launches);
        Assert.Equal(1, fixture.ValidationStarts);
        Assert.Equal(fixture.Profile.Id, fixture.Service.SelectedProfileId);
    }

    [Fact]
    public async Task LaunchFailureCannotRollbackAnotherFinalizedReceipt()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        var external = Auth("external");
        var externalProfile = Guid.NewGuid();
        fixture.Desktop.OnLaunch = () =>
        {
            fixture.Store.Install(DesktopAuthFileStore.ParseSnapshot(external), externalProfile, () => true);
            fixture.Store.FinalizeSwitch(() => true);
            throw new InvalidOperationException("fixture_launch_failed");
        };
        var exception = await Assert.ThrowsAsync<DesktopReopenException>(() => fixture.Service.SwitchAsync(fixture.Profile.Id));
        Assert.DoesNotContain("needs recovery", exception.Message);
        Assert.Equal(external, File.ReadAllBytes(fixture.DesktopAuth));
        Assert.Equal(externalProfile, fixture.Store.GetReceipt()!.SelectedProfileId);
    }

    [Fact]
    public async Task PendingInterruptedSwitchRecoversBeforeValidatingNewSelection()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        fixture.Store.Install(DesktopAuthFileStore.ParseSnapshot(Auth("interrupted")), Guid.NewGuid(), () => true);
        fixture.Client.Email = "wrong@example.test";
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SwitchAsync(fixture.Profile.Id));
        Assert.Equal(fixture.Original, File.ReadAllBytes(fixture.DesktopAuth));
        Assert.Null(fixture.Store.GetReceipt());
    }

    [Fact]
    public async Task NewlyReauthenticatedSourceIsNeverOverwrittenByOlderDesktopLogin()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        fixture.Store.Install(DesktopAuthFileStore.ParseSnapshot(File.ReadAllBytes(fixture.ProfileAuth)), fixture.Profile.Id, () => true);
        fixture.Store.FinalizeSwitch(() => true);
        var reauthenticated = Auth("target", "new-independent-login");
        File.WriteAllBytes(fixture.ProfileAuth, reauthenticated);
        await fixture.Service.SwitchAsync(fixture.Profile.Id);
        Assert.Equal(reauthenticated, File.ReadAllBytes(fixture.ProfileAuth));
        Assert.Equal(reauthenticated, File.ReadAllBytes(fixture.DesktopAuth));
        Assert.Equal(1, fixture.ValidationStarts);
        Assert.Equal(1, fixture.Desktop.Launches);
    }

    [Fact]
    public async Task SameExactSelectedLoginOpensWithoutRotatingRefreshToken()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        var selected = File.ReadAllBytes(fixture.ProfileAuth);
        fixture.Store.Install(DesktopAuthFileStore.ParseSnapshot(selected), fixture.Profile.Id, () => true);
        fixture.Store.FinalizeSwitch(() => true);
        await fixture.Service.SwitchAsync(fixture.Profile.Id);
        Assert.Equal(0, fixture.ValidationStarts);
        Assert.Equal(selected, File.ReadAllBytes(fixture.ProfileAuth));
        Assert.Equal(selected, File.ReadAllBytes(fixture.DesktopAuth));
        Assert.Equal(1, fixture.Desktop.Launches);
    }

    [Fact]
    public async Task ReopeningSelectedDesktopDoesNotRewriteItsSourceProvenance()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        var selected = File.ReadAllBytes(fixture.ProfileAuth);
        fixture.Store.Install(DesktopAuthFileStore.ParseSnapshot(selected), fixture.Profile.Id, () => true);
        fixture.Store.FinalizeSwitch(() => true);
        var desktopRotated = Auth("target", "desktop-rotated");
        File.WriteAllBytes(fixture.DesktopAuth, desktopRotated);
        await fixture.Service.SwitchAsync(fixture.Profile.Id);
        Assert.Equal(0, fixture.ValidationStarts);
        Assert.Equal(selected, File.ReadAllBytes(fixture.ProfileAuth));
        Assert.Equal(desktopRotated, File.ReadAllBytes(fixture.DesktopAuth));
        Assert.Equal(fixture.Profile.Id, fixture.Service.SelectedProfileId);
    }

    [Fact]
    public async Task RejectedNextAccountKeepsPreviousDesktopTokenRoutingAfterSynchronization()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        var previous = await fixture.AddSelectedPreviousProfileAsync();
        fixture.Client.Email = "wrong@example.test";
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SwitchAsync(fixture.Profile.Id));
        Assert.Equal(previous.RotatedAuth, File.ReadAllBytes(previous.SourceAuthPath));
        Assert.Equal(previous.RotatedAuth, File.ReadAllBytes(fixture.DesktopAuth));
        Assert.Equal(previous.Profile.Id, fixture.Service.SelectedProfileId);
        Assert.False(fixture.Store.GetReceipt()!.IsPending);
        Assert.Equal(0, fixture.Desktop.Launches);
    }

    [Fact]
    public async Task FailedNextLaunchPreservesPreviousSourceAndSelectsNewDesktopTokenRouting()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        var previous = await fixture.AddSelectedPreviousProfileAsync();
        fixture.Desktop.OnLaunch = () => throw new InvalidOperationException("fixture_launch_failed");
        await Assert.ThrowsAsync<DesktopReopenException>(() => fixture.Service.SwitchAsync(fixture.Profile.Id));
        Assert.Equal(previous.RotatedAuth, File.ReadAllBytes(previous.SourceAuthPath));
        Assert.Equal(File.ReadAllBytes(fixture.ProfileAuth), File.ReadAllBytes(fixture.DesktopAuth));
        Assert.Equal(fixture.Profile.Id, fixture.Service.SelectedProfileId);
        Assert.False(fixture.Store.GetReceipt()!.IsPending);
    }

    [Fact]
    public async Task SuccessfulNextSwitchSavesPreviousDesktopRotationAndSelectsTarget()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        var previous = await fixture.AddSelectedPreviousProfileAsync();
        await fixture.Service.SwitchAsync(fixture.Profile.Id);
        Assert.Equal(previous.RotatedAuth, File.ReadAllBytes(previous.SourceAuthPath));
        Assert.Equal(File.ReadAllBytes(fixture.ProfileAuth), File.ReadAllBytes(fixture.DesktopAuth));
        Assert.Equal(fixture.Profile.Id, fixture.Service.SelectedProfileId);
        Assert.Equal(1, fixture.Desktop.Launches);
    }

    private static byte[] Auth(string account, string generation = "initial")
    {
        var claims = new Dictionary<string, object>
        {
            ["email"] = account + "@example.test",
            ["sub"] = "auth0|" + account,
            ["exp"] = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
            ["https://api.openai.com/auth"] = new Dictionary<string, object>
            {
                ["chatgpt_account_id"] = account,
                ["chatgpt_user_id"] = "user-" + account
            }
        };
        var jwt = Encode(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\"}")) + "." +
                  Encode(JsonSerializer.SerializeToUtf8Bytes(claims)) + ".fixture";
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            auth_mode = "chatgpt",
            tokens = new { id_token = jwt, access_token = jwt, refresh_token = "fixture-" + account + "-" + generation, account_id = account }
        });
    }

    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "CodexDesktopSwitchServiceTests", Guid.NewGuid().ToString("N"));
        private readonly SafeJsonLogger _logger;
        private readonly AccountRepository _repository;
        private readonly AccountDirectoryService _directories;
        public AccountProfile Profile { get; } = new()
        {
            Id = Guid.NewGuid(), Slot = 1, Alias = "Target", IdentityFingerprint = IdentityUtilities.FingerprintEmail("target@example.test")
        };
        public AppDataPaths Paths { get; }
        public string DesktopHome => Path.Combine(_root, "desktop");
        public string DesktopAuth => Path.Combine(DesktopHome, "auth.json");
        public string ProfileAuth => Path.Combine(Paths.CodexHome(Profile.Id), "auth.json");
        public byte[] Original { get; } = Auth("original");
        public FakeDesktop Desktop { get; } = new();
        public FakeClient Client { get; } = new();
        public DesktopAuthFileStore Store { get; }
        public AccountMonitorService Monitor { get; }
        public DesktopAccountSwitchService Service { get; }
        public int ValidationStarts { get; private set; }
        public ProfileOperationCoordinator Operations { get; } = new();

        public Fixture()
        {
            Paths = new AppDataPaths(Path.Combine(_root, "local"));
            _logger = new SafeJsonLogger(Paths);
            _repository = new AccountRepository(Paths);
            var settings = new SettingsRepository(Paths);
            _directories = new AccountDirectoryService(Paths);
            var locator = new CodexExecutableLocator(_logger);
            var operations = Operations;
            Store = new DesktopAuthFileStore(DesktopHome, Path.Combine(Paths.Root, "recovery"));
            var router = new DesktopCredentialRouter(Store, Paths, _directories, locator, settings, _logger);
            Monitor = new AccountMonitorService(_repository, settings, _directories, Paths, locator, _logger, operations,
                clientFactory: (_, _) => throw new InvalidOperationException("Unexpected monitor client request in switch fixture."));
            Service = new DesktopAccountSwitchService(Monitor, Paths, DesktopHome, Store, router, Desktop, operations,
                locator, settings, _logger, (home, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Assert.Equal(Paths.CodexHome(Profile.Id), home);
                    ValidationStarts++;
                    return Task.FromResult<IAccountAppServerClient>(Client);
                });
        }

        public async Task InitializeAsync()
        {
            Directory.CreateDirectory(DesktopHome);
            File.WriteAllBytes(DesktopAuth, Original);
            _directories.CreateIsolatedCodexHome(Profile.Id);
            File.WriteAllBytes(ProfileAuth, Auth("target"));
            await _repository.SaveAsync([new PersistedAccount(Profile, new AccountUsageState())]);
            await Monitor.InitializeAsync();
        }

        public JsonElement[] SwitchEvents() => Directory.GetFiles(Paths.LogsRoot, "*.jsonl")
            .SelectMany(path =>
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            })
            .Select(line => { using var document = JsonDocument.Parse(line); return document.RootElement.Clone(); })
            .Where(item => item.GetProperty("event").GetString()?.StartsWith("desktop_switch_", StringComparison.Ordinal) == true)
            .ToArray();

        public DesktopSwitchReceipt SeedReceipt(string account)
        {
            var receipt = Store.Install(DesktopAuthFileStore.ParseSnapshot(Auth(account)), Guid.NewGuid(), () => true);
            Store.FinalizeSwitch(() => true);
            return receipt;
        }

        public async Task<(AccountProfile Profile, string SourceAuthPath, byte[] RotatedAuth)> AddSelectedPreviousProfileAsync()
        {
            var previous = new AccountProfile
            {
                Id = Guid.NewGuid(), Slot = 2, Alias = "Previous",
                IdentityFingerprint = IdentityUtilities.FingerprintEmail("previous@example.test")
            };
            var source = Path.Combine(_directories.CreateIsolatedCodexHome(previous.Id), "auth.json");
            var initial = Auth("previous", "before-desktop");
            File.WriteAllBytes(source, initial);
            await _repository.SaveAsync([
                new PersistedAccount(Profile, new AccountUsageState()),
                new PersistedAccount(previous, new AccountUsageState())]);
            await Monitor.InitializeAsync();
            Store.Install(DesktopAuthFileStore.ParseSnapshot(initial), previous.Id, () => true);
            Store.FinalizeSwitch(() => true);
            var rotated = Auth("previous", "after-desktop");
            File.WriteAllBytes(DesktopAuth, rotated);
            return (previous, source, rotated);
        }

        public void Dispose()
        {
            _logger.Dispose();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }

    private sealed class FakeDesktop : ICodexDesktopAdapter
    {
        public bool Closed { get; set; } = true;
        public Func<int, bool>? IsClosedAtInspection { get; set; }
        public Action? OnLaunch { get; set; }
        public int Launches { get; private set; }
        public int Inspections { get; private set; }
        public int CloseRequests { get; private set; }
        public Func<CancellationToken, Task>? CloseHandler { get; set; }
        public CodexDesktopState Inspect(IReadOnlyCollection<int>? monitorOwnedProcessIds = null)
        {
            Inspections++;
            var closed = IsClosedAtInspection?.Invoke(Inspections) ?? Closed;
            return new(new CodexDesktopInstallation(@"C:\fixtures\desktop\ChatGPT.exe", "Codex"),
                closed ? [] : [new DesktopProcessInfo(500, 0, "ChatGPT.exe", null)], null);
        }
        public void Launch(CodexDesktopInstallation installation)
        {
            Launches++;
            OnLaunch?.Invoke();
        }
        public async Task<CodexDesktopState> RequestCloseAndWaitAsync(CodexDesktopState expected, TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(TimeSpan.FromSeconds(60), timeout);
            Assert.NotNull(expected.Installation);
            CloseRequests++;
            if (CloseHandler is not null) await CloseHandler(cancellationToken);
            else Closed = true;
            return Inspect();
        }
    }

    private sealed class RecordingProgress : IProgress<DesktopSwitchProgress>
    {
        public List<DesktopSwitchProgress> Items { get; } = [];
        public Action<DesktopSwitchProgress>? OnReport { get; init; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Report(DesktopSwitchProgress value) { Items.Add(value); OnReport?.Invoke(value); Started.TrySetResult(); }
    }

    private sealed class FakeClient : IAccountAppServerClient
    {
        public string Email { get; set; } = "target@example.test";
        public Action? OnQuotaRead { get; set; }
        public bool Disposed { get; private set; }
        public bool RefreshRequested { get; private set; }
        public int QuotaReads { get; private set; }
        public Task<CodexAccountIdentity?> ReadAccountAsync(bool refreshToken, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RefreshRequested = refreshToken;
            return Task.FromResult<CodexAccountIdentity?>(new("chatgpt", Email, "test"));
        }
        public Task<RawRateLimitsResponse> ReadRateLimitsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            QuotaReads++;
            OnQuotaRead?.Invoke();
            return Task.FromResult(new RawRateLimitsResponse([], 0));
        }
        public Task<LoginPrompt> StartChatGptLoginAsync(bool deviceCode, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LoginCompletion> WaitForLoginCompletionAsync(TimeSpan timeout, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> ConsumeResetCreditAsync(string idempotencyKey, string? creditId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
