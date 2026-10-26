using CodexAccountMonitor.Infrastructure.DesktopSwitching;
using static CodexAccountMonitor.Infrastructure.DesktopSwitching.WindowsDesktopInfrastructureSource;

namespace CodexAccountMonitor.Tests;

public sealed class WindowsDesktopInfrastructureSourceTests
{
    private const string AppPath = @"C:\Program Files\WindowsApps\OpenAI.Codex_1.0_x64__publisher\app\ChatGPT.exe";
    private const string ServicePath = @"C:\Program Files\WindowsApps\OpenAI.Codex_1.0_x64__publisher\app\resources\codex-windows-sandbox-service.exe";
    private static readonly CodexDesktopInstallation Installation = new(AppPath, "Codex");
    private static readonly DesktopProcessInfo Service = new(20, 4, ServiceExecutable, null);
    private static readonly SandboxServiceRegistration Good = new(20, ServicePath, true, true, true);

    [Fact]
    public void ExactRunningScmOwnedSignedManifestDeclaredSystemServiceCanBeExcluded()
    {
        var source = Create(Good);
        Assert.Equal(20, Assert.Single(source.GetIndependentProcessIds(Installation, [Service])));
    }

    [Fact]
    public void QuotedExactServiceImageIsSupported()
    {
        var source = Create(Good with { BinaryPath = '"' + ServicePath + '"' });
        Assert.Single(source.GetIndependentProcessIds(Installation, [Service]));
    }

    [Theory]
    [InlineData("wrong-pid")]
    [InlineData("stopped")]
    [InlineData("manual")]
    [InlineData("user-account")]
    [InlineData("wrong-directory")]
    [InlineData("extra-arguments")]
    public void UnverifiedServiceRegistrationCannotHideProcess(string problem)
    {
        var registration = problem switch
        {
            "wrong-pid" => Good with { ProcessId = 21 },
            "stopped" => Good with { IsRunning = false },
            "manual" => Good with { IsAutoStart = false },
            "user-account" => Good with { IsLocalSystem = false },
            "wrong-directory" => Good with { BinaryPath = @"C:\Other\codex-windows-sandbox-service.exe" },
            _ => Good with { BinaryPath = ServicePath + " --unverified" }
        };
        Assert.Empty(Create(registration).GetIndependentProcessIds(Installation, [Service]));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void BothTrustedSignatureAndPackageServiceDeclarationAreRequired(bool signature, bool manifest)
    {
        var source = new WindowsDesktopInfrastructureSource(() => Good, _ => signature, _ => manifest);
        Assert.Empty(source.GetIndependentProcessIds(Installation, [Service]));
    }

    [Fact]
    public void ChangedScmPidAfterVerificationCannotExemptStaleProcess()
    {
        var reads = 0;
        var source = new WindowsDesktopInfrastructureSource(() => ++reads == 1 ? Good : Good with { ProcessId = 21 }, _ => true, _ => true);
        Assert.Empty(source.GetIndependentProcessIds(Installation, [Service]));
    }

    [Fact]
    public void SamePidWithDifferentNameOrReadablePathCannotBeExempted()
    {
        Assert.Empty(Create(Good).GetIndependentProcessIds(Installation, [Service with { Name = "codex.exe" }]));
        Assert.Empty(Create(Good).GetIndependentProcessIds(Installation, [Service with { ExecutablePath = @"C:\Other\codex-windows-sandbox-service.exe" }]));
    }

    [Fact]
    public void ServiceLookupFailureLeavesProcessBlocking()
    {
        var source = new WindowsDesktopInfrastructureSource(() => throw new UnauthorizedAccessException(), _ => true, _ => true);
        Assert.Empty(source.GetIndependentProcessIds(Installation, [Service]));
    }

    private static WindowsDesktopInfrastructureSource Create(SandboxServiceRegistration registration) =>
        new(() => registration, _ => true, _ => true);
}
