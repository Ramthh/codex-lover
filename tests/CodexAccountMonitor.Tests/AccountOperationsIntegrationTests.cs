using CodexAccountMonitor.Core.Models;
using CodexAccountMonitor.Core.Services;
using CodexAccountMonitor.Infrastructure;
using CodexAccountMonitor.Infrastructure.AppServer;
using CodexAccountMonitor.Infrastructure.Membership;

namespace CodexAccountMonitor.Tests;

public sealed class AccountOperationsIntegrationTests
{
    [Theory]
    [InlineData("io")]
    [InlineData("timeout")]
    [InlineData("rpc_timeout")]
    public async Task VerifiedEnrollmentSurvivesInitialUsageTransportFailure(string failure)
    {
        using var fixture = new Fixture();
        await fixture.Monitor.InitializeAsync();
        fixture.ReadFailures.Enqueue(failure switch
        {
            "io" => new IOException("private-provider-detail"),
            "timeout" => new TimeoutException("private-provider-detail"),
            _ => new OperationCanceledException("private-provider-detail")
        });

        var connected = await fixture.Monitor.EnrollAsync("Connected", 1, false, _ => Task.CompletedTask);

        Assert.True(fixture.LoginClientDisposed);
        Assert.Single(fixture.Monitor.Accounts);
        Assert.Equal(AuthenticationHealth.Healthy, connected.Usage.Authentication);
        Assert.Equal("connection_unavailable", connected.Usage.SafeErrorCode);
        Assert.Null(connected.Usage.LastSuccessfulRefreshUtc);
        Assert.Equal("new-test-auth", await File.ReadAllTextAsync(
            Path.Combine(fixture.Paths.CodexHome(connected.Profile.Id), "auth.json")));
        Assert.Equal(connected, Assert.Single(await fixture.Repository.LoadAsync()));

        await fixture.Monitor.RefreshAsync(connected.Profile.Id);

        Assert.Null(Assert.Single(fixture.Monitor.Accounts).Usage.SafeErrorCode);
        Assert.NotNull(Assert.Single(fixture.Monitor.Accounts).Usage.LastSuccessfulRefreshUtc);
    }

    [Fact]
    public async Task ExplicitEnrollmentCancellationStillDiscardsTheUncommittedProfile()
    {
        using var fixture = new Fixture();
        await fixture.Monitor.InitializeAsync();
        using var cancellation = new CancellationTokenSource();
        fixture.BeforeUsageRead = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Monitor.EnrollAsync(
            "Cancelled", 1, false, _ => Task.CompletedTask, cancellation.Token));

        Assert.Empty(fixture.Monitor.Accounts);
        Assert.Empty(await fixture.Repository.LoadAsync());
        Assert.Empty(Directory.GetDirectories(fixture.Paths.AccountsRoot));
    }

    [Theory]
    [InlineData("desktop_sign_in_unavailable", "desktop_sign_in_unavailable", true)]
    [InlineData("The configured Codex executable is not validly signed by OpenAI.", "codex_untrusted", false)]
    [InlineData("Codex App Server started with an unexpected profile directory.", "profile_isolation_failed", false)]
    [InlineData("secret-provider-detail", "refresh_failed", false)]
    public async Task RefreshRecordsAllowlistedDiagnosticAndCorrectRecoveryState(string failure, string expectedCode, bool expired)
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        var snapshot = fixture.Monitor.Accounts.Single().Usage.Snapshot;
        fixture.ReadFailures.Enqueue(new InvalidOperationException(failure));

        await fixture.Monitor.RefreshAsync(fixture.Profile.Id);

        var saved = Assert.Single(await fixture.Repository.LoadAsync()).Usage;
        Assert.Equal(expectedCode, saved.SafeErrorCode);
        Assert.Equal(expired ? AuthenticationHealth.Expired : AuthenticationHealth.Healthy, saved.Authentication);
        Assert.Equal(snapshot, saved.Snapshot);
        Assert.Equal(1, fixture.UsageReadCalls);
        foreach (var file in Directory.GetFiles(fixture.Paths.LogsRoot))
        {
            using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            var text = await reader.ReadToEndAsync();
            Assert.Contains($"\"errorCode\":\"{expectedCode}\"", text);
            Assert.DoesNotContain("secret-provider-detail", text);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshReconnectsAfterTransientFailureWithoutPublishingAnError(bool requestTimedOut)
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        fixture.ReadFailures.Enqueue(requestTimedOut
            ? new TaskCanceledException("test RPC request deadline")
            : new IOException("test transport closed"));
        fixture.ReadFailures.Enqueue(new AppServerRpcException(503, false, false));
        fixture.Credits = 5;
        var publishedErrors = new List<string?>();
        fixture.Monitor.AccountsChanged += (_, _) => publishedErrors.Add(fixture.Monitor.Accounts.Single().Usage.SafeErrorCode);

        await fixture.Monitor.RefreshAsync(fixture.Profile.Id);

        Assert.Equal(3, fixture.UsageReadCalls);
        Assert.Equal(3, fixture.ClientsCreated);
        Assert.Equal(3, fixture.ClientsDisposed);
        Assert.All(publishedErrors, error => Assert.Null(error));
        Assert.Equal(5, fixture.Monitor.Accounts.Single().Usage.Snapshot?.ResetCreditsAvailable);
        Assert.Null((await fixture.Repository.LoadAsync()).Single().Usage.SafeErrorCode);
        Assert.Empty(fixture.ConsumedKeys);
    }

    [Fact]
    public async Task RefreshStopsAfterThreeFailuresAndKeepsLastSuccessfulReading()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        var previous = fixture.Monitor.Accounts.Single().Usage;
        for (var i = 0; i < 4; i++) fixture.ReadFailures.Enqueue(new IOException("test offline"));

        await fixture.Monitor.RefreshAsync(fixture.Profile.Id);

        var saved = (await fixture.Repository.LoadAsync()).Single().Usage;
        Assert.Equal(3, fixture.UsageReadCalls);
        Assert.Equal(3, fixture.ClientsDisposed);
        Assert.Equal(previous.Snapshot, saved.Snapshot);
        Assert.Equal(previous.LastSuccessfulRefreshUtc, saved.LastSuccessfulRefreshUtc);
        Assert.Equal(previous.Authentication, saved.Authentication);
        Assert.Equal("connection_unavailable", saved.SafeErrorCode);
    }

    [Theory]
    [InlineData("auth", "rpc_401")]
    [InlineData("rate_limit", "rate_limited")]
    [InlineData("missing", "codex_not_found")]
    [InlineData("corrupt", "refresh_failed")]
    [InlineData("access_denied", "profile_access_denied")]
    [InlineData("isolation", "refresh_failed")]
    public async Task RefreshDoesNotRetryFailuresThatNeedOtherAction(string failure, string expectedCode)
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        fixture.ReadFailures.Enqueue(failure switch
        {
            "auth" => new AppServerRpcException(401, true, false),
            "rate_limit" => new AppServerRpcException(429, false, true),
            "missing" => new FileNotFoundException(),
            "corrupt" => new InvalidDataException(),
            "access_denied" => new UnauthorizedAccessException(),
            _ => new InvalidOperationException("profile mismatch")
        });

        await fixture.Monitor.RefreshAsync(fixture.Profile.Id);

        Assert.Equal(1, fixture.UsageReadCalls);
        Assert.Equal(1, fixture.ClientsDisposed);
        Assert.Equal(expectedCode, fixture.Monitor.Accounts.Single().Usage.SafeErrorCode);
    }

    [Fact]
    public async Task CancellationDuringRetryDelayDoesNotPersistAnErrorOrStartAnotherClient()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        var previous = fixture.Monitor.Accounts.Single().Usage;
        fixture.ReadFailures.Enqueue(new IOException());
        using var cancellation = new CancellationTokenSource();

        var refresh = fixture.Monitor.RefreshAsync(fixture.Profile.Id, cancellation.Token);
        await fixture.FirstUsageRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);

        Assert.Equal(1, fixture.ClientsCreated);
        Assert.Equal(1, fixture.ClientsDisposed);
        Assert.Equal(previous, fixture.Monitor.Accounts.Single().Usage);
        Assert.Null((await fixture.Repository.LoadAsync()).Single().Usage.SafeErrorCode);
    }

    [Fact]
    public async Task ExpiredMembershipBlocksEveryNetworkOperationBeforeCredentialsChange()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        fixture.Monitor.MembershipAccessGuard = (_, _) => throw new MembershipRequiredException("expired");
        await Assert.ThrowsAsync<MembershipRequiredException>(() => fixture.Monitor.RefreshAsync(fixture.Profile.Id));
        await Assert.ThrowsAsync<MembershipRequiredException>(() => fixture.Monitor.RefreshAllAsync());
        await Assert.ThrowsAsync<MembershipRequiredException>(() => fixture.Monitor.ConsumeResetCreditAsync(fixture.Profile.Id));
        await Assert.ThrowsAsync<MembershipRequiredException>(() => fixture.Monitor.ReauthenticateAsync(fixture.Profile.Id, false, _ => Task.CompletedTask));
        await Assert.ThrowsAsync<MembershipRequiredException>(() => fixture.Monitor.EnrollAsync("Third", 3, false, _ => Task.CompletedTask));
        Assert.Empty(fixture.ConsumedKeys);
        Assert.Equal("original-test-auth", await File.ReadAllTextAsync(fixture.AuthFile));
        Assert.Single(Directory.GetDirectories(fixture.Paths.AccountsRoot));
    }

    [Fact]
    public async Task ExpiredMembershipDoesNotPreventRemovingYourOwnSavedCredentials()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        fixture.Monitor.MembershipAccessGuard = (_, _) => throw new MembershipRequiredException("expired");
        await fixture.Monitor.RemoveAsync(fixture.Profile.Id);
        Assert.Empty(fixture.Monitor.Accounts);
        Assert.False(File.Exists(fixture.AuthFile));
    }

    [Fact]
    public async Task ResetRejectsChangedIdentityBeforeCallingConsume()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        fixture.Identity = "different@example.test";
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Monitor.ConsumeResetCreditAsync(fixture.Profile.Id));
        Assert.Empty(fixture.ConsumedKeys);
        Assert.Equal(AuthenticationHealth.Expired, fixture.Monitor.Accounts.Single().Usage.Authentication);
    }

    [Fact]
    public async Task WrongAccountReauthenticationPreservesExistingCredentials()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        fixture.Identity = "different@example.test";
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Monitor.ReauthenticateAsync(
            fixture.Profile.Id, false, _ => Task.CompletedTask));
        Assert.Equal("original-test-auth", await File.ReadAllTextAsync(fixture.AuthFile));
        Assert.Single(Directory.GetDirectories(fixture.Paths.AccountsRoot));
        Assert.Equal(fixture.Profile.Id, fixture.Monitor.Accounts.Single().Profile.Id);
    }

    [Fact]
    public async Task CancelledReauthenticationPreservesExistingCredentials()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Monitor.ReauthenticateAsync(
            fixture.Profile.Id, false, _ => { cancellation.Cancel(); return Task.CompletedTask; }, cancellation.Token));
        Assert.Equal("original-test-auth", await File.ReadAllTextAsync(fixture.AuthFile));
        Assert.Single(Directory.GetDirectories(fixture.Paths.AccountsRoot));
    }

    [Fact]
    public async Task VerifiedReauthenticationReplacesOnlyCredentialsAfterClientCloses()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        var history = Path.Combine(fixture.Paths.CodexHome(fixture.Profile.Id), "history.test");
        await File.WriteAllTextAsync(history, "keep history");
        await fixture.Monitor.ReauthenticateAsync(fixture.Profile.Id, false, _ => Task.CompletedTask);
        Assert.Equal("new-test-auth", await File.ReadAllTextAsync(fixture.AuthFile));
        Assert.Equal("keep history", await File.ReadAllTextAsync(history));
        Assert.True(fixture.LoginClientDisposed);
        Assert.Single(Directory.GetDirectories(fixture.Paths.AccountsRoot));
    }

    [Fact]
    public async Task UnknownResetOutcomeReusesKeyEvenAfterBalanceBecomesZero()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        fixture.ResetOutcome = "unknown";
        fixture.Credits = 0;
        Assert.Equal("unknown", await fixture.Monitor.ConsumeResetCreditAsync(fixture.Profile.Id));
        Assert.True(fixture.Monitor.Accounts.Single().Usage.HasPendingResetCredit);
        Assert.True((await fixture.Repository.LoadAsync()).Single().Usage.HasPendingResetCredit);
        fixture.ResetOutcome = "alreadyRedeemed";
        Assert.Equal("alreadyRedeemed", await fixture.Monitor.ConsumeResetCreditAsync(fixture.Profile.Id));
        Assert.Equal(2, fixture.ConsumedKeys.Count);
        Assert.Equal(fixture.ConsumedKeys[0], fixture.ConsumedKeys[1]);
        Assert.Null(await new ResetOperationRepository(fixture.Paths).FindPendingAsync(fixture.Profile.Id));
        Assert.False(fixture.Monitor.Accounts.Single().Usage.HasPendingResetCredit);
    }

    [Fact]
    public async Task RestartReconcilesResetWrittenBeforePendingCardStateWasSaved()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        await new ResetOperationRepository(fixture.Paths).GetOrCreatePendingAsync(fixture.Profile.Id);
        Assert.False(fixture.Monitor.Accounts.Single().Usage.HasPendingResetCredit);
        await fixture.Monitor.InitializeAsync();
        Assert.True(fixture.Monitor.Accounts.Single().Usage.HasPendingResetCredit);
    }

    [Fact]
    public async Task MissingJournalDoesNotErasePersistedUnresolvedResetOrCreateNewKey()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        fixture.ResetOutcome = "unknown";
        await fixture.Monitor.ConsumeResetCreditAsync(fixture.Profile.Id);
        File.Delete(fixture.Paths.ResetOperationsFile);
        await fixture.Monitor.InitializeAsync();
        Assert.True(fixture.Monitor.Accounts.Single().Usage.HasPendingResetCredit);
        Assert.NotNull(fixture.Monitor.RecoveryMessage);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Monitor.ConsumeResetCreditAsync(fixture.Profile.Id));
        Assert.Single(fixture.ConsumedKeys);
    }

    [Fact]
    public async Task CorruptResetJournalFailsClosedBeforeCallingServer()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        await File.WriteAllTextAsync(fixture.Paths.ResetOperationsFile, "{ broken journal");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Monitor.ConsumeResetCreditAsync(fixture.Profile.Id));
        Assert.Empty(fixture.ConsumedKeys);
        Assert.Equal("{ broken journal", await File.ReadAllTextAsync(fixture.Paths.ResetOperationsFile));
    }

    [Fact]
    public async Task RestartBeforeRemovalCommitPreservesListedProfileDirectory()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        await new PendingAccountDeletionRepository(fixture.Paths).MarkAsync(fixture.Profile.Id);
        await fixture.Monitor.InitializeAsync();
        Assert.True(File.Exists(fixture.AuthFile));
        Assert.Single(fixture.Monitor.Accounts);
        Assert.Empty(await new PendingAccountDeletionRepository(fixture.Paths).LoadAsync());
    }

    [Fact]
    public async Task RestartAfterRemovalCommitDeletesUnlistedProfileDirectory()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        await new PendingAccountDeletionRepository(fixture.Paths).MarkAsync(fixture.Profile.Id);
        await fixture.Repository.SaveAsync(Array.Empty<PersistedAccount>());
        await fixture.Monitor.InitializeAsync();
        Assert.False(Directory.Exists(fixture.Paths.AccountRoot(fixture.Profile.Id)));
        Assert.Empty(fixture.Monitor.Accounts);
    }

    [Fact]
    public async Task RemovingOnlyActiveIdleProfileReleasesSessionAndPersistsRemoval()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        await fixture.Coordinator.SwitchAsync(fixture.Profile.Id);
        await fixture.Settings.UpdateAsync(settings => settings with { ActiveProfileId = fixture.Profile.Id });
        await fixture.Monitor.RemoveAsync(fixture.Profile.Id);
        Assert.True(fixture.Session.Disposed);
        Assert.Empty(fixture.Monitor.Accounts);
        Assert.Null((await fixture.Settings.LoadAsync()).ActiveProfileId);
        Assert.False(Directory.Exists(fixture.Paths.AccountRoot(fixture.Profile.Id)));
    }

    [Fact]
    public async Task MaintenanceCannotInterruptRunningTask()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        await fixture.Coordinator.SwitchAsync(fixture.Profile.Id);
        fixture.Session.Running = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Monitor.RemoveAsync(fixture.Profile.Id));
        Assert.False(fixture.Session.Disposed);
        Assert.True(File.Exists(fixture.AuthFile));
        Assert.Single(fixture.Monitor.Accounts);
        fixture.Session.Running = false;
    }

    [Fact]
    public async Task ResidentResetRechecksIdentityImmediatelyBeforeSpending()
    {
        using var fixture = new Fixture();
        await fixture.InitializeAsync();
        await fixture.Coordinator.SwitchAsync(fixture.Profile.Id);
        fixture.Session.IdentityMatches = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Monitor.ConsumeResetCreditAsync(fixture.Profile.Id));
        Assert.Equal(0, fixture.Session.ConsumeCalls);
    }

    [Fact]
    public async Task RenameAndMoveSwapSlotsWithoutLosingAccountsOrUsage()
    {
        using var fixture = new Fixture();
        var second = fixture.Profile with { Id = Guid.NewGuid(), Slot = 2, Alias = "Second" };
        await fixture.InitializeAsync(second);
        await fixture.Monitor.RenameAccountAsync(fixture.Profile.Id, "  Work  ");
        await fixture.Monitor.MoveAccountAsync(fixture.Profile.Id, 2);
        var saved = await fixture.Repository.LoadAsync();
        Assert.Equal(second.Id, saved[0].Profile.Id);
        Assert.Equal("Work", saved[1].Profile.Alias);
        Assert.Equal(2, saved[1].Usage.Snapshot?.ResetCreditsAvailable);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "CodexAccountMonitorTests", Guid.NewGuid().ToString("N"));
        private readonly SafeJsonLogger _logger;
        public AppDataPaths Paths { get; }
        public AccountRepository Repository { get; }
        public SettingsRepository Settings { get; }
        public AccountMonitorService Monitor { get; }
        public ProfileSwitchCoordinator Coordinator { get; }
        public FakeSession Session { get; }
        public AccountProfile Profile { get; } = new()
        {
            Id = Guid.NewGuid(), Slot = 1, Alias = "Original",
            IdentityFingerprint = IdentityUtilities.FingerprintEmail("fixture@example.test")
        };
        public string Identity { get; set; } = "fixture@example.test";
        public int Credits { get; set; } = 2;
        public string ResetOutcome { get; set; } = "reset";
        public List<string> ConsumedKeys { get; } = new();
        public bool LoginClientDisposed { get; set; }
        public Queue<Exception> ReadFailures { get; } = new();
        public int UsageReadCalls { get; set; }
        public int ClientsCreated { get; set; }
        public int ClientsDisposed { get; set; }
        public TaskCompletionSource FirstUsageRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action? BeforeUsageRead { get; set; }
        public string AuthFile => Path.Combine(Paths.CodexHome(Profile.Id), "auth.json");

        public Fixture()
        {
            Paths = new AppDataPaths(_root);
            _logger = new SafeJsonLogger(Paths);
            Repository = new AccountRepository(Paths);
            Settings = new SettingsRepository(Paths);
            var operations = new ProfileOperationCoordinator();
            Session = new FakeSession(Profile.Id);
            Coordinator = new ProfileSwitchCoordinator(new FakeSessionFactory(Session), operations);
            Monitor = new AccountMonitorService(Repository, Settings, new AccountDirectoryService(Paths), Paths,
                new CodexExecutableLocator(_logger), _logger, operations, Coordinator,
                (home, _) =>
                {
                    ClientsCreated++;
                    return Task.FromResult<IAccountAppServerClient>(new FakeClient(this, home));
                });
        }

        public async Task InitializeAsync(AccountProfile? second = null)
        {
            var usage = UsageStateReducer.Success(new AccountUsageState(), new QuotaSnapshot(null, null, 2, null), DateTimeOffset.UtcNow);
            var accounts = new List<PersistedAccount> { new(Profile, usage) };
            if (second is not null) accounts.Add(new PersistedAccount(second, usage));
            await Repository.SaveAsync(accounts);
            new AccountDirectoryService(Paths).CreateIsolatedCodexHome(Profile.Id);
            await File.WriteAllTextAsync(AuthFile, "original-test-auth");
            await Monitor.InitializeAsync();
            Coordinator.RegisterProfiles(accounts.Select(account => account.Profile));
        }

        public void Dispose()
        {
            Session.Running = false;
            _logger.Dispose();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }

    private sealed class FakeClient(Fixture fixture, string home) : IAccountAppServerClient
    {
        private bool _login;
        public Task<CodexAccountIdentity?> ReadAccountAsync(bool refreshToken, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<CodexAccountIdentity?>(new("chatgpt", fixture.Identity, "test"));
        }
        public Task<RawRateLimitsResponse> ReadRateLimitsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            fixture.UsageReadCalls++;
            fixture.FirstUsageRead.TrySetResult();
            fixture.BeforeUsageRead?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (fixture.ReadFailures.TryDequeue(out var failure)) throw failure;
            return Task.FromResult(new RawRateLimitsResponse(Array.Empty<RawRateLimitBucket>(), fixture.Credits));
        }
        public Task<LoginPrompt> StartChatGptLoginAsync(bool deviceCode, CancellationToken cancellationToken = default)
        {
            _login = true;
            File.WriteAllText(Path.Combine(home, "auth.json"), "new-test-auth");
            return Task.FromResult(new LoginPrompt("chatgpt", "test", null, null, null));
        }
        public Task<LoginCompletion> WaitForLoginCompletionAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new LoginCompletion("test", true, null));
        }
        public Task<string> ConsumeResetCreditAsync(string idempotencyKey, string? creditId = null, CancellationToken cancellationToken = default)
        {
            fixture.ConsumedKeys.Add(idempotencyKey);
            return Task.FromResult(fixture.ResetOutcome);
        }
        public ValueTask DisposeAsync()
        {
            fixture.ClientsDisposed++;
            if (_login) fixture.LoginClientDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeSessionFactory(FakeSession session) : IManagedProfileSessionFactory
    {
        public Task<IManagedProfileSession> StartAsync(AccountProfile profile, CancellationToken cancellationToken) => Task.FromResult<IManagedProfileSession>(session);
    }
    private sealed class FakeSession(Guid profileId) : IManagedProfileSession
    {
        public Guid ProfileId => profileId;
        public bool Running { get; set; }
        public bool IsTaskRunning => Running;
        public bool Disposed { get; private set; }
        public bool IdentityMatches { get; set; } = true;
        public int ConsumeCalls { get; private set; }
        public Task<bool> VerifyIdentityAsync(AccountProfile expectedProfile, CancellationToken cancellationToken) => Task.FromResult(IdentityMatches);
        public Task StopGracefullyAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<string> ConsumeResetCreditAsync(string idempotencyKey, CancellationToken cancellationToken)
        {
            ConsumeCalls++;
            return Task.FromResult("reset");
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
