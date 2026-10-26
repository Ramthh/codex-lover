using CodexAccountMonitor.Core.Services;
using CodexAccountMonitor.Infrastructure.AppServer;
using System.Net.Http;
using System.Security.Cryptography;

namespace CodexAccountMonitor.Infrastructure.DesktopSwitching;

public sealed class DesktopAccountSwitchService
{
    private readonly AccountMonitorService _monitor;
    private readonly AppDataPaths _paths;
    private readonly string _desktopHome;
    private readonly DesktopAuthFileStore _store;
    private readonly DesktopCredentialRouter _router;
    private readonly ICodexDesktopAdapter _desktop;
    private readonly ProfileOperationCoordinator _operations;
    private readonly CodexExecutableLocator _locator;
    private readonly SettingsRepository _settings;
    private readonly SafeJsonLogger _logger;
    private readonly Func<string, CancellationToken, Task<IAccountAppServerClient>>? _validationClientFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _busy;

    public DesktopAccountSwitchService(AccountMonitorService monitor, AppDataPaths paths, string desktopHome,
        DesktopAuthFileStore store, DesktopCredentialRouter router, ICodexDesktopAdapter desktop,
        ProfileOperationCoordinator operations, CodexExecutableLocator locator,
        SettingsRepository settings, SafeJsonLogger logger,
        Func<string, CancellationToken, Task<IAccountAppServerClient>>? validationClientFactory = null)
    {
        _monitor = monitor; _paths = paths; _desktopHome = desktopHome; _store = store;
        _router = router; _desktop = desktop; _operations = operations;
        _locator = locator; _settings = settings; _logger = logger;
        _validationClientFactory = validationClientFactory;
    }

    public bool IsBusy => Volatile.Read(ref _busy) != 0;
    public Guid? SelectedProfileId => _router.FindSelectedProfile(_monitor.Accounts.Select(x => x.Profile));
    public Task<CodexDesktopState> InspectAsync() => Task.Run(() => _desktop.Inspect());

    public async Task OpenCodexAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Interlocked.Exchange(ref _busy, 1);
            await Task.Run(() =>
            {
                if (_store.GetReceipt()?.IsPending == true)
                    throw new InvalidOperationException("An interrupted sign-in change must be recovered before opening Codex. Retry the switch first.");
                var state = _desktop.Inspect();
                if (state.Problem is not null || state.Installation is null)
                    throw new InvalidOperationException(state.Problem ?? "The official Codex desktop installation was not found.");
                _desktop.Launch(state.Installation);
            }).ConfigureAwait(false);
        }
        finally { Interlocked.Exchange(ref _busy, 0); _gate.Release(); }
    }

    public Task SwitchAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        SwitchCoreAsync(profileId, automaticClose: false, cancellationToken, progress: null);

    public Task SwitchAndReopenAsync(Guid profileId, CancellationToken cancellationToken = default,
        IProgress<DesktopSwitchProgress>? progress = null) =>
        SwitchCoreAsync(profileId, automaticClose: true, cancellationToken, progress);

    private async Task SwitchCoreAsync(Guid profileId, bool automaticClose, CancellationToken cancellationToken,
        IProgress<DesktopSwitchProgress>? progress)
    {
        var attempt = new SwitchAttemptDiagnostics(_logger, automaticClose);
        var gateAcquired = false;
        try
        {
            _monitor.AssertMembershipAccess(profileId);
            attempt.Enter(SwitchDiagnosticStage.WaitingForSwitchLock);
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateAcquired = true;
            Interlocked.Exchange(ref _busy, 1);
            // All disk/process work is kept off the WPF dispatcher.
            await Task.Run(async () =>
            {
                attempt.Enter(SwitchDiagnosticStage.Preflight);
                Report(progress, DesktopSwitchStage.Checking, "Checking the selected account and Codex installation…");
                var state = automaticClose ? _desktop.Inspect() : RequireClosed();
                if (state.Problem is not null) throw new InvalidOperationException(state.Problem);
                if (state.Installation is null) throw new InvalidOperationException("A trusted Codex desktop installation was not found.");
                DesktopHomePolicy.AssertSupported(_desktopHome);
                var profiles = _monitor.Accounts.Select(x => x.Profile).ToArray();
                attempt.Enter(SwitchDiagnosticStage.PreparingProfiles);
                foreach (var profile in profiles)
                    await _monitor.PrepareProfileForMaintenanceAsync(profile.Id, cancellationToken).ConfigureAwait(false);
                var leases = new List<IDisposable>();
                var automaticallyClosed = false;
                var baselineCaptured = false;
                string? baselineHash = null;
                try
                {
                    // A desktop switch is rare. Lock all enrolled profiles in a
                    // stable order so refresh, redemption and removal cannot race.
                    attempt.Enter(SwitchDiagnosticStage.WaitingForProfileLocks);
                    foreach (var profile in profiles.OrderBy(x => x.Id))
                        leases.Add(await _operations.AcquireAsync(profile.Id, cancellationToken).ConfigureAwait(false));
                    var selected = _monitor.Accounts.FirstOrDefault(x => x.Profile.Id == profileId)?.Profile
                        ?? throw new InvalidOperationException("This account is no longer in the monitor.");
                    _monitor.AssertMembershipAccess(profileId);
                    attempt.Enter(SwitchDiagnosticStage.CheckingSavedSignIn);
                    if (automaticClose)
                    {
                        // Preflight without rotating any token the running desktop might own.
                        var preflight = DesktopAuthFileStore.ReadSnapshotFromHome(_paths.CodexHome(profileId), false)
                            ?? throw new InvalidOperationException("Sign in to this account again before switching.");
                        if (string.IsNullOrEmpty(selected.IdentityFingerprint)
                            || selected.IdentityFingerprint != preflight.Identity.EmailFingerprint)
                            throw new InvalidOperationException("The saved login does not match this account. Sign in again before switching.");
                        DesktopHomePolicy.AssertSupported(_desktopHome, preflight.Identity.AccountId);
                        _ = _store.GetReceipt(); // Reject an unreadable recovery journal before closing Codex.
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!state.CanSwitch)
                        {
                            Report(progress, DesktopSwitchStage.ClosingCodex, "Requesting Codex to close normally…");
                            Report(progress, DesktopSwitchStage.WaitingForExit, "Waiting for Codex to exit. Your sign-in has not changed yet.");
                            attempt.CloseRequested = true;
                            attempt.Enter(SwitchDiagnosticStage.WaitingForExit);
                            try
                            {
                                await _desktop.RequestCloseAndWaitAsync(state, TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
                            }
                            catch
                            {
                                // Cancellation can arrive after Quit succeeded but before the
                                // adapter returned. Reopen only a freshly verified closed desktop,
                                // with unchanged credentials and no pending recovery journal.
                                try
                                {
                                    if (_desktop.Inspect().CanSwitch && _store.GetReceipt()?.IsPending != true)
                                    {
                                        baselineHash = AuthHash(_store.ReadSnapshot(false));
                                        baselineCaptured = true;
                                        automaticallyClosed = true;
                                    }
                                }
                                catch { /* An unverified state must remain untouched. */ }
                                throw;
                            }
                            state = RequireClosed(); // Never trust a stale close acknowledgement.
                            automaticallyClosed = true;
                            attempt.DesktopExitVerified = true;
                            attempt.Enter(SwitchDiagnosticStage.DesktopClosed);
                        }
                        else state = RequireClosed();
                    }
                    var pendingReceipt = _store.GetReceipt();
                    if (pendingReceipt?.IsPending == true)
                    {
                        attempt.Enter(SwitchDiagnosticStage.RecoveringPendingSwitch);
                        _store.Rollback(IsClosed, pendingOnly: true, expectedReceipt: pendingReceipt);
                    }
                    if (automaticallyClosed)
                    {
                        // Closing can flush a rotated login. Capture only the final, closed
                        // desktop state, after recovery of any earlier interrupted switch.
                        try { baselineHash = AuthHash(_store.ReadSnapshot(false)); baselineCaptured = true; }
                        catch { /* Unverifiable previous credentials must never be auto-reopened. */ }
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    var previousId = SelectedProfileId;
                    if (previousId == profileId)
                    {
                        // Codex already has this exact login; avoid needlessly
                        // rotating its refresh token just to open its window.
                        RequireClosed();
                        attempt.Enter(SwitchDiagnosticStage.ReopeningCodex);
                        Report(progress, DesktopSwitchStage.ReopeningCodex, "Reopening Codex with the selected account…");
                        ReopenSelectedSignIn(state.Installation!);
                        Report(progress, DesktopSwitchStage.Completed, "Codex reopened with the selected sign-in.");
                        return;
                    }
                    if (previousId is Guid prior)
                    {
                        attempt.Enter(SwitchDiagnosticStage.SynchronizingPreviousSignIn);
                        var previous = _monitor.Accounts.FirstOrDefault(x => x.Profile.Id == prior)?.Profile;
                        var latest = previous is null ? null : _router.MatchingSnapshot(previous);
                        if (latest is not null)
                        {
                            var source = DesktopAuthFileStore.ReadSnapshotFromHome(_paths.CodexHome(prior), false)
                                ?? throw new InvalidOperationException("The previous saved account is unavailable.");
                            var receipt = _store.GetReceipt()
                                ?? throw new InvalidOperationException("The previous account's switch receipt is unavailable.");
                            // Register both sides before the source write. If copying fails or the
                            // next account's validation fails, refresh routing still recognizes
                            // the previous profile as owned by the desktop on either side of the copy.
                            _store.PrepareSourceSynchronization(receipt,
                                Convert.ToHexString(SHA256.HashData(source.CloneAuthBytes())),
                                Convert.ToHexString(SHA256.HashData(latest.CloneAuthBytes())), IsClosed);
                            DesktopAuthFileStore.WriteProfileSnapshot(_paths.CodexHome(prior), latest, IsClosed);
                        }
                    }
                    var before = DesktopAuthFileStore.ReadSnapshotFromHome(_paths.CodexHome(profileId), false)
                        ?? throw new InvalidOperationException("Sign in to this account again before switching.");
                    if (string.IsNullOrEmpty(selected.IdentityFingerprint)
                        || !string.Equals(selected.IdentityFingerprint, before.Identity.EmailFingerprint, StringComparison.Ordinal))
                        throw new InvalidOperationException("The saved login does not match this account. Sign in again before switching.");
                    attempt.Enter(SwitchDiagnosticStage.ValidatingSelectedSignIn);
                    Report(progress, DesktopSwitchStage.Checking, "Verifying the selected account with Codex. Your desktop sign-in has not changed yet.");
                    await using (var client = await CreateValidationClientAsync(
                        _paths.CodexHome(profileId), cancellationToken).ConfigureAwait(false))
                    {
                        var identity = await client.ReadAccountAsync(true, cancellationToken).ConfigureAwait(false);
                        if (identity?.Type != "chatgpt" || IdentityUtilities.FingerprintEmail(identity.Email) != selected.IdentityFingerprint)
                            throw new InvalidOperationException("Codex could not verify the selected account. The current desktop login was preserved.");
                        // Service validation, without starting a task or spending a reset.
                        await client.ReadRateLimitsAsync(cancellationToken).ConfigureAwait(false);
                    }
                    var verified = DesktopAuthFileStore.ReadSnapshotFromHome(_paths.CodexHome(profileId), true)
                        ?? throw new InvalidOperationException("The selected account has no saved login.");
                    if (verified.Identity != before.Identity)
                        throw new InvalidOperationException("The selected account identity changed during verification. No desktop login was changed.");
                    DesktopHomePolicy.AssertSupported(_desktopHome, verified.Identity.AccountId);
                    _monitor.AssertMembershipAccess(profileId);
                    cancellationToken.ThrowIfCancellationRequested();
                    RequireClosed();
                    if (automaticallyClosed && baselineCaptured && AuthHash(_store.ReadSnapshot(false)) != baselineHash)
                        throw new InvalidOperationException("Codex's sign-in changed while the selected account was being checked. No new sign-in was installed. Retry after checking Codex.");
                    Report(progress, DesktopSwitchStage.SwitchingSignIn, "Switching the saved Codex sign-in…");
                    DesktopSwitchReceipt? installedReceipt = null;
                    var attemptStartedUtc = DateTimeOffset.UtcNow;
                    try
                    {
                        attempt.InstallAttempted = true;
                        attempt.Enter(SwitchDiagnosticStage.InstallingSignIn);
                        installedReceipt = _store.Install(verified, profileId, () =>
                        {
                            if (!IsClosed()) return false;
                            if (automaticallyClosed && baselineCaptured && AuthHash(_store.ReadSnapshot(false)) != baselineHash)
                                throw new InvalidOperationException("Codex's sign-in changed before it could be switched. No new sign-in was installed.");
                            return true;
                        });
                        if (_store.ReadSnapshot(true)?.Identity != verified.Identity)
                            throw new InvalidOperationException("The saved Codex account could not be verified after switching.");
                        attempt.Enter(SwitchDiagnosticStage.FinalizingSignIn);
                        _store.FinalizeSwitch(IsClosed);
                        RequireClosed();
                    }
                    catch (Exception exception)
                    {
                        attempt.CaptureFailure(exception, cancellationToken.IsCancellationRequested);
                        try
                        {
                            attempt.Enter(SwitchDiagnosticStage.RollingBack);
                            var currentReceipt = _store.GetReceipt();
                            if (installedReceipt is not null)
                            {
                                if (currentReceipt is null || currentReceipt.CreatedUtc != installedReceipt.CreatedUtc
                                    || currentReceipt.SelectedProfileId != installedReceipt.SelectedProfileId
                                    || currentReceipt.IntendedAuthHash != installedReceipt.IntendedAuthHash)
                                    throw new InvalidOperationException("The account-switch recovery receipt changed.");
                                _store.Rollback(IsClosed, expectedReceipt: installedReceipt);
                            }
                            else if (currentReceipt?.IsPending == true)
                            {
                                // Install can fail after its durable journal write. Recover only
                                // this attempt, never an earlier completed switch or another account.
                                if (currentReceipt.SelectedProfileId != profileId || currentReceipt.CreatedUtc < attemptStartedUtc)
                                    throw new InvalidOperationException("Another account switch needs recovery.");
                                _store.Rollback(IsClosed, pendingOnly: true, expectedReceipt: currentReceipt);
                            }
                        }
                        catch { throw new InvalidOperationException("The switch needs recovery. Quit Codex and retry Switch to recover the interrupted change. Your previous login is protected in the local recovery file."); }
                        throw;
                    }
                    // Credential commit has completed. A window activation failure must
                    // never roll back a sign-in that a newly started desktop may already use.
                    attempt.Enter(SwitchDiagnosticStage.ReopeningCodex);
                    Report(progress, DesktopSwitchStage.ReopeningCodex, "Opening the Codex window with the selected account…");
                    ReopenSelectedSignIn(state.Installation!);
                    _logger.Info("desktop_account_switch_prepared", new Dictionary<string, object?> { ["profileId"] = profileId });
                    Report(progress, DesktopSwitchStage.Completed, "Codex reopened with the selected sign-in.");
                }
                catch (Exception exception)
                {
                    attempt.CaptureFailure(exception, cancellationToken.IsCancellationRequested);
                    if (automaticallyClosed && baselineCaptured && exception is not DesktopReopenException)
                        TryReopenPreviousSignIn(baselineHash, progress, attempt);
                    throw;
                }
                finally { for (var i = leases.Count - 1; i >= 0; i--) leases[i].Dispose(); }
            }, cancellationToken).ConfigureAwait(false);
            attempt.Complete();
        }
        catch (Exception exception)
        {
            attempt.Fail(exception, cancellationToken.IsCancellationRequested);
            throw;
        }
        finally
        {
            if (gateAcquired) { Interlocked.Exchange(ref _busy, 0); _gate.Release(); }
        }
    }

    private async Task<IAccountAppServerClient> CreateValidationClientAsync(string profileHome, CancellationToken cancellationToken)
    {
        if (_validationClientFactory is not null)
            return await _validationClientFactory(profileHome, cancellationToken).ConfigureAwait(false);
        var settings = await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        return await CodexAppServerClient.StartAsync(_locator.Locate(settings.CodexExecutablePath),
            profileHome, _logger, cancellationToken).ConfigureAwait(false);
    }

    private bool IsClosed() => _desktop.Inspect().CanSwitch;

    private void ReopenSelectedSignIn(CodexDesktopInstallation installation)
    {
        try { _desktop.Launch(installation); }
        catch (Exception exception)
        {
            throw new DesktopReopenException("The selected sign-in is ready, but Codex could not display its window. Select Open Codex to retry opening it. The sign-in was not rolled back.", exception);
        }
    }

    private static string? AuthHash(DesktopAuthSnapshot? snapshot) => snapshot is null ? null
        : Convert.ToHexString(SHA256.HashData(snapshot.CloneAuthBytes()));

    private void TryReopenPreviousSignIn(string? baselineHash, IProgress<DesktopSwitchProgress>? progress,
        SwitchAttemptDiagnostics attempt)
    {
        try
        {
            var state = _desktop.Inspect();
            if (!state.CanSwitch || _store.GetReceipt()?.IsPending == true
                || AuthHash(_store.ReadSnapshot(false)) != baselineHash) return;
            // This is only a guarded reopen, never a second credential rollback or close.
            // Another app opening during failure recovery must stay untouched.
            state = RequireClosed();
            if (_store.GetReceipt()?.IsPending == true || AuthHash(_store.ReadSnapshot(false)) != baselineHash) return;
            attempt.Enter(SwitchDiagnosticStage.ReopeningPreviousSignIn);
            Report(progress, DesktopSwitchStage.ReopeningCodex, "Reopening Codex with its previous sign-in. The switch was not completed.");
            _desktop.Launch(state.Installation!);
            attempt.Enter(SwitchDiagnosticStage.PreviousSignInReopened);
        }
        catch (Exception exception)
        {
            _logger.Error("desktop_previous_login_reopen_failed", exception);
            attempt.RecoveryFailed(exception);
        }
    }

    private enum SwitchDiagnosticStage
    {
        Preflight, WaitingForSwitchLock, PreparingProfiles, WaitingForProfileLocks,
        CheckingSavedSignIn, WaitingForExit, DesktopClosed, RecoveringPendingSwitch,
        SynchronizingPreviousSignIn, ValidatingSelectedSignIn, InstallingSignIn,
        FinalizingSignIn, ReopeningCodex, RollingBack, ReopeningPreviousSignIn,
        PreviousSignInReopened, Completed
    }

    /// <summary>
    /// Fixed-value checkpoints are flushed by SafeJsonLogger before external actions.
    /// An interrupted process leaves its last checkpoint, without logging credentials,
    /// profile identifiers, account names, paths, exception messages or stack traces.
    /// </summary>
    private sealed class SwitchAttemptDiagnostics
    {
        private readonly SafeJsonLogger _logger;
        private readonly Guid _attemptId = Guid.NewGuid();
        private readonly bool _automaticClose;
        private readonly System.Diagnostics.Stopwatch _elapsed = System.Diagnostics.Stopwatch.StartNew();
        private SwitchDiagnosticStage _stage = SwitchDiagnosticStage.Preflight;
        private SwitchDiagnosticStage? _failureStage;
        private string? _failureReason;
        public bool CloseRequested { get; set; }
        public bool DesktopExitVerified { get; set; }
        public bool InstallAttempted { get; set; }

        public SwitchAttemptDiagnostics(SafeJsonLogger logger, bool automaticClose)
        {
            _logger = logger;
            _automaticClose = automaticClose;
            Write("desktop_switch_started");
        }

        public void Enter(SwitchDiagnosticStage stage)
        {
            _stage = stage;
            Write("desktop_switch_stage");
        }

        public void CaptureFailure(Exception exception, bool cancellationRequested)
        {
            if (_failureStage is not null) return;
            _failureStage = _stage;
            _failureReason = Reason(exception, cancellationRequested);
            Write("desktop_switch_failure_detected", exception);
        }

        public void Fail(Exception exception, bool cancellationRequested)
        {
            CaptureFailure(exception, cancellationRequested);
            Write("desktop_switch_failed", exception);
        }

        public void RecoveryFailed(Exception exception) => Write("desktop_switch_recovery_failed", exception);

        public void Complete()
        {
            _stage = SwitchDiagnosticStage.Completed;
            Write("desktop_switch_completed");
        }

        private void Write(string eventName, Exception? exception = null)
        {
            try
            {
                var fields = new Dictionary<string, object?>
                {
                    ["attemptId"] = _attemptId,
                    ["monitorProcessId"] = Environment.ProcessId,
                    ["automaticClose"] = _automaticClose,
                    ["stage"] = _stage,
                    ["failureStage"] = _failureStage,
                    ["reason"] = _failureReason,
                    ["elapsedMs"] = _elapsed.ElapsedMilliseconds,
                    ["closeRequested"] = CloseRequested,
                    ["desktopExitVerified"] = DesktopExitVerified,
                    // Attempted does not claim whether an interrupted atomic write completed.
                    ["installAttempted"] = InstallAttempted
                };
                if (exception is null) _logger.Info(eventName, fields);
                else _logger.Error(eventName, exception, fields);
            }
            catch { /* Diagnostics cannot alter close, credential commit or recovery. */ }
        }

        private static string Reason(Exception exception, bool cancellationRequested) => exception switch
        {
            OperationCanceledException when cancellationRequested => "cancelled",
            OperationCanceledException or TimeoutException => "timeout",
            UnauthorizedAccessException or System.Security.SecurityException => "access_denied",
            InvalidDataException => "invalid_data",
            IOException => "io_error",
            HttpRequestException => "network_error",
            CryptographicException => "cryptographic_error",
            DesktopQuitValidationException quit when quit.ReasonCode is
                "desktop_closed_before_quit" or "desktop_identity_changed" or "desktop_signature_unverified"
                or "codex_process_metadata_unavailable" or "another_codex_process_started" => quit.ReasonCode,
            InvalidOperationException => "operation_rejected",
            _ => "unexpected_error"
        };
    }

    private static void Report(IProgress<DesktopSwitchProgress>? progress, DesktopSwitchStage stage, string message)
    {
        try { progress?.Report(new DesktopSwitchProgress(stage, message)); }
        catch { /* A UI observer must not interrupt credential commit or recovery. */ }
    }

    private CodexDesktopState RequireClosed()
    {
        var state = _desktop.Inspect();
        if (state.Problem is not null) throw new InvalidOperationException(state.Problem);
        if (!state.CanSwitch)
            throw new InvalidOperationException("Codex is still running. Finish your tasks, quit Codex and any other Codex CLI sessions, then select Switch and open Codex again. Nothing was force-closed.");
        return state;
    }
}
