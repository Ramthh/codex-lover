using CodexAccountMonitor.Core.Models;
using CodexAccountMonitor.Infrastructure.AppServer;
using System.Security.Cryptography;

namespace CodexAccountMonitor.Infrastructure.DesktopSwitching;

public sealed class DesktopCredentialRouter
{
    private readonly DesktopAuthFileStore _store;
    private readonly AppDataPaths _paths;
    private readonly AccountDirectoryService _directories;
    private readonly CodexExecutableLocator _locator;
    private readonly SettingsRepository _settings;
    private readonly SafeJsonLogger _logger;

    public DesktopCredentialRouter(DesktopAuthFileStore store, AppDataPaths paths,
        AccountDirectoryService directories, CodexExecutableLocator locator,
        SettingsRepository settings, SafeJsonLogger logger)
    {
        _store = store; _paths = paths; _directories = directories;
        _locator = locator; _settings = settings; _logger = logger;
    }

    public DesktopAuthSnapshot? MatchingSnapshot(AccountProfile profile)
    {
        var receipt = _store.GetReceipt();
        if (receipt?.SelectedProfileId != profile.Id || receipt.IsPending) return null;
        var desktop = _store.ReadSnapshot();
        if (desktop is null || !string.Equals(desktop.Identity.EmailFingerprint,
                profile.IdentityFingerprint, StringComparison.Ordinal)) return null;
        var source = DesktopAuthFileStore.ReadSnapshotFromHome(_paths.CodexHome(profile.Id), false);
        return source is not null && source.Identity == desktop.Identity
            && SourceMatchesReceipt(source, receipt) ? desktop : null;
    }

    internal static bool SourceMatchesReceipt(DesktopAuthSnapshot source, DesktopSwitchReceipt receipt) =>
        (receipt.AcceptedSourceHashes ?? new[] { receipt.IntendedAuthHash })
            .Contains(Convert.ToHexString(SHA256.HashData(source.CloneAuthBytes())), StringComparer.Ordinal);

    private bool IsLentProfile(AccountProfile profile)
    {
        var receipt = _store.GetReceipt();
        if (receipt?.SelectedProfileId != profile.Id) return false;
        var source = DesktopAuthFileStore.ReadSnapshotFromHome(_paths.CodexHome(profile.Id), false);
        return source is null || SourceMatchesReceipt(source, receipt);
    }

    public Guid? FindSelectedProfile(IEnumerable<AccountProfile> profiles)
    {
        foreach (var profile in profiles)
        {
            try { if (MatchingSnapshot(profile) is not null) return profile.Id; }
            catch { /* An unreadable sign-in must never appear verified. */ }
        }
        return null;
    }

    public async Task<IAccountAppServerClient?> CreateClientAsync(AccountProfile profile, CancellationToken cancellationToken)
    {
        DesktopAuthSnapshot? snapshot;
        try { snapshot = MatchingSnapshot(profile); }
        catch
        {
            if (IsLentProfile(profile)) throw;
            return null;
        }
        if (snapshot is null)
        {
            // Do not try a stale copy of a refresh token lent to the desktop.
            if (IsLentProfile(profile))
                throw new InvalidOperationException("desktop_sign_in_unavailable");
            return null;
        }

        var stage = Guid.NewGuid();
        CodexAppServerClient? client = null;
        try
        {
            var home = _directories.CreateIsolatedCodexHome(stage);
            var settings = await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);
            client = await CodexAppServerClient.StartAsync(_locator.Locate(settings.CodexExecutablePath),
                home, _logger, cancellationToken).ConfigureAwait(false);
            await client.UseExternalAccessTokenAsync(snapshot.AccessToken, snapshot.Identity.AccountId, cancellationToken).ConfigureAwait(false);
            return new EphemeralClient(client, _directories, stage);
        }
        catch
        {
            if (client is not null) await client.DisposeAsync().ConfigureAwait(false);
            _directories.RemoveIsolatedAccount(stage);
            throw;
        }
    }

    private sealed class EphemeralClient(CodexAppServerClient inner, AccountDirectoryService directories, Guid stage) : IAccountAppServerClient
    {
        public Task<CodexAccountIdentity?> ReadAccountAsync(bool refreshToken, CancellationToken cancellationToken = default) => inner.ReadAccountAsync(false, cancellationToken);
        public Task<RawRateLimitsResponse> ReadRateLimitsAsync(CancellationToken cancellationToken = default) => inner.ReadRateLimitsAsync(cancellationToken);
        public Task<LoginPrompt> StartChatGptLoginAsync(bool deviceCode, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LoginCompletion> WaitForLoginCompletionAsync(TimeSpan timeout, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> ConsumeResetCreditAsync(string idempotencyKey, string? creditId = null, CancellationToken cancellationToken = default) => inner.ConsumeResetCreditAsync(idempotencyKey, creditId, cancellationToken);
        public async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            directories.RemoveIsolatedAccount(stage);
        }
    }
}
