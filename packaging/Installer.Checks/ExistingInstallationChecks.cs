using System.Security.Cryptography;
using System.Text.Json;

internal static class ExistingInstallationChecks
{
    internal static void Run(Action<string, Action> check, Func<string, object[], object?> call)
    {
        check("an intact existing release with matching payload is reused", () =>
        {
            using var fixture = new Fixture(call);
            string existing = fixture.CreateRelease("existing", Fixture.ExpectedBytes);
            Assert(fixture.Find() == existing, "A verified matching release was not reused.");
        });

        foreach (var damage in new[]
        {
            (Name: "file size", Bytes: new byte[] { 1, 2, 3, 4, 5 }),
            (Name: "same-length checksum", Bytes: new byte[] { 4, 3, 2, 1 })
        })
        {
            check($"{damage.Name} corruption selects a fresh verified candidate and preserves the damaged release", () =>
            {
                using var fixture = new Fixture(call);
                string damaged = fixture.CreateRelease("damaged", damage.Bytes);
                // No valid release exists yet: production setup must extract a fresh copy.
                Assert(fixture.Find() is null, "A damaged release was reused instead of requiring fresh extraction.");
                fixture.AssertUnchanged(damaged, damage.Bytes);
                string fresh = fixture.CreateRelease("fresh", Fixture.ExpectedBytes);
                Assert(fixture.Find() == fresh, "The fresh verified copy was not selected after an older damaged release.");
                fixture.AssertUnchanged(damaged, damage.Bytes);
            });
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class Fixture : IDisposable
    {
        internal static readonly byte[] ExpectedBytes = [1, 2, 3, 4];
        private const string Version = "1.7.5";
        private static readonly string PayloadHash = new('a', 64);
        private readonly string _fixtureParent = Path.Combine(Path.GetTempPath(), "CodexLoverExistingInstallationChecks");
        private readonly string _root;
        private readonly Func<string, object[], object?> _call;
        private readonly object _files;

        internal Fixture(Func<string, object[], object?> call)
        {
            _root = Path.Combine(_fixtureParent, Guid.NewGuid().ToString("N"));
            _call = call;
            Directory.CreateDirectory(_root);
            using var manifest = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                files = new[] { new { path = "CodexAccountMonitor.exe", bytes = ExpectedBytes.Length,
                    sha256 = Convert.ToHexString(SHA256.HashData(ExpectedBytes)) } }
            }));
            _files = call("ReadPackageFiles", [manifest.RootElement])!;
        }

        internal string CreateRelease(string name, byte[] contents)
        {
            string directory = Path.Combine(_root, name);
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, "CodexAccountMonitor.exe"), contents);
            File.WriteAllText(Path.Combine(directory, "installation.json"), JsonSerializer.Serialize(new
            {
                version = Version, payloadSha256 = PayloadHash
            }));
            return directory;
        }

        internal string? Find() => (string?)_call("FindVerifiedInstallation", [_root, Version, PayloadHash, _files]);

        internal void AssertUnchanged(string directory, byte[] contents)
        {
            Assert(File.Exists(Path.Combine(directory, "installation.json")), "The damaged release identity was removed.");
            Assert(File.ReadAllBytes(Path.Combine(directory, "CodexAccountMonitor.exe")).SequenceEqual(contents),
                "The damaged release was deleted or modified during discovery.");
        }

        public void Dispose()
        {
            string resolved = Path.GetFullPath(_root);
            string expectedParent = Path.GetFullPath(_fixtureParent) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unsafe fixture cleanup path.");
            Directory.Delete(resolved, recursive: true);
        }
    }
}
