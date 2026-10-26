namespace CodexAccountMonitor.Infrastructure;

public sealed class MonitorIndependentLaunchException(string message, Exception? inner = null) : Exception(message, inner);

public static class MonitorStartupBootstrap
{
    internal const string HandoffPrefix = "--explorer-handoff=";
    private const string EventPrefix = "Local\\CodexLover_ExplorerReady_";

    public static bool WasRelaunched(IReadOnlyList<string> arguments) => arguments.Any(argument => argument.StartsWith(HandoffPrefix, StringComparison.Ordinal));

    public static void PrepareSavedAccountEnvironment() => PrepareSavedAccountEnvironment(
        Environment.GetEnvironmentVariable,
        (name, value) => Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.Process));

    internal static void PrepareSavedAccountEnvironment(Func<string, string?> readEnvironment, Action<string, string?> writeEnvironment)
    {
        try { AppServer.AppServerLaunchEnvironment.AssertNoInheritedWorkloadIdentity(readEnvironment); }
        catch (AppServer.InheritedWorkloadIdentityException exception)
        {
            // Explorer's environment must not silently discard a managed identity selection.
            throw new MonitorIndependentLaunchException(exception.Message);
        }
        // Membership and saved Codex accounts do not use the launcher's API credentials.
        // A developer shell or Store test harness may carry them. Remove them only from
        // this process so both our helpers and a reopened desktop use their intended
        // sign-in. Never alter the parent process, user/machine variables or auth files.
        foreach (var name in new[] { "OPENAI_API_KEY", "CODEX_API_KEY", "CODEX_ACCESS_TOKEN" })
            writeEnvironment(name, null);
    }

    public static void AcknowledgeIndependentLaunch(IReadOnlyList<string> arguments)
    {
        var handoff = ReadHandoff(arguments);
        if (handoff is null) return;
        try
        {
            using var ready = EventWaitHandle.OpenExisting(EventPrefix + handoff.Value.ToString("N"));
            ready.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The original bootstrap may have exited. Independence remains required;
            // absence of an acknowledgment listener does not change account access.
        }
    }

    public static async Task RelaunchIndependentAsync(IReadOnlyList<string> arguments)
    {
        if (WasRelaunched(arguments))
            throw new MonitorIndependentLaunchException(WindowsMonitorLifetimeGuard.IndependentLaunchRequired);
        // Environment.ProcessPath can be a package-virtualized logical path that
        // Explorer cannot resolve. QueryFullProcessImageName supplies the physical
        // image path through our existing read-only process metadata source.
        string? executable;
        try
        {
            executable = new DesktopSwitching.WindowsDesktopProcessSource().ReadProcesses()
                .SingleOrDefault(process => process.ProcessId == Environment.ProcessId)?.ExecutablePath;
        }
        catch (Exception exception)
        {
            throw new MonitorIndependentLaunchException("Windows could not locate Codex Lover. Open it from the Windows Start menu.", exception);
        }
        if (string.IsNullOrWhiteSpace(executable))
            throw new MonitorIndependentLaunchException("Windows could not locate Codex Lover. Open it from the Windows Start menu.");
        var handoff = Guid.NewGuid();
        using var ready = new EventWaitHandle(false, EventResetMode.AutoReset, EventPrefix + handoff.ToString("N"));
        var forwarded = BuildRelaunchArguments(arguments, handoff);
        if (Path.GetFileName(executable).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            // Local framework-dependent test builds run through the signed .NET host.
            // Explorer must receive the app DLL as well as the existing safe flags.
            forwarded = [typeof(App).Assembly.Location, .. forwarded];
        }
        try
        {
            await WaitForIndependentLaunchAsync(
                token => new ExplorerLaunchBroker().LaunchAsync(executable, forwarded, token),
                token => Task.Run(() => WaitHandle.WaitAny([ready, token.WaitHandle]) == 0),
                TimeSpan.FromSeconds(15)).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            throw new MonitorIndependentLaunchException(
                "Windows could not confirm that Codex Lover reopened independently. Open Codex Lover from the Windows Start menu and retry. " +
                "Your accounts and Codex sign-in were not changed.", exception);
        }
    }

    internal static Guid? ReadHandoff(IReadOnlyList<string> arguments)
    {
        var values = arguments.Where(argument => argument.StartsWith(HandoffPrefix, StringComparison.Ordinal)).ToArray();
        if (values.Length == 0) return null;
        if (values.Length != 1 || !Guid.TryParseExact(values[0][HandoffPrefix.Length..], "N", out var value))
            throw new MonitorIndependentLaunchException("The Codex Lover restart request is invalid. Open the app from the Windows Start menu.");
        return value;
    }

    internal static string[] BuildRelaunchArguments(IReadOnlyList<string> arguments, Guid handoff) =>
        arguments.Where(argument => argument.Equals("--minimized", StringComparison.OrdinalIgnoreCase)
            || argument.Equals("--settings", StringComparison.OrdinalIgnoreCase)
            || argument.Equals("--local-test", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).Append(HandoffPrefix + handoff.ToString("N")).ToArray();

    internal static async Task WaitForIndependentLaunchAsync(Func<CancellationToken, Task> launch,
        Func<CancellationToken, Task<bool>> waitForReady, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        // The same deadline covers Explorer COM activation and the child acknowledgment.
        // A stalled COM apartment is a background thread and cannot keep startup waiting forever.
        await launch(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);
        deadline.Token.ThrowIfCancellationRequested();
        if (!await waitForReady(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false))
            throw new TimeoutException("Explorer did not acknowledge an independent launch.");
    }
}
