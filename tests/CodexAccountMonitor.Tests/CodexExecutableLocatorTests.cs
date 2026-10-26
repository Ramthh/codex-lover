using System.ComponentModel;
using CodexAccountMonitor.Infrastructure;

namespace CodexAccountMonitor.Tests;

public sealed class CodexExecutableLocatorTests
{
    [Fact]
    public void BundledSignedRuntimeTakesPrecedenceOverAdjacentAndExternalInstalls()
    {
        using var files = new Fixture();
        var bundled = files.Create("Adjacent/codex-runtime/bin/codex.exe");
        files.Create("Adjacent/codex.exe");
        files.Create("Path/codex.exe");
        files.Create("Local/Programs/OpenAI/Codex/bin/codex.exe");
        var source = files.Source(path: files.PathOf("Path"));
        Assert.Equal(bundled, new CodexExecutableLocator(source.GetCandidates, _ => true).Locate());
    }

    [Fact]
    public void UntrustedBundledRuntimeIsNeverAccepted()
    {
        using var files = new Fixture();
        files.Create("Adjacent/codex-runtime/bin/codex.exe");
        var standalone = files.Create("Local/Programs/OpenAI/Codex/bin/codex.exe");
        var source = files.Source();
        Assert.Equal(standalone, new CodexExecutableLocator(source.GetCandidates, path => path == standalone).Locate());
    }

    [Fact]
    public void RegisteredPackageHelperIsFoundWithoutCliOnPath()
    {
        using var files = new Fixture();
        var desktop = files.Create("OtherDrivePackage/app/Codex.exe");
        var helper = files.Create("OtherDrivePackage/app/resources/codex.exe");
        var source = files.Source(desktops: [desktop]);
        Assert.Equal(helper, new CodexExecutableLocator(source.GetCandidates, path => path == helper).Locate());
    }

    [Theory]
    [InlineData("Local/Programs/Codex")]
    [InlineData("Local/OpenAI/Codex")]
    [InlineData("ProgramFiles/Codex")]
    [InlineData("ProgramFiles/OpenAI/Codex")]
    public void SupportedDesktopLayoutsUseResourcesHelper(string directory)
    {
        using var files = new Fixture();
        var desktop = files.Create(directory + "/Codex.exe");
        var helper = files.Create(directory + "/resources/codex.exe");
        var source = files.Source(desktops: [desktop]);
        Assert.Equal(helper, new CodexExecutableLocator(source.GetCandidates, _ => true).Locate());
    }

    [Fact]
    public void StandalonePathCliTakesPrecedenceOverProtectedPackageHelper()
    {
        using var files = new Fixture();
        var desktop = files.Create("Package/app/Codex.exe");
        var helper = files.Create("Package/app/resources/codex.exe");
        var cli = files.Create("Path/codex.exe");
        var source = files.Source(path: files.PathOf("Path"), desktops: [desktop]);
        var checkedPaths = new List<string>();
        var locator = new CodexExecutableLocator(source.GetCandidates, path => { checkedPaths.Add(path); return true; });
        Assert.Equal(cli, locator.Locate());
        Assert.Equal(new[] { cli }, checkedPaths);
    }

    [Theory]
    [InlineData("Local/Programs/OpenAI/Codex/bin/codex.exe")]
    [InlineData("Local/Programs/OpenAI/Codex/bin/version/codex.exe")]
    [InlineData("Local/OpenAI/Codex/bin/version/codex.exe")]
    public void StandaloneDefaultAndLegacyInstallsPrecedeDesktopWithoutPath(string relativeCli)
    {
        using var files = new Fixture();
        var desktop = files.Create("Package/app/Codex.exe");
        files.Create("Package/app/resources/codex.exe");
        var cli = files.Create(relativeCli);
        var source = files.Source(desktops: [desktop]);
        Assert.Equal(cli, new CodexExecutableLocator(source.GetCandidates, _ => true).Locate());
    }

    [Theory]
    [InlineData("Package/app")]
    [InlineData("Package/app/resources")]
    public void DesktopLauncherOrHelperOnPathCannotShadowStandaloneCli(string pathDirectory)
    {
        using var files = new Fixture();
        files.Create("Package/app/Codex.exe");
        files.Create("Package/app/resources/codex.exe");
        var cli = files.Create("Local/Programs/OpenAI/Codex/bin/codex.exe");
        var source = files.Source(path: files.PathOf(pathDirectory));
        Assert.Equal(cli, new CodexExecutableLocator(source.GetCandidates, _ => true).Locate());
    }

    [Fact]
    public void DesktopDirectoryOnPathUsesHelperInsteadOfSameNamedLauncher()
    {
        using var files = new Fixture();
        files.Create("Path/Codex.exe");
        var helper = files.Create("Path/resources/codex.exe");
        var source = files.Source(path: '"' + files.PathOf("Path") + '"');
        Assert.Equal(helper, new CodexExecutableLocator(source.GetCandidates, _ => true).Locate());
    }

    [Fact]
    public void DesktopWithoutHelperIsNeverAcceptedAsCli()
    {
        using var files = new Fixture();
        var desktop = files.Create("Path/Codex.exe");
        files.Create("Path/resources/app.asar");
        var source = files.Source(path: files.PathOf("Path"), desktops: [desktop]);
        var verified = new List<string>();
        Assert.Throws<FileNotFoundException>(() => new CodexExecutableLocator(source.GetCandidates,
            path => { verified.Add(path); return true; }).Locate());
        Assert.DoesNotContain(desktop, verified);
    }

    [Fact]
    public void UntrustedDesktopHelperIsSkippedForNextTrustedHelper()
    {
        using var files = new Fixture();
        var desktop = files.Create("Package/app/Codex.exe");
        files.Create("Package/app/resources/codex.exe");
        var secondDesktop = files.Create("Desktop/Codex.exe");
        var cli = files.Create("Desktop/resources/codex.exe");
        var source = files.Source(desktops: [desktop, secondDesktop]);
        Assert.Equal(cli, new CodexExecutableLocator(source.GetCandidates, path => path == cli).Locate());
    }

    [Fact]
    public void MissingInstallationProducesGuidanceWithoutTrustingAnyFile()
    {
        using var files = new Fixture();
        var checkedPaths = new List<string>();
        var source = files.Source();
        var error = Assert.Throws<FileNotFoundException>(() => new CodexExecutableLocator(source.GetCandidates,
            path => { checkedPaths.Add(path); return true; }).Locate());
        Assert.Contains("Install or update Codex", error.Message);
        Assert.Empty(checkedPaths);
    }

    [Fact]
    public void PackageDiscoveryFailureDoesNotHideAWorkingCliOnPath()
    {
        using var files = new Fixture();
        var cli = files.Create("Path/codex.exe");
        var source = new CodexCliCandidateSource(files.PathOf("Adjacent"), files.PathOf("Path"), files.PathOf("Local"),
            () => throw new Win32Exception(5));
        Assert.Equal(cli, new CodexExecutableLocator(source.GetCandidates, _ => true).Locate());
    }

    [Fact]
    public void LegacyBinStillSelectsNewestTrustedCli()
    {
        using var files = new Fixture();
        var older = files.Create("Local/OpenAI/Codex/bin/old/codex.exe");
        var newer = files.Create("Local/OpenAI/Codex/bin/new/codex.exe");
        File.SetLastWriteTimeUtc(older, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(newer, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var source = files.Source();
        Assert.Equal(newer, new CodexExecutableLocator(source.GetCandidates, _ => true).Locate());
        Assert.Equal(older, new CodexExecutableLocator(source.GetCandidates, path => path == older).Locate());
    }

    [Fact]
    public void ExplicitCliKeepsPrecedenceAndDoesNotEnumerateOtherInstallations()
    {
        using var files = new Fixture();
        var cli = files.Create("Custom/codex.exe");
        var locator = new CodexExecutableLocator(() => throw new InvalidOperationException("Should not search"), path => path == cli);
        Assert.Equal(cli, locator.Locate(cli));
    }

    [Theory]
    [InlineData("Codex.exe")]
    [InlineData("ChatGPT.exe")]
    public void ExplicitDesktopSelectionVerifiesHostAndResolvesVerifiedHelper(string executable)
    {
        using var files = new Fixture();
        var desktop = files.Create("Custom/" + executable);
        var helper = files.Create("Custom/resources/codex.exe");
        var verified = new List<string>();
        var locator = new CodexExecutableLocator(() => [], path => { verified.Add(path); return true; });
        Assert.Equal(helper, locator.Locate(desktop));
        Assert.Equal(new[] { desktop, helper }, verified);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void ExplicitDesktopRequiresBothSignatures(bool trustedHost, bool trustedHelper)
    {
        using var files = new Fixture();
        var desktop = files.Create("Custom/Codex.exe");
        var helper = files.Create("Custom/resources/codex.exe");
        var locator = new CodexExecutableLocator(() => [], path => path == desktop ? trustedHost : path == helper && trustedHelper);
        Assert.Throws<InvalidOperationException>(() => locator.Locate(desktop));
    }

    [Fact]
    public void ExplicitDesktopMissingHelperExplainsRecoveryWithoutReturningLauncher()
    {
        using var files = new Fixture();
        var desktop = files.Create("Custom/Codex.exe");
        files.Create("Custom/chrome_elf.dll");
        var locator = new CodexExecutableLocator(() => [], _ => true);
        var error = Assert.Throws<FileNotFoundException>(() => locator.Locate(desktop));
        Assert.Contains("missing its account connection helper", error.Message);
    }

    [Fact]
    public void MissingOrUntrustedOverrideDoesNotFallBack()
    {
        using var files = new Fixture();
        var existing = files.Create("Custom/codex.exe");
        var locator = new CodexExecutableLocator(() => throw new InvalidOperationException("Should not search"), _ => false);
        Assert.Throws<FileNotFoundException>(() => locator.Locate(files.PathOf("Missing/codex.exe")));
        Assert.Throws<InvalidOperationException>(() => locator.Locate(existing));
    }

    [Fact]
    public void ProductionSignatureCheckRejectsAnUnsignedExecutable()
    {
        using var files = new Fixture();
        var unsigned = files.Create("Untrusted/codex.exe");
        Assert.Throws<InvalidOperationException>(() => new CodexExecutableLocator().Locate(unsigned));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CodexLocatorTests", Guid.NewGuid().ToString("N"));
        public string PathOf(string relative) => System.IO.Path.GetFullPath(System.IO.Path.Combine(_root, relative));
        public string Create(string relative)
        {
            var file = PathOf(relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "Unsigned test fixture");
            return file;
        }
        public CodexCliCandidateSource Source(string path = "", IReadOnlyList<string>? desktops = null) =>
            new(PathOf("Adjacent"), path, PathOf("Local"), () => desktops ?? []);
        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }
}
