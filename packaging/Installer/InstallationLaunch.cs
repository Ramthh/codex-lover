using CodexAccountMonitor.Infrastructure;

namespace CodexAccountMonitor.Setup;

internal static class InstallationLaunch
{
    internal static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(10);

    internal static async Task LaunchAsync(string executable, bool minimized,
        Func<string, IReadOnlyList<string>, CancellationToken, Task>? launch = null, TimeSpan? timeout = null)
    {
        // ShellExecute in the installer can retain its caller's Windows job. Use the
        // verified existing Explorer desktop so a tool-launched setup cannot leave
        // the installed monitor owned by Codex when Switch & reopen requests Quit.
        launch ??= new ExplorerLaunchBroker().LaunchAsync;
        string[] arguments = minimized ? ["--minimized"] : [];
        using var deadline = new CancellationTokenSource(timeout ?? LaunchTimeout);
        await launch(executable, arguments, deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
        deadline.Token.ThrowIfCancellationRequested();
        // Setup has already committed installation. Its caller handles unavailable
        // Explorer/timeout by keeping that installation and the Start-menu shortcut.
    }
}
