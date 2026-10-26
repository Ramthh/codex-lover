using System.Text.Json;
using CodexAccountMonitor.Setup;

internal static class InstallationLocationChecks
{
    internal static void Run(Action<string, Action> check, Func<string, object[], object?> call)
    {
        check("a new local installation folder with spaces is accepted without creating it", () =>
        {
            using var fixture = new Fixture();
            string path = Path.Combine(fixture.Root, "Custom App", "CodexLover");
            Assert(InstallationLocation.Normalize(path) == Path.GetFullPath(path), "A valid local folder was rejected.");
            Assert(!Directory.Exists(path), "Validation wrote an installation folder.");
        });
        check("an empty local folder is prepared with an ownership marker and supports reinstall", () =>
        {
            using var fixture = new Fixture();
            string path = Path.Combine(fixture.Root, "CodexLover");
            Directory.CreateDirectory(path);
            Assert(InstallationLocation.Normalize(path) == path, "An existing empty folder was rejected.");
            InstallationLocation.Prepare(path);
            string marker = Path.Combine(path, InstallationLocation.MarkerName);
            byte[] markerBytes = File.ReadAllBytes(marker);
            string retained = Path.Combine(path, "retained-release.txt");
            File.WriteAllText(retained, "existing installation fixture");
            InstallationLocation.Prepare(path);
            Assert(File.ReadAllBytes(marker).SequenceEqual(markerBytes), "Reinstall changed its ownership marker.");
            Assert(File.ReadAllText(retained) == "existing installation fixture", "Reinstall changed retained release content.");
            Assert(!Directory.EnumerateFiles(path, ".setup-write-check-*").Any(), "A write probe was left behind.");
        });
        var rejectedPaths = new[]
        {
            (Name: "drive root", Path: Path.GetPathRoot(Environment.SystemDirectory)!),
            (Name: "network path", Path: @"\\server\share\CodexLover"),
            (Name: "relative path", Path: @"Apps\CodexLover"),
            (Name: "Windows folder", Path: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "CodexLover")),
            (Name: "Codex credentials folder", Path: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "CodexLover")),
            (Name: "Codex Lover credentials folder", Path: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex-lover", "CodexLover"))
        };
        foreach (var candidate in rejectedPaths)
            check("installation folder rejects " + candidate.Name,
                () => Expect<InvalidDataException>(() => InstallationLocation.Normalize(candidate.Path)));
        check("an unrelated nonempty folder is rejected and its files are retained", () =>
        {
            using var fixture = new Fixture();
            string file = Path.Combine(fixture.Root, "unrelated-document.txt");
            File.WriteAllText(file, "user-owned fixture");
            Expect<InvalidDataException>(() => InstallationLocation.Normalize(fixture.Root));
            Expect<InvalidDataException>(() => InstallationLocation.Prepare(fixture.Root));
            Assert(File.ReadAllText(file) == "user-owned fixture", "Location rejection modified unrelated data.");
            Assert(!File.Exists(Path.Combine(fixture.Root, InstallationLocation.MarkerName)), "Rejected location acquired an ownership marker.");
        });
        check("a legacy release receipt is accepted without moving or changing its files", () =>
        {
            using var fixture = new Fixture();
            string release = Path.Combine(fixture.Root, "1.7.5-existing");
            Directory.CreateDirectory(release);
            string executable = Path.Combine(release, "CodexAccountMonitor.exe");
            File.WriteAllText(executable, "synthetic executable");
            string receipt = Path.Combine(release, "installation.json");
            File.WriteAllText(receipt, JsonSerializer.Serialize(new { executable }));
            File.WriteAllText(Path.Combine(release, "publish-manifest.json"), "{}");
            byte[] originalReceipt = File.ReadAllBytes(receipt);
            Assert(InstallationLocation.Normalize(fixture.Root) == fixture.Root, "A legacy Codex Lover release was not recognized.");
            InstallationLocation.Prepare(fixture.Root);
            Assert(File.ReadAllBytes(receipt).SequenceEqual(originalReceipt), "Preparing the legacy folder changed its receipt.");
            Assert(File.ReadAllText(executable) == "synthetic executable", "Preparing the legacy folder changed the retained executable.");
        });
        check("a mismatched legacy receipt cannot claim an unrelated nonempty folder", () =>
        {
            using var fixture = new Fixture();
            string release = Path.Combine(fixture.Root, "release");
            Directory.CreateDirectory(release);
            File.WriteAllText(Path.Combine(release, "installation.json"), JsonSerializer.Serialize(new { executable = Path.Combine(fixture.Root, "elsewhere.exe") }));
            File.WriteAllText(Path.Combine(release, "publish-manifest.json"), "{}");
            Expect<InvalidDataException>(() => InstallationLocation.Normalize(fixture.Root));
        });
        check("an invalid ownership marker cannot claim an unrelated nonempty folder", () =>
        {
            using var fixture = new Fixture();
            File.WriteAllText(Path.Combine(fixture.Root, InstallationLocation.MarkerName), "not an installation marker");
            Expect<InvalidDataException>(() => InstallationLocation.Normalize(fixture.Root));
        });
        check("the install directory argument preserves spaces and accepts one explicit folder", () =>
        {
            string path = @"C:\Custom Apps\CodexLover";
            Assert(Equals(call("ReadInstallDirectoryArgument", [new[] { "--silent", "--install-dir", path }]), path),
                "The install directory argument changed the requested path.");
            Assert(call("ReadInstallDirectoryArgument", [new[] { "--silent" }]) is null,
                "A default invocation unexpectedly requested a custom folder.");
        });
        foreach (var args in new[]
        {
            new[] { "--install-dir" },
            new[] { "--install-dir", "--silent" },
            new[] { "--install-dir", @"C:\First", "--install-dir", @"C:\Second" }
        })
            check("the install directory argument rejects " + string.Join(" ", args),
                () => Expect<ArgumentException>(() => call("ReadInstallDirectoryArgument", [args])));
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name} was not reported.");
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string _parent = Path.Combine(Path.GetTempPath(), "CodexLoverInstallationLocationChecks");
        internal string Root { get; }
        internal Fixture()
        {
            Root = Path.Combine(_parent, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }
        public void Dispose()
        {
            string resolved = Path.GetFullPath(Root);
            string expectedParent = Path.GetFullPath(_parent) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unsafe fixture cleanup path.");
            Directory.Delete(resolved, recursive: true);
        }
    }
}
