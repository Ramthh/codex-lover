using System.ComponentModel;
using CodexAccountMonitor.Infrastructure.DesktopSwitching;
using CodexAccountMonitor.Infrastructure;

namespace CodexAccountMonitor.Tests;

public sealed class WindowsCodexDesktopAdapterTests
{
    private const string DesktopPath = @"C:\Program Files\WindowsApps\OpenAI.Codex_1.0_x64__publisher\app\ChatGPT.exe";

    [Fact]
    public void ClosedDesktopAndCliCanSwitchWhenDesktopSignatureIsTrusted()
    {
        var fixture = new Fixture([Process(1, 0, "CodexAccountMonitor.exe"), Process(2, 0, "explorer.exe")]);
        var state = fixture.Adapter.Inspect();
        Assert.True(state.CanSwitch);
        Assert.Equal(DesktopPath, state.Installation?.ExecutablePath);
        Assert.Empty(state.BlockingProcesses);
        Assert.Empty(fixture.Launcher.Launched);
    }

    [Theory]
    [InlineData("ChatGPT.exe")]
    [InlineData("Codex.exe")]
    [InlineData("codex-app-server.exe")]
    [InlineData("codex-code-mode-host.exe")]
    public void InaccessibleCodexMetadataStillBlocksSwitching(string name)
    {
        var fixture = new Fixture([new DesktopProcessInfo(8, 0, name, null)]);
        var state = fixture.Adapter.Inspect();
        Assert.False(state.CanSwitch);
        Assert.Equal(8, Assert.Single(state.BlockingProcesses).ProcessId);
    }

    [Fact]
    public void CodexDesktopHelperWithDifferentNameStillBlocks()
    {
        var fixture = new Fixture([new DesktopProcessInfo(8, 0, "chrome_proxy.exe",
            @"C:\Program Files\WindowsApps\OpenAI.Codex_1.0_x64__publisher\app\chrome_proxy.exe")]);
        Assert.False(fixture.Adapter.Inspect().CanSwitch);
    }

    [Theory]
    [InlineData("dotnet.exe")]
    [InlineData("VBCSCompiler.exe")]
    [InlineData("node.exe")]
    public void OrphanedCachedToolchainsAreNotDesktopProcesses(string name)
    {
        var fixture = new Fixture([new DesktopProcessInfo(8, 999, name,
            @"C:\Users\user\AppData\Local\Packages\OpenAI.Codex_1.0_x64__publisher\LocalCache\Codex\Toolchains\" + name, 10)]);
        Assert.True(fixture.Adapter.Inspect().CanSwitch);
    }

    [Fact]
    public void AppDirectoryPrefixWithoutBoundaryDoesNotClaimUnrelatedProcess()
    {
        var fixture = new Fixture([new DesktopProcessInfo(8, 999, "helper.exe",
            @"C:\Program Files\WindowsApps\OpenAI.Codex_1.0_x64__publisher\app-other\helper.exe", 10)]);
        Assert.True(fixture.Adapter.Inspect().CanSwitch);
    }

    [Fact]
    public void CachedCodexCliRemainsBlockingWithoutDesktopAncestor()
    {
        var fixture = new Fixture([new DesktopProcessInfo(8, 999, "codex.exe",
            @"C:\Users\user\AppData\Local\Packages\OpenAI.Codex_1.0_x64__publisher\LocalCache\codex.exe", 10)]);
        Assert.False(fixture.Adapter.Inspect().CanSwitch);
    }

    [Fact]
    public void VerifiedSystemServiceExemptsOnlyExactPidNotItsCliChildrenOrSameNamePeer()
    {
        var fixture = new Fixture([new(8, 999, "codex-windows-sandbox-service.exe", null),
            new(9, 999, "codex-windows-sandbox-service.exe", null), Process(10, 8, "codex.exe")]);
        fixture.Infrastructure.VerifiedIds.Add(8);
        Assert.Equal(new[] { 9, 10 }, fixture.Adapter.Inspect().BlockingProcesses.Select(process => process.ProcessId));
    }

    [Fact]
    public async Task VerifiedServiceAndOrphanedCompilerDoNotPreventNormalQuitAndClosedResult()
    {
        var service = new DesktopProcessInfo(8, 999, "codex-windows-sandbox-service.exe", null);
        var compiler = new DesktopProcessInfo(9, 999, "VBCSCompiler.exe",
            @"C:\Users\user\AppData\Local\Packages\OpenAI.Codex_1.0_x64__publisher\LocalCache\Toolchains\VBCSCompiler.exe", 10);
        var fixture = new Fixture([Desktop(2, 0), service, compiler]);
        fixture.Infrastructure.VerifiedIds.Add(8);
        fixture.Quit.AfterRequest = () => fixture.Processes.Processes = [service, compiler];
        var closed = await fixture.Adapter.RequestCloseAndWaitAsync(fixture.Adapter.Inspect(), TimeSpan.FromSeconds(1));
        Assert.True(closed.CanSwitch);
        Assert.Equal(2, Assert.Single(fixture.Quit.Requested));
    }

    [Fact]
    public async Task UnverifiedSystemServiceStillRefusesBeforeQuit()
    {
        var fixture = new Fixture([Desktop(2, 0), new(8, 999, "codex-windows-sandbox-service.exe", null)]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Adapter.RequestCloseAndWaitAsync(fixture.Adapter.Inspect(), TimeSpan.FromSeconds(1)));
        Assert.Empty(fixture.Quit.Requested);
    }

    [Fact]
    public void InfrastructureCannotExemptAnActualDesktopDescendant()
    {
        var fixture = new Fixture([Desktop(2, 0), Process(8, 2, "codex-windows-sandbox-service.exe", 12)]);
        fixture.Infrastructure.VerifiedIds.Add(8);
        Assert.Equal(new[] { 2, 8 }, fixture.Adapter.Inspect().BlockingProcesses.Select(process => process.ProcessId));
    }

    [Fact]
    public void OnlyMonitorDescendantsAreIgnoredAndExternalCliRemainsBlocking()
    {
        var fixture = new Fixture([
            Process(1, 0, "CodexAccountMonitor.exe", started: 10),
            Process(2, 1, "codex.exe", started: 11),
            Process(3, 2, "codex-code-mode-host.exe", started: 12),
            Process(4, 99, "codex.exe", started: 13)]);
        var state = fixture.Adapter.Inspect();
        Assert.Equal(4, Assert.Single(state.BlockingProcesses).ProcessId);
        Assert.False(state.CanSwitch);
    }

    [Fact]
    public void ReusedMonitorPidDoesNotHideOlderExternalProcess()
    {
        var fixture = new Fixture([
            Process(1, 0, "CodexAccountMonitor.exe", started: 50),
            Process(2, 1, "codex.exe", started: 10)]);
        Assert.Equal(2, Assert.Single(fixture.Adapter.Inspect().BlockingProcesses).ProcessId);
    }

    [Fact]
    public void MissingChildStartTimeDoesNotClaimOwnership()
    {
        var fixture = new Fixture([
            Process(1, 0, "CodexAccountMonitor.exe"),
            new DesktopProcessInfo(2, 1, "codex.exe", null)]);
        Assert.False(fixture.Adapter.Inspect().CanSwitch);
    }

    [Fact]
    public void ExplicitOwnedProcessAndVerifiedDescendantsAreIgnored()
    {
        var fixture = new Fixture([
            Process(2, 0, "codex.exe", started: 10),
            Process(3, 2, "codex-code-mode-host.exe", started: 11)]);
        Assert.True(fixture.Adapter.Inspect([2]).CanSwitch);
    }

    [Fact]
    public void AnotherMonitorsCliBlocksBecauseItsOwnershipIsNotOurs()
    {
        var fixture = new Fixture([
            Process(9, 0, "CodexAccountMonitor.exe"),
            Process(10, 9, "codex.exe")]);
        Assert.False(fixture.Adapter.Inspect().CanSwitch);
    }

    [Fact]
    public void FailedProcessEnumerationNeverMeansClosed()
    {
        var fixture = new Fixture([]);
        fixture.Processes.Failure = new Win32Exception(5);
        var state = fixture.Adapter.Inspect();
        Assert.False(state.CanSwitch);
        Assert.Contains("could not verify that Codex is closed", state.Problem);
        Assert.Empty(fixture.Launcher.Launched);
    }

    [Fact]
    public void InaccessibleInstallationNeverMeansSafeToSwitch()
    {
        var fixture = new Fixture([]);
        fixture.Installations.Failure = new UnauthorizedAccessException();
        var state = fixture.Adapter.Inspect();
        Assert.False(state.CanSwitch);
        Assert.Contains("could not verify the Codex desktop", state.Problem);
    }

    [Fact]
    public void MissingOrUntrustedInstallationCannotSwitch()
    {
        var fixture = new Fixture([]);
        fixture.Signatures.Trusted = false;
        var state = fixture.Adapter.Inspect();
        Assert.False(state.CanSwitch);
        Assert.Null(state.Installation);
        Assert.Contains("trusted Codex desktop installation was not found", state.Problem);
    }

    [Fact]
    public void LaunchRevalidatesSignatureAndDoesNotLaunchReplacedExecutable()
    {
        var fixture = new Fixture([]);
        var installation = fixture.Adapter.Inspect().Installation!;
        fixture.Signatures.Trusted = false;
        Assert.Throws<InvalidOperationException>(() => fixture.Adapter.Launch(installation));
        Assert.Empty(fixture.Launcher.Launched);
    }

    [Fact]
    public void LaunchUsesOnlyVerifiedSelectedExecutable()
    {
        var fixture = new Fixture([]);
        fixture.Adapter.Launch(fixture.Adapter.Inspect().Installation!);
        Assert.Equal(DesktopPath, Assert.Single(fixture.Launcher.Launched));
    }

    [Fact]
    public void MonitorLaunchedDesktopAndItsWorkersAreNeverIgnored()
    {
        var fixture = new Fixture([Process(1, 0, "CodexAccountMonitor.exe"), Desktop(2, 1),
            Process(3, 2, "codex.exe", 12), Process(4, 3, "node.exe", 13), Process(5, 1, "codex.exe", 12)]);
        Assert.Equal(new[] { 2, 3, 4 }, fixture.Adapter.Inspect([2]).BlockingProcesses.Select(process => process.ProcessId));
    }

    [Theory]
    [InlineData("ChatGPT.exe")]
    [InlineData("Codex.exe")]
    public void InaccessibleDesktopChildCannotBeMistakenForMonitorWorker(string name)
    {
        var fixture = new Fixture([Process(1, 0, "CodexAccountMonitor.exe"), new DesktopProcessInfo(2, 1, name, null, 11)]);
        Assert.False(fixture.Adapter.Inspect().CanSwitch);
    }

    [Fact]
    public void MonitorAndItsWorkersSurviveWhenCodexOriginallyLaunchedMonitor()
    {
        var fixture = new Fixture([Desktop(10, 0), Process(1, 10, "CodexAccountMonitor.exe", 12), Process(2, 1, "codex.exe", 13)]);
        Assert.Equal(10, Assert.Single(fixture.Adapter.Inspect().BlockingProcesses).ProcessId);
    }

    [Fact]
    public void OlderChildOfReusedDesktopPidIsNotOwnedByDesktop()
    {
        var fixture = new Fixture([Desktop(2, 0, 50), Process(3, 2, "node.exe", 10)]);
        Assert.Equal(2, Assert.Single(fixture.Adapter.Inspect().BlockingProcesses).ProcessId);
    }

    [Fact]
    public async Task AutomaticQuitRequestsOnlyVerifiedDesktopAndWaitsForExit()
    {
        var fixture = new Fixture([Process(1, 0, "CodexAccountMonitor.exe"), Desktop(2, 0), Desktop(3, 2, 12)]);
        fixture.Quit.AfterRequest = () => fixture.Processes.Processes = [Process(1, 0, "CodexAccountMonitor.exe")];
        var closed = await fixture.Adapter.RequestCloseAndWaitAsync(fixture.Adapter.Inspect(), TimeSpan.FromSeconds(1));
        Assert.True(closed.CanSwitch);
        Assert.Equal(2, Assert.Single(fixture.Quit.Requested));
    }

    [Fact]
    public async Task ExternalCliPreventsAnyDesktopQuitRequest()
    {
        var fixture = new Fixture([Desktop(2, 0), Process(3, 99, "codex.exe")]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Adapter.RequestCloseAndWaitAsync(fixture.Adapter.Inspect(), TimeSpan.FromSeconds(1)));
        Assert.Empty(fixture.Quit.Requested);
    }

    [Fact]
    public async Task MonitorInsideCodexLifetimeNeverRequestsQuit()
    {
        var fixture = new Fixture([Desktop(2, 0)]);
        fixture.Lifetime.Independent = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Adapter.RequestCloseAndWaitAsync(fixture.Adapter.Inspect(), TimeSpan.FromSeconds(1)));
        Assert.Empty(fixture.Quit.Requested);
    }

    [Fact]
    public async Task IndependenceIsRecheckedImmediatelyBeforeNativeQuit()
    {
        var fixture = new Fixture([Desktop(2, 0)]);
        fixture.Quit.BeforeVerification = () => fixture.Lifetime.Independent = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Adapter.RequestCloseAndWaitAsync(fixture.Adapter.Inspect(), TimeSpan.FromSeconds(1)));
        Assert.Empty(fixture.Quit.Requested);
    }

    [Fact]
    public async Task ReusedDesktopPidIsRejectedBeforeSendingQuit()
    {
        var fixture = new Fixture([Desktop(2, 0)]);
        var original = fixture.Adapter.Inspect();
        fixture.Processes.Processes = [Desktop(2, 0, 99)];
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Adapter.RequestCloseAndWaitAsync(original, TimeSpan.FromSeconds(1)));
        Assert.Empty(fixture.Quit.Requested);
    }

    [Fact]
    public async Task MissingDesktopCreationTimeCannotAuthorizeQuit()
    {
        var fixture = new Fixture([new DesktopProcessInfo(2, 0, "ChatGPT.exe", DesktopPath)]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Adapter.RequestCloseAndWaitAsync(fixture.Adapter.Inspect(), TimeSpan.FromSeconds(1)));
        Assert.Empty(fixture.Quit.Requested);
    }

    [Fact]
    public async Task QuitRefusalTimesOutWithoutAnyForceTermination()
    {
        var fixture = new Fixture([Desktop(2, 0)]);
        await Assert.ThrowsAsync<TimeoutException>(() => fixture.Adapter.RequestCloseAndWaitAsync(fixture.Adapter.Inspect(), TimeSpan.FromMilliseconds(30)));
        Assert.Equal(2, Assert.Single(fixture.Quit.Requested));
        Assert.False(fixture.Adapter.Inspect().CanSwitch);
    }

    [Fact]
    public async Task OrphanedNonCodexHelperStillBlocksAfterDesktopExits()
    {
        var helper = Process(3, 2, "node.exe", 12);
        var fixture = new Fixture([Desktop(2, 0), helper]);
        fixture.Quit.AfterRequest = () => fixture.Processes.Processes = [helper];
        await Assert.ThrowsAsync<TimeoutException>(() => fixture.Adapter.RequestCloseAndWaitAsync(fixture.Adapter.Inspect(), TimeSpan.FromMilliseconds(30)));
    }

    [Fact]
    public async Task HelperCreatedDuringQuitRemainsTrackedAfterItsParentExits()
    {
        var helper = Process(3, 2, "node.exe", 12);
        var fixture = new Fixture([Desktop(2, 0)]);
        fixture.Quit.BeforeVerification = () => fixture.Processes.Processes = [Desktop(2, 0), helper];
        fixture.Quit.AfterRequest = () => fixture.Processes.Processes = [helper];
        await Assert.ThrowsAsync<TimeoutException>(() => fixture.Adapter.RequestCloseAndWaitAsync(fixture.Adapter.Inspect(), TimeSpan.FromMilliseconds(30)));
    }

    [Fact]
    public async Task RevalidationRejectsDesktopChangedWhileFocusing()
    {
        var fixture = new Fixture([Desktop(2, 0)]);
        fixture.Quit.BeforeVerification = () => fixture.Processes.Processes = [Desktop(2, 0, 50)];
        var exception = await Assert.ThrowsAsync<DesktopQuitValidationException>(() => fixture.Adapter.RequestCloseAndWaitAsync(fixture.Adapter.Inspect(), TimeSpan.FromSeconds(1)));
        Assert.Equal("desktop_identity_changed", exception.ReasonCode);
        Assert.Empty(fixture.Quit.Requested);
    }

    [Theory]
    [InlineData(false, "another_codex_process_started")]
    [InlineData(true, "codex_process_metadata_unavailable")]
    public async Task RevalidationNamesTheActualBackgroundProcessProblem(bool unknown, string expectedCode)
    {
        var fixture = new Fixture([Desktop(2, 0)]);
        fixture.Quit.BeforeVerification = () => fixture.Processes.Processes = [Desktop(2, 0),
            unknown ? new DesktopProcessInfo(3, 99, "codex.exe", null) : Process(3, 99, "codex.exe")];
        var exception = await Assert.ThrowsAsync<DesktopQuitValidationException>(() => fixture.Adapter.RequestCloseAndWaitAsync(fixture.Adapter.Inspect(), TimeSpan.FromSeconds(1)));
        Assert.Equal(expectedCode, exception.ReasonCode);
        Assert.Empty(fixture.Quit.Requested);
    }

    [Fact]
    public async Task RevalidationReportsPublisherVerificationFailure()
    {
        var fixture = new Fixture([Desktop(2, 0)]);
        fixture.Quit.BeforeVerification = () => fixture.Signatures.Trusted = false;
        var exception = await Assert.ThrowsAsync<DesktopQuitValidationException>(() => fixture.Adapter.RequestCloseAndWaitAsync(fixture.Adapter.Inspect(), TimeSpan.FromSeconds(1)));
        Assert.Equal("desktop_signature_unverified", exception.ReasonCode);
        Assert.Empty(fixture.Quit.Requested);
    }

    [Fact]
    public async Task RevalidationReportsDesktopAlreadyClosed()
    {
        var fixture = new Fixture([Desktop(2, 0)]);
        fixture.Quit.BeforeVerification = () => fixture.Processes.Processes = [];
        var exception = await Assert.ThrowsAsync<DesktopQuitValidationException>(() => fixture.Adapter.RequestCloseAndWaitAsync(fixture.Adapter.Inspect(), TimeSpan.FromSeconds(1)));
        Assert.Equal("desktop_closed_before_quit", exception.ReasonCode);
        Assert.Empty(fixture.Quit.Requested);
    }

    [Theory]
    [InlineData(123, 87, true)]
    [InlineData(0, 87, false)]
    [InlineData(123, 5, false)]
    [InlineData(123, 6, false)]
    public void ProcessMetadataFailureOnlyOmitsProvenAbsentPid(uint pid, int error, bool exited)
    {
        var metadata = WindowsDesktopProcessSource.UnavailableMetadata(pid, error);
        Assert.Equal(exited, metadata.KnownExited);
        Assert.Null(metadata.Path);
        Assert.Null(metadata.Started);
    }

    [Fact]
    public async Task CancellationBeforeCloseNeverSendsQuit()
    {
        var fixture = new Fixture([Desktop(2, 0)]);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Adapter.RequestCloseAndWaitAsync(fixture.Adapter.Inspect(), TimeSpan.FromSeconds(1), cancellation.Token));
        Assert.Empty(fixture.Quit.Requested);
    }

    [Fact]
    public void WindowsSnapshotIncludesCurrentProcessAndCreationTime()
    {
        var processes = new WindowsDesktopProcessSource().ReadProcesses();
        var current = Assert.Single(processes, process => process.ProcessId == Environment.ProcessId);
        Assert.False(string.IsNullOrWhiteSpace(current.Name));
        Assert.False(string.IsNullOrWhiteSpace(current.ExecutablePath));
        Assert.NotNull(current.StartedAtFileTimeUtc);
    }

    [Fact]
    public void WindowsPackageDiscoveryCanRunWithoutLaunchingOrReadingCredentials()
    {
        var candidates = new WindowsDesktopInstallationSource().GetCandidateExecutables([]);
        Assert.NotEmpty(candidates);
        Assert.All(candidates, candidate => Assert.True(Path.IsPathFullyQualified(candidate)));
    }

    [Theory]
    [InlineData("codex.exe")]
    [InlineData(@"C:\missing\codex.exe")]
    [InlineData(@"C:\Windows\System32\notepad.exe")]
    public void DesktopSignatureCheckRejectsRelativeMissingAndUnrelatedExecutables(string path)
    {
        Assert.False(new OpenAiDesktopSignatureVerifier().IsTrustedDesktopExecutable(path));
    }

    private static DesktopProcessInfo Process(int id, int parent, string name, long started = 10)
        => new(id, parent, name, @"C:\fixtures\" + name, started);
    private static DesktopProcessInfo Desktop(int id, int parent, long started = 11)
        => new(id, parent, "ChatGPT.exe", DesktopPath, started);

    private sealed class Fixture
    {
        public FakeProcessSource Processes { get; }
        public FakeInstallationSource Installations { get; } = new();
        public FakeSignatures Signatures { get; } = new();
        public FakeLauncher Launcher { get; } = new();
        public FakeQuitRequester Quit { get; } = new();
        public FakeInfrastructure Infrastructure { get; } = new();
        public FakeLifetimeGuard Lifetime { get; } = new();
        public WindowsCodexDesktopAdapter Adapter { get; }

        public Fixture(IReadOnlyList<DesktopProcessInfo> processes)
        {
            Processes = new FakeProcessSource(processes);
            Adapter = new WindowsCodexDesktopAdapter(Processes, Installations, Signatures, Launcher, monitorProcessId: 1, Quit, Infrastructure, Lifetime);
        }
    }

    private sealed class FakeProcessSource(IReadOnlyList<DesktopProcessInfo> processes) : IDesktopProcessSource
    {
        public Exception? Failure { get; set; }
        public IReadOnlyList<DesktopProcessInfo> Processes { get; set; } = processes;
        public IReadOnlyList<DesktopProcessInfo> ReadProcesses() => Failure is null ? Processes : throw Failure;
    }

    private sealed class FakeInstallationSource : IDesktopInstallationSource
    {
        public Exception? Failure { get; set; }
        public IReadOnlyList<string> GetCandidateExecutables(IReadOnlyList<DesktopProcessInfo> processes)
            => Failure is null ? [DesktopPath] : throw Failure;
    }

    private sealed class FakeSignatures : IDesktopSignatureVerifier
    {
        public bool Trusted { get; set; } = true;
        public bool IsTrustedDesktopExecutable(string executablePath) => Trusted && executablePath.Equals(DesktopPath, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FakeQuitRequester : IDesktopQuitRequester
    {
        public List<int> Requested { get; } = [];
        public Action? BeforeVerification { get; set; }
        public Action? AfterRequest { get; set; }
        public Task RequestQuitAsync(DesktopProcessInfo desktopRoot, Func<bool> revalidateIdentity, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeforeVerification?.Invoke();
            if (!revalidateIdentity()) throw new InvalidOperationException("Desktop identity changed.");
            Requested.Add(desktopRoot.ProcessId);
            AfterRequest?.Invoke();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLauncher : IDesktopLauncher
    {
        public List<string> Launched { get; } = [];
        public void Launch(string executablePath) => Launched.Add(executablePath);
    }

    private sealed class FakeInfrastructure : IDesktopInfrastructureSource
    {
        public HashSet<int> VerifiedIds { get; } = [];
        public IReadOnlySet<int> GetIndependentProcessIds(CodexDesktopInstallation installation, IReadOnlyList<DesktopProcessInfo> processes) => VerifiedIds;
    }

    private sealed class FakeLifetimeGuard : IMonitorLifetimeGuard
    {
        public bool Independent { get; set; } = true;
        public void AssertIndependent()
        {
            if (!Independent) throw new InvalidOperationException("Independent monitor launch required.");
        }
    }
}
