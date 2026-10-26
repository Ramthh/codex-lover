using System.Runtime.InteropServices;
using CodexAccountMonitor.Infrastructure;
using CodexAccountMonitor.Infrastructure.DesktopSwitching;

namespace CodexAccountMonitor.Tests;

public sealed class MonitorStartupBootstrapTests
{
    [Fact]
    public void CodexToolGrandparentIsUnsafeEvenWithOrdinaryShellInBetween()
    {
        DesktopProcessInfo[] processes = [Process(1, 2, "CodexAccountMonitor.exe", 30), Process(2, 3, "pwsh.exe", 20),
            Process(3, 4, "codex.exe", 10), Process(4, 0, "ChatGPT.exe", 5)];
        Assert.True(WindowsMonitorLifetimeGuard.HasCodexAncestor(processes, 1));
    }

    [Fact]
    public void ExplorerOwnedMonitorHasNoCodexAncestor()
    {
        Assert.False(WindowsMonitorLifetimeGuard.HasCodexAncestor(
            [Process(1, 2, "CodexAccountMonitor.exe", 30), Process(2, 0, "explorer.exe", 5)], 1));
    }

    [Theory]
    [InlineData(0x800u, true)]
    [InlineData(0x2000u, false)]
    [InlineData(0x2800u, false)]
    [InlineData(null, false)]
    public void OnlyReadableNonTerminatingDirectExplorerJobIsAccepted(uint? flags, bool expected)
    {
        DesktopProcessInfo[] processes = [Process(1, 2, "CodexAccountMonitor.exe", 30),
            new(2, 0, "explorer.exe", @"C:\Windows\explorer.exe", 5)];
        Assert.Equal(expected, WindowsMonitorLifetimeGuard.IsNonTerminatingExplorerJob(processes, 1, flags, @"C:\Windows\explorer.exe"));
    }

    [Fact]
    public void AJobWithDistantOrUnverifiedExplorerParentRemainsUnsafe()
    {
        var monitor = Process(1, 2, "CodexAccountMonitor.exe", 30);
        Assert.False(WindowsMonitorLifetimeGuard.IsNonTerminatingExplorerJob(
            [monitor, Process(2, 3, "pwsh.exe", 20), new(3, 0, "explorer.exe", @"C:\Windows\explorer.exe", 5)], 1, 0x800, @"C:\Windows\explorer.exe"));
        Assert.False(WindowsMonitorLifetimeGuard.IsNonTerminatingExplorerJob(
            [monitor, new(2, 0, "explorer.exe", @"C:\Other\explorer.exe", 5)], 1, 0x800, @"C:\Windows\explorer.exe"));
        Assert.False(WindowsMonitorLifetimeGuard.IsNonTerminatingExplorerJob(
            [monitor, new(2, 0, "explorer.exe", @"C:\Windows\explorer.exe", 50)], 1, 0x800, @"C:\Windows\explorer.exe"));
        Assert.False(WindowsMonitorLifetimeGuard.IsNonTerminatingExplorerJob(
            [monitor, new(2, 0, "explorer.exe", @"C:\Windows\explorer.exe", null)], 1, 0x800, @"C:\Windows\explorer.exe"));
    }

    [Fact]
    public void ReusedParentPidDoesNotCreateFalseCodexAncestry()
    {
        Assert.False(WindowsMonitorLifetimeGuard.HasCodexAncestor(
            [Process(1, 2, "CodexAccountMonitor.exe", 30), Process(2, 0, "codex.exe", 50)], 1));
    }

    [Fact]
    public void UnknownCurrentIdentityAndInaccessibleCodexAncestorAreUnsafe()
    {
        Assert.True(WindowsMonitorLifetimeGuard.HasCodexAncestor([], 1));
        Assert.True(WindowsMonitorLifetimeGuard.HasCodexAncestor(
            [Process(1, 2, "CodexAccountMonitor.exe", 30), new(2, 0, "ChatGPT.exe", null)], 1));
    }

    [Fact]
    public void OnlySupportedNonsecretStartupFlagsAreForwarded()
    {
        var nonce = Guid.NewGuid();
        var forwarded = MonitorStartupBootstrap.BuildRelaunchArguments(
            ["--settings", "--minimized", "--settings", "--api-key=not-forwarded", "--unknown=data"], nonce);
        Assert.Equal(new[] { "--settings", "--minimized", "--explorer-handoff=" + nonce.ToString("N") }, forwarded);
    }

    [Fact]
    public void LocalTestSurvivesIndependentHandoffWithoutForwardingOtherArguments()
    {
        var nonce = Guid.NewGuid();
        Assert.Equal(new[] { "--local-test", "--explorer-handoff=" + nonce.ToString("N") },
            MonitorStartupBootstrap.BuildRelaunchArguments(["--local-test", "--local-test", "--demo", "--token=fixture"], nonce));
    }

    [Fact]
    public void HandoffMustBeOneWellFormedNonce()
    {
        var nonce = Guid.NewGuid();
        Assert.Equal(nonce, MonitorStartupBootstrap.ReadHandoff(["--explorer-handoff=" + nonce.ToString("N")]));
        Assert.Null(MonitorStartupBootstrap.ReadHandoff(["--settings"]));
        Assert.Throws<MonitorIndependentLaunchException>(() => MonitorStartupBootstrap.ReadHandoff(["--explorer-handoff=invalid"]));
        Assert.Throws<MonitorIndependentLaunchException>(() => MonitorStartupBootstrap.ReadHandoff(
            ["--explorer-handoff=" + nonce.ToString("N"), "--explorer-handoff=" + nonce.ToString("N")]));
    }

    [Fact]
    public async Task ASecondBrokerHandoffIsRefusedWithoutAnyLaunch()
    {
        await Assert.ThrowsAsync<MonitorIndependentLaunchException>(() => MonitorStartupBootstrap.RelaunchIndependentAsync(
            ["--explorer-handoff=" + Guid.NewGuid().ToString("N")]));
    }

    [Fact]
    public async Task HungExplorerComCallIsBoundedBeforeAcknowledgmentWait()
    {
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waited = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MonitorStartupBootstrap.WaitForIndependentLaunchAsync(
            _ => never.Task, _ => { waited = true; return Task.FromResult(true); }, TimeSpan.FromMilliseconds(20)));
        Assert.False(waited);
    }

    [Fact]
    public async Task MissingChildAcknowledgmentUsesSameBoundedDeadline()
    {
        var never = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MonitorStartupBootstrap.WaitForIndependentLaunchAsync(
            _ => Task.CompletedTask, _ => never.Task, TimeSpan.FromMilliseconds(20)));
    }

    [Theory]
    [InlineData("OPENAI_FEDERATION_RULE_ID")]
    [InlineData("OPENAI_IDENTITY_TOKEN_FILE")]
    public void BrokerCannotSilentlyDropInheritedWorkloadIdentity(string variable)
    {
        var exception = Assert.Throws<MonitorIndependentLaunchException>(() =>
            MonitorStartupBootstrap.PrepareSavedAccountEnvironment(name => name == variable ? "sensitive-test-value" : null,
                (_, _) => throw new Exception("No environment mutation is allowed before policy validation.")));
        Assert.DoesNotContain("sensitive-test-value", exception.Message);
    }

    [Theory]
    [InlineData("OPENAI_API_KEY", "synthetic-key")]
    [InlineData("CODEX_API_KEY", "synthetic-key")]
    [InlineData("CODEX_ACCESS_TOKEN", "synthetic-token")]
    [InlineData("OPENAI_API_KEY", "")]
    [InlineData("CODEX_API_KEY", " ")]
    [InlineData("codex_access_token", "synthetic-token")]
    public void InheritedApiCredentialsDoNotBlockStartupOrReachReopenedDesktop(string variable, string value)
    {
        var parent = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [variable] = value, ["CODEX_HOME"] = @"C:\fixture\saved-home",
            ["HTTPS_PROXY"] = "http://fixture:8080", ["CODEX_CA_CERTIFICATE"] = @"C:\fixture\ca.pem",
            ["CODEX_MANAGED_CONFIG_PATH"] = @"C:\fixture\managed.toml",
            ["OPENAI_WORKLOAD_IDENTITY_CONTEXT"] = "synthetic-audit-context", ["PATH"] = @"C:\Windows"
        };
        var process = new Dictionary<string, string?>(parent, StringComparer.OrdinalIgnoreCase);
        void Prepare() => MonitorStartupBootstrap.PrepareSavedAccountEnvironment(
            name => process.TryGetValue(name, out var result) ? result : null,
            (name, updated) => { Assert.Null(updated); process.Remove(name); });

        Prepare();
        Prepare(); // Explorer handoff/startup can prepare again safely.

        Assert.Equal(value, parent[variable]);
        foreach (var credential in new[] { "OPENAI_API_KEY", "CODEX_API_KEY", "CODEX_ACCESS_TOKEN" })
            Assert.False(process.ContainsKey(credential));
        foreach (var setting in parent.Where(entry => !entry.Key.Equals(variable, StringComparison.OrdinalIgnoreCase)))
            Assert.Equal(setting.Value, process[setting.Key]);
    }

    [Theory]
    [InlineData("OPENAI_FEDERATION_RULE_ID", "")]
    [InlineData("OPENAI_IDENTITY_TOKEN_FILE", " ")]
    public void WorkloadPolicyIsValidatedBeforeAnyApiCredentialIsRemoved(string selector, string value)
    {
        var writes = 0;
        var environment = new Dictionary<string, string?>
        {
            [selector] = value, ["OPENAI_API_KEY"] = "synthetic-key"
        };
        Assert.Throws<MonitorIndependentLaunchException>(() => MonitorStartupBootstrap.PrepareSavedAccountEnvironment(
            name => environment.TryGetValue(name, out var result) ? result : null,
            (_, _) => writes++));
        Assert.Equal(0, writes);
    }

    [Fact]
    public void WindowsArgumentsRoundTripWithoutShellExpansion()
    {
        string[] arguments = ["--minimized", "", @"C:\path with space\", "a\"quoted\"value", "\\\\\"", "$(no-execution)&value"];
        var command = ExplorerLaunchBroker.QuoteArguments(new[] { @"C:\App\Monitor.exe" }.Concat(arguments));
        var pointer = CommandLineToArgvW(command, out var count);
        Assert.NotEqual(IntPtr.Zero, pointer);
        try
        {
            var parsed = Enumerable.Range(0, count).Select(index => Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, index * IntPtr.Size))).ToArray();
            Assert.Equal(arguments, parsed.Skip(1));
        }
        finally { LocalFree(pointer); }
    }

    private static DesktopProcessInfo Process(int id, int parent, string name, long started) =>
        new(id, parent, name, @"C:\fixtures\" + name, started);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
