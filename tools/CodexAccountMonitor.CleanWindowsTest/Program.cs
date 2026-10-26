using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

// This executable performs disposable guest-only installer tests. It never runs
// PowerShell, changes execution policy, or force-closes any program.
internal static class Program
{
    private const string HarnessKind = "compiled-v2";
    private const string SandboxUser = "WDAGUtilityAccount";
    private const string SandboxProfile = @"C:\Users\WDAGUtilityAccount";
    private const string HarnessFile = "CodexLover.CleanWindowsTest.exe";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\CodexLover";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    [STAThread]
    private static int Main(string[] args)
    {
        // Keep this before creating directories or launching any subprocess.
        string userName = Environment.UserName;
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!OperatingSystem.IsWindows() ||
            !string.Equals(userName, SandboxUser, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(userProfile, SandboxProfile, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("This lifecycle harness runs only inside a disposable Windows Sandbox. No installation was changed.");
            return 2;
        }

        string inputDirectory = AppContext.BaseDirectory;
        string resultsDirectory = @"C:\CodexLoverTest\Results";
        try
        {
            for (int index = 0; index < args.Length; index++)
            {
                string option = args[index];
                Assert(index + 1 < args.Length, "An argument value is missing.");
                string value = args[++index];
                Assert(Path.IsPathFullyQualified(value), "Input and result directories must be absolute paths.");
                if (option == "--input-directory") inputDirectory = Path.GetFullPath(value);
                else if (option == "--results-directory") resultsDirectory = Path.GetFullPath(value);
                else throw new InvalidOperationException("Unknown harness argument.");
            }
            Assert(!string.Equals(Path.TrimEndingDirectorySeparator(inputDirectory),
                Path.TrimEndingDirectorySeparator(resultsDirectory), StringComparison.OrdinalIgnoreCase),
                "Input and results must be separate directories.");
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 2;
        }

        if (File.Exists(Path.Combine(resultsDirectory, "result.json")) ||
            File.Exists(Path.Combine(resultsDirectory, "result.json.tmp")) ||
            File.Exists(Path.Combine(resultsDirectory, "app-diagnostics.json")) ||
            File.Exists(Path.Combine(resultsDirectory, "app-diagnostics-custom.json")))
        {
            Console.Error.WriteLine("The results directory already contains test evidence. Use a new empty results directory; existing evidence was not changed.");
            return 2;
        }

        var result = new LifecycleResult
        {
            StartedUtc = DateTimeOffset.UtcNow,
            HarnessKind = HarnessKind,
            Environment = new { userName, userProfile, machineName = Environment.MachineName },
            Os = ReadOperatingSystem(),
            Scope = "Actual installers in a fresh offline Windows Sandbox; no account enrollment or live services."
        };

        void SaveResult()
        {
            Directory.CreateDirectory(resultsDirectory);
            string path = Path.Combine(resultsDirectory, "result.json");
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(result, JsonOptions), new UTF8Encoding(false));
            File.Move(temporary, path, true);
        }
        void Check(string name, Action action)
        {
            action();
            result.Checks.Add(new CheckResult(name, true, DateTimeOffset.UtcNow));
            SaveResult();
            Console.WriteLine("PASS: " + name);
        }

        string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string installRoot = Path.Combine(localData, "Programs", "CodexLover");
        const string customInstallRoot = @"C:\CodexLoverTest\Custom App\CodexLover";
        string pointerPath = Path.Combine(installRoot, "current.json");
        string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        string shortcutPath = Path.Combine(programs, "Codex Lover.lnk");
        string legacyShortcutPath = Path.Combine(programs, "Codex Account Monitor.lnk");
        string previousShortcutName = "Codex Account Monitor.lnk";
        string currentSetup = Path.Combine(inputDirectory, "current-setup.exe");
        string currentSetupSha256 = "";
        Receipt ReadReceipt() => ReadJson<Receipt>(pointerPath);
        object? ReadStartup() => ReadRegistry(RunKey, "CodexAccountMonitor");
        void AssertDiagnosticLaunch(string executable, string version, string filename)
        {
            string reportPath = Path.Combine(resultsDirectory, filename);
            Assert(!File.Exists(reportPath), "Diagnostic evidence already exists; it cannot be reused.");
            DateTimeOffset launchStartedUtc = DateTimeOffset.UtcNow;
            RunProgram(executable, "--diagnostics=" + reportPath);
            DateTimeOffset launchCompletedUtc = DateTimeOffset.UtcNow;
            using var report = JsonDocument.Parse(File.ReadAllText(reportPath));
            Assert(report.RootElement.GetProperty("version").GetString() == version + ".0", "Application version differs from installer.");
            Assert(report.RootElement.GetProperty("accountCount").GetInt32() == 0, "A clean machine unexpectedly contained saved accounts.");
            Assert(string.Equals(report.RootElement.GetProperty("executable").GetString(), executable, StringComparison.OrdinalIgnoreCase),
                "Diagnostic evidence was not produced by the launched installed executable.");
            DateTimeOffset diagnosticUtc = report.RootElement.GetProperty("checkedUtc").GetDateTimeOffset();
            Assert(diagnosticUtc >= launchStartedUtc && diagnosticUtc <= launchCompletedUtc,
                "Diagnostic evidence is outside the current launch window.");
        }
        string ReadUninstaller()
        {
            string command = ReadRegistry(UninstallKey, "QuietUninstallString") as string ?? "";
            var match = Regex.Match(command, "^\"([^\"]+)\" --uninstall --silent$");
            Assert(match.Success, "Unexpected uninstall command.");
            string uninstaller = Path.GetFullPath(match.Groups[1].Value);
            string maintenancePrefix = Path.Combine(installRoot, "maintenance") + Path.DirectorySeparatorChar;
            Assert(uninstaller.StartsWith(maintenancePrefix, StringComparison.OrdinalIgnoreCase), "Unexpected maintenance path.");
            return uninstaller;
        }
        void AssertActivation(string version, bool startup, bool isCandidate = true)
        {
            var receipt = ReadReceipt();
            Assert(receipt.Version == version, "The active receipt has the wrong version.");
            Assert(Path.IsPathFullyQualified(receipt.Executable) && File.Exists(receipt.Executable) &&
                Path.GetFullPath(receipt.Executable).StartsWith(Path.GetFullPath(installRoot) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path.GetFileName(receipt.Executable), "CodexAccountMonitor.exe", StringComparison.OrdinalIgnoreCase),
                "The active receipt must target an existing application executable inside the per-user installation.");
            Assert(Equals(ReadRegistry(UninstallKey, "DisplayVersion"), version), "Installed Apps has the wrong version.");
            Assert(receipt.StartWithWindows == startup, "The receipt has the wrong startup preference.");
            object? command = ReadStartup();
            Assert(startup ? Equals(command, "\"" + receipt.Executable + "\" --minimized") : command is null,
                "The startup registration is inconsistent.");
            string expectedShortcut = isCandidate ? shortcutPath : Path.Combine(programs, previousShortcutName);
            Assert(File.Exists(expectedShortcut), "The expected Start shortcut is missing.");
            Assert(string.Equals(ReadShortcutTarget(expectedShortcut), receipt.Executable, StringComparison.OrdinalIgnoreCase),
                "Start shortcut targets the wrong release.");
            if (isCandidate)
            {
                Assert(!File.Exists(legacyShortcutPath), "The candidate left the legacy Start shortcut behind.");
                Assert(string.Equals(ReadRegistry(UninstallKey, "InstallLocation") as string, installRoot, StringComparison.OrdinalIgnoreCase),
                    "Installed Apps has the wrong installation folder.");
                Assert(Equals(ReadRegistry(UninstallKey, "DisplayName"), "Codex Lover"), "Installed Apps has the wrong product name.");
                Assert(string.Equals(ReadRegistry(UninstallKey, "DisplayIcon") as string, receipt.Executable, StringComparison.OrdinalIgnoreCase),
                    "Installed Apps has the wrong executable icon path.");
                Assert(HashFile(ReadUninstaller()) == currentSetupSha256, "The active uninstaller is not the tested setup wrapper.");
            }
        }

        try
        {
            using (var uninstallRegistration = Registry.CurrentUser.OpenSubKey(UninstallKey))
            {
                var activeProcesses = Process.GetProcessesByName("CodexAccountMonitor");
                bool appRunning = activeProcesses.Length != 0;
                foreach (var process in activeProcesses) process.Dispose();
                Assert(!Directory.Exists(installRoot) && uninstallRegistration is null &&
                    !File.Exists(shortcutPath) && !File.Exists(legacyShortcutPath) &&
                    !Directory.Exists(customInstallRoot) && !appRunning && ReadStartup() is null,
                    "The Sandbox is not clean. Close it and start a new disposable session.");
            }
            string retainedRoot = Path.Combine(localData, "CodexAccountMonitor");
            Assert(!Directory.Exists(retainedRoot), "The Sandbox already contains application data. Start a fresh disposable session.");

            string manifestPath = Path.Combine(inputDirectory, "kit-manifest.json");
            var manifest = ReadJson<KitManifest>(manifestPath);
            Assert(manifest.HarnessKind == HarnessKind, "Unexpected kit harness kind.");
            Assert(manifest.PreviousShortcutName is "Codex Account Monitor.lnk" or "Codex Lover.lnk", "Unexpected previous shortcut name.");
            previousShortcutName = manifest.PreviousShortcutName;
            Assert(Regex.IsMatch(manifest.PreviousVersion, @"^\d+\.\d+\.\d+$") &&
                Regex.IsMatch(manifest.CurrentVersion, @"^\d+\.\d+\.\d+$"), "Invalid manifest versions.");
            string[] expectedNames = ["current-setup.exe", "previous-setup.exe", HarnessFile];
            Assert(manifest.Files.Count == 3 && manifest.Files.Select(file => file.Name)
                .Order(StringComparer.Ordinal).SequenceEqual(expectedNames.Order(StringComparer.Ordinal)),
                "The compiled kit must contain exactly the two installers and this test harness.");
            foreach (var file in manifest.Files)
            {
                Assert(file.Name == Path.GetFileName(file.Name) && file.Bytes > 0 &&
                    Regex.IsMatch(file.Sha256, "^[a-fA-F0-9]{64}$"), "Unsafe or invalid kit input.");
                string path = Path.Combine(inputDirectory, file.Name);
                Assert(new FileInfo(path).Length == file.Bytes && string.Equals(HashFile(path), file.Sha256, StringComparison.OrdinalIgnoreCase),
                    "A kit input changed. Regenerate the kit before testing.");
            }
            Assert(string.Equals(Path.GetFullPath(Environment.ProcessPath ?? ""),
                Path.GetFullPath(Path.Combine(inputDirectory, HarnessFile)), StringComparison.OrdinalIgnoreCase),
                "The running harness is not the validated kit executable.");
            result.PreviousVersion = manifest.PreviousVersion;
            result.CurrentVersion = manifest.CurrentVersion;
            result.Inputs = manifest.Files;
            result.InputManifestSha256 = HashFile(manifestPath);
            result.PreviousShortcutName = previousShortcutName;
            result.CurrentShortcutName = "Codex Lover.lnk";
            result.CustomInstallRoot = customInstallRoot;
            currentSetupSha256 = manifest.Files.Single(file => file.Name == "current-setup.exe").Sha256.ToLowerInvariant();
            SaveResult();

            string previousSetup = Path.Combine(inputDirectory, "previous-setup.exe");
            Check("verify both installer payloads without installation", () =>
            {
                RunProgram(previousSetup, "--verify-only", "--silent");
                RunProgram(currentSetup, "--verify-only", "--silent");
                Assert(!Directory.Exists(installRoot), "Verification unexpectedly installed a release.");
            });
            Check("fresh per-user install with startup disabled", () =>
            {
                RunProgram(previousSetup, "--silent", "--no-start-with-windows");
                AssertActivation(manifest.PreviousVersion, false, isCandidate: false);
            });
            var previousReceipt = ReadReceipt();
            Directory.CreateDirectory(retainedRoot);
            string sentinel = Path.Combine(retainedRoot, "synthetic-retained-data.txt");
            File.WriteAllText(sentinel, "Synthetic data only: preserve across installer upgrade and uninstall.");
            string sentinelHash = HashFile(sentinel);
            Check("upgrade preserves startup choice and earlier release", () =>
            {
                RunProgram(currentSetup, "--silent");
                AssertActivation(manifest.CurrentVersion, false);
                Assert(File.Exists(previousReceipt.Executable), "The previous release was deleted.");
                Assert(HashFile(sentinel) == sentinelHash, "Retained application data changed.");
            });
            Check("self-contained app starts in diagnostic mode without a preinstalled runtime", () =>
            {
                string executable = ReadReceipt().Executable;
                result.CandidateBuild = ReadCandidateBuild(Path.Combine(Path.GetDirectoryName(executable)!, "publish-manifest.json"), manifest.CurrentVersion);
                AssertDiagnosticLaunch(executable, manifest.CurrentVersion, "app-diagnostics.json");
            });
            Check("reinstall enables startup when explicitly requested", () =>
            {
                RunProgram(currentSetup, "--silent", "--start-with-windows");
                AssertActivation(manifest.CurrentVersion, true);
            });
            Check("reinstall explicitly disables startup", () =>
            {
                RunProgram(currentSetup, "--silent", "--no-start-with-windows");
                AssertActivation(manifest.CurrentVersion, false);
            });
            Check("damaged installed release is preserved and replaced by a verified fresh copy", () =>
            {
                var before = ReadReceipt();
                File.AppendAllText(Path.Combine(Path.GetDirectoryName(before.Executable)!, "CodexAccountMonitor.Core.dll"), "synthetic-damage");
                RunProgram(currentSetup, "--silent");
                var after = ReadReceipt();
                Assert(!string.Equals(before.Executable, after.Executable, StringComparison.OrdinalIgnoreCase), "Setup reused a damaged release.");
                Assert(File.Exists(before.Executable), "Setup deleted the older damaged release.");
                AssertActivation(manifest.CurrentVersion, false);
            });
            Check("custom installation folder preserves earlier releases and user data", () =>
            {
                var before = ReadReceipt();
                string beforeExecutableHash = HashFile(before.Executable);
                string previousExecutableHash = HashFile(previousReceipt.Executable);
                RunProgram(currentSetup, "--silent", "--install-dir", customInstallRoot, "--start-with-windows");
                installRoot = customInstallRoot;
                pointerPath = Path.Combine(installRoot, "current.json");
                AssertActivation(manifest.CurrentVersion, true);
                Assert(HashFile(before.Executable) == beforeExecutableHash && HashFile(previousReceipt.Executable) == previousExecutableHash,
                    "Moving the installation deleted or modified an earlier release.");
                Assert(HashFile(sentinel) == sentinelHash, "Changing the installation folder modified saved user data.");
                Assert(File.Exists(Path.Combine(installRoot, ".codex-lover-installation")), "The custom installation has no ownership marker.");
                AssertDiagnosticLaunch(ReadReceipt().Executable, manifest.CurrentVersion, "app-diagnostics-custom.json");
            });
            Check("reinstall discovers and preserves the registered custom folder", () =>
            {
                string beforeExecutable = ReadReceipt().Executable;
                RunProgram(currentSetup, "--silent", "--no-start-with-windows");
                AssertActivation(manifest.CurrentVersion, false);
                Assert(ReadReceipt().Executable == beforeExecutable, "Reinstall did not reuse the verified custom installation.");
                Assert(HashFile(sentinel) == sentinelHash, "Reinstalling the custom location modified saved user data.");
            });
            var lastReceipt = ReadReceipt();
            Check("uninstall removes Windows registration and preserves release plus user data", () =>
            {
                string uninstaller = ReadUninstaller();
                RunProgram(uninstaller, "--uninstall", "--silent");
                Assert(!File.Exists(pointerPath), "The active pointer remains after uninstall.");
                Assert(!File.Exists(shortcutPath) && !File.Exists(legacyShortcutPath), "A Start shortcut remains after uninstall.");
                using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
                Assert(key is null, "Installed Apps registration remains after uninstall.");
                Assert(ReadStartup() is null, "The Windows startup entry remains after uninstall.");
                Assert(File.Exists(lastReceipt.Executable), "The retained release disappeared.");
                Assert(HashFile(sentinel) == sentinelHash, "Retained application data changed.");
            });
            Check("reinstall after uninstall restores a valid activation", () =>
            {
                RunProgram(currentSetup, "--silent", "--install-dir", customInstallRoot);
                // Uninstall removed registration: a fresh installation now enables startup by default.
                AssertActivation(manifest.CurrentVersion, true);
                Assert(ReadReceipt().Executable == lastReceipt.Executable, "Reinstall after uninstall did not reuse the verified custom release.");
                Assert(HashFile(sentinel) == sentinelHash, "Reinstall after uninstall modified saved user data.");
            });
            result.Status = "passed";
        }
        catch (Exception error)
        {
            result.Status = "failed";
            result.Error = error.Message;
            Console.Error.WriteLine(error.Message);
        }
        finally
        {
            result.CompletedUtc = DateTimeOffset.UtcNow;
            try { SaveResult(); }
            catch (Exception error)
            {
                result.Status = "failed";
                Console.Error.WriteLine("Could not persist test result: " + error.Message);
            }
        }
        return result.Status == "passed" ? 0 : 1;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static T ReadJson<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidOperationException("Empty JSON document: " + Path.GetFileName(path));

    private static string HashFile(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
    }

    private static object? ReadRegistry(string path, string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path);
        return key?.GetValue(name);
    }

    private static object ReadOperatingSystem()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        return new
        {
            caption = key?.GetValue("ProductName") as string ?? "Windows",
            version = System.Environment.OSVersion.Version.ToString(),
            buildNumber = key?.GetValue("CurrentBuildNumber") as string ?? "",
            osArchitecture = RuntimeInformation.OSArchitecture.ToString()
        };
    }

    private static void RunProgram(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start test program.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(90_000))
            throw new TimeoutException("The test program did not exit within 90 seconds; it was not force-closed.");
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Test program {Path.GetFileName(executable)} exited with code {process.ExitCode}.");
        // Streams are consumed asynchronously to avoid a subprocess pipe deadlock.
        Task.WhenAll(stdout, stderr).GetAwaiter().GetResult();
    }

    private static string ReadShortcutTarget(string path)
    {
        Type shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows Script Host COM is unavailable.");
        object shell = Activator.CreateInstance(shellType) ?? throw new InvalidOperationException("Could not create shortcut reader.");
        object? shortcut = null;
        try
        {
            shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [path]);
            Assert(shortcut is not null, "Could not read shortcut.");
            return shortcut!.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, shortcut, null) as string ?? "";
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }

    private static Dictionary<string, string> ReadCandidateBuild(string path, string version)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var expected = new Dictionary<string, string>
        {
            ["product"] = "Codex Account Monitor", ["version"] = version,
            ["releaseChannel"] = "unsigned-beta", ["signatureStatus"] = "NotSigned",
            ["configuration"] = "Release", ["deployment"] = "self-contained", ["runtime"] = "win-x64"
        };
        foreach (var entry in expected)
        {
            Assert(document.RootElement.TryGetProperty(entry.Key, out var property) && property.GetString() == entry.Value,
                $"Installed candidate manifest has unexpected {entry.Key}; it is not the approved unsigned beta build.");
        }
        expected.Remove("product");
        return expected;
    }

    private sealed class KitManifest
    {
        public string HarnessKind { get; set; } = "";
        public string PreviousVersion { get; set; } = "";
        public string CurrentVersion { get; set; } = "";
        public string PreviousShortcutName { get; set; } = "Codex Account Monitor.lnk";
        public List<KitFile> Files { get; set; } = [];
    }

    private sealed class KitFile
    {
        public string Name { get; set; } = "";
        public long Bytes { get; set; }
        public string Sha256 { get; set; } = "";
    }

    private sealed class Receipt
    {
        public string Version { get; set; } = "";
        public string Executable { get; set; } = "";
        public bool? StartWithWindows { get; set; }
    }

    private sealed record CheckResult(string Name, bool Passed, DateTimeOffset CheckedUtc);

    private sealed class LifecycleResult
    {
        public string Status { get; set; } = "running";
        public DateTimeOffset StartedUtc { get; set; }
        public DateTimeOffset? CompletedUtc { get; set; }
        public object? Os { get; set; }
        public string HarnessKind { get; set; } = "";
        public object? Environment { get; set; }
        public string PreviousVersion { get; set; } = "";
        public string CurrentVersion { get; set; } = "";
        public string PreviousShortcutName { get; set; } = "";
        public string CurrentShortcutName { get; set; } = "";
        public string CustomInstallRoot { get; set; } = "";
        public List<KitFile> Inputs { get; set; } = [];
        public string InputManifestSha256 { get; set; } = "";
        public Dictionary<string, string>? CandidateBuild { get; set; }
        public string Scope { get; set; } = "";
        public List<CheckResult> Checks { get; set; } = [];
        public string? Error { get; set; }
    }
}
