using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Xml;
using System.Xml.Linq;

namespace CodexAccountMonitor.Infrastructure.DesktopSwitching;

/// <summary>
/// Identifies the package's machine-lifetime sandbox provisioning service. This
/// excludes only the fresh SCM-owned service PID, never children or CLI workers.
/// All native handles are opened with query-only access.
/// </summary>
public sealed class WindowsDesktopInfrastructureSource : IDesktopInfrastructureSource
{
    internal const string ServiceName = "CodexSandboxService.OpenAI.Codex";
    internal const string ServiceExecutable = "codex-windows-sandbox-service.exe";
    private readonly Func<SandboxServiceRegistration?> _readRegistration;
    private readonly Func<string, bool> _verifySignature;
    private readonly Func<string, bool> _verifyManifest;

    public WindowsDesktopInfrastructureSource()
        : this(ReadRegistration, OpenAiDesktopSignatureVerifier.HasTrustedOpenAiSignature, VerifyManifest) { }

    internal WindowsDesktopInfrastructureSource(Func<SandboxServiceRegistration?> readRegistration,
        Func<string, bool> verifySignature, Func<string, bool> verifyManifest)
    {
        _readRegistration = readRegistration;
        _verifySignature = verifySignature;
        _verifyManifest = verifyManifest;
    }

    public IReadOnlySet<int> GetIndependentProcessIds(CodexDesktopInstallation installation,
        IReadOnlyList<DesktopProcessInfo> processes)
    {
        try
        {
            if (!processes.Any(process => process.Name.Equals(ServiceExecutable, StringComparison.OrdinalIgnoreCase))) return new HashSet<int>();
            var registration = _readRegistration();
            if (registration is not { IsRunning: true, IsAutoStart: true, IsLocalSystem: true, ProcessId: > 0 }) return new HashSet<int>();
            var process = processes.SingleOrDefault(candidate => candidate.ProcessId == registration.ProcessId);
            if (process is null || !process.Name.Equals(ServiceExecutable, StringComparison.OrdinalIgnoreCase)) return new HashSet<int>();
            var appDirectory = Path.GetDirectoryName(Path.GetFullPath(installation.ExecutablePath))!;
            var expected = Path.Combine(appDirectory, "resources", ServiceExecutable);
            // No arbitrary arguments or shell parsing: the registered image must be
            // exactly the declared package binary (optionally enclosed in quotes).
            var configured = registration.BinaryPath.Trim();
            if (configured.Length >= 2 && configured[0] == '"' && configured[^1] == '"') configured = configured[1..^1];
            if (!Path.IsPathFullyQualified(configured) || !string.Equals(Path.GetFullPath(configured), expected, StringComparison.OrdinalIgnoreCase)) return new HashSet<int>();
            if (process.ExecutablePath is not null && !string.Equals(Path.GetFullPath(process.ExecutablePath), expected, StringComparison.OrdinalIgnoreCase)) return new HashSet<int>();
            if (!_verifyManifest(appDirectory) || !_verifySignature(expected)) return new HashSet<int>();
            // Recheck the actual SCM owner/status/configuration after filesystem checks.
            // A stale PID, reconfigured service, or stopped service grants no exemption.
            if (_readRegistration() != registration) return new HashSet<int>();
            return new HashSet<int> { registration.ProcessId };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException or Win32Exception or XmlException)
        {
            return new HashSet<int>();
        }
    }

    internal static bool VerifyManifest(string appDirectory)
    {
        var packageDirectory = Path.GetDirectoryName(appDirectory);
        if (packageDirectory is null) return false;
        using var reader = XmlReader.Create(Path.Combine(packageDirectory, "AppxManifest.xml"),
            new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var manifest = XDocument.Load(reader);
        XNamespace desktop6 = "http://schemas.microsoft.com/appx/manifest/desktop/windows10/6";
        return manifest.Descendants(desktop6 + "Extension").Any(extension =>
            (string?)extension.Attribute("Category") == "windows.service"
            && string.Equals(((string?)extension.Attribute("Executable"))?.Replace('\\', '/'),
                "app/resources/" + ServiceExecutable, StringComparison.OrdinalIgnoreCase)
            && extension.Elements(desktop6 + "Service").Any(service =>
                (string?)service.Attribute("Name") == ServiceName
                && (string?)service.Attribute("StartupType") == "auto"
                && (string?)service.Attribute("StartAccount") == "localSystem"));
    }

    internal sealed record SandboxServiceRegistration(int ProcessId, string BinaryPath,
        bool IsRunning, bool IsAutoStart, bool IsLocalSystem);

    private static SandboxServiceRegistration? ReadRegistration()
    {
        var manager = OpenSCManagerW(null, null, 0x0001); // SC_MANAGER_CONNECT
        if (manager == IntPtr.Zero) return null;
        try
        {
            var service = OpenServiceW(manager, ServiceName, 0x0001 | 0x0004); // QUERY_CONFIG | QUERY_STATUS
            if (service == IntPtr.Zero) return null;
            try
            {
                if (!QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<ServiceStatusProcess>(), out _)) return null;
                QueryServiceConfigW(service, IntPtr.Zero, 0, out var bytesNeeded);
                if (Marshal.GetLastWin32Error() != 122 || bytesNeeded <= 0 || bytesNeeded > 65536) return null;
                var buffer = Marshal.AllocHGlobal(bytesNeeded);
                try
                {
                    if (!QueryServiceConfigW(service, buffer, bytesNeeded, out _)) return null;
                    var config = Marshal.PtrToStructure<ServiceConfig>(buffer);
                    var image = Marshal.PtrToStringUni(config.BinaryPath);
                    var account = Marshal.PtrToStringUni(config.ServiceStartName);
                    return image is null ? null : new(checked((int)status.ProcessId), image,
                        status.CurrentState == 4, config.StartType == 2,
                        string.Equals(account, "LocalSystem", StringComparison.OrdinalIgnoreCase));
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode,
            CheckPoint, WaitHint, ProcessId, ServiceFlags;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceConfig
    {
        public uint ServiceType, StartType, ErrorControl;
        public IntPtr BinaryPath, LoadOrderGroup;
        public uint TagId;
        public IntPtr Dependencies, ServiceStartName, DisplayName;
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManagerW(string? machineName, string? databaseName, uint desiredAccess);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenServiceW(IntPtr manager, string serviceName, uint desiredAccess);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(IntPtr service, int infoLevel, out ServiceStatusProcess status, int bufferSize, out int bytesNeeded);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfigW(IntPtr service, IntPtr config, int bufferSize, out int bytesNeeded);
    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr service);
}
