using System.Diagnostics;

namespace CodexAccountMonitor.Infrastructure.DesktopSwitching;

/// <summary>A successful shell request is not proof that Electron opened a window.</summary>
public sealed class WindowsDesktopLauncher : IDesktopLauncher
{
    private readonly IDesktopProcessSource _processes;
    private readonly IDesktopSignatureVerifier _signatures;
    private readonly IDesktopWindowController _windows;
    private readonly Action<ProcessStartInfo> _start;
    private readonly Func<string, bool> _selectCodexView;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _retryAfter;

    public WindowsDesktopLauncher() : this(new WindowsDesktopProcessSource(),
        new OpenAiDesktopSignatureVerifier(), new WindowsDesktopWindowController(), Start,
        TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(5), new WindowsCodexViewLaunchCapability().Supports) { }

    internal WindowsDesktopLauncher(IDesktopProcessSource processes, IDesktopSignatureVerifier signatures,
        IDesktopWindowController windows, Action<ProcessStartInfo> start, TimeSpan timeout, TimeSpan retryAfter,
        Func<string, bool> selectCodexView)
    {
        _processes = processes; _signatures = signatures; _windows = windows; _start = start;
        _timeout = timeout; _retryAfter = retryAfter;
        _selectCodexView = selectCodexView;
    }

    public void Launch(string executablePath)
    {
        VerifyTrustedExecutable(executablePath);
        // Resolve intent once after trust validation. A bounded activation retry must
        // request the same product, rather than re-reading changing preferences.
        var argument = _selectCodexView(executablePath) ? WindowsCodexViewLaunchCapability.CodexViewArgument : null;
        _start(CreateStartInfo(executablePath, argument));
        var elapsed = Stopwatch.StartNew();
        var retried = false;
        while (elapsed.Elapsed < _timeout)
        {
            foreach (var process in _processes.ReadProcesses().Where(p =>
                p.StartedAtFileTimeUtc is not null && string.Equals(p.ExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var window in _windows.GetWindows(process.ProcessId).OrderByDescending(w => w.Area))
                {
                    // A reused PID must never authorize showing an unrelated application's window.
                    var current = _processes.ReadProcesses().SingleOrDefault(p => p.ProcessId == process.ProcessId);
                    if (current?.StartedAtFileTimeUtc != process.StartedAtFileTimeUtc
                        || !string.Equals(current?.ExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase)) break;
                    _windows.ShowAndActivate(window, process.ProcessId);
                    if (_windows.IsVisible(window, process.ProcessId)) return;
                }
            }
            // A second activation also wakes a tray-only single-instance desktop.
            // It is bounded, still uses the same verified executable, and never closes anything.
            if (!retried && elapsed.Elapsed >= _retryAfter)
            {
                VerifyTrustedExecutable(executablePath);
                _start(CreateStartInfo(executablePath, argument));
                retried = true;
            }
            Thread.Sleep(100);
        }
        throw new InvalidOperationException("Codex was asked to open, but a visible desktop window did not appear. Select Open Codex to retry opening it.");
    }

    private void VerifyTrustedExecutable(string executablePath)
    {
        if (!_signatures.IsTrustedDesktopExecutable(executablePath))
            throw new InvalidOperationException("The Codex desktop executable could not be verified. Update or reinstall the official Codex app.");
    }

    private static ProcessStartInfo CreateStartInfo(string executablePath, string? argument)
    {
        var request = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath),
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal
        };
        if (argument is not null) request.ArgumentList.Add(argument);
        return request;
    }

    private static void Start(ProcessStartInfo request) { using var process = Process.Start(request); }
}
