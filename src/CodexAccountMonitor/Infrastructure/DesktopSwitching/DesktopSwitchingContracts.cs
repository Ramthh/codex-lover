namespace CodexAccountMonitor.Infrastructure.DesktopSwitching;

public sealed record CodexDesktopInstallation(string ExecutablePath, string DisplayName);

public enum DesktopSwitchStage
{
    Checking,
    ClosingCodex,
    WaitingForExit,
    SwitchingSignIn,
    ReopeningCodex,
    Completed
}

public sealed record DesktopSwitchProgress(DesktopSwitchStage Stage, string Message);

public sealed record DesktopProcessInfo(
    int ProcessId,
    int ParentProcessId,
    string Name,
    string? ExecutablePath,
    long? StartedAtFileTimeUtc = null);

public sealed record CodexDesktopState(
    CodexDesktopInstallation? Installation,
    IReadOnlyList<DesktopProcessInfo> BlockingProcesses,
    string? Problem)
{
    public bool CanSwitch => Installation is not null && Problem is null && BlockingProcesses.Count == 0;
}

public interface ICodexDesktopAdapter
{
    /// <summary>
    /// Takes a fresh, conservative process snapshot. Only pass PIDs owned by this monitor;
    /// its own PID and verified descendants are excluded automatically.
    /// </summary>
    CodexDesktopState Inspect(IReadOnlyCollection<int>? monitorOwnedProcessIds = null);

    /// <summary>
    /// Requests normal exit only from verified desktop windows, then waits for every
    /// Codex process to exit. Never terminates a process or approves an exit prompt.
    /// A refused/timed-out/cancelled close must leave credentials untouched.
    /// </summary>
    Task<CodexDesktopState> RequestCloseAndWaitAsync(CodexDesktopState expected, TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        Task.FromException<CodexDesktopState>(new InvalidOperationException(
            "This Codex installation does not support automatic closing. Finish your tasks and quit Codex, then retry."));

    /// <summary>Revalidates and starts the desktop; never force-terminates existing processes.</summary>
    void Launch(CodexDesktopInstallation installation);
}

public interface IDesktopProcessSource
{
    IReadOnlyList<DesktopProcessInfo> ReadProcesses();
}

public interface IDesktopInstallationSource
{
    IReadOnlyList<string> GetCandidateExecutables(IReadOnlyList<DesktopProcessInfo> processes);
}

public interface IDesktopSignatureVerifier
{
    bool IsTrustedDesktopExecutable(string executablePath);
}

public interface IDesktopLauncher
{
    void Launch(string executablePath);
}

public interface IDesktopInfrastructureSource
{
    IReadOnlySet<int> GetIndependentProcessIds(CodexDesktopInstallation installation,
        IReadOnlyList<DesktopProcessInfo> processes);
}
