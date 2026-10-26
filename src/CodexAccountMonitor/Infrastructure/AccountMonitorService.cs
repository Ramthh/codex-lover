using System.Diagnostics;
using CodexAccountMonitor.Core.Models;
using CodexAccountMonitor.Core.Services;
using CodexAccountMonitor.Infrastructure.AppServer;

namespace CodexAccountMonitor.Infrastructure;

public sealed record EnrollmentProgress(string Stage, LoginPrompt? Prompt = null);

public sealed class AccountMonitorService
{
    private readonly AccountRepository _repository;
    private readonly SettingsRepository _settings;
    private readonly AccountDirectoryService _directories;
    private readonly AppDataPaths _paths;
    private readonly CodexExecutableLocator _locator;
    private readonly SafeJsonLogger _logger;
    private readonly ResetOperationRepository _resetOperations;
    private readonly PendingAccountDeletionRepository _pendingDeletions;
    private readonly SemaphoreSlim _refreshConcurrency = new(2, 2);
    private readonly ProfileOperationCoordinator _refreshOperations = new();
    private readonly ProfileOperationCoordinator _profileOperations;
    private readonly ProfileSwitchCoordinator? _switchCoordinator;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly List<PersistedAccount> _accounts = new();
    private readonly Func<string, CancellationToken, Task<IAccountAppServerClient>>? _clientFactory;
    private string? _resetRecoveryMessage;

    public AccountMonitorService(
        AccountRepository repository,
        SettingsRepository settings,
        AccountDirectoryService directories,
        AppDataPaths paths,
        CodexExecutableLocator locator,
        SafeJsonLogger logger,
        ProfileOperationCoordinator? profileOperations = null,
        ProfileSwitchCoordinator? switchCoordinator = null,
        Func<string, CancellationToken, Task<IAccountAppServerClient>>? clientFactory = null)
    {
        _repository = repository;
        _settings = settings;
        _directories = directories;
        _paths = paths;
        _locator = locator;
        _logger = logger;
        _resetOperations = new ResetOperationRepository(paths);
        _pendingDeletions = new PendingAccountDeletionRepository(paths);
        _profileOperations = profileOperations ?? new ProfileOperationCoordinator();
        _switchCoordinator = switchCoordinator;
        _clientFactory = clientFactory;
    }

    public event EventHandler? AccountsChanged;
    // Override only existing-account reads, never enrollment or reauthentication.
    public Func<AccountProfile, CancellationToken, Task<IAccountAppServerClient?>>? ProfileClientFactory { get; set; }
    // Installed by the normal application before initialization. Demo never creates network clients.
    public Action<Guid?, bool>? MembershipAccessGuard { get; set; }
    public void AssertMembershipAccess(Guid? profileId = null, bool enrollment = false) => MembershipAccessGuard?.Invoke(profileId, enrollment);
    public string? RecoveryMessage => _resetRecoveryMessage ?? _repository.RecoveryMessage;

    public IReadOnlyList<PersistedAccount> Accounts
    {
        get
        {
            lock (_accounts)
            {
                return _accounts.OrderBy(static account => account.Profile.Slot).ToArray();
            }
        }
    }

    public bool IsActiveProfile(Guid profileId) => _switchCoordinator?.IsActiveProfile(profileId) == true;

    public bool HasResidentProfile(Guid profileId) => _switchCoordinator?.OwnsProfile(profileId) == true;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var loaded = await _repository.LoadAsync(cancellationToken).ConfigureAwait(false);
        lock (_accounts)
        {
            _accounts.Clear();
            _accounts.AddRange(loaded);
        }
        await RetryPendingAccountDeletionsAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            foreach (var account in Accounts)
            {
                var pending = await _resetOperations.FindPendingAsync(account.Profile.Id, cancellationToken).ConfigureAwait(false);
                if (pending is null && account.Usage.HasPendingResetCredit
                    && await _resetOperations.FindLatestAsync(account.Profile.Id, cancellationToken).ConfigureAwait(false) is null)
                    throw new InvalidDataException("An unresolved reset has lost its saved operation key.");
                if (account.Usage.HasPendingResetCredit != (pending is not null))
                    await SetPendingResetAsync(account.Profile.Id, pending is not null, cancellationToken).ConfigureAwait(false);
            }
            _resetRecoveryMessage = null;
        }
        catch (InvalidDataException)
        {
            _resetRecoveryMessage = "The reset history could not be read. Reset credits are blocked until the saved reset history is recovered; account profiles were preserved.";
        }

        OnAccountsChanged();
    }

    private async Task RetryPendingAccountDeletionsAsync(CancellationToken cancellationToken)
    {
        var pending = await _pendingDeletions.LoadAsync(cancellationToken).ConfigureAwait(false);
        foreach (var profileId in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // A journal entry can precede the manifest commit. A crash or
                // cancellation in that interval must not delete a retained profile.
                if (Accounts.Any(account => account.Profile.Id == profileId))
                {
                    await _pendingDeletions.CompleteAsync(profileId, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                _directories.RemoveIsolatedAccount(profileId);
                await _pendingDeletions.CompleteAsync(profileId, cancellationToken).ConfigureAwait(false);
                _logger.Info("pending_account_directory_removed", new Dictionary<string, object?>
                {
                    ["profileId"] = profileId
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.Error("pending_account_directory_cleanup_failed", exception, new Dictionary<string, object?>
                {
                    ["profileId"] = profileId
                });
            }
        }
    }

    public async Task RefreshAllAsync(CancellationToken cancellationToken = default)
    {
        AssertMembershipAccess();
        var profiles = Accounts.Select(static account => account.Profile).ToArray();
        await Parallel.ForEachAsync(profiles, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = cancellationToken },
            async (profile, token) =>
            {
                try { await RefreshAsync(profile.Id, token).ConfigureAwait(false); }
                catch (Membership.MembershipRequiredException) { /* Preserve extra trial profiles without refreshing them. */ }
            }).ConfigureAwait(false);
    }

    public async Task RefreshAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        AssertMembershipAccess(profileId);
        // Keep the read/reduce/persist transaction ordered per account. The
        // separate coordinator deliberately avoids a lock-order cycle with
        // profile switching, which owns the switch gate before its profile lease.
        using var refreshLease = await _refreshOperations.AcquireAsync(profileId, cancellationToken)
            .ConfigureAwait(false);
        while (true)
        {
            var persisted = Accounts.FirstOrDefault(account => account.Profile.Id == profileId);
            if (persisted is null)
            {
                return;
            }

            if (_switchCoordinator?.IsActiveProfile(profileId) == true)
            {
                var residentAttempt = await RunRefreshAttemptAsync(
                    persisted,
                    () => _switchCoordinator.ReadActiveUsageAsync(profileId, cancellationToken),
                    cancellationToken,
                    useGlobalSlot: false).ConfigureAwait(false);
                if (residentAttempt.Handled)
                {
                    if (residentAttempt.RequiresQuarantine)
                    {
                        await QuarantineActiveProfileAsync(
                                profileId,
                                residentAttempt.Usage?.SafeErrorCode ?? "authentication_required",
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    await UpdateUsageAsync(profileId, residentAttempt.Usage!, cancellationToken).ConfigureAwait(false);
                    return;
                }
            }

            else if (_switchCoordinator?.OwnsProfile(profileId) == true)
            {
                // A quarantined resident may still own a running task. Never
                // start a second App Server against the same CODEX_HOME.
                return;
            }

            using var profileLease = await _profileOperations.AcquireAsync(profileId, cancellationToken)
                .ConfigureAwait(false);
            if (_switchCoordinator?.OwnsProfile(profileId) == true)
            {
                // The switch completed while this refresh was waiting for the profile lease.
                continue;
            }

            persisted = Accounts.FirstOrDefault(account => account.Profile.Id == profileId);
            if (persisted is null)
            {
                return;
            }

            var shortLivedAttempt = await RunRefreshAttemptAsync(
                persisted,
                () => ReadUsageFromShortLivedServerAsync(persisted.Profile, cancellationToken),
                cancellationToken,
                useGlobalSlot: true).ConfigureAwait(false);
            await UpdateUsageAsync(profileId, shortLivedAttempt.Usage!, cancellationToken).ConfigureAwait(false);
            return;
        }
    }

    private async Task<RefreshAttempt> RunRefreshAttemptAsync(
        PersistedAccount persisted,
        Func<Task<ManagedProfileUsageReadResult?>> reader,
        CancellationToken cancellationToken,
        bool useGlobalSlot)
    {
        if (useGlobalSlot)
        {
            await _refreshConcurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var result = await ReadWithRecoveryAsync(persisted.Profile.Id, reader, cancellationToken).ConfigureAwait(false);
            if (result is null)
            {
                return new RefreshAttempt(false, null);
            }

            var updated = result.HasAccount && result.IdentityMatches && result.Snapshot is not null
                ? UsageStateReducer.Success(persisted.Usage, result.Snapshot, DateTimeOffset.UtcNow)
                : UsageStateReducer.Failure(
                    persisted.Usage,
                    result.HasAccount ? "identity_mismatch" : "authentication_required",
                    DateTimeOffset.UtcNow,
                    isAuthenticationFailure: true);
            return new RefreshAttempt(
                true,
                updated,
                !result.HasAccount || !result.IdentityMatches);
        }
        catch (AppServerRpcException exception)
        {
            return new RefreshAttempt(
                true,
                UsageStateReducer.Failure(
                    persisted.Usage,
                    exception.IsRateLimited ? "rate_limited" : $"rpc_{exception.Code}",
                    DateTimeOffset.UtcNow,
                    exception.IsAuthenticationFailure,
                    exception.IsRateLimited),
                exception.IsAuthenticationFailure);
        }
        catch (FileNotFoundException)
        {
            return new RefreshAttempt(true, UsageStateReducer.Failure(
                persisted.Usage,
                "codex_not_found",
                DateTimeOffset.UtcNow));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var errorCode = RefreshErrorCode(exception);
            _logger.Error("account_refresh_failed", exception, new Dictionary<string, object?>
            {
                ["profileId"] = persisted.Profile.Id,
                ["errorCode"] = errorCode
            });
            return new RefreshAttempt(true, UsageStateReducer.Failure(
                persisted.Usage,
                errorCode,
                DateTimeOffset.UtcNow,
                isAuthenticationFailure: errorCode == "desktop_sign_in_unavailable"),
                RequiresQuarantine: errorCode == "desktop_sign_in_unavailable");
        }
        finally
        {
            if (useGlobalSlot)
            {
                _refreshConcurrency.Release();
            }
        }
    }

    private async Task<ManagedProfileUsageReadResult?> ReadWithRecoveryAsync(
        Guid profileId,
        Func<Task<ManagedProfileUsageReadResult?>> reader,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return await reader().ConfigureAwait(false); }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested
                && ShouldRetryUsageRead(exception, attempt, started.Elapsed))
            {
                // Only usage reads are replayed. A short-lived client is disposed by the
                // reader before reconnecting; resets, login and switches never use this path.
                _logger.Warning("account_refresh_retry", new Dictionary<string, object?>
                {
                    ["profileId"] = profileId,
                    ["nextAttempt"] = attempt + 1,
                    ["exceptionType"] = exception.GetType().Name,
                    ["elapsedMilliseconds"] = started.ElapsedMilliseconds
                });
                await Task.Delay(attempt == 1 ? 500 : 1500, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal static bool ShouldRetryUsageRead(Exception exception, int failedAttempt, TimeSpan elapsed) =>
        IsTransientRefreshFailure(exception)
        && failedAttempt < 3
        && (elapsed < TimeSpan.FromSeconds(10)
            // The App Server's request deadline is 45 seconds. Allow one fresh
            // connection after that deadline; the old 10-second retry window
            // excluded every real request timeout. A second slow failure ends
            // this refresh so an offline service cannot hold the queue forever.
            || failedAttempt == 1 && elapsed < TimeSpan.FromMinutes(1));

    private static bool IsTransientRefreshFailure(Exception exception) => exception switch
    {
        AppServerRpcException rpc => !rpc.IsAuthenticationFailure && !rpc.IsRateLimited
            && rpc.Code is 408 or 500 or 502 or 503 or 504 or -32603,
        FileNotFoundException or DirectoryNotFoundException or InvalidDataException => false,
        IOException or System.Net.Http.HttpRequestException or TimeoutException or OperationCanceledException => true,
        _ => false
    };

    // Only allowlisted diagnostic codes cross the logging/UI boundary. Never copy
    // an arbitrary provider exception message, which can contain credentials.
    private static string RefreshErrorCode(Exception exception) => exception switch
    {
        InvalidOperationException { Message: "desktop_sign_in_unavailable" } => "desktop_sign_in_unavailable",
        InvalidOperationException { Message: "The configured Codex executable is not validly signed by OpenAI." } => "codex_untrusted",
        InvalidOperationException { Message: "Codex App Server started with an unexpected profile directory." } => "profile_isolation_failed",
        UnauthorizedAccessException => "profile_access_denied",
        _ when IsTransientRefreshFailure(exception) => "connection_unavailable",
        _ => "refresh_failed"
    };

    private async Task<ManagedProfileUsageReadResult?> ReadUsageFromShortLivedServerAsync(
        AccountProfile profile,
        CancellationToken cancellationToken)
    {
        await using var client = await StartProfileClientAsync(profile, cancellationToken).ConfigureAwait(false);
        var identity = await client.ReadAccountAsync(refreshToken: false, cancellationToken).ConfigureAwait(false);
        if (identity is null || !string.Equals(identity.Type, "chatgpt", StringComparison.OrdinalIgnoreCase))
        {
            return new ManagedProfileUsageReadResult(false, false, null);
        }

        if (!IdentityMatches(profile, identity))
        {
            return new ManagedProfileUsageReadResult(true, false, null);
        }

        var rateLimits = await client.ReadRateLimitsAsync(cancellationToken).ConfigureAwait(false);
        return new ManagedProfileUsageReadResult(true, true, QuotaMapper.Map(rateLimits));
    }

    private async Task<IAccountAppServerClient> StartProfileClientAsync(AccountProfile profile, CancellationToken cancellationToken)
    {
        AssertMembershipAccess(profile.Id);
        if (ProfileClientFactory is not null)
        {
            var routed = await ProfileClientFactory(profile, cancellationToken).ConfigureAwait(false);
            if (routed is not null) return routed;
        }
        return await StartClientAsync(_directories.CreateIsolatedCodexHome(profile.Id), cancellationToken).ConfigureAwait(false);
    }

    private async Task<IAccountAppServerClient> StartClientAsync(string home, CancellationToken cancellationToken)
    {
        if (_clientFactory is not null)
        {
            return await _clientFactory(home, cancellationToken).ConfigureAwait(false);
        }
        var settings = await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        return await CodexAppServerClient.StartAsync(_locator.Locate(settings.CodexExecutablePath),
            home, _logger, cancellationToken).ConfigureAwait(false);
    }

    public async Task PrepareProfileForMaintenanceAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        if (_switchCoordinator is not null)
        {
            await _switchCoordinator.DeactivateAsync(profileId, cancellationToken).ConfigureAwait(false);
        }
        await _settings.UpdateAsync(settings => settings.ActiveProfileId == profileId
            ? settings with { ActiveProfileId = null } : settings, cancellationToken).ConfigureAwait(false);
    }

    public async Task RenameAccountAsync(Guid profileId, string alias, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        if (alias.Trim().Length > 80) throw new ArgumentOutOfRangeException(nameof(alias), "Account names must be 80 characters or fewer.");
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var accounts = Accounts.ToArray();
            var index = Array.FindIndex(accounts, account => account.Profile.Id == profileId);
            if (index < 0) throw new KeyNotFoundException("Account profile not found.");
            accounts[index] = accounts[index] with { Profile = accounts[index].Profile with { Alias = alias.Trim() } };
            await CommitAccountsAsync(accounts, cancellationToken).ConfigureAwait(false);
        }
        finally { _mutationGate.Release(); }
        OnAccountsChanged();
    }

    public async Task MoveAccountAsync(Guid profileId, int targetSlot, CancellationToken cancellationToken = default)
    {
        if (targetSlot < 1) throw new ArgumentOutOfRangeException(nameof(targetSlot));
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var accounts = Accounts.ToArray();
            var index = Array.FindIndex(accounts, account => account.Profile.Id == profileId);
            if (index < 0) throw new KeyNotFoundException("Account profile not found.");
            var oldSlot = accounts[index].Profile.Slot;
            var other = Array.FindIndex(accounts, account => account.Profile.Slot == targetSlot);
            if (other >= 0) accounts[other] = accounts[other] with { Profile = accounts[other].Profile with { Slot = oldSlot } };
            accounts[index] = accounts[index] with { Profile = accounts[index].Profile with { Slot = targetSlot } };
            await CommitAccountsAsync(accounts, cancellationToken).ConfigureAwait(false);
        }
        finally { _mutationGate.Release(); }
        OnAccountsChanged();
    }

    private async Task CommitAccountsAsync(IReadOnlyList<PersistedAccount> accounts, CancellationToken cancellationToken)
    {
        await _repository.SaveAsync(accounts, cancellationToken).ConfigureAwait(false);
        lock (_accounts) { _accounts.Clear(); _accounts.AddRange(accounts); }
        _switchCoordinator?.RegisterProfiles(accounts.Select(static account => account.Profile));
    }

    private sealed record RefreshAttempt(
        bool Handled,
        AccountUsageState? Usage,
        bool RequiresQuarantine = false);

    private async Task QuarantineActiveProfileAsync(
        Guid profileId,
        string safeErrorCode,
        CancellationToken cancellationToken)
    {
        if (_switchCoordinator is null)
        {
            return;
        }

        await _switchCoordinator.QuarantineActiveSessionAsync(profileId, safeErrorCode, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await _settings.UpdateAsync(
                    settings => settings.ActiveProfileId == profileId
                        ? settings with { ActiveProfileId = null }
                        : settings,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.Error("quarantined_profile_persistence_failed", exception, new Dictionary<string, object?>
            {
                ["profileId"] = profileId
            });
        }
    }

    public async Task<PersistedAccount> EnrollAsync(
        string alias,
        int slot,
        bool useDeviceCode,
        Func<EnrollmentProgress, Task> progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);
        AssertMembershipAccess(enrollment: true);
        if (slot < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(slot));
        }

        if (Accounts.Any(account => account.Profile.Slot == slot))
        {
            throw new InvalidOperationException("The selected account slot is unavailable.");
        }

        var id = Guid.NewGuid();
        var stage = "create_profile";
        PersistedAccount account;
        try
        {
            _directories.CreateIsolatedCodexHome(id);
            await progress(new EnrollmentProgress("starting")).ConfigureAwait(false);
            stage = "initialize";
            await using (var client = await StartClientAsync(_paths.CodexHome(id), cancellationToken).ConfigureAwait(false))
            {
                stage = "login_start";
                var prompt = await client.StartChatGptLoginAsync(useDeviceCode, cancellationToken).ConfigureAwait(false);
                await progress(new EnrollmentProgress("authorization_required", prompt)).ConfigureAwait(false);
                stage = "login_wait";
                var completed = await client.WaitForLoginCompletionAsync(
                    TimeSpan.FromMinutes(10),
                    cancellationToken).ConfigureAwait(false);
                if (!completed.Success)
                {
                    throw new InvalidOperationException(completed.SafeErrorCode ?? "login_failed");
                }

                stage = "verify_identity";
                var identity = await client.ReadAccountAsync(refreshToken: true, cancellationToken).ConfigureAwait(false);
                if (identity is null || !string.Equals(identity.Type, "chatgpt", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("identity_verification_failed");
                }

                var identityFingerprint = IdentityUtilities.FingerprintEmail(identity.Email);
                if (identityFingerprint is null)
                {
                    throw new InvalidOperationException("identity_email_unavailable");
                }

                if (Accounts.Any(existing => string.Equals(
                        existing.Profile.IdentityFingerprint,
                        identityFingerprint,
                        StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException("account_already_connected");
                }

                var profile = new AccountProfile
                {
                    Id = id,
                    Slot = slot,
                    Alias = alias.Trim(),
                    MaskedEmail = IdentityUtilities.MaskEmail(identity.Email),
                    IdentityFingerprint = identityFingerprint,
                    AddedAtUtc = DateTimeOffset.UtcNow
                };

                AccountUsageState usage;
                try
                {
                    var limits = await client.ReadRateLimitsAsync(cancellationToken).ConfigureAwait(false);
                    usage = UsageStateReducer.Success(
                        new AccountUsageState(),
                        QuotaMapper.Map(limits),
                        DateTimeOffset.UtcNow);
                }
                catch (AppServerRpcException exception)
                {
                    usage = UsageStateReducer.Failure(
                        new AccountUsageState { Authentication = AuthenticationHealth.Healthy },
                        exception.IsRateLimited ? "rate_limited" : $"rpc_{exception.Code}",
                        DateTimeOffset.UtcNow,
                        exception.IsAuthenticationFailure,
                        exception.IsRateLimited);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (IsTransientRefreshFailure(exception))
                {
                    // Identity is already verified. A temporary usage outage must
                    // not delete a successful login or force another browser sign-in.
                    usage = UsageStateReducer.Failure(
                        new AccountUsageState { Authentication = AuthenticationHealth.Healthy },
                        "connection_unavailable",
                        DateTimeOffset.UtcNow);
                    _logger.Warning("account_enrolled_usage_unavailable", new Dictionary<string, object?>
                    {
                        ["profileId"] = id,
                        ["errorCode"] = "connection_unavailable",
                        ["exceptionType"] = exception.GetType().Name
                    });
                }

                account = new PersistedAccount(profile, usage);
            }

            stage = "save_profile";
            await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var current = Accounts;
                AssertMembershipAccess(enrollment: true);
                if (current.Any(existing => existing.Profile.Slot == slot)
                    || current.Any(existing => string.Equals(
                        existing.Profile.IdentityFingerprint,
                        account.Profile.IdentityFingerprint,
                        StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException("The account or selected slot is already connected.");
                }

                var committed = current.Append(account).ToArray();
                await _repository.SaveAsync(committed, cancellationToken).ConfigureAwait(false);
                lock (_accounts)
                {
                    _accounts.Clear();
                    _accounts.AddRange(committed);
                }
            }
            finally
            {
                _mutationGate.Release();
            }
        }
        catch (Exception exception)
        {
            if (exception is not OperationCanceledException)
                _logger.Error("account_enrollment_failed", exception, new Dictionary<string, object?>
                {
                    ["stage"] = stage,
                    ["rpcCode"] = (exception as AppServerRpcException)?.Code,
                    ["nativeCode"] = (exception as System.ComponentModel.Win32Exception)?.NativeErrorCode
                });
            try
            {
                _directories.RemoveIsolatedAccount(id);
            }
            catch
            {
            }

            throw;
        }

        _logger.Info("account_enrolled", new Dictionary<string, object?>
        {
            ["profileId"] = id,
            ["slot"] = slot,
            ["identityVerifiable"] = true
        });
        OnAccountsChanged();
        try
        {
            await progress(new EnrollmentProgress("complete")).ConfigureAwait(false);
        }
        catch
        {
            // Enrollment is already committed; a closed progress UI cannot roll it back.
        }

        return account;
    }

    public async Task<PersistedAccount> ReauthenticateAsync(
        Guid profileId,
        bool useDeviceCode,
        Func<EnrollmentProgress, Task> progress,
        CancellationToken cancellationToken = default)
    {
        AssertMembershipAccess(profileId);
        var account = Accounts.FirstOrDefault(value => value.Profile.Id == profileId)
                      ?? throw new KeyNotFoundException("Account profile not found.");
        await PrepareProfileForMaintenanceAsync(profileId, cancellationToken).ConfigureAwait(false);

        using (await _profileOperations.AcquireAsync(profileId, cancellationToken).ConfigureAwait(false))
        {
            if (_switchCoordinator?.OwnsProfile(profileId) == true)
            {
                throw new InvalidOperationException("active_profile_reauthentication_blocked");
            }

            account = Accounts.FirstOrDefault(value => value.Profile.Id == profileId)
                      ?? throw new KeyNotFoundException("Account profile not found.");

            var stagedId = Guid.NewGuid();
            var codexHome = _directories.CreateIsolatedCodexHome(stagedId);
            try
            {
                await using (var client = await StartClientAsync(codexHome, cancellationToken).ConfigureAwait(false))
                {
                    var prompt = await client.StartChatGptLoginAsync(useDeviceCode, cancellationToken).ConfigureAwait(false);
                    await progress(new EnrollmentProgress("authorization_required", prompt)).ConfigureAwait(false);
                    var completion = await client.WaitForLoginCompletionAsync(TimeSpan.FromMinutes(10), cancellationToken)
                        .ConfigureAwait(false);
                    if (!completion.Success)
                    {
                        throw new InvalidOperationException(completion.SafeErrorCode ?? "login_failed");
                    }

                    var identity = await client.ReadAccountAsync(refreshToken: true, cancellationToken).ConfigureAwait(false);
                    if (identity is null || !string.Equals(identity.Type, "chatgpt", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("identity_verification_failed");
                    }

                    var identityFingerprint = IdentityUtilities.FingerprintEmail(identity.Email);
                    if (identityFingerprint is null
                        || account.Profile.IdentityFingerprint is null
                        || !string.Equals(account.Profile.IdentityFingerprint, identityFingerprint, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("identity_mismatch");
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                AssertMembershipAccess(profileId);
                _directories.CommitVerifiedAuthentication(stagedId, profileId);
                await UpdateUsageAsync(profileId,
                    account.Usage with { Authentication = AuthenticationHealth.Healthy, SafeErrorCode = null },
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                try { _directories.RemoveIsolatedAccount(stagedId); }
                catch (Exception exception)
                {
                    _logger.Error("staged_auth_cleanup_failed", exception);
                    await _pendingDeletions.MarkAsync(stagedId, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }

        await RefreshAsync(profileId, cancellationToken).ConfigureAwait(false);
        return Accounts.Single(value => value.Profile.Id == profileId);
    }

    public async Task<string> ConsumeResetCreditAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        AssertMembershipAccess(profileId);
        var account = Accounts.FirstOrDefault(value => value.Profile.Id == profileId)
                      ?? throw new KeyNotFoundException("Account profile not found.");
        var pendingOperation = await _resetOperations.FindPendingAsync(profileId, cancellationToken).ConfigureAwait(false);
        if (pendingOperation is null && account.Usage.HasPendingResetCredit)
        {
            var completed = await _resetOperations.FindLatestAsync(profileId, cancellationToken).ConfigureAwait(false);
            if (completed is not { Completed: true })
                throw new InvalidDataException("An unresolved reset has lost its saved operation key. Restore the reset history before retrying.");
            await SetPendingResetAsync(profileId, false, cancellationToken).ConfigureAwait(false);
            return completed.Outcome ?? "unknown";
        }
        if (pendingOperation is null && account.Usage.Snapshot?.ResetCreditsAvailable is not > 0)
        {
            return "noCredit";
        }

        var operation = pendingOperation ?? await _resetOperations.GetOrCreatePendingAsync(profileId, cancellationToken).ConfigureAwait(false);
        await SetPendingResetAsync(profileId, true, CancellationToken.None).ConfigureAwait(false);
        string outcome;
        while (true)
        {
            if (_switchCoordinator?.IsActiveProfile(profileId) == true)
            {
                ManagedResetCreditResult residentResult;
                try
                {
                    residentResult = await _switchCoordinator.ConsumeActiveResetCreditAsync(
                        profileId, operation.IdempotencyKey, cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidOperationException exception) when (exception.Message == "identity_mismatch")
                {
                    await QuarantineActiveProfileAsync(profileId, "identity_mismatch", cancellationToken).ConfigureAwait(false);
                    await UpdateUsageAsync(profileId, UsageStateReducer.Failure(account.Usage,
                        "identity_mismatch", DateTimeOffset.UtcNow, isAuthenticationFailure: true), cancellationToken)
                        .ConfigureAwait(false);
                    throw;
                }
                if (residentResult.Handled)
                {
                    outcome = residentResult.Outcome ?? "unknown";
                    break;
                }
            }

            if (_switchCoordinator?.OwnsProfile(profileId) == true)
            {
                throw new InvalidOperationException("resident_profile_not_verified");
            }

            using var profileLease = await _profileOperations.AcquireAsync(profileId, cancellationToken)
                .ConfigureAwait(false);
            if (_switchCoordinator?.OwnsProfile(profileId) == true)
            {
                continue;
            }

            account = Accounts.FirstOrDefault(value => value.Profile.Id == profileId)
                      ?? throw new KeyNotFoundException("Account profile not found.");
            await using var client = await StartProfileClientAsync(account.Profile, cancellationToken).ConfigureAwait(false);
            var identity = await client.ReadAccountAsync(refreshToken: true, cancellationToken).ConfigureAwait(false);
            if (identity is null || !IdentityMatches(account.Profile, identity))
            {
                await UpdateUsageAsync(profileId, UsageStateReducer.Failure(account.Usage,
                    "identity_mismatch", DateTimeOffset.UtcNow, isAuthenticationFailure: true), cancellationToken)
                    .ConfigureAwait(false);
                throw new InvalidOperationException("identity_mismatch");
            }
            AssertMembershipAccess(profileId);
            outcome = await client.ConsumeResetCreditAsync(operation.IdempotencyKey, null, cancellationToken)
                .ConfigureAwait(false);
            break;
        }

        if (outcome is not ("reset" or "alreadyRedeemed" or "noCredit" or "nothingToReset")) outcome = "unknown";
        await _resetOperations.CompleteAsync(operation, outcome, cancellationToken).ConfigureAwait(false);
        if (outcome != "unknown")
            await SetPendingResetAsync(profileId, false, CancellationToken.None).ConfigureAwait(false);
        try
        {
            await RefreshAsync(profileId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.Error("post_reset_refresh_failed", exception, new Dictionary<string, object?>
            {
                ["profileId"] = profileId
            });
        }

        _logger.Info("reset_credit_result", new Dictionary<string, object?>
        {
            ["profileId"] = profileId,
            ["outcome"] = outcome
        });
        return outcome;
    }

    public async Task RemoveAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        await PrepareProfileForMaintenanceAsync(profileId, cancellationToken).ConfigureAwait(false);

        using (await _profileOperations.AcquireAsync(profileId, cancellationToken).ConfigureAwait(false))
        {
            if (_switchCoordinator?.OwnsProfile(profileId) == true)
            {
                throw new InvalidOperationException("active_profile_removal_blocked");
            }

            var removalCommitted = false;
            await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var current = Accounts;
                var remaining = current.Where(account => account.Profile.Id != profileId).ToArray();
                if (remaining.Length == current.Count)
                {
                    return;
                }

                await _pendingDeletions.MarkAsync(profileId, cancellationToken).ConfigureAwait(false);
                await CommitAccountsAsync(remaining, cancellationToken).ConfigureAwait(false);

                removalCommitted = true;
            }
            finally
            {
                _mutationGate.Release();
                if (!removalCommitted)
                {
                    try
                    {
                        await _pendingDeletions.CompleteAsync(profileId, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        _logger.Error("account_removal_tombstone_rollback_failed", exception, new Dictionary<string, object?>
                        {
                            ["profileId"] = profileId
                        });
                    }
                }
            }

            try
            {
                _directories.RemoveIsolatedAccount(profileId);
                await _pendingDeletions.CompleteAsync(profileId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.Error("account_directory_cleanup_failed", exception, new Dictionary<string, object?>
                {
                    ["profileId"] = profileId
                });
            }
        }

        _logger.Info("account_removed", new Dictionary<string, object?> { ["profileId"] = profileId });
        OnAccountsChanged();
    }

    private async Task UpdateUsageAsync(Guid profileId, AccountUsageState usage, CancellationToken cancellationToken)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var updated = Accounts.ToArray();
            var index = Array.FindIndex(updated, value => value.Profile.Id == profileId);
            if (index < 0)
            {
                return;
            }

            updated[index] = updated[index] with { Usage = usage with
                { HasPendingResetCredit = updated[index].Usage.HasPendingResetCredit } };
            await CommitAccountsAsync(updated, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutationGate.Release();
        }

        OnAccountsChanged();
    }

    private async Task SetPendingResetAsync(Guid profileId, bool pending, CancellationToken cancellationToken)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var accounts = Accounts.ToArray();
            var index = Array.FindIndex(accounts, account => account.Profile.Id == profileId);
            if (index < 0 || accounts[index].Usage.HasPendingResetCredit == pending) return;
            accounts[index] = accounts[index] with { Usage = accounts[index].Usage with { HasPendingResetCredit = pending } };
            await CommitAccountsAsync(accounts, cancellationToken).ConfigureAwait(false);
        }
        finally { _mutationGate.Release(); }
        OnAccountsChanged();
    }

    private static bool IdentityMatches(AccountProfile profile, CodexAccountIdentity identity)
    {
        if (!string.Equals(identity.Type, "chatgpt", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var actual = IdentityUtilities.FingerprintEmail(identity.Email);
        return profile.IdentityFingerprint is not null
               && actual is not null
               && string.Equals(profile.IdentityFingerprint, actual, StringComparison.Ordinal);
    }

    private void OnAccountsChanged()
    {
        var handlers = AccountsChanged?.GetInvocationList();
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers)
        {
            try
            {
                ((EventHandler)handler)(this, EventArgs.Empty);
            }
            catch
            {
                // Persistence has already committed; observers cannot roll it back.
            }
        }
    }
}

public sealed class RefreshScheduler : IAsyncDisposable
{
    private readonly AccountMonitorService _monitor;
    private readonly SettingsRepository _settings;
    private readonly SafeJsonLogger? _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _loop;
    private int _disposed;

    public RefreshScheduler(
        AccountMonitorService monitor,
        SettingsRepository settings,
        SafeJsonLogger? logger = null)
    {
        _monitor = monitor;
        _settings = settings;
        _logger = logger;
    }

    public void Start()
    {
        _loop ??= RunAsync(_lifetime.Token);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var settings = await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);
                var baseDelay = TimeSpan.FromMinutes(Math.Clamp(settings.RefreshIntervalMinutes, 1, 60));
                var jitter = TimeSpan.FromSeconds(Random.Shared.Next(-30, 31));
                var delay = baseDelay + jitter;
                if (delay < TimeSpan.FromSeconds(30))
                {
                    delay = TimeSpan.FromSeconds(30);
                }

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                await _monitor.RefreshAllAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger?.Error("refresh_scheduler_iteration_failed", exception);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                _logger?.Error("refresh_scheduler_shutdown_failed", exception);
            }
        }

        _lifetime.Dispose();
    }
}
