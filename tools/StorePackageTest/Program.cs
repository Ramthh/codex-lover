using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

internal static class Program
{
    const string Input = @"C:\StoreTest\Input";
    const string Output = @"C:\StoreTest\Results";
    const string Family = "RamiforIT.CodexLover_s9a7ne5wq60b6";
    static readonly List<string> Checks = [];
    static string? Error;
    static string? ExpectedVersion;
    static string? CandidateSha256;
    static string? ExpectedRuntimeVersion;
    static string? RuntimeExecutableSha256;
    static long CandidateBytes;
    static readonly List<object> StartupLaunches = [];
    static string ExpectedFirstWindow = "Codex Lover · Membership";
    static bool RequireContainedStartup;

    [STAThread]
    static int Main(string[] arguments)
    {
        if (Environment.UserName != "WDAGUtilityAccount" || Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) != @"C:\Users\WDAGUtilityAccount")
            throw new InvalidOperationException("Only run this test inside disposable Windows Sandbox.");
        foreach (var argument in arguments)
        {
            if (argument == "--require-contained-startup")
            {
                RequireContainedStartup = true;
                continue;
            }
            if (!argument.StartsWith("--expected-first-window=", StringComparison.Ordinal))
                throw new ArgumentException("Unknown test argument.");
            ExpectedFirstWindow = argument["--expected-first-window=".Length..];
            if (string.IsNullOrWhiteSpace(ExpectedFirstWindow))
                throw new ArgumentException("Expected first window title cannot be empty.");
        }
        Directory.CreateDirectory(Output);
        if (Directory.EnumerateFileSystemEntries(Output).Any())
            throw new InvalidOperationException("Results must be empty. Create a new kit to preserve previous test evidence.");
        Save("running");
        try
        {
            Assert(string.IsNullOrWhiteSpace(Ps("Get-AppxPackage -Name RamiforIT.CodexLover | Select-Object -ExpandProperty PackageFullName")), "Clean guest has no previous package");
            var expected = File.ReadAllText(Path.Combine(Input, "candidate.sha256")).Trim();
            Assert(Regex.IsMatch(expected, "\\A[0-9A-Fa-f]{64}\\z"), "Expected candidate SHA-256 is valid");
            var candidate = Path.Combine(Input, "candidate.msix");
            CandidateBytes = new FileInfo(candidate).Length;
            using (var file = File.OpenRead(candidate))
                CandidateSha256 = Convert.ToHexString(SHA256.HashData(file));
            Assert(CandidateSha256.Equals(expected, StringComparison.OrdinalIgnoreCase), "Candidate matches exact package hash");
            ExpectedVersion = ReadExpectedVersion(candidate);
            Assert(true, "Candidate manifest and expected version agree: " + ExpectedVersion);
            ReadExpectedRuntime(candidate);
            var codexHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            Assert(!Directory.Exists(codexHome) && !CodexIsRunning(), "Clean guest has no Codex home or running Codex process");
            // The certificate and private key exist only in this disposable guest.
            // Only a guest-local COPY is signed. The submission package stays unsigned.
            Ps("$cert = New-SelfSignedCertificate -Type Custom -Subject 'CN=F6236B59-AAB3-46EA-BBF5-A90C296BCE3F' -KeyUsage DigitalSignature -FriendlyName 'Disposable Codex Lover MSIX test' -CertStoreLocation 'Cert:\\CurrentUser\\My' -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3','2.5.29.19={text}'); Export-Certificate -Cert $cert -FilePath C:\\StoreTest\\test.cer | Out-Null; Import-Certificate -FilePath C:\\StoreTest\\test.cer -CertStoreLocation Cert:\\LocalMachine\\TrustedPeople | Out-Null; $cert.Thumbprint | Set-Content C:\\StoreTest\\thumbprint.txt");
            File.Copy(Path.Combine(Input, "candidate.msix"), @"C:\StoreTest\candidate.msix");
            Run(Path.Combine(Input,"signtool.exe"), ["sign","/sha1",File.ReadAllText(@"C:\StoreTest\thumbprint.txt").Trim(),"/s","My","/fd","SHA256",@"C:\StoreTest\candidate.msix"]);
            Ps("Add-AppxPackage -Path C:\\StoreTest\\candidate.msix");
            Assert(Ps("(Get-AppxPackage -Name RamiforIT.CodexLover).PackageFamilyName").Trim()==Family,"MSIX installs with official identity");
            var first = Diagnose("initial");
            Assert(Ps("(Get-AppxPackage -Name RamiforIT.CodexLover).Version.ToString()").Trim() == ExpectedVersion, "Registered package version matches candidate " + ExpectedVersion);
            Assert(first.GetProperty("storePackage").GetBoolean(), "Packaged launch detects Store distribution");
            Assert(first.GetProperty("version").GetString() == ExpectedVersion, "Installed app assembly version matches candidate " + ExpectedVersion);
            Assert(first.GetProperty("accountCount").GetInt32() == 0, "Clean installation has zero connected accounts");
            Assert(first.GetProperty("switcherAccess").ValueKind == JsonValueKind.Null, "Diagnostics do not perform authenticated account or usage checks");
            Assert(!first.GetProperty("switcher").GetProperty("desktopFound").GetBoolean(), "Clean guest has no Codex desktop installation");
            var installation = first.GetProperty("codexInstallation");
            var bundledExecutable = Path.Combine(Path.GetDirectoryName(first.GetProperty("executable").GetString())!, "codex-runtime", "bin", "codex.exe");
            Assert(installation.GetProperty("ready").GetBoolean()
                && installation.GetProperty("supportChecked").GetBoolean()
                && installation.GetProperty("version").GetString() == ExpectedRuntimeVersion
                && string.Equals(installation.GetProperty("executablePath").GetString(), bundledExecutable, StringComparison.OrdinalIgnoreCase),
                "Bundled signed Codex runtime passes isolated version and App Server help checks: " + ExpectedRuntimeVersion);
            Assert(installation.GetProperty("isolatedProtocolReady").GetBoolean(),
                "Bundled runtime initializes isolated App Server and reports no authenticated account");
            using (var runtimeFile = File.OpenRead(bundledExecutable))
                Assert(Convert.ToHexString(SHA256.HashData(runtimeFile)).Equals(RuntimeExecutableSha256, StringComparison.OrdinalIgnoreCase),
                    "Installed Codex runtime matches reviewed vendor executable hash");
            Assert(!Directory.Exists(codexHome) && !CodexIsRunning(), "Isolated runtime checks leave shared Codex home absent and no Codex process running");
            Assert(first.GetProperty("startWithWindows").GetBoolean(), "Packaged Windows startup enabled by default");
            Assert(first.GetProperty("startupCommand").ValueKind==JsonValueKind.Null,"Store app does not create a legacy Run entry");
            // COM activation directly from a test process deliberately has the test
            // runner as its parent. Check the same Explorer handoff used by startup.
            var independent = Diagnose("explorer", first.GetProperty("executable").GetString());
            Assert(independent.GetProperty("storePackage").GetBoolean(), "Explorer handoff preserves Store identity");
            Assert(independent.GetProperty("independentLifetime").GetBoolean(),"Explorer launch satisfies independent switching lifetime guard");
            var stable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex-lover", "appdata", "CodexAccountMonitor");
            // Let the real startup create its migration marker and settings. A bare
            // folder without that marker intentionally triggers preservation protection.
            new CodexAccountMonitor.Infrastructure.ExplorerLaunchBroker().LaunchAsync(first.GetProperty("executable").GetString()!, []).GetAwaiter().GetResult();
            using (var running = WaitForNormalWindow("clean-explorer", first.GetProperty("executable").GetString()!))
            {
                Assert(File.Exists(Path.Combine(stable,"storage-migration.json")), "Normal Store launch creates stable storage and opens the expected first window");
                WaitForReviewerClose(running, "clean-explorer");
            }
            var overriddenLaunch = new ProcessStartInfo(first.GetProperty("executable").GetString()!)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(first.GetProperty("executable").GetString())!
            };
            // Synthetic values are added only to this new child process. The guest
            // Explorer, harness, user/machine environment and credentials are unchanged.
            foreach (var name in new[] { "OPENAI_API_KEY", "CODEX_API_KEY", "CODEX_ACCESS_TOKEN" })
                overriddenLaunch.Environment[name] = "store-test-synthetic-value";
            using (var initialProcess = Process.Start(overriddenLaunch)
                ?? throw new InvalidOperationException("Synthetic credential-override startup did not create a process."))
            using (var running = WaitForNormalWindow("synthetic-api-overrides", first.GetProperty("executable").GetString()!))
            {
                Assert(File.Exists(Path.Combine(stable,"storage-migration.json")), "Packaged normal startup opens with three unrelated inherited API credentials");
                Assert(!Directory.Exists(codexHome) && !CodexIsRunning(), "Synthetic-override startup does not create shared Codex credentials or start Codex");
                WaitForReviewerClose(running, "synthetic-api-overrides");
            }
            using (var jobLaunch = new ContainedProcessLaunch(first.GetProperty("executable").GetString()!))
            using (var running = WaitForNormalWindow("contained-harness-job", first.GetProperty("executable").GetString()!))
            {
                var retainedJob = running.Id == jobLaunch.ProcessId && jobLaunch.Contains(running);
                StartupLaunches.Add(new { scenario = "contained-harness-job", originalProcessId = jobLaunch.ProcessId,
                    observedProcessId = running.Id, retainedJob, required = RequireContainedStartup });
                Assert(!RequireContainedStartup || retainedJob,
                    "Normal app monitoring opens within its original test harness job without an Explorer relaunch");
                Assert(!Directory.Exists(codexHome) && !CodexIsRunning(), "Contained normal startup leaves shared Codex home absent and Codex stopped");
                WaitForReviewerClose(running, "contained-harness-job");
            }
            File.WriteAllText(Path.Combine(stable,"store-test-preservation.txt"),"synthetic-data-only");
            Ps("Get-AppxPackage -Name RamiforIT.CodexLover | Remove-AppxPackage");
            Assert(string.IsNullOrWhiteSpace(Ps("Get-AppxPackage -Name RamiforIT.CodexLover | Select-Object -ExpandProperty PackageFullName")), "Uninstall removes Store package registration");
            Assert(File.ReadAllText(Path.Combine(stable,"store-test-preservation.txt"))=="synthetic-data-only", "Uninstall preserves stable user data");
            Ps("Add-AppxPackage -Path C:\\StoreTest\\candidate.msix");
            var again=Diagnose("reinstalled");
            Assert(again.GetProperty("dataRoot").GetString()==stable, "Reinstalled package uses preserved stable data folder");
            Assert(again.GetProperty("version").GetString() == ExpectedVersion, "Reinstalled app retains candidate version " + ExpectedVersion);
            using (var file = File.OpenRead(candidate))
                Assert(Convert.ToHexString(SHA256.HashData(file)).Equals(CandidateSha256, StringComparison.OrdinalIgnoreCase), "Original mapped package hash is unchanged after lifecycle test");
            Save("passed");
            return 0;
        }
        catch(Exception ex) { Error=ex.ToString(); Save("failed"); return 1; }
    }

    static string ReadExpectedVersion(string candidate)
    {
        using var package = ZipFile.OpenRead(candidate);
        var manifestEntry = package.GetEntry("AppxManifest.xml")
            ?? throw new InvalidDataException("Candidate MSIX has no AppxManifest.xml.");
        using var manifestStream = manifestEntry.Open();
        var manifest = XDocument.Load(manifestStream);
        var identity = manifest.Root?.Elements().SingleOrDefault(element => element.Name.LocalName == "Identity")
            ?? throw new InvalidDataException("Candidate manifest has no Identity.");
        if ((string?)identity.Attribute("Name") != "RamiforIT.CodexLover"
            || (string?)identity.Attribute("Publisher") != "CN=F6236B59-AAB3-46EA-BBF5-A90C296BCE3F")
            throw new InvalidDataException("Candidate manifest does not contain the expected Store identity.");
        var manifestVersion = RequirePackageVersion((string?)identity.Attribute("Version"), "candidate manifest");
        var versionFile = Path.Combine(Input, "candidate.version.txt");
        if (!File.Exists(versionFile)) return manifestVersion;
        var declaredVersion = RequirePackageVersion(File.ReadAllText(versionFile).Trim(), "candidate.version.txt");
        if (declaredVersion != manifestVersion)
            throw new InvalidDataException("candidate.version.txt does not match the candidate manifest version.");
        return declaredVersion;
    }

    static string RequirePackageVersion(string? value, string source)
    {
        if (value is null || !Regex.IsMatch(value, "\\A[0-9]+\\.[0-9]+\\.[0-9]+\\.[0-9]+\\z")
            || !Version.TryParse(value, out var version)
            || version.Major is < 1 or > ushort.MaxValue || version.Minor > ushort.MaxValue
            || version.Build > ushort.MaxValue || version.Revision > ushort.MaxValue)
            throw new InvalidDataException("A valid four-part MSIX version is required in " + source + ".");
        return version.ToString(4);
    }

    static void ReadExpectedRuntime(string candidate)
    {
        using var package = ZipFile.OpenRead(candidate);
        var runtimeManifest = package.GetEntry("app/codex-runtime/runtime-manifest.json")
            ?? throw new InvalidDataException("Candidate package has no pinned runtime manifest.");
        using var stream = runtimeManifest.Open();
        using var document = JsonDocument.Parse(stream);
        var runtime = document.RootElement;
        ExpectedRuntimeVersion = runtime.GetProperty("version").GetString();
        if (ExpectedRuntimeVersion is null || !Regex.IsMatch(ExpectedRuntimeVersion, "\\A[0-9]+\\.[0-9]+\\.[0-9]+\\z"))
            throw new InvalidDataException("Candidate runtime manifest has an invalid version.");
        var entrypoint = runtime.GetProperty("files").EnumerateArray().Single(file => file.GetProperty("path").GetString() == "bin/codex.exe");
        RuntimeExecutableSha256 = entrypoint.GetProperty("sha256").GetString();
        Assert(RuntimeExecutableSha256 is not null && Regex.IsMatch(RuntimeExecutableSha256, "\\A[0-9A-Fa-f]{64}\\z")
            && entrypoint.GetProperty("openAiSigned").GetBoolean(), "Candidate declares a pinned signed Codex runtime");
    }

    static bool CodexIsRunning()
    {
        var names = new[] { "codex", "codex-code-mode-host", "codex-command-runner", "codex-windows-sandbox-setup" };
        var processes = names.SelectMany(Process.GetProcessesByName).ToArray();
        try { return processes.Length != 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    static Process WaitForNormalWindow(string scenario, string expectedExecutable)
    {
        var timer = Stopwatch.StartNew();
        var end = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < end)
        {
            foreach (var process in Process.GetProcessesByName("CodexAccountMonitor"))
            {
                try
                {
                    if (process.MainWindowHandle != IntPtr.Zero
                        && process.MainWindowTitle == ExpectedFirstWindow
                        && string.Equals(process.MainModule?.FileName, expectedExecutable, StringComparison.OrdinalIgnoreCase))
                    {
                        StartupLaunches.Add(new { scenario, status = "expected-window-observed", processId = process.Id,
                            processStartUtc = process.StartTime.ToUniversalTime(), elapsedMilliseconds = timer.ElapsedMilliseconds,
                            executable = process.MainModule!.FileName, windowTitle = process.MainWindowTitle });
                        Save("running");
                        return process;
                    }
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                process.Dispose();
            }
            Thread.Sleep(300);
        }
        StartupLaunches.Add(new { scenario, status = "window-timeout", elapsedMilliseconds = timer.ElapsedMilliseconds,
            expectedExecutable, expectedWindowTitle = ExpectedFirstWindow });
        Save("running");
        throw new TimeoutException("Normal packaged startup did not show the expected first window within 60 seconds: " + scenario);
    }

    static void WaitForReviewerClose(Process process, string scenario)
    {
        // UI review and closing are performed by the reviewer through native UI.
        // The harness reads process metadata and never sends window messages or clicks.
        Save("awaiting-native-ui-review-and-close:" + scenario);
        Assert(WaitForExternalProcessExit(process, 600000), "Normal Store app process exits after native review in " + scenario);
        // This Process was discovered rather than started by this object. The
        // runtime cannot always retrieve its exit status after it has exited.
        // Observed exit is evidence; an unavailable exit code is not success proof.
        StartupLaunches.Add(new { scenario, status = "process-exit-observed", exitCodeVerified = false });
        Save("running");
    }

    static bool WaitForExternalProcessExit(Process process, int timeoutMilliseconds) => process.WaitForExit(timeoutMilliseconds);

    static JsonElement Diagnose(string name, string? executable = null)
    {
        var file=Path.Combine(Output,name+".json");
        if (executable is not null)
            new CodexAccountMonitor.Infrastructure.ExplorerLaunchBroker().LaunchAsync(executable,["--diagnostics="+file,"--verify-codex-runtime"]).GetAwaiter().GetResult();
        else
        {
            var manager=(IActivationManager)new ActivationManager();
            Marshal.ThrowExceptionForHR(manager.ActivateApplication(Family+"!App", "--diagnostics="+file+" --verify-codex-runtime", 0, out _));
        }
        var end=DateTime.UtcNow.AddSeconds(45);
        while(!File.Exists(file) && DateTime.UtcNow<end) Thread.Sleep(300);
        if(!File.Exists(file)) throw new Exception("Package did not write diagnostics within 45 seconds.");
        using var doc=JsonDocument.Parse(File.ReadAllText(file));
        return doc.RootElement.Clone();
    }
    static void Assert(bool condition,string description)
    {
        if(!condition) throw new Exception(description);
        Checks.Add(description); Save("running");
    }
    static void Save(string status) => File.WriteAllText(Path.Combine(Output,"result.json"),JsonSerializer.Serialize(new
    {
        status, checks = Checks, expectedVersion = ExpectedVersion, expectedFirstWindow = ExpectedFirstWindow,
        startupLaunches = StartupLaunches, requireContainedStartup = RequireContainedStartup,
        candidate = new { sha256 = CandidateSha256, bytes = CandidateBytes },
        bundledRuntime = new { version = ExpectedRuntimeVersion, executableSha256 = RuntimeExecutableSha256 },
        scope = "Clean-Sandbox MSIX lifecycle, exact installed Store startup with and without synthetic inherited API credentials and inside a harness job, bundled runtime version/help, and isolated unauthenticated App Server initialization. Native UI contents and browser launch are reviewed separately. Does not validate membership sign-in, Codex account connection, usage retrieval, or switching.",
        error = Error, utc = DateTimeOffset.UtcNow
    },new JsonSerializerOptions{WriteIndented=true}));
    static string Ps(string command) => Run("powershell.exe",["-NoProfile","-NonInteractive","-EncodedCommand",Convert.ToBase64String(Encoding.Unicode.GetBytes("$ErrorActionPreference='Stop'; "+command))]);
    static string Run(string executable,string[] args)
    {
        var info=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in args) info.ArgumentList.Add(arg);
        using var p=Process.Start(info)!;
        var output=p.StandardOutput.ReadToEndAsync(); var error=p.StandardError.ReadToEndAsync();
        if(!p.WaitForExit(90000)) throw new Exception("Guest test command timed out.");
        Task.WaitAll(output,error);
        if(p.ExitCode!=0) throw new Exception(executable+": "+output.Result+error.Result);
        return output.Result;
    }
    [ComImport,Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    class ActivationManager { }
    [ComImport,Guid("2e941141-7f97-4756-ba1d-9decde894a3d"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivationManager
    {
        [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)]string appUserModelId,[MarshalAs(UnmanagedType.LPWStr)]string arguments,uint options,out uint processId);
        [PreserveSig] int ActivateForFile(IntPtr app,IntPtr items,IntPtr verb,out uint processId);
        [PreserveSig] int ActivateForProtocol(IntPtr app,IntPtr items,out uint processId);
    }

    // Process containment is an integration fixture, not UI automation. A suspended
    // guest-owned child is assigned to its job before its application code runs.
    // UI review/closing remains native and is handled by the reviewer.
    sealed class ContainedProcessLaunch : IDisposable
    {
        readonly Microsoft.Win32.SafeHandles.SafeFileHandle job;
        public int ProcessId { get; }

        public ContainedProcessLaunch(string executable)
        {
            job = CreateJobObject(IntPtr.Zero, null);
            if (job.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var limit = new JobExtendedLimits { BasicLimits = new JobBasicLimits { LimitFlags = 0x2000 } };
            if (!SetInformationJobObject(job, 9, ref limit, (uint)Marshal.SizeOf<JobExtendedLimits>()))
            {
                job.Dispose();
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
            var startup = new StartupInfo { Size = (uint)Marshal.SizeOf<StartupInfo>() };
            if (!CreateProcess(executable, new StringBuilder("\"" + executable + "\""), IntPtr.Zero, IntPtr.Zero,
                false, 0x4, IntPtr.Zero, Path.GetDirectoryName(executable), ref startup, out var info))
            {
                job.Dispose();
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
            try
            {
                if (!AssignProcessToJobObject(job, info.Process))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                ProcessId = checked((int)info.ProcessId);
                if (ResumeThread(info.Thread) == uint.MaxValue)
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
            catch
            {
                // This is exclusively the new suspended fixture child in the
                // disposable guest; no existing process is ever terminated here.
                TerminateProcess(info.Process, 1);
                job.Dispose();
                throw;
            }
            finally
            {
                CloseHandle(info.Thread);
                CloseHandle(info.Process);
            }
        }

        public bool Contains(Process process)
        {
            if (!IsProcessInJob(process.Handle, job, out var contained))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            return contained;
        }

        public void Dispose() => job.Dispose();

        [StructLayout(LayoutKind.Sequential)]
        struct JobBasicLimits
        {
            public long ProcessUserTime, JobUserTime;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct JobIoCounters
        {
            public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct JobExtendedLimits
        {
            public JobBasicLimits BasicLimits;
            public JobIoCounters Io;
            public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct StartupInfo
        {
            public uint Size;
            public IntPtr Reserved, Desktop, Title;
            public uint X, Y, Width, Height, CharacterWidth, CharacterHeight, FillAttribute, Flags;
            public ushort ShowWindow, ReservedSize;
            public IntPtr ReservedData, StandardInput, StandardOutput, StandardError;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct ProcessInfo
        {
            public IntPtr Process, Thread;
            public uint ProcessId, ThreadId;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SetInformationJobObject(Microsoft.Win32.SafeHandles.SafeFileHandle job, int informationClass,
            ref JobExtendedLimits information, uint informationLength);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool CreateProcess(string executable, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment, string? directory,
            ref StartupInfo startup, out ProcessInfo info);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool AssignProcessToJobObject(Microsoft.Win32.SafeHandles.SafeFileHandle job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool IsProcessInJob(IntPtr process, Microsoft.Win32.SafeHandles.SafeFileHandle job,
            [MarshalAs(UnmanagedType.Bool)] out bool contained);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool TerminateProcess(IntPtr process, uint exitCode);
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool CloseHandle(IntPtr handle);
    }
}
