using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;

namespace CodexAccountMonitor.Setup;

internal static class Program
{
    private static readonly string DefaultInstallRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "CodexLover");
    private static string InstallRoot = DefaultInstallRoot;
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "CodexAccountMonitor";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\CodexLover";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var silent = args.Contains("--silent", StringComparer.OrdinalIgnoreCase);
        var verifyOnly = args.Contains("--verify-only", StringComparer.OrdinalIgnoreCase);
        var enableStartup = args.Contains("--start-with-windows", StringComparer.OrdinalIgnoreCase);
        var disableStartup = args.Contains("--no-start-with-windows", StringComparer.OrdinalIgnoreCase);
        var uninstall = args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase);
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        using var mutex = new Mutex(false, @"Local\CodexAccountMonitorSetup_" + sid);
        var ownsMutex = false;
        try
        {
            try { ownsMutex = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { ownsMutex = true; }
            if (!ownsMutex) throw new InvalidOperationException("Another Codex Lover installation is already in progress.");
            if (enableStartup && disableStartup) throw new ArgumentException("Choose only one Windows startup option.");
            var requestedRoot = ReadInstallDirectoryArgument(args);
            if (verifyOnly && (uninstall || requestedRoot is not null))
                throw new ArgumentException("Package verification cannot be combined with installation or uninstall options.");
            if (uninstall && requestedRoot is not null)
                throw new ArgumentException("Uninstall uses the registered installation folder.");
            if (!verifyOnly) InstallRoot = requestedRoot is not null
                ? InstallationLocation.Normalize(requestedRoot) : ResolveExistingInstallRoot(uninstall);
            if (uninstall) return Uninstall(silent);
            var existingStartup = verifyOnly ? new StartupSnapshot(false, null, RegistryValueKind.String) : CaptureStartup();
            bool previouslyInstalled;
            using (var installedApp = verifyOnly ? null : Registry.CurrentUser.OpenSubKey(UninstallKey))
                previouslyInstalled = installedApp is not null;
            var startup = ResolveStartupPreference(enableStartup, disableStartup,
                previouslyInstalled, existingStartup.Value is string);
            if (silent || verifyOnly) return Install(silent, verifyOnly, startup, null).ExitCode;

            using var wizard = new SetupWizard(InstallRoot, startup, InstallationLocation.ValidationMessage,
                (selection, progress) => RunInstallationOnSta(selection, progress));
            wizard.ShowDialog();
            return wizard.ExitCode;
        }
        catch (Exception exception)
        {
            if (!verifyOnly) SaveError(exception);
            if (!silent && !verifyOnly) MessageBox.Show(exception.Message, "Codex Lover installation failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally { if (ownsMutex) mutex.ReleaseMutex(); }
    }

    private static Task<SetupResult> RunInstallationOnSta(SetupSelection selection, IProgress<SetupProgress> progress)
    {
        var completion = new TaskCompletionSource<SetupResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try
            {
                InstallRoot = InstallationLocation.Normalize(selection.InstallRoot);
                completion.SetResult(Install(false, false, selection.StartWithWindows, progress));
            }
            catch (Exception exception)
            {
                SaveError(exception);
                completion.SetResult(new SetupResult(1, exception.Message));
            }
        }) { IsBackground = true, Name = "Codex Lover installation" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        return completion.Task;
    }

    private static SetupResult Install(bool silent, bool verifyOnly, bool startup, IProgress<SetupProgress>? progress)
    {
        progress?.Report(new SetupProgress(5, "Verifying the installation package…"));
        using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip")
                ?? throw new InvalidOperationException("This setup was built without an application payload.");
            var payloadHash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
            payload.Position = 0;
            using var zip = new ZipArchive(payload, ZipArchiveMode.Read);
            var manifestEntry = zip.GetEntry("publish-manifest.json")
                ?? throw new InvalidDataException("Release manifest is missing.");
            using var manifestStream = manifestEntry.Open();
            using var manifest = JsonDocument.Parse(manifestStream);
            var versionText = manifest.RootElement.GetProperty("version").GetString();
            if (!Version.TryParse(versionText, out var parsedVersion))
                throw new InvalidDataException("Release version is invalid.");
            var version = parsedVersion.ToString();
            if (manifest.RootElement.GetProperty("runtime").GetString() != "win-x64")
                throw new InvalidDataException("This installer requires the Windows x64 application package.");

            var files = ReadPackageFiles(manifest.RootElement);
            ValidateArchive(zip, files);
            if (verifyOnly)
            {
                VerifyArchiveFiles(zip, files);
                return new SetupResult(0, "The installation package was verified.");
            }
            InstallationLocation.Prepare(InstallRoot);
            progress?.Report(new SetupProgress(20, "Preparing your installation folder…"));
            var target = FindVerifiedInstallation(InstallRoot, version, payloadHash, files);
            if (target is null)
            {
                target = Path.Combine(InstallRoot,
                    version + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
                Directory.CreateDirectory(target);
                var copiedEntries = 0;
                foreach (var entry in zip.Entries)
                {
                    var destination = ResolvePackagePath(target, entry.FullName);
                    if (entry.FullName.EndsWith('/'))
                    {
                        Directory.CreateDirectory(destination);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    entry.ExtractToFile(destination, overwrite: false);
                    progress?.Report(new SetupProgress(25 + 50 * ++copiedEntries / zip.Entries.Count, "Installing app files…"));
                }

                progress?.Report(new SetupProgress(78, "Checking installed files…"));
                VerifyInstallation(target, files);
                var identity = new
                {
                    version, payloadSha256 = payloadHash, installedUtc = DateTimeOffset.UtcNow,
                    executable = Path.Combine(target, "CodexAccountMonitor.exe")
                };
                WriteDurable(Path.Combine(target, "installation.json"), JsonSerializer.SerializeToUtf8Bytes(identity, JsonOptions));
            }

            var executable = Path.Combine(target, "CodexAccountMonitor.exe");
            var restartRequested = HasRunningMonitor();
            progress?.Report(new SetupProgress(85, "Preparing to activate Codex Lover…"));
            if (!WaitForMonitorExit(silent)) return new SetupResult(2, "Installation was paused. Close the existing Codex Lover widget, then try again.");
            using var setupFile = File.OpenRead(Environment.ProcessPath!);
            var setupHash = Convert.ToHexString(SHA256.HashData(setupFile));
            var maintenanceDirectory = Path.Combine(InstallRoot, "maintenance", version + "-" + setupHash[..12].ToLowerInvariant());
            Directory.CreateDirectory(maintenanceDirectory);
            var uninstaller = Path.Combine(maintenanceDirectory, "Uninstall.exe");
            if (!File.Exists(uninstaller)) File.Copy(Environment.ProcessPath!, uninstaller, overwrite: false);
            using (var maintenanceFile = File.OpenRead(uninstaller))
                if (Convert.ToHexString(SHA256.HashData(maintenanceFile)) != setupHash) throw new InvalidDataException("The saved uninstaller changed unexpectedly. Installation was not activated.");
            var receipt = JsonSerializer.SerializeToUtf8Bytes(new
            {
                version, payloadSha256 = payloadHash, activatedUtc = DateTimeOffset.UtcNow,
                executable, startWithWindows = startup
            }, JsonOptions);
            progress?.Report(new SetupProgress(94, "Creating shortcuts and saving your preferences…"));
            ActivateInstallation(executable, startup, receipt, uninstaller, version);

            var launchMessage = "Codex Lover is ready. You can open it from the Start menu.";
            if (!silent || restartRequested)
            {
                try
                {
                    if (!HasRunningMonitor()) InstallationLaunch.LaunchAsync(executable, minimized: silent).GetAwaiter().GetResult();
                    else launchMessage += "\nAnother widget was opened during setup. Exit it from its tray before opening the update.";
                }
                catch
                {
                    // Installation is committed. Failure to launch is a separate, recoverable action.
                    launchMessage += "\nWindows could not launch it automatically. Open the Start shortcut to try again.";
                }


            }
            progress?.Report(new SetupProgress(100, "Installation complete."));
            return new SetupResult(0, launchMessage);
    }

    private static void SaveError(Exception exception)
    {
        try
        {
            var logRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexAccountMonitor", "logs");
            Directory.CreateDirectory(logRoot);
            File.WriteAllText(Path.Combine(logRoot, "setup-error.txt"), exception.ToString());
        }
        catch { /* Diagnostics must not obscure the setup result. */ }
    }

    private static string? ReadInstallDirectoryArgument(string[] args)
    {
        string? result = null;
        for (var index = 0; index < args.Length; index++)
        {
            if (!args[index].Equals("--install-dir", StringComparison.OrdinalIgnoreCase)) continue;
            if (result is not null || index + 1 >= args.Length || args[index + 1].StartsWith("--"))
                throw new ArgumentException("Specify one folder after --install-dir.");
            result = args[++index];
        }
        return result;
    }

    internal static bool ResolveStartupPreference(bool enable, bool disable, bool installed, bool previouslyEnabled)
        => enable || (!disable && (!installed || previouslyEnabled));

    private static string ResolveExistingInstallRoot(bool uninstall)
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
        if (key?.GetValue("InstallLocation") is string registered && !string.IsNullOrWhiteSpace(registered))
        {
            // Let the wizard repair an unavailable previous location by choosing another folder.
            // Installation validates the selected folder again before any file is written.
            var root = uninstall ? InstallationLocation.Normalize(registered) : registered;
            if (uninstall && key.GetValue("UninstallString") is string command
                && !command.StartsWith("\"" + Environment.ProcessPath + "\"", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A newer Codex Lover installer is active. Use Windows Installed Apps to uninstall the current copy.");
            return root;
        }
        if (uninstall)
        {
            var binaryDirectory = Path.GetDirectoryName(Environment.ProcessPath!);
            var maintenanceDirectory = Directory.GetParent(binaryDirectory!)?.FullName;
            if (maintenanceDirectory is not null && Path.GetFileName(maintenanceDirectory).Equals("maintenance", StringComparison.OrdinalIgnoreCase))
                return InstallationLocation.Normalize(Directory.GetParent(maintenanceDirectory)!.FullName);
        }
        return DefaultInstallRoot;
    }

    private static IReadOnlyList<PackageFile> ReadPackageFiles(JsonElement manifest)
    {
        var files = new List<PackageFile>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in manifest.GetProperty("files").EnumerateArray())
        {
            var name = NormalizeName(element.GetProperty("path").GetString()
                ?? throw new InvalidDataException("A package path is missing."));
            _ = ResolvePackagePath(InstallRoot, name);
            if (name.EndsWith('/') || name.Equals("publish-manifest.json", StringComparison.OrdinalIgnoreCase)
                || !names.Add(name)) throw new InvalidDataException("The release manifest contains a duplicate or invalid file.");
            var hash = element.GetProperty("sha256").GetString();
            if (hash is null || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
                throw new InvalidDataException("A package checksum is invalid.");
            var length = element.GetProperty("bytes").GetInt64();
            if (length < 0) throw new InvalidDataException("A package file size is invalid.");
            files.Add(new PackageFile(name, hash, length));
        }

        if (!names.Contains("CodexAccountMonitor.exe"))
            throw new InvalidDataException("The application executable is not covered by the release manifest.");
        return files;
    }

    private static void ValidateArchive(ZipArchive zip, IReadOnlyList<PackageFile> files)
    {
        var expected = files.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        expected.Add("publish-manifest.json");
        var actual = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            var name = NormalizeName(entry.FullName);
            _ = ResolvePackagePath(InstallRoot, name);
            if (name.EndsWith('/')) continue;
            if (!actual.Add(name) || !expected.Contains(name))
                throw new InvalidDataException("The package contains an unexpected or duplicate file.");
        }
        if (!actual.SetEquals(expected)) throw new InvalidDataException("The package is missing a listed file.");
    }

    private static string? FindVerifiedInstallation(string installRoot, string version, string payloadHash, IReadOnlyList<PackageFile> files)
    {
        foreach (var directory in Directory.EnumerateDirectories(installRoot))
        {
            var identityPath = Path.Combine(directory, "installation.json");
            if (!File.Exists(identityPath)) continue;
            try
            {
                using var identity = JsonDocument.Parse(File.ReadAllBytes(identityPath));
                var root = identity.RootElement;
                if (!root.TryGetProperty("version", out var recordedVersion) || recordedVersion.GetString() != version
                    || !root.TryGetProperty("payloadSha256", out var recordedHash)
                    || !string.Equals(recordedHash.GetString(), payloadHash, StringComparison.OrdinalIgnoreCase)) continue;
                VerifyInstallation(directory, files);
                return directory;
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                // InvalidDataException is not an IOException. A failed cached-release checksum
                // must trigger a fresh verified install, while preserving the changed older folder.
            }
        }
        return null;
    }

    private static void VerifyArchiveFiles(ZipArchive zip, IReadOnlyList<PackageFile> files)
    {
        var entries = zip.Entries.Where(entry => !entry.FullName.EndsWith('/'))
            .ToDictionary(entry => NormalizeName(entry.FullName), StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var entry = entries[file.Path];
            using var stream = entry.Open();
            if (entry.Length != file.Bytes
                || !Convert.ToHexString(SHA256.HashData(stream)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A packaged file did not match its release checksum.");
        }
    }

    private static void VerifyInstallation(string target, IReadOnlyList<PackageFile> files)
    {
        foreach (var file in files)
        {
            var path = ResolvePackagePath(target, file.Path);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != file.Bytes)
                throw new InvalidDataException("A release file is missing or incomplete. Installation was not activated.");
            using var stream = File.OpenRead(path);
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The release checksum did not match. Installation was not activated.");
        }

        var expected = files.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        expected.UnionWith(["publish-manifest.json", "installation.json"]);
        foreach (var path in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
        {
            if (!expected.Contains(NormalizeName(Path.GetRelativePath(target, path))))
                throw new InvalidDataException("The installed folder contains an unexpected file.");
        }
    }

    private static void ActivateInstallation(string executable, bool startup, byte[] receipt, string uninstaller, string version)
    {
        CreateActivation().Activate(executable, startup, receipt, uninstaller, version);
    }

    private static bool WaitForMonitorExit(bool silent)
    {
        while (HasRunningMonitor())
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value?.Replace('-', '_') ?? Environment.UserName;
            if (EventWaitHandle.TryOpenExisting($@"Local\CodexAccountMonitor_Activate_{sid}_UpgradeExit", out var exitEvent))
            {
                using (exitEvent) exitEvent.Set();
                var wait = Stopwatch.StartNew();
                while (HasRunningMonitor() && wait.Elapsed < TimeSpan.FromSeconds(15)) Thread.Sleep(200);
                if (!HasRunningMonitor()) return true;
            }
            if (silent) throw new InvalidOperationException("The monitor is still running. Finish its active operations and exit it before retrying setup. Codex itself does not need to be closed.");
            if (MessageBox.Show("Finish any active account operation, then exit the older Codex Lover widget from its tray menu and choose Retry. Setup will never force-close the monitor or Codex.\n\nThe previous installation remains active until the monitor closes.",
                "Close Codex Lover to continue", MessageBoxButtons.RetryCancel, MessageBoxIcon.Information) != DialogResult.Retry) return false;
        }
        return true;
    }

    private static bool HasRunningMonitor()
    {
        using var current = Process.GetCurrentProcess();
        var currentSession = current.SessionId;
        var processes = Process.GetProcessesByName("CodexAccountMonitor");
        try { return processes.Any(process => !process.HasExited && process.SessionId == currentSession); }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private static Dictionary<string, RegistryEntry>? CaptureUninstall()
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
        return key?.GetValueNames().ToDictionary(name => name, name => new RegistryEntry(key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)!, key.GetValueKind(name)));
    }

    private static void RestoreUninstall(Dictionary<string, RegistryEntry>? previous)
    {
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        if (previous is null) return;
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        foreach (var pair in previous) key.SetValue(pair.Key, pair.Value.Value, pair.Value.Kind);
    }

    private static void RegisterUninstall(string executable, string uninstaller, string version)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", "Codex Lover");
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", "Codex Lover");
        key.SetValue("DisplayIcon", executable);
        key.SetValue("InstallLocation", InstallRoot);
        key.SetValue("UninstallString", $"\"{uninstaller}\" --uninstall");
        key.SetValue("QuietUninstallString", $"\"{uninstaller}\" --uninstall --silent");
        key.SetValue("URLInfoAbout", "https://codexlover.com/");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    private static int Uninstall(bool silent)
    {
        if (!silent && MessageBox.Show("Remove Codex Lover from the Start menu, Windows startup and Installed Apps?\n\nYour saved accounts, settings and earlier release folders will remain on this PC. This does not cancel a website subscription.",
            "Uninstall Codex Lover", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return 0;
        if (!WaitForMonitorExit(silent)) return 2;
        CreateActivation().Uninstall();
        if (!silent) MessageBox.Show("Codex Lover was removed from Windows startup, Start and Installed Apps. Saved accounts, settings and release folders were preserved.", "Codex Lover uninstalled");
        return 0;
    }

    private static void WriteDurable(string path, byte[] bytes)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        file.Write(bytes);
        file.Flush(flushToDisk: true);
    }

    private static StartupSnapshot CaptureStartup()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        var exists = run?.GetValueNames().Contains(RunValue, StringComparer.OrdinalIgnoreCase) == true;
        return exists
            ? new StartupSnapshot(true, run!.GetValue(RunValue, null, RegistryValueOptions.DoNotExpandEnvironmentNames), run.GetValueKind(RunValue))
            : new StartupSnapshot(false, null, RegistryValueKind.String);
    }

    private static void SetStartup(string executable, bool enabled)
    {
        if (enabled)
        {
            using var run = Registry.CurrentUser.CreateSubKey(RunKey);
            run.SetValue(RunValue, $"\"{executable}\" --minimized", RegistryValueKind.String);
        }
        else
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            run?.DeleteValue(RunValue, throwOnMissingValue: false);
        }
    }

    private static void RestoreStartup(StartupSnapshot previous)
    {
        if (previous.Exists)
        {
            using var run = Registry.CurrentUser.CreateSubKey(RunKey);
            run.SetValue(RunValue, previous.Value!, previous.Kind);
        }
        else
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            run?.DeleteValue(RunValue, throwOnMissingValue: false);
        }
    }

    private static string NormalizeName(string path) => path.Replace('\\', '/');

    private static string ResolvePackagePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':'))
            throw new InvalidDataException("Unsafe package path.");
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unsafe package path.");
        return path;
    }

    private static void CreateShortcut(string executable, string path)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows shortcut support is unavailable.");
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            dynamic shortcut = shell.CreateShortcut(path);
            try
            {
                shortcut.TargetPath = executable;
                shortcut.WorkingDirectory = Path.GetDirectoryName(executable);
                shortcut.Description = "Codex Lover account quota monitor";
                shortcut.Save();
            }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut); }
        }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
    }

    private sealed record PackageFile(string Path, string Sha256, long Bytes);
    private sealed record StartupSnapshot(bool Exists, object? Value, RegistryValueKind Kind);
    private sealed record RegistryEntry(object Value, RegistryValueKind Kind);

    private static InstallationActivation CreateActivation() => new(InstallRoot,
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), new WindowsRegistration());

    private sealed class WindowsRegistration : IInstallationRegistration
    {
        public object CaptureStartup() => Program.CaptureStartup();
        public object? CaptureUninstall() => Program.CaptureUninstall();
        public void SetStartup(string executable, bool enabled) => Program.SetStartup(executable, enabled);
        public void RestoreStartup(object snapshot) => Program.RestoreStartup((StartupSnapshot)snapshot);
        public void RegisterUninstall(string executable, string uninstaller, string version) => Program.RegisterUninstall(executable, uninstaller, version);
        public void RestoreUninstall(object? snapshot) => Program.RestoreUninstall((Dictionary<string, RegistryEntry>?)snapshot);
        public void RemoveUninstall() => Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        public void CreateShortcut(string executable, string path) => Program.CreateShortcut(executable, path);
    }
}
