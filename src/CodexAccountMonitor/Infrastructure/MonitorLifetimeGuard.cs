using System.ComponentModel;
using System.Runtime.InteropServices;
using CodexAccountMonitor.Infrastructure.DesktopSwitching;

namespace CodexAccountMonitor.Infrastructure;

public interface IMonitorLifetimeGuard
{
    void AssertIndependent();
}

/// <summary>Read-only guard against quitting the process tree/job that owns this monitor.</summary>
public sealed class WindowsMonitorLifetimeGuard : IMonitorLifetimeGuard
{
    internal const string IndependentLaunchRequired =
        "Codex Lover is still running inside another app's process group and may close with Codex. " +
        "Exit Codex Lover and open it from the Windows Start menu, then retry. Your Codex sign-in was not changed.";

    public void AssertIndependent()
    {
        if (!IsIndependent()) throw new InvalidOperationException(IndependentLaunchRequired);
    }

    internal static object ReadDiagnostics()
    {
        var processes = new WindowsDesktopProcessSource().ReadProcesses();
        var current = processes.SingleOrDefault(p => p.ProcessId == Environment.ProcessId);
        var parent = processes.SingleOrDefault(p => p.ProcessId == current?.ParentProcessId);
        var queried = IsProcessInJob(GetCurrentProcess(), IntPtr.Zero, out var inJob);
        return new { queried, inJob, flags = inJob ? ReadJobFlags() : null,
            hasCodexAncestor = HasCodexAncestor(processes, Environment.ProcessId), current, parent };
    }

    public bool IsIndependent()
    {
        try
        {
            if (!IsProcessInJob(GetCurrentProcess(), IntPtr.Zero, out var inJob)) return false;
            var processes = new WindowsDesktopProcessSource().ReadProcesses();
            if (HasCodexAncestor(processes, Environment.ProcessId)) return false;
            if (!inJob) return true;
            return IsNonTerminatingExplorerJob(processes, Environment.ProcessId, ReadJobFlags(),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"));
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool IsNonTerminatingExplorerJob(IReadOnlyList<DesktopProcessInfo> processes,
        int currentProcessId, uint? flags, string systemExplorerPath)
    {
        // Windows can assign an Explorer-launched app a compatibility job (observed
        // BREAKAWAY_OK without KILL_ON_JOB_CLOSE). A job alone is not Codex ownership.
        // Only permit this case with the direct, readable, time-verified system shell
        // parent; a distant shell ancestor cannot prove nested launcher jobs safe.
        if (flags is null || (flags.Value & 0x2000) != 0) return false;
        var current = processes.SingleOrDefault(process => process.ProcessId == currentProcessId);
        if (current?.StartedAtFileTimeUtc is not long childStarted) return false;
        var parent = processes.SingleOrDefault(process => process.ProcessId == current.ParentProcessId);
        return parent?.StartedAtFileTimeUtc is long parentStarted && parentStarted <= childStarted
            && parent.Name.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)
            && parent.ExecutablePath is not null
            && string.Equals(Path.GetFullPath(parent.ExecutablePath), Path.GetFullPath(systemExplorerPath), StringComparison.OrdinalIgnoreCase);
    }

    private static uint? ReadJobFlags()
    {
        var size = Environment.Is64BitProcess ? 144 : 112; // JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            return QueryInformationJobObject(IntPtr.Zero, 9, buffer, size, out _)
                ? unchecked((uint)Marshal.ReadInt32(buffer, 16)) : null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static bool HasCodexAncestor(IReadOnlyList<DesktopProcessInfo> processes, int currentProcessId)
    {
        var byId = processes.ToDictionary(process => process.ProcessId);
        if (!byId.TryGetValue(currentProcessId, out var child)) return true;
        var visited = new HashSet<int> { currentProcessId };
        while (byId.TryGetValue(child.ParentProcessId, out var parent) && visited.Add(parent.ProcessId))
        {
            // A stale parent PID that was reused is not an ancestor of this process.
            if (parent.StartedAtFileTimeUtc is long parentStarted && child.StartedAtFileTimeUtc is long childStarted
                && parentStarted > childStarted) break;
            var name = Path.GetFileNameWithoutExtension(parent.Name);
            if (name.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase)
                || name.Equals("codex", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("codex-", StringComparison.OrdinalIgnoreCase)) return true;
            child = parent;
        }
        return false;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(IntPtr process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool result);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(IntPtr job, int infoClass, IntPtr information, int size, out int written);
}
