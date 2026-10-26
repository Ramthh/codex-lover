using CodexAccountMonitor.Infrastructure;

namespace CodexAccountMonitor.Tests;

public sealed class CodexInstallationProbeTests
{
    [Fact]
    public async Task ConnectionCheckUsesOnlyInformationalCommandsAndReportsVersion()
    {
        var commands = new List<string>();
        var probe = new CodexInstallationProbe(_ => "fixture.exe", (_, arguments, _) =>
        {
            var command = string.Join(" ", arguments);
            commands.Add(command);
            return Task.FromResult((0, command == "--version" ? "codex-cli 0.153.1\n" : "Usage: codex app-server [OPTIONS]"));
        });
        var result = await probe.InspectAsync(null, testAppServer: true);
        Assert.True(result.IsReady);
        Assert.Equal("0.153.1", result.Version);
        Assert.Equal(new[] { "--version", "app-server --help" }, commands);
        Assert.Contains("Account sign-in", result.Message);
    }

    [Fact]
    public async Task UntrustedExecutableNeverRunsAndReturnsGuidance()
    {
        var ran = false;
        var probe = new CodexInstallationProbe(_ => throw new InvalidOperationException("Untrusted signer"), (_, _, _) =>
        {
            ran = true;
            return Task.FromResult((0, string.Empty));
        });
        var result = await probe.InspectAsync(null, testAppServer: true);
        Assert.False(result.IsReady);
        Assert.False(ran);
        Assert.Contains("official OpenAI", result.Message);
    }

    [Fact]
    public async Task MissingOverrideDoesNotSilentlyFallBackToAnotherExecutable()
    {
        var located = false;
        var probe = new CodexInstallationProbe(_ => { located = true; return "fixture.exe"; }, (_, _, _) => Task.FromResult((0, string.Empty)));
        var result = await probe.InspectAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "codex.exe"), true);
        Assert.False(result.IsReady);
        Assert.False(located);
        Assert.Contains("selected file is missing", result.Message);
    }

    [Fact]
    public async Task UnsupportedAppServerIsNotReportedAsConnected()
    {
        var probe = new CodexInstallationProbe(_ => "fixture.exe", (_, arguments, _) =>
            Task.FromResult(arguments.Count == 1 ? (0, "codex-cli 0.1.0") : (1, "unknown command")));
        var result = await probe.InspectAsync(null, true);
        Assert.False(result.IsReady);
        Assert.Equal("0.1.0", result.Version);
        Assert.Contains("Update Codex", result.Message);
    }

    [Fact]
    public async Task TimeoutProducesRecoveryGuidance()
    {
        var probe = new CodexInstallationProbe(_ => "fixture.exe", (_, _, _) => throw new OperationCanceledException());
        var result = await probe.InspectAsync(null, true);
        Assert.False(result.IsReady);
        Assert.Contains("10 seconds", result.Message);
    }

    [Fact]
    public async Task CallerCancellationRemainsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var probe = new CodexInstallationProbe(_ => "fixture.exe", (_, _, token) => Task.FromCanceled<(int, string)>(token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.InspectAsync(null, true, cancellation.Token));
    }
}
