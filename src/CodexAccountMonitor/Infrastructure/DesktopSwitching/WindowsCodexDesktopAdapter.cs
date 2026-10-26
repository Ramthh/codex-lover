using System.ComponentModel;
using System.Security;

namespace CodexAccountMonitor.Infrastructure.DesktopSwitching;

public sealed class WindowsCodexDesktopAdapter : ICodexDesktopAdapter
{
    private readonly IDesktopProcessSource _processSource;
    private readonly IDesktopInstallationSource _installationSource;
    private readonly IDesktopSignatureVerifier _signatureVerifier;
    private readonly IDesktopLauncher _launcher;
    private readonly int _monitorProcessId;
    private readonly IDesktopQuitRequester _quitRequester;
    private readonly IDesktopInfrastructureSource _infrastructureSource;
    private readonly IMonitorLifetimeGuard _lifetimeGuard;

    public WindowsCodexDesktopAdapter()
        : this(new WindowsDesktopProcessSource(), new WindowsDesktopInstallationSource(),
            new OpenAiDesktopSignatureVerifier(), new WindowsDesktopLauncher(), Environment.ProcessId)
    {
    }

    public WindowsCodexDesktopAdapter(
        IDesktopProcessSource processSource,
        IDesktopInstallationSource installationSource,
        IDesktopSignatureVerifier signatureVerifier,
        IDesktopLauncher launcher,
        int monitorProcessId,
        IDesktopQuitRequester? quitRequester = null,
        IDesktopInfrastructureSource? infrastructureSource = null,
        IMonitorLifetimeGuard? lifetimeGuard = null)
    {
        _processSource = processSource;
        _installationSource = installationSource;
        _signatureVerifier = signatureVerifier;
        _launcher = launcher;
        _monitorProcessId = monitorProcessId;
        _quitRequester = quitRequester ?? new WindowsDesktopQuitRequester();
        _infrastructureSource = infrastructureSource ?? new WindowsDesktopInfrastructureSource();
        _lifetimeGuard = lifetimeGuard ?? new WindowsMonitorLifetimeGuard();
    }

    public CodexDesktopState Inspect(IReadOnlyCollection<int>? monitorOwnedProcessIds = null)
    {
        IReadOnlyList<DesktopProcessInfo> processes;
        try
        {
            processes = _processSource.ReadProcesses();
        }
        catch (Exception exception) when (IsInspectionFailure(exception))
        {
            return new(null, Array.Empty<DesktopProcessInfo>(),
                "Windows could not verify that Codex is closed. Try again before switching accounts.");
        }

        try
        {
            return InspectProcesses(processes, monitorOwnedProcessIds);
        }
        catch (Exception exception) when (IsInspectionFailure(exception))
        {
            return new(null, processes.Where(process => IsCodexProcess(process, null)).ToArray(),
                "Windows could not verify the Codex desktop installation. Try again before switching accounts.");
        }
    }

    private CodexDesktopState InspectProcesses(IReadOnlyList<DesktopProcessInfo> processes,
        IReadOnlyCollection<int>? monitorOwnedProcessIds = null)
    {
        var installation = _installationSource.GetCandidateExecutables(processes)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(_signatureVerifier.IsTrustedDesktopExecutable)
            .Select(path => new CodexDesktopInstallation(path, "Codex"))
            .FirstOrDefault();
        var desktopTree = FindDesktopProcesses(processes, includeUnverified: true);
        var ignored = FindMonitorProcesses(processes, monitorOwnedProcessIds, desktopTree);
        // The packaged LocalSystem sandbox service is independent infrastructure, not
        // a user account session. Exempt only a freshly verified SCM/manifest/signature
        // identity, never an executable name or an unverified descendant.
        if (installation is not null)
            ignored.UnionWith(_infrastructureSource.GetIndependentProcessIds(installation, processes)
                .Where(id => !desktopTree.Contains(id)));
        var blockers = processes.Where(process => !ignored.Contains(process.ProcessId)
            && (IsCodexProcess(process, installation) || desktopTree.Contains(process.ProcessId)))
            .OrderBy(process => process.ProcessId).ToArray();
        return installation is null
            ? new(null, blockers, "A trusted Codex desktop installation was not found. Install or update the official Codex desktop app.")
            : new(installation, blockers, null);
    }

    public async Task<CodexDesktopState> RequestCloseAndWaitAsync(CodexDesktopState expected, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
        cancellationToken.ThrowIfCancellationRequested();
        var processes = _processSource.ReadProcesses();
        var initial = InspectProcesses(processes);
        EnsureInstallationUnchanged(expected, initial);
        if (initial.CanSwitch) return initial;
        // A tool-launched monitor can be in Codex's kill-on-close Windows job.
        // Never quit its owner before an independent Explorer launch succeeds.
        _lifetimeGuard.AssertIndependent();
        var desktopTree = FindDesktopProcesses(processes);
        if (initial.BlockingProcesses.Any(process => !desktopTree.Contains(process.ProcessId)))
            throw new InvalidOperationException("Another Codex CLI or an unverified Codex process is still running. Finish and close it before switching. The desktop was not asked to quit.");
        var desktopRoots = FindTrustedDesktopProcesses(processes)
            .Where(process => !desktopTree.Contains(process.ParentProcessId)).ToArray();
        if (desktopRoots.Length == 0)
            throw new InvalidOperationException("A Codex desktop window owner could not be verified. Quit Codex normally, then retry.");
        foreach (var root in desktopRoots)
        {
            var original = expected.BlockingProcesses.SingleOrDefault(process => process.ProcessId == root.ProcessId);
            if (original is null || !SameProcessIdentity(original, root))
                throw new InvalidOperationException("Codex changed since the switch dialog opened. Retry the switch.");
        }
        var tracked = processes.Where(process => desktopTree.Contains(process.ProcessId)).ToDictionary(process => process.ProcessId);
        var started = System.Diagnostics.Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            foreach (var root in desktopRoots)
            {
                deadline.Token.ThrowIfCancellationRequested();
                var fresh = _processSource.ReadProcesses();
                TrackObservedDescendants(fresh, tracked);
                var current = InspectProcesses(fresh);
                EnsureInstallationUnchanged(expected, current);
                var freshDesktopTree = FindDesktopProcesses(fresh);
                if (current.BlockingProcesses.Any(process => !freshDesktopTree.Contains(process.ProcessId)
                    && !IsTrackedProcess(tracked, process)))
                    throw new InvalidOperationException("Another Codex process started before quitting. Close it and retry the switch.");
                var match = fresh.SingleOrDefault(process => process.ProcessId == root.ProcessId);
                if (match is null) continue;
                if (!SameProcessIdentity(root, match)) throw new InvalidOperationException("Codex restarted before quitting. Retry the switch.");
                await _quitRequester.RequestQuitAsync(root, () =>
                {
                    _lifetimeGuard.AssertIndependent();
                    var snapshot = _processSource.ReadProcesses();
                    TrackObservedDescendants(snapshot, tracked);
                    var candidate = snapshot.SingleOrDefault(process => process.ProcessId == root.ProcessId);
                    var knownDesktop = FindDesktopProcesses(snapshot);
                    if (candidate is null)
                        throw new DesktopQuitValidationException("desktop_closed_before_quit", "Codex closed before its Quit command was sent. Retry the switch; your sign-in was not changed.");
                    if (!SameProcessIdentity(root, candidate))
                        throw new DesktopQuitValidationException("desktop_identity_changed", "Codex restarted or Windows could not confirm its process identity. Retry the switch; your sign-in was not changed.");
                    if (!IsTrustedDesktopProcess(candidate))
                        throw new DesktopQuitValidationException("desktop_signature_unverified", "Windows could not verify Codex's publisher before quitting. Update or reopen the official Codex app, then retry. Your sign-in was not changed.");
                    var unexpected = InspectProcesses(snapshot).BlockingProcesses
                        .FirstOrDefault(process => !knownDesktop.Contains(process.ProcessId) && !IsTrackedProcess(tracked, process));
                    if (unexpected is not null)
                    {
                        var metadataMissing = unexpected.ExecutablePath is null || unexpected.StartedAtFileTimeUtc is null;
                        throw new DesktopQuitValidationException(metadataMissing ? "codex_process_metadata_unavailable" : "another_codex_process_started",
                            metadataMissing ? "Windows could not verify a background Codex process. Wait a moment and retry; your sign-in was not changed."
                                : "Another Codex command-line or background process started before quitting. Close it and retry; your sign-in was not changed.");
                    }
                    return true;
                }, deadline.Token).ConfigureAwait(false);
            }
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                var fresh = _processSource.ReadProcesses();
                TrackObservedDescendants(fresh, tracked);
                var current = InspectProcesses(fresh);
                EnsureInstallationUnchanged(expected, current);
                var remaining = current.BlockingProcesses.Concat(fresh.Where(process => IsTrackedProcess(tracked, process)))
                    .DistinctBy(process => process.ProcessId).ToArray();
                if (remaining.Length == 0) return current with { BlockingProcesses = remaining };
                if (started.Elapsed >= timeout) break;
                await Task.Delay(TimeSpan.FromMilliseconds(200), deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { /* A deadline is a refused close, never permission to terminate processes. */ }
        throw new TimeoutException("Codex did not fully quit in time. Check for a confirmation or remaining task, quit Codex normally, then retry. Your sign-in was not changed.");
    }

    private static bool IsTrackedProcess(IReadOnlyDictionary<int, DesktopProcessInfo> tracked, DesktopProcessInfo candidate)
        => tracked.TryGetValue(candidate.ProcessId, out var original)
           && (original.StartedAtFileTimeUtc is null || candidate.StartedAtFileTimeUtc is null
               || original.StartedAtFileTimeUtc == candidate.StartedAtFileTimeUtc);

    private static bool SameProcessIdentity(DesktopProcessInfo first, DesktopProcessInfo second)
        => first.ProcessId == second.ProcessId && first.StartedAtFileTimeUtc is long started
            && second.StartedAtFileTimeUtc == started && first.ExecutablePath is not null
            && string.Equals(first.ExecutablePath, second.ExecutablePath, StringComparison.OrdinalIgnoreCase);

    private static void EnsureInstallationUnchanged(CodexDesktopState expected, CodexDesktopState current)
    {
        if (expected.Problem is not null || expected.Installation is null || current.Problem is not null || current.Installation is null
            || !string.Equals(expected.Installation.ExecutablePath, current.Installation.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(current.Problem ?? "The Codex installation changed. Retry the switch.");
    }

    private bool IsTrustedDesktopProcess(DesktopProcessInfo process) => process.ExecutablePath is not null
        && (process.Name.Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase) || process.Name.Equals("Codex.exe", StringComparison.OrdinalIgnoreCase))
        && _signatureVerifier.IsTrustedDesktopExecutable(process.ExecutablePath);

    private HashSet<int> FindDesktopProcesses(IReadOnlyList<DesktopProcessInfo> processes, bool includeUnverified = false)
    {
        var retained = FindTrustedDesktopProcesses(processes).Select(process => process.ProcessId).ToHashSet();
        if (includeUnverified) retained.UnionWith(processes.Where(IsPotentialDesktopProcess).Select(process => process.ProcessId));
        var byId = processes.ToDictionary(process => process.ProcessId);
        bool added;
        do
        {
            added = false;
            foreach (var process in processes)
                // The monitor may itself have been launched by a Codex task. It and
                // its own worker branch must survive; separately launched desktops
                // remain retained because they are independently verified roots.
                if (process.ProcessId != _monitorProcessId && retained.Contains(process.ParentProcessId)
                    && byId.TryGetValue(process.ParentProcessId, out var parent)
                    && (parent.StartedAtFileTimeUtc is long parentStarted && process.StartedAtFileTimeUtc is long childStarted
                        ? childStarted >= parentStarted : includeUnverified))
                    added |= retained.Add(process.ProcessId);
        } while (added);
        return retained;
    }

    private static bool IsPotentialDesktopProcess(DesktopProcessInfo process)
    {
        if (process.Name.Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase)) return true;
        if (!process.Name.Equals("Codex.exe", StringComparison.OrdinalIgnoreCase)) return false;
        if (process.ExecutablePath is null) return true;
        var path = process.ExecutablePath.Replace('/', '\\');
        return path.EndsWith("\\app\\Codex.exe", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("\\Codex\\Codex.exe", StringComparison.OrdinalIgnoreCase)
            || File.Exists(Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, "resources", "app.asar"));
    }

    private void TrackObservedDescendants(IReadOnlyList<DesktopProcessInfo> processes, Dictionary<int, DesktopProcessInfo> tracked)
    {
        var verifiedTree = FindDesktopProcesses(processes);
        foreach (var process in processes.Where(process => verifiedTree.Contains(process.ProcessId))) tracked[process.ProcessId] = process;
        var byId = processes.ToDictionary(process => process.ProcessId);
        bool added;
        do
        {
            added = false;
            foreach (var process in processes)
            {
                if (process.ProcessId == _monitorProcessId || tracked.ContainsKey(process.ProcessId)
                    || !tracked.TryGetValue(process.ParentProcessId, out var parent)) continue;
                if (byId.TryGetValue(parent.ProcessId, out var currentParent) && !SameProcessIdentity(parent, currentParent)) continue;
                if (parent.StartedAtFileTimeUtc is long parentStarted && process.StartedAtFileTimeUtc is long childStarted && childStarted < parentStarted) continue;
                // Keep a helper observed during shutdown blocking after its parent exits.
                // Missing metadata cannot prove that a previously observed branch is gone.
                tracked[process.ProcessId] = process;
                added = true;
            }
        } while (added);
    }

    private IReadOnlyList<DesktopProcessInfo> FindTrustedDesktopProcesses(IReadOnlyList<DesktopProcessInfo> processes)
    {
        var trustedPaths = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        return processes.Where(process =>
        {
            if (process.ExecutablePath is null || (!process.Name.Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase)
                && !process.Name.Equals("Codex.exe", StringComparison.OrdinalIgnoreCase))) return false;
            if (!trustedPaths.TryGetValue(process.ExecutablePath, out var trusted))
                trustedPaths[process.ExecutablePath] = trusted = _signatureVerifier.IsTrustedDesktopExecutable(process.ExecutablePath);
            return trusted;
        }).ToArray();
    }

    public void Launch(CodexDesktopInstallation installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (!_signatureVerifier.IsTrustedDesktopExecutable(installation.ExecutablePath))
        {
            throw new InvalidOperationException("The Codex desktop executable could not be verified as signed by OpenAI. Reinstall or update Codex.");
        }

        _launcher.Launch(installation.ExecutablePath);
    }

    private HashSet<int> FindMonitorProcesses(
        IReadOnlyList<DesktopProcessInfo> processes,
        IReadOnlyCollection<int>? monitorOwnedProcessIds,
        IReadOnlySet<int> desktopProcesses)
    {
        var ignored = new HashSet<int>(monitorOwnedProcessIds ?? Array.Empty<int>()) { _monitorProcessId };
        ignored.ExceptWith(desktopProcesses);
        var byId = processes.GroupBy(process => process.ProcessId).ToDictionary(group => group.Key, group => group.First());
        bool added;
        do
        {
            added = false;
            foreach (var process in processes)
            {
                // The start-time check prevents a reused parent PID from turning an unrelated
                // orphaned process into a monitor-owned child. Missing metadata stays blocking.
                if (!desktopProcesses.Contains(process.ProcessId) && !ignored.Contains(process.ProcessId) && ignored.Contains(process.ParentProcessId) &&
                    byId.TryGetValue(process.ParentProcessId, out var parent) &&
                    parent.StartedAtFileTimeUtc is long parentStarted &&
                    process.StartedAtFileTimeUtc is long childStarted && childStarted >= parentStarted)
                {
                    added |= ignored.Add(process.ProcessId);
                }
            }
        } while (added);
        return ignored;
    }

    private static bool IsCodexProcess(DesktopProcessInfo process, CodexDesktopInstallation? installation)
    {
        var name = Path.GetFileNameWithoutExtension(process.Name);
        // Do not depend on an accessible executable path, signature, command line, or session ID:
        // a process with inaccessible metadata must not be mistaken for a closed app.
        return name.Equals("codex", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("codex-", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase) ||
               IsInsideDesktopInstallation(process.ExecutablePath, installation);
    }

    private static bool IsInsideDesktopInstallation(string? executablePath, CodexDesktopInstallation? installation)
    {
        if (executablePath is null || installation is null || !Path.IsPathFullyQualified(executablePath)) return false;
        var directory = Path.GetDirectoryName(Path.GetFullPath(installation.ExecutablePath));
        if (directory is null) return false;
        var prefix = Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar;
        // A package's redirected LocalCache also contains unrelated SDKs and compilers.
        // Only the verified app directory, with a directory boundary, owns app helpers.
        return Path.GetFullPath(executablePath).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInspectionFailure(Exception exception) => exception is
        Win32Exception or IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException;
}
