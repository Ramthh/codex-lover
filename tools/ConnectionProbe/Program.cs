using System.Text.Json;
using CodexAccountMonitor.Infrastructure;
using CodexAccountMonitor.Infrastructure.AppServer;

// Uses a new empty profile only. Optional --login-start checks that Codex can
// create its sign-in prompt, then closes it without opening a browser or signing in.
var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "CodexAccountMonitor", "diagnostic-runs", "connection-" + Guid.NewGuid().ToString("N"));
var paths = args.Contains("--normal-profile-root") ? new AppDataPaths() : new AppDataPaths(root);
using var logger = new SafeJsonLogger(new AppDataPaths(root));
var directories = new AccountDirectoryService(paths);
var profileId = Guid.NewGuid();
var stage = "create_profile";
try
{
    var home = directories.CreateIsolatedCodexHome(profileId);
    stage = "locate";
    var configuredPath = args.FirstOrDefault(value => value.StartsWith("--codex=", StringComparison.Ordinal))?[8..];
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    stage = "informational_commands";
    var installation = await new CodexInstallationProbe().InspectAsync(configuredPath, testAppServer: true, timeout.Token);
    if (!installation.IsReady || installation.ExecutablePath is null)
        throw new InvalidOperationException(installation.Message);
    var executable = installation.ExecutablePath;
    stage = "initialize";
    await using var client = await CodexAppServerClient.StartAsync(executable, home, logger, timeout.Token);
    stage = "verify_empty_account";
    if (await client.ReadAccountAsync(refreshToken: false, timeout.Token) is not null)
        throw new InvalidOperationException("The isolated probe unexpectedly contained an authenticated account.");
    if (args.Contains("--login-start"))
    {
        stage = "login_start";
        var prompt = await client.StartChatGptLoginAsync(false, timeout.Token);
        if (prompt.AuthorizationUri?.Scheme != "https" || prompt.AuthorizationUri.Host != "auth.openai.com")
            throw new InvalidOperationException("Codex did not return its expected sign-in page.");
    }
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        success = true, stage, isolatedProfile = true, emptyAccount = true,
        informationalCommandsPassed = true, runtimeVersion = installation.Version
    }));
}
catch (Exception exception)
{
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        success = false, stage, exceptionType = exception.GetType().Name,
        message = exception is AppServerRpcException ? "App Server initialization RPC failed" : exception.Message,
        rpcCode = (exception as AppServerRpcException)?.Code,
        nativeCode = (exception as System.ComponentModel.Win32Exception)?.NativeErrorCode
    }));
    Environment.ExitCode = 1;
}
finally
{
    if (args.Contains("--normal-profile-root")) directories.RemoveIsolatedAccount(profileId);
}
