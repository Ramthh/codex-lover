using System.Text;
using CodexAccountMonitor.Setup;

internal static class LifecycleChecks
{
    internal static void Run(Action<string, Action> check)
    {
        check("fresh activation creates one consistent pointer, shortcut and registration", () =>
        {
            using var f = new Fixture();
            f.Activate("1.7.3", true);
            f.AssertActive("1.7.3", true);
            f.AssertRetained();
        });
        check("upgrade retains disabled startup, earlier release, data and activation history", () =>
        {
            using var f = new Fixture();
            f.Activate("1.7.2", false);
            f.Activate("1.7.3", false);
            f.AssertActive("1.7.3", false);
            Assert(File.Exists(f.Executable("1.7.2")), "The previous release disappeared.");
            Assert(Directory.GetFiles(Path.Combine(f.InstallRoot, "activation-history")).Any(p => File.ReadAllText(p) == "receipt-1.7.2"), "The previous activation is not recoverable.");
            f.AssertRetained();
        });
        check("upgrade replaces the legacy Start shortcut with Codex Lover", () =>
        {
            using var f = new Fixture();
            f.Activate("1.7.2", true);
            File.Move(f.Shortcut, f.LegacyShortcut);
            f.Activate("1.7.3", true);
            f.AssertActive("1.7.3", true);
            Assert(!File.Exists(f.LegacyShortcut), "The legacy shortcut was left alongside Codex Lover.");
            f.AssertNoStage();
            f.AssertRetained();
        });
        check("locked legacy shortcut rolls back the upgrade without losing its old activation", () =>
        {
            using var f = new Fixture();
            f.Activate("1.7.2", true);
            File.Move(f.Shortcut, f.LegacyShortcut);
            using (File.Open(f.LegacyShortcut, FileMode.Open, FileAccess.Read, FileShare.Read))
                Expect<IOException>(() => f.Activate("1.7.3", false));
            Assert(!File.Exists(f.Shortcut), "A failed upgrade left the replacement shortcut active.");
            Assert(File.ReadAllText(f.LegacyShortcut) == f.Executable("1.7.2"), "The legacy shortcut changed after rollback.");
            Assert(File.ReadAllText(f.Pointer) == "receipt-1.7.2", "The previous activation was not restored.");
            Assert(f.Registration.Startup == f.Executable("1.7.2") && f.Registration.Uninstall == "1.7.2", "Windows registration was not restored.");
            f.AssertNoStage();
            f.AssertRetained();
        });
        foreach (var fault in new[] { "CreateShortcut", "SetStartup", "RegisterUninstall" })
        {
            check($"upgrade restores the previous activation after {fault} fails", () =>
            {
                using var f = new Fixture();
                f.Activate("1.7.2", true);
                f.Registration.FailOperation = fault;
                Expect<IOException>(() => f.Activate("1.7.3", false));
                f.AssertActive("1.7.2", true);
                f.AssertNoStage();
                f.AssertRetained();
                f.Activate("1.7.3", false); // Failure is one-shot; normal retry works.
                f.AssertActive("1.7.3", false);
            });
            check($"first install leaves no false activation after {fault} fails", () =>
            {
                using var f = new Fixture();
                f.Registration.FailOperation = fault;
                Expect<IOException>(() => f.Activate("1.7.3", true));
                f.AssertInactive();
                f.AssertNoStage();
                f.AssertRetained();
            });
        }
        check("locked activation pointer leaves old release active and startup restored", () =>
        {
            using var f = new Fixture();
            f.Activate("1.7.2", true);
            using (File.Open(f.Pointer, FileMode.Open, FileAccess.Read, FileShare.Read))
                Expect<IOException>(() => f.Activate("1.7.3", false));
            f.AssertActive("1.7.2", true);
            f.AssertNoStage();
        });
        check("rollback failure is reported and remaining rollback steps still run", () =>
        {
            using var f = new Fixture();
            f.Activate("1.7.2", true);
            f.Registration.FailOperation = "RegisterUninstall";
            f.Registration.FailStartupRestore = true;
            var error = Expect<AggregateException>(() => f.Activate("1.7.3", false));
            Assert(error.InnerExceptions.Count == 2, "Rollback failure was hidden.");
            Assert(File.ReadAllText(f.Pointer) == "receipt-1.7.2", "Pointer rollback was skipped.");
            Assert(File.ReadAllText(f.Shortcut) == f.Executable("1.7.2"), "Shortcut rollback was skipped.");
            Assert(f.Registration.Uninstall == "1.7.2", "Uninstall rollback was skipped.");
            f.AssertRetained();
        });
        check("uninstall removes activation only and preserves releases and account data", () =>
        {
            using var f = new Fixture();
            f.Activate("1.7.3", true);
            f.Activation.Uninstall();
            f.AssertInactive();
            f.AssertRetained();
            Assert(File.Exists(f.Executable("1.7.3")), "Uninstall removed retained release files.");
            f.Activation.Uninstall(); // Retry/idempotent cleanup.
            f.AssertInactive();
        });
        check("uninstall removes a legacy-only Start shortcut", () =>
        {
            using var f = new Fixture();
            f.Activate("1.7.2", true);
            File.Move(f.Shortcut, f.LegacyShortcut);
            f.Activation.Uninstall();
            f.AssertInactive();
            f.AssertRetained();
        });
        check("failed uninstall restores active pointer, shortcut and Windows registration", () =>
        {
            using var f = new Fixture();
            f.Activate("1.7.3", true);
            File.Copy(f.Shortcut, f.LegacyShortcut);
            f.Registration.FailOperation = "RemoveUninstall";
            Expect<AggregateException>(() => f.Activation.Uninstall());
            f.AssertActive("1.7.3", true);
            Assert(File.ReadAllText(f.LegacyShortcut) == f.Executable("1.7.3"), "Legacy shortcut rollback was skipped.");
            f.AssertRetained();
            f.Activation.Uninstall();
            f.AssertInactive();
        });
    }

    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    private static T Expect<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T exception) { return exception; }
        throw new Exception($"Expected {typeof(T).Name} was not reported.");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "CodexLoverInstallerChecks", Guid.NewGuid().ToString("N"));
        internal string InstallRoot => Path.Combine(_root, "Programs", "CodexLover");
        private string Programs => Path.Combine(_root, "StartMenu");
        internal string Pointer => Path.Combine(InstallRoot, "current.json");
        internal string Shortcut => Path.Combine(Programs, "Codex Lover.lnk");
        internal string LegacyShortcut => Path.Combine(Programs, "Codex Account Monitor.lnk");
        private string Data => Path.Combine(_root, "AccountData", "synthetic-data.txt");
        internal readonly MemoryRegistration Registration = new();
        internal InstallationActivation Activation { get; }
        internal Fixture()
        {
            Directory.CreateDirectory(InstallRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(Data)!);
            File.WriteAllText(Data, "synthetic-preserved-account-data");
            Activation = new(InstallRoot, Programs, Registration);
        }
        internal string Executable(string version) => Path.Combine(InstallRoot, version, "CodexAccountMonitor.exe");
        internal void Activate(string version, bool startup)
        {
            var exe = Executable(version);
            Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
            File.WriteAllText(exe, "synthetic-not-executable");
            Activation.Activate(exe, startup, Encoding.UTF8.GetBytes("receipt-" + version), "synthetic-uninstaller", version);
        }
        internal void AssertActive(string version, bool startup)
        {
            Assert(File.ReadAllText(Pointer) == "receipt-" + version, "Wrong active receipt.");
            Assert(File.ReadAllText(Shortcut) == Executable(version), "Wrong Start shortcut.");
            Assert(Registration.Startup == (startup ? Executable(version) : null), "Startup preference changed unexpectedly.");
            Assert(Registration.Uninstall == version, "Wrong Installed Apps registration.");
        }
        internal void AssertInactive()
        {
            Assert(!File.Exists(Pointer) && !File.Exists(Shortcut) && !File.Exists(LegacyShortcut), "Stale activation remains.");
            Assert(Registration.Startup is null && Registration.Uninstall is null, "Stale Windows registration remains.");
        }
        internal void AssertRetained() => Assert(File.ReadAllText(Data) == "synthetic-preserved-account-data", "Account data was modified.");
        internal void AssertNoStage() => Assert(!Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Any(p =>
            p.EndsWith(".tmp", StringComparison.Ordinal) || p.EndsWith(".restore", StringComparison.Ordinal) ||
            (p.EndsWith(".lnk", StringComparison.Ordinal) && p != Shortcut && p != LegacyShortcut)), "A temporary activation file remains.");
        public void Dispose()
        {
            var expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CodexLoverInstallerChecks")) + Path.DirectorySeparatorChar;
            var resolved = Path.GetFullPath(_root);
            if (!resolved.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe fixture cleanup path.");
            Directory.Delete(resolved, recursive: true);
        }
    }

    private sealed class MemoryRegistration : IInstallationRegistration
    {
        internal string? Startup;
        internal string? Uninstall;
        internal string? FailOperation;
        internal bool FailStartupRestore;
        public object CaptureStartup() => new StartupValue(Startup);
        public object? CaptureUninstall() => Uninstall;
        public void SetStartup(string executable, bool enabled) { Startup = enabled ? executable : null; Fail("SetStartup"); }
        public void RestoreStartup(object snapshot)
        {
            if (FailStartupRestore) throw new IOException("Injected startup rollback failure.");
            Startup = ((StartupValue)snapshot).Value;
        }
        public void RegisterUninstall(string executable, string uninstaller, string version) { Uninstall = version; Fail("RegisterUninstall"); }
        public void RestoreUninstall(object? snapshot) => Uninstall = (string?)snapshot;
        public void RemoveUninstall() { Uninstall = null; Fail("RemoveUninstall"); }
        public void CreateShortcut(string executable, string path) { File.WriteAllText(path, executable); Fail("CreateShortcut"); }
        private void Fail(string operation)
        {
            if (FailOperation != operation) return;
            FailOperation = null;
            throw new IOException("Injected " + operation + " failure after mutation.");
        }
        private sealed record StartupValue(string? Value);
    }
}
