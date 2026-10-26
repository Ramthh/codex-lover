using CodexAccountMonitor.Core.Services;
using CodexAccountMonitor.Infrastructure.AppServer;

namespace CodexAccountMonitor.Infrastructure.DesktopSwitching;

// Opt-in diagnostic: reads the current desktop's quota with only an access
// token. Never redeems credits, starts a model turn, or changes desktop auth.
public static class SwitcherAccessProbe
{
    public static async Task<object> VerifyAsync(AppDataPaths paths)
    {
        var stage = Guid.NewGuid();
        var probePaths = new AppDataPaths(Path.Combine(paths.Root, "diagnostic-runs", stage.ToString("N")));
        var directories = new AccountDirectoryService(probePaths);
        var phase = "read_login";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            var snapshot = DesktopAuthFileStore.ReadSnapshotFromHome(DesktopHomePolicy.DefaultHome, true);
            if (snapshot is null) return new { checkedAccess = false, reason = "no_desktop_login" };
            var settings = await new SettingsRepository(paths).LoadAsync(timeout.Token);
            using var logger = new SafeJsonLogger(probePaths);
            var home = directories.CreateIsolatedCodexHome(stage);
            phase = "start_client";
            await using var client = await CodexAppServerClient.StartAsync(
                new CodexExecutableLocator(logger).Locate(settings.CodexExecutablePath), home, logger, timeout.Token);
            phase = "external_access";
            await client.UseExternalAccessTokenAsync(snapshot.AccessToken, snapshot.Identity.AccountId, timeout.Token);
            phase = "verify_identity";
            var identity = await client.ReadAccountAsync(false, timeout.Token);
            var matches = identity?.Type == "chatgpt" && IdentityUtilities.FingerprintEmail(identity.Email) == snapshot.Identity.EmailFingerprint;
            if (!matches) return new { checkedAccess = false, reason = "identity_mismatch" };
            phase = "read_usage";
            await client.ReadRateLimitsAsync(timeout.Token);
            return new { checkedAccess = true, identityMatches = true, usageRead = true };
        }
        catch (AppServerRpcException exception) { return new { checkedAccess = false, reason = "rpc_failed", phase, code = exception.Code, authentication = exception.IsAuthenticationFailure }; }
        catch (Exception exception) { return new { checkedAccess = false, reason = "access_check_failed", phase, errorType = exception.GetType().Name }; }
        finally { directories.RemoveIsolatedAccount(stage); }
    }
}
