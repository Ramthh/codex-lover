using CodexAccountMonitor.Infrastructure.DesktopSwitching;

namespace CodexAccountMonitor.Tests;

public sealed class WindowsDesktopQuitRequesterTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task SmallCaptionedPrimaryReceivesQuitInsteadOfLargerExposedBlankPrewarm(bool rawVisible, bool iconic)
    {
        var fixture = new Fixture();
        fixture.Windows.Candidates = [new(new(20), 960L * 720, HasCaption: false),
            new(new(10), iconic ? 160L * 28 : 420L * 300,
                RawVisible: rawVisible, Iconic: iconic, RestoredArea: 420L * 300)];
        fixture.Environment.OnOpen = () => fixture.Windows.Candidates = fixture.Windows.Candidates
            .Select(window => window.Handle == new IntPtr(10) ? window with { RawVisible = true } : window).ToArray();
        await fixture.Request();
        Assert.Equal(new IntPtr(10), Assert.Single(fixture.Windows.Shown));
        Assert.Equal((new IntPtr(10), 123), Assert.Single(fixture.Environment.Sent));
        Assert.Equal(rawVisible ? 0 : 1, fixture.Environment.Opened.Count);
    }

    [Fact]
    public async Task SmallDisabledPrimaryRefusesInsteadOfLargerExposedBlankPrewarm()
    {
        var fixture = new Fixture();
        fixture.Windows.Candidates = [new(new(20), 960L * 720, HasCaption: false),
            new(new(10), 420L * 300, Enabled: false)];
        var error = await Assert.ThrowsAsync<InvalidOperationException>(fixture.Request);
        Assert.Contains("dialog", error.Message);
        Assert.Empty(fixture.Windows.Shown);
        Assert.Empty(fixture.Environment.Sent);
    }

    [Fact]
    public async Task MinimizedMainReceivesOneQuitDespiteVisiblePrewarmAndLargerHiddenHelper()
    {
        var fixture = new Fixture();
        fixture.Windows.Candidates = [
            new(new(20), 960L * 720),
            new(new(30), 1600L * 1000, RawVisible: false),
            new(new(10), 160L * 28, Iconic: true, RestoredArea: 1280L * 620)];
        await fixture.Request();
        Assert.Equal(new IntPtr(10), Assert.Single(fixture.Windows.Shown));
        Assert.Equal((new IntPtr(10), 123), Assert.Single(fixture.Environment.Sent));
        Assert.Empty(fixture.Environment.Opened);
    }

    [Fact]
    public async Task HiddenTrayMainWaitsForVendorReopenBeforeNativeActivationAndQuit()
    {
        var fixture = new Fixture();
        var sequence = new List<string>();
        var opened = false;
        var vendorVisible = false;
        fixture.Environment.OnOpen = () => { opened = true; sequence.Add("public-open"); };
        fixture.Windows.ReadCandidates = read =>
        {
            if (opened && read >= 3 && !vendorVisible)
            {
                vendorVisible = true;
                sequence.Add("vendor-visible");
            }
            return [new(new(10), 1280L * 620, RawVisible: vendorVisible)];
        };
        fixture.Windows.OnShow = handle =>
        {
            Assert.True(vendorVisible);
            sequence.Add("native-focus");
            fixture.Environment.Foreground = handle;
        };
        await fixture.Request();
        Assert.Equal(["public-open", "vendor-visible", "native-focus"], sequence);
        Assert.Equal(Fixture.ExecutablePath, Assert.Single(fixture.Environment.Opened));
        Assert.Equal(new IntPtr(10), Assert.Single(fixture.Windows.Shown));
        Assert.Single(fixture.Environment.Sent);
    }

    [Fact]
    public async Task TrayMainThatStaysHiddenTimesOutWithoutNativeShowingOrQuit()
    {
        var fixture = new Fixture();
        fixture.Windows.Candidates = [new(new(10), 1280L * 620, RawVisible: false),
            new(new(20), 960L * 720, HasCaption: false)];
        await Assert.ThrowsAsync<InvalidOperationException>(fixture.Request);
        Assert.Single(fixture.Environment.Opened);
        Assert.Empty(fixture.Windows.Shown);
        Assert.Empty(fixture.Environment.Sent);
    }

    [Fact]
    public async Task DisabledHiddenMainRefusesBeforePublicReopen()
    {
        var fixture = new Fixture();
        fixture.Windows.Candidates = [new(new(10), 420L * 300, RawVisible: false, Enabled: false),
            new(new(20), 960L * 720, HasCaption: false)];
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(fixture.Request);
        Assert.Contains("dialog", failure.Message);
        Assert.Empty(fixture.Environment.Opened);
        Assert.Empty(fixture.Windows.Shown);
        Assert.Empty(fixture.Environment.Sent);
    }

    [Fact]
    public async Task IdentityChangingAfterTrayReopenPreventsNativeActivationAndQuit()
    {
        var fixture = new Fixture();
        fixture.Windows.Candidates = [new(new(10), 420L * 300, RawVisible: false)];
        var identityValid = true;
        fixture.Revalidate = () => identityValid;
        fixture.Environment.OnOpen = () =>
        {
            identityValid = false;
            fixture.Windows.Candidates = [new(new(10), 420L * 300)];
        };
        await Assert.ThrowsAsync<InvalidOperationException>(fixture.Request);
        Assert.Single(fixture.Environment.Opened);
        Assert.Empty(fixture.Windows.Shown);
        Assert.Empty(fixture.Environment.Sent);
    }

    [Fact]
    public async Task CancellationAfterTrayReopenPreventsNativeActivationAndQuit()
    {
        var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Windows.Candidates = [new(new(10), 420L * 300, RawVisible: false)];
        fixture.Environment.OnOpen = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Request(cancellation.Token));
        Assert.Single(fixture.Environment.Opened);
        Assert.Empty(fixture.Windows.Shown);
        Assert.Empty(fixture.Environment.Sent);
    }

    [Fact]
    public async Task WindowlessReopenDoesNotRepeatWhenHiddenMainAppearsBeforeVendorShowsIt()
    {
        var fixture = new Fixture();
        fixture.Windows.ReadCandidates = read => read switch
        {
            1 => [],
            2 => [new(new(10), 420L * 300, RawVisible: false)],
            _ => [new(new(10), 420L * 300)]
        };
        await fixture.Request();
        Assert.Single(fixture.Environment.Opened);
        Assert.Equal(new IntPtr(10), Assert.Single(fixture.Windows.Shown));
        Assert.Single(fixture.Environment.Sent);
    }

    [Fact]
    public async Task DisabledMainRefusesInsteadOfActivatingSmallerEnabledHelper()
    {
        var fixture = new Fixture();
        fixture.Windows.Candidates = [new(new(20), 960L * 720),
            new(new(10), 160L * 28, Iconic: true, RestoredArea: 1280L * 620, Enabled: false)];
        var error = await Assert.ThrowsAsync<InvalidOperationException>(fixture.Request);
        Assert.Contains("dialog", error.Message);
        Assert.Empty(fixture.Windows.Shown);
        Assert.Empty(fixture.Environment.Sent);
    }

    [Fact]
    public async Task SameProcessPopupTakingFocusDuringActivationNeverReceivesQuit()
    {
        var fixture = new Fixture();
        fixture.Windows.OnShow = _ => fixture.Environment.Foreground = new(99);
        await Assert.ThrowsAsync<InvalidOperationException>(fixture.Request);
        Assert.NotEmpty(fixture.Windows.Shown);
        Assert.Empty(fixture.Environment.Sent);
        Assert.Empty(fixture.Environment.Opened);
    }

    [Fact]
    public async Task FocusMovingToSameProcessPopupAfterActivationRefusesInput()
    {
        var fixture = new Fixture();
        fixture.Environment.ReadForeground = read => read == 1 ? new(10) : new(99);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(fixture.Request);
        Assert.Contains("Focus moved", error.Message);
        Assert.Single(fixture.Windows.Shown);
        Assert.Empty(fixture.Environment.Sent);
    }

    [Theory]
    [InlineData("disappeared")]
    [InlineData("owned")]
    [InlineData("tool")]
    [InlineData("noactivate")]
    [InlineData("disabled")]
    public async Task MainMustStillBeEligibleAtInputBoundary(string change)
    {
        var fixture = new Fixture();
        fixture.Windows.ReadCandidates = read => read == 1 ? [new(new(10), 1000)] : change switch
        {
            "owned" => [new(new(10), 1000, Owned: true)],
            "tool" => [new(new(10), 1000, ToolWindow: true)],
            "noactivate" => [new(new(10), 1000, NoActivate: true)],
            "disabled" => [new(new(10), 1000, Enabled: false)],
            _ => []
        };
        await Assert.ThrowsAsync<InvalidOperationException>(fixture.Request);
        Assert.Single(fixture.Windows.Shown);
        Assert.Empty(fixture.Environment.Sent);
    }

    [Fact]
    public async Task RestoringWithoutBecomingVisibleDoesNotAuthorizeQuit()
    {
        var fixture = new Fixture();
        fixture.Windows.Visible = false;
        await Assert.ThrowsAsync<InvalidOperationException>(fixture.Request);
        Assert.Empty(fixture.Environment.Sent);
    }

    [Fact]
    public async Task LostProcessIdentityAfterActivationPreventsInput()
    {
        var fixture = new Fixture();
        var identityReads = 0;
        fixture.Revalidate = () => ++identityReads < 3;
        await Assert.ThrowsAsync<InvalidOperationException>(fixture.Request);
        Assert.Single(fixture.Windows.Shown);
        Assert.Empty(fixture.Environment.Sent);
    }

    [Fact]
    public async Task AnotherSessionIsRefusedBeforeShowingWindows()
    {
        var fixture = new Fixture();
        fixture.Environment.OnSession = _ => throw new InvalidOperationException("Other session");
        await Assert.ThrowsAsync<InvalidOperationException>(fixture.Request);
        Assert.Empty(fixture.Windows.Shown);
        Assert.Empty(fixture.Environment.Sent);
        Assert.Empty(fixture.Environment.Opened);
    }

    [Fact]
    public async Task HeldModifierAtFinalCheckPreventsInput()
    {
        var fixture = new Fixture();
        var checks = 0;
        fixture.Environment.OnModifierCheck = () =>
        {
            if (++checks == 2) throw new InvalidOperationException("Held modifier");
        };
        await Assert.ThrowsAsync<InvalidOperationException>(fixture.Request);
        Assert.Single(fixture.Windows.Shown);
        Assert.Empty(fixture.Environment.Sent);
    }

    [Fact]
    public async Task CancellationDuringRestorePreventsInput()
    {
        var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Windows.OnShow = _ => cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Request(cancellation.Token));
        Assert.Empty(fixture.Environment.Sent);
    }

    [Fact]
    public async Task WindowlessRootGetsAtMostOneDirectOpenRequestAndNoQuit()
    {
        var fixture = new Fixture();
        fixture.Windows.Candidates = [];
        await Assert.ThrowsAsync<InvalidOperationException>(fixture.Request);
        Assert.Equal(Fixture.ExecutablePath, Assert.Single(fixture.Environment.Opened));
        Assert.Empty(fixture.Environment.Sent);
    }

    [Fact]
    public async Task SeparateRootsAreSelectedAndRevalidatedIndependently()
    {
        var fixture = new Fixture();
        fixture.Windows.ByProcess = pid => [new(new(pid * 10), 1000)];
        var validatedRoots = new List<int>();
        foreach (var processId in new[] { 123, 456 })
        {
            await fixture.Requester.RequestQuitAsync(new(processId, 0, "ChatGPT.exe", Fixture.ExecutablePath, 100),
                () => { validatedRoots.Add(processId); return true; }, CancellationToken.None);
        }
        Assert.Equal([(new IntPtr(1230), 123), (new IntPtr(4560), 456)], fixture.Environment.Sent);
        Assert.Equal([123, 123, 123, 456, 456, 456], validatedRoots);
        Assert.All(fixture.Windows.ReadProcesses, pid => Assert.Contains(pid, new[] { 123, 456 }));
    }

    private sealed class Fixture
    {
        internal const string ExecutablePath = @"C:\fixture\ChatGPT.exe";
        public FakeWindows Windows { get; } = new();
        public FakeEnvironment Environment { get; } = new();
        public WindowsDesktopQuitRequester Requester { get; }
        public Func<bool> Revalidate { get; set; } = () => true;
        public Fixture()
        {
            Windows.OnShow = handle => Environment.Foreground = handle;
            Requester = new(Windows, Environment, TimeSpan.FromMilliseconds(100),
                (_, token) => Task.Delay(1, token));
        }
        public Task Request() => Request(CancellationToken.None);
        public Task Request(CancellationToken cancellationToken) => Requester.RequestQuitAsync(
            new(123, 0, "ChatGPT.exe", ExecutablePath, 100), Revalidate, cancellationToken);
    }

    private sealed class FakeWindows : IDesktopWindowController
    {
        public IReadOnlyList<DesktopWindowInfo> Candidates { get; set; } = [new(new(10), 1000)];
        public Func<int, IReadOnlyList<DesktopWindowInfo>>? ReadCandidates { get; set; }
        public Func<int, IReadOnlyList<DesktopWindowInfo>>? ByProcess { get; set; }
        public Action<IntPtr>? OnShow { get; set; }
        public List<IntPtr> Shown { get; } = [];
        public List<int> ReadProcesses { get; } = [];
        public bool Visible { get; set; } = true;
        public IReadOnlyList<DesktopWindowInfo> GetWindows(int processId)
        {
            ReadProcesses.Add(processId);
            return ByProcess?.Invoke(processId) ?? ReadCandidates?.Invoke(ReadProcesses.Count) ?? Candidates;
        }
        public void ShowAndActivate(DesktopWindowInfo window, int processId)
        { Shown.Add(window.Handle); OnShow?.Invoke(window.Handle); }
        public bool IsVisible(DesktopWindowInfo window, int processId) => Visible;
    }

    private sealed class FakeEnvironment : IDesktopQuitEnvironment
    {
        public Action<int>? OnSession { get; set; }
        public Action? OnModifierCheck { get; set; }
        public Action? OnOpen { get; set; }
        public IntPtr Foreground { get; set; }
        public Func<int, IntPtr>? ReadForeground { get; set; }
        private int _foregroundReads;
        public IntPtr ForegroundWindow => ReadForeground?.Invoke(++_foregroundReads) ?? Foreground;
        public List<string> Opened { get; } = [];
        public List<(IntPtr Window, int Process)> Sent { get; } = [];
        public void EnsureSameSession(int processId) => OnSession?.Invoke(processId);
        public void EnsureNoHeldModifiers() => OnModifierCheck?.Invoke();
        public bool IsEnabled(IntPtr window) => true;
        public void OpenDesktop(string executablePath)
        { Opened.Add(executablePath); OnOpen?.Invoke(); }
        public void SendQuit(IntPtr expectedWindow, int processId) => Sent.Add((expectedWindow, processId));
    }
}
