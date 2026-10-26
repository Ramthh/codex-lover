using System.Diagnostics;
using CodexAccountMonitor.Infrastructure.DesktopSwitching;

namespace CodexAccountMonitor.Tests;

public sealed class WindowsDesktopLauncherTests
{
    private const string DesktopPath = @"C:\fixture\Codex.exe";

    [Fact]
    public void SuccessfulShellRequestWaitsForVisibleWindowAndRestoresIt()
    {
        var fixture = new Fixture();
        fixture.Windows.VisibleAfterShows = 2;
        fixture.Launcher.Launch(DesktopPath);
        Assert.Equal(1, fixture.Starts);
        Assert.Equal(2, fixture.Windows.Shows);
        var request = Assert.Single(fixture.Requests);
        Assert.Equal(DesktopPath, request.FileName);
        Assert.Empty(request.ArgumentList);
    }

    [Fact]
    public void WindowlessFirstLaunchGetsOneVerifiedActivationRetry()
    {
        var fixture = new Fixture();
        fixture.Windows.HasWindow = () => fixture.Starts == 2;
        fixture.Launcher.Launch(DesktopPath);
        Assert.Equal(2, fixture.Starts);
        Assert.Equal(2, fixture.Signatures.Checks);
        Assert.Equal(1, fixture.Windows.Shows);
    }

    [Fact]
    public void InvisibleWindowNeverReportsSuccessAndRetryIsBounded()
    {
        var fixture = new Fixture();
        fixture.Windows.VisibleAfterShows = int.MaxValue;
        Assert.Throws<InvalidOperationException>(() => fixture.Launcher.Launch(DesktopPath));
        Assert.Equal(2, fixture.Starts);
        Assert.True(fixture.Windows.Shows > 0);
    }

    [Fact]
    public void UntrustedExecutableIsNeverLaunched()
    {
        var fixture = new Fixture();
        fixture.Signatures.Trusted = false;
        Assert.Throws<InvalidOperationException>(() => fixture.Launcher.Launch(DesktopPath));
        Assert.Equal(0, fixture.Starts);
        Assert.Equal(0, fixture.Windows.Shows);
        Assert.Equal(0, fixture.CapabilityChecks);
    }

    [Fact]
    public void ReviewedMergedDesktopGetsOneFixedCodexLinkOnTheVerifiedExecutable()
    {
        const string mergedPath = @"C:\fixture with spaces\app\ChatGPT.exe";
        var fixture = new Fixture { SelectCodexView = _ => true };
        fixture.Processes.OnRead = _ => [new(123, 0, "ChatGPT.exe", mergedPath, 100)];
        fixture.Launcher.Launch(mergedPath);
        var request = Assert.Single(fixture.Requests);
        Assert.Equal(mergedPath, request.FileName);
        Assert.Equal(Path.GetDirectoryName(mergedPath), request.WorkingDirectory);
        Assert.Equal("codex://threads/new?mode=codex", Assert.Single(request.ArgumentList));
        Assert.True(request.UseShellExecute);
        Assert.Equal(ProcessWindowStyle.Normal, request.WindowStyle);
        Assert.Equal(1, fixture.Signatures.Checks);
    }

    [Fact]
    public void MergedActivationRetryKeepsTheOriginalIntentAndRevalidatesTrust()
    {
        const string mergedPath = @"C:\fixture\app\ChatGPT.exe";
        var fixture = new Fixture { SelectCodexView = _ => true };
        fixture.Processes.OnRead = _ => [new(123, 0, "ChatGPT.exe", mergedPath, 100)];
        fixture.Windows.HasWindow = () => fixture.Starts == 2;
        fixture.OnStart = () => fixture.SelectCodexView = _ => false;
        fixture.Launcher.Launch(mergedPath);
        Assert.Equal(2, fixture.Starts);
        Assert.All(fixture.Requests, request => Assert.Equal("codex://threads/new?mode=codex", Assert.Single(request.ArgumentList)));
        Assert.Equal(1, fixture.CapabilityChecks);
        Assert.Equal(2, fixture.Signatures.Checks);
    }

    [Fact]
    public void UnknownMergedDesktopPreservesArgumentFreeLaunch()
    {
        const string mergedPath = @"C:\fixture\app\ChatGPT.exe";
        var fixture = new Fixture();
        fixture.Processes.OnRead = _ => [new(123, 0, "ChatGPT.exe", mergedPath, 100)];
        fixture.Launcher.Launch(mergedPath);
        Assert.Equal(mergedPath, Assert.Single(fixture.Requests).FileName);
        Assert.Empty(fixture.Requests[0].ArgumentList);
    }

    [Fact]
    public void RetryRechecksExecutableTrust()
    {
        var fixture = new Fixture();
        fixture.Windows.HasWindow = () => false;
        fixture.OnStart = () => fixture.Signatures.Trusted = false;
        Assert.Throws<InvalidOperationException>(() => fixture.Launcher.Launch(DesktopPath));
        Assert.Equal(1, fixture.Starts);
    }

    [Fact]
    public void ReusedPidCannotAuthorizeRestoringUnrelatedWindow()
    {
        var fixture = new Fixture();
        fixture.Processes.OnRead = read => read % 2 == 1
            ? [new(123, 0, "Codex.exe", DesktopPath, 100)]
            : [new(123, 0, "Codex.exe", DesktopPath, 200)];
        Assert.Throws<InvalidOperationException>(() => fixture.Launcher.Launch(DesktopPath));
        Assert.Equal(0, fixture.Windows.Shows);
    }

    [Fact]
    public void AnotherExecutableOrUnknownCreationTimeCannotCompleteLaunch()
    {
        var fixture = new Fixture();
        fixture.Processes.OnRead = _ => [new(123, 0, "Codex.exe", @"C:\other\Codex.exe", 100),
            new(124, 0, "Codex.exe", DesktopPath, null)];
        Assert.Throws<InvalidOperationException>(() => fixture.Launcher.Launch(DesktopPath));
        Assert.Equal(0, fixture.Windows.Shows);
    }

    private sealed class Fixture
    {
        public FakeProcesses Processes { get; } = new();
        public FakeSignatures Signatures { get; } = new();
        public FakeWindows Windows { get; } = new();
        public int Starts { get; private set; }
        public Action? OnStart { get; set; }
        public List<ProcessStartInfo> Requests { get; } = [];
        public Func<string, bool> SelectCodexView { get; set; } = _ => false;
        public int CapabilityChecks { get; private set; }
        public WindowsDesktopLauncher Launcher { get; }
        public Fixture() => Launcher = new(Processes, Signatures, Windows,
            request => { Requests.Add(request); Starts++; OnStart?.Invoke(); }, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(200),
            path => { CapabilityChecks++; return SelectCodexView(path); });
    }

    private sealed class FakeProcesses : IDesktopProcessSource
    {
        private int _reads;
        public Func<int, IReadOnlyList<DesktopProcessInfo>> OnRead { get; set; } = _ => [new(123, 0, "Codex.exe", DesktopPath, 100)];
        public IReadOnlyList<DesktopProcessInfo> ReadProcesses() => OnRead(++_reads);
    }

    private sealed class FakeSignatures : IDesktopSignatureVerifier
    {
        public bool Trusted { get; set; } = true;
        public int Checks { get; private set; }
        public bool IsTrustedDesktopExecutable(string executablePath) { Checks++; return Trusted; }
    }

    private sealed class FakeWindows : IDesktopWindowController
    {
        public Func<bool> HasWindow { get; set; } = () => true;
        public int VisibleAfterShows { get; set; } = 1;
        public int Shows { get; private set; }
        public IReadOnlyList<DesktopWindowInfo> GetWindows(int processId) => HasWindow() ? [new(new IntPtr(1234), 100)] : [];
        public void ShowAndActivate(DesktopWindowInfo window, int processId) => Shows++;
        public bool IsVisible(DesktopWindowInfo window, int processId) => Shows >= VisibleAfterShows;
    }
}
