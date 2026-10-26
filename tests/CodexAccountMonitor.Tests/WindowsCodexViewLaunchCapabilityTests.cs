using System.ComponentModel;
using System.Diagnostics;
using System.Xml.Linq;
using CodexAccountMonitor.Infrastructure.DesktopSwitching;

namespace CodexAccountMonitor.Tests;

public sealed class WindowsCodexViewLaunchCapabilityTests
{
    [Fact]
    public void ExactRegisteredReviewedX64PackageSupportsTheCodexViewArgument()
    {
        using var fixture = new Fixture();
        Assert.True(fixture.Capability.Supports(fixture.Executable));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("publisher")]
    [InlineData("future-version")]
    [InlineData("older-version")]
    [InlineData("arm64")]
    [InlineData("x86")]
    [InlineData("entrypoint")]
    [InlineData("entrypoint-traversal")]
    [InlineData("missing-entrypoint")]
    [InlineData("duplicate-identity")]
    [InlineData("wrong-schema")]
    public void OtherPackageMetadataCannotEnableTheVersionSpecificArgument(string problem)
    {
        using var fixture = new Fixture();
        var manifest = fixture.ReadManifest();
        var identity = manifest.Root!.Element(Fixture.Schema + "Identity")!;
        var application = manifest.Root.Element(Fixture.Schema + "Applications")!.Element(Fixture.Schema + "Application")!;
        switch (problem)
        {
            case "name": identity.SetAttributeValue("Name", "Unrelated.Codex"); break;
            case "publisher": identity.SetAttributeValue("Publisher", "CN=Unreviewed publisher"); break;
            case "future-version": identity.SetAttributeValue("Version", "26.929.1.0"); break;
            case "older-version": identity.SetAttributeValue("Version", "26.927.1.0"); break;
            case "arm64": identity.SetAttributeValue("ProcessorArchitecture", "arm64"); break;
            case "x86": identity.SetAttributeValue("ProcessorArchitecture", "x86"); break;
            case "entrypoint": application.SetAttributeValue("Executable", @"app\Other.exe"); break;
            case "entrypoint-traversal": application.SetAttributeValue("Executable", @"app\..\app\ChatGPT.exe"); break;
            case "missing-entrypoint": application.Remove(); break;
            case "duplicate-identity": manifest.Root.Add(new XElement(identity)); break;
            case "wrong-schema":
                manifest.Root.Name = XName.Get("Package", "https://unrelated.example/schema");
                manifest.Root.SetAttributeValue("xmlns", "https://unrelated.example/schema");
                break;
        }
        fixture.WriteManifest(manifest);
        Assert.False(fixture.Capability.Supports(fixture.Executable));
    }

    [Fact]
    public void AMatchingManifestOutsideTheRegisteredPackageIsNotACapability()
    {
        using var fixture = new Fixture();
        fixture.RegisteredPaths = () => [];
        Assert.False(fixture.Capability.Supports(fixture.Executable));
        fixture.RegisteredPaths = () => [Path.Combine(fixture.Root, "another-package")];
        Assert.False(fixture.Capability.Supports(fixture.Executable));
    }

    [Theory]
    [InlineData("Codex.exe")]
    [InlineData("codex-cli.exe")]
    [InlineData("Other.exe")]
    public void LegacyAndOtherExecutableNamesRemainArgumentFree(string executableName)
    {
        using var fixture = new Fixture();
        Assert.False(fixture.Capability.Supports(Path.Combine(fixture.Root, "app", executableName)));
    }

    [Theory]
    [InlineData("dtd")]
    [InlineData("oversize")]
    [InlineData("malformed")]
    [InlineData("missing")]
    public void AnUnreadableOrUnsafeManifestCannotEnableTheArgument(string problem)
    {
        using var fixture = new Fixture();
        switch (problem)
        {
            case "dtd":
                File.WriteAllText(fixture.ManifestPath,
                    "<!DOCTYPE Package [<!ENTITY external SYSTEM 'https://unrelated.example/identity'>]>" + fixture.ReadManifest());
                break;
            case "oversize": File.WriteAllText(fixture.ManifestPath, new string(' ', 262145)); break;
            case "malformed": File.WriteAllText(fixture.ManifestPath, "<Package"); break;
            case "missing": File.Delete(fixture.ManifestPath); break;
        }
        Assert.False(fixture.Capability.Supports(fixture.Executable));
    }

    [Fact]
    public void FailedRegistrationDiscoveryFallsBackToOrdinaryLaunch()
    {
        using var fixture = new Fixture();
        fixture.RegisteredPaths = () => throw new Win32Exception(5);
        Assert.False(fixture.Capability.Supports(fixture.Executable));
    }

    [Fact]
    public void RealManifestGuardControlsTheActualLauncherRequestWithoutProtocolDispatch()
    {
        using var fixture = new Fixture();
        var requests = new List<ProcessStartInfo>();
        var launcher = new WindowsDesktopLauncher(new Processes(fixture.Executable), new TrustedSignature(), new VisibleWindow(),
            requests.Add, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(50), fixture.Capability.Supports);
        launcher.Launch(fixture.Executable);
        var codexRequest = Assert.Single(requests);
        Assert.Equal(fixture.Executable, codexRequest.FileName);
        Assert.Equal("codex://threads/new?mode=codex", Assert.Single(codexRequest.ArgumentList));

        var manifest = fixture.ReadManifest();
        manifest.Root!.Element(Fixture.Schema + "Identity")!.SetAttributeValue("Version", "26.929.1.0");
        fixture.WriteManifest(manifest);
        launcher.Launch(fixture.Executable);
        Assert.Equal(fixture.Executable, requests[1].FileName);
        Assert.Empty(requests[1].ArgumentList);
    }

    private sealed class Fixture : IDisposable
    {
        internal static readonly XNamespace Schema = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        private static readonly string Parent = Path.Combine(Path.GetTempPath(), "CodexLover-codex-launch-fixtures");
        public string Root { get; } = Path.Combine(Parent, Guid.NewGuid().ToString("N"));
        public string Executable => Path.Combine(Root, "app", "ChatGPT.exe");
        public string ManifestPath => Path.Combine(Root, "AppxManifest.xml");
        public Func<IReadOnlyList<string>> RegisteredPaths { get; set; }
        public WindowsCodexViewLaunchCapability Capability { get; }
        public Fixture()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Executable)!);
            File.WriteAllBytes(Executable, [0]);
            RegisteredPaths = () => [Root];
            Capability = new(() => RegisteredPaths());
            WriteManifest(new XDocument(new XElement(Schema + "Package",
                new XElement(Schema + "Identity", new XAttribute("Name", "OpenAI.Codex"),
                    new XAttribute("Publisher", "CN=50BDFD77-8903-4850-9FFE-6E8522F64D5B"),
                    new XAttribute("Version", "26.928.2636.0"), new XAttribute("ProcessorArchitecture", "x64")),
                new XElement(Schema + "Applications", new XElement(Schema + "Application",
                    new XAttribute("Id", "App"), new XAttribute("Executable", @"app\ChatGPT.exe"))))));
        }
        public XDocument ReadManifest() => XDocument.Load(ManifestPath);
        public void WriteManifest(XDocument manifest) => manifest.Save(ManifestPath);
        public void Dispose()
        {
            var resolved = Path.GetFullPath(Root);
            if (!resolved.StartsWith(Path.GetFullPath(Parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unsafe synthetic package cleanup path.");
            Directory.Delete(resolved, recursive: true);
        }
    }

    private sealed class Processes(string path) : IDesktopProcessSource
    {
        public IReadOnlyList<DesktopProcessInfo> ReadProcesses() => [new(123, 0, "ChatGPT.exe", path, 100)];
    }
    private sealed class TrustedSignature : IDesktopSignatureVerifier
    {
        public bool IsTrustedDesktopExecutable(string executablePath) => true;
    }
    private sealed class VisibleWindow : IDesktopWindowController
    {
        public IReadOnlyList<DesktopWindowInfo> GetWindows(int processId) => [new(new IntPtr(1234), 100)];
        public void ShowAndActivate(DesktopWindowInfo window, int processId) { }
        public bool IsVisible(DesktopWindowInfo window, int processId) => true;
    }
}
