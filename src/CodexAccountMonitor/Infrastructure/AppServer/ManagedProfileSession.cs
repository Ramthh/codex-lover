using CodexAccountMonitor.Core.Models;
using CodexAccountMonitor.Core.Services;

namespace CodexAccountMonitor.Infrastructure.AppServer;

public sealed class ManagedProfileSession : IManagedProfileSession
{
    private readonly CodexAppServerClient _client;

    public ManagedProfileSession(Guid profileId, CodexAppServerClient client)
    {
        ProfileId = profileId;
        _client = client;
    }

    public Guid ProfileId { get; }
    public bool IsTaskRunning => _client.HasRunningTurn;
    public bool IsHealthy => _client.IsHealthy;

    public async Task<bool> VerifyIdentityAsync(
        AccountProfile expectedProfile,
        CancellationToken cancellationToken)
    {
        var identity = await _client.ReadAccountAsync(refreshToken: true, cancellationToken).ConfigureAwait(false);
        if (identity is null || !string.Equals(identity.Type, "chatgpt", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var fingerprint = IdentityUtilities.FingerprintEmail(identity.Email);
        return expectedProfile.IdentityFingerprint is not null
               && fingerprint is not null
               && string.Equals(expectedProfile.IdentityFingerprint, fingerprint, StringComparison.Ordinal);
    }

    public Task StopGracefullyAsync(CancellationToken cancellationToken)
    {
        if (IsTaskRunning)
        {
            throw new InvalidOperationException("A monitor-managed task is still running.");
        }

        return Task.CompletedTask;
    }

    public async Task<ManagedProfileUsageReadResult> ReadUsageAsync(
        AccountProfile expectedProfile,
        CancellationToken cancellationToken)
    {
        var identity = await _client.ReadAccountAsync(refreshToken: false, cancellationToken).ConfigureAwait(false);
        if (identity is null || !string.Equals(identity.Type, "chatgpt", StringComparison.OrdinalIgnoreCase))
        {
            return new ManagedProfileUsageReadResult(false, false, null);
        }

        var actualFingerprint = IdentityUtilities.FingerprintEmail(identity.Email);
        var identityMatches = expectedProfile.IdentityFingerprint is not null
                              && actualFingerprint is not null
                              && string.Equals(
                                  expectedProfile.IdentityFingerprint,
                                  actualFingerprint,
                                  StringComparison.Ordinal);
        if (!identityMatches)
        {
            return new ManagedProfileUsageReadResult(true, false, null);
        }

        var rateLimits = await _client.ReadRateLimitsAsync(cancellationToken).ConfigureAwait(false);
        return new ManagedProfileUsageReadResult(true, true, QuotaMapper.Map(rateLimits));
    }

    public Task<string> ConsumeResetCreditAsync(
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        _client.ConsumeResetCreditAsync(idempotencyKey, null, cancellationToken);

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}

public sealed class ManagedProfileSessionFactory : IManagedProfileSessionFactory
{
    private readonly CodexExecutableLocator _locator;
    private readonly SettingsRepository _settings;
    private readonly SafeJsonLogger _logger;
    private readonly AccountDirectoryService _directories;

    public ManagedProfileSessionFactory(
        CodexExecutableLocator locator,
        SettingsRepository settings,
        SafeJsonLogger logger,
        AccountDirectoryService directories)
    {
        _locator = locator;
        _settings = settings;
        _logger = logger;
        _directories = directories;
    }

    public event EventHandler<Guid>? RateLimitsUpdated;
    public event EventHandler<Guid>? TurnBecameIdle;
    public event EventHandler<Guid>? SessionTerminated;

    public async Task<IManagedProfileSession> StartAsync(
        AccountProfile profile,
        CancellationToken cancellationToken)
    {
        var settings = await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        var executable = _locator.Locate(settings.CodexExecutablePath);
        var home = _directories.CreateIsolatedCodexHome(profile.Id);

        var client = await CodexAppServerClient.StartAsync(
            executable,
            home,
            _logger,
            cancellationToken).ConfigureAwait(false);
        client.NotificationReceived += (_, notification) =>
        {
            if (string.Equals(notification.Method, "account/rateLimits/updated", StringComparison.Ordinal))
            {
                RateLimitsUpdated?.Invoke(this, profile.Id);
            }
            else if (notification.Method is "turn/completed" or "turn/failed" or "turn/cancelled")
            {
                TurnBecameIdle?.Invoke(this, profile.Id);
            }
        };
        client.Terminated += (_, _) => SessionTerminated?.Invoke(this, profile.Id);
        return new ManagedProfileSession(profile.Id, client);
    }
}
