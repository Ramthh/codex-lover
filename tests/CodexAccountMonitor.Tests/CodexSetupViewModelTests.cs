using CodexAccountMonitor.Infrastructure;
using CodexAccountMonitor.ViewModels;

namespace CodexAccountMonitor.Tests;

public sealed class CodexSetupViewModelTests
{
    [Fact]
    public async Task MissingCodexBlocksConnectionWithoutChangingSettings()
    {
        var writes = 0;
        using var setup = Create(new Probe((_, _) => Task.FromResult(Missing)), (_, _) => { writes++; return Task.CompletedTask; });
        using var connection = new AccountConnectionViewModel(null!, 1, setup: setup);
        Assert.False(connection.ConnectCommand.CanExecute(null));
        await connection.InitializeAsync();
        Assert.False(setup.IsReady);
        Assert.False(connection.ConnectCommand.CanExecute(null));
        Assert.Contains("Install", setup.Message);
        Assert.Equal(0, writes);
    }

    [Fact]
    public async Task RetryAfterInstallationEnablesConnectionWithUsefulDefaultName()
    {
        var installed = false;
        var probe = new Probe((_, _) => Task.FromResult(installed ? Ready : Missing));
        using var setup = Create(probe);
        using var connection = new AccountConnectionViewModel(null!, 1, setup: setup);
        await connection.InitializeAsync();
        installed = true;
        Assert.True(await setup.CheckAsync());
        Assert.True(connection.ConnectCommand.CanExecute(null));
        Assert.Equal("Personal", connection.Alias);
        connection.Alias = " ";
        Assert.False(connection.ConnectCommand.CanExecute(null));
        Assert.All(probe.Tests, Assert.True);
    }

    [Fact]
    public async Task InvalidSelectionIsNotSavedAndRetryKeepsTheAttemptedPath()
    {
        var paths = new List<string?>();
        var writes = 0;
        using var setup = Create(new Probe((path, _) => { paths.Add(path); return Task.FromResult(Missing); }),
            (_, _) => { writes++; return Task.CompletedTask; });
        Assert.False(await setup.SelectExecutableAsync("untrusted.exe"));
        Assert.False(await setup.CheckAsync());
        Assert.Equal(new[] { "untrusted.exe", "untrusted.exe" }, paths);
        Assert.Equal(0, writes);
    }

    [Fact]
    public async Task RepairedSelectionIsSavedWhenRetryPasses()
    {
        var installed = false;
        var writes = new List<string?>();
        using var setup = Create(new Probe((_, _) => Task.FromResult(installed ? Ready : Missing)),
            (path, _) => { writes.Add(path); return Task.CompletedTask; });
        Assert.False(await setup.SelectExecutableAsync("selected-codex.exe"));
        Assert.Empty(writes);
        installed = true;
        Assert.True(await setup.CheckAsync());
        Assert.Equal(new[] { "selected-codex.exe" }, writes);
        Assert.True(await setup.CheckAsync());
        Assert.Single(writes);
    }

    [Fact]
    public async Task VerifiedSelectionAndAutomaticDetectionPersistOnlyChosenSetting()
    {
        var writes = new List<string?>();
        using var setup = Create(new Probe((_, _) => Task.FromResult(Ready)), (path, _) => { writes.Add(path); return Task.CompletedTask; });
        Assert.True(await setup.SelectExecutableAsync("official-helper.exe"));
        Assert.True(await setup.SelectExecutableAsync(null));
        Assert.Equal(new string?[] { "official-helper.exe", null }, writes);
    }

    [Fact]
    public async Task PendingCheckCannotStartAnotherCheckOrConnect()
    {
        var completed = new TaskCompletionSource<CodexInstallationStatus>();
        using var setup = Create(new Probe((_, _) => completed.Task));
        using var connection = new AccountConnectionViewModel(null!, 2, setup: setup);
        var first = setup.CheckAsync();
        Assert.True(setup.IsChecking);
        Assert.False(connection.ConnectCommand.CanExecute(null));
        Assert.False(await setup.SelectExecutableAsync("different.exe"));
        completed.SetResult(Ready);
        Assert.True(await first);
        Assert.True(connection.ConnectCommand.CanExecute(null));
    }

    [Fact]
    public async Task ClosingDuringCheckDoesNotSaveOrEnableConnection()
    {
        var completed = new TaskCompletionSource<CodexInstallationStatus>();
        var writes = 0;
        using var setup = Create(new Probe((_, _) => completed.Task), (_, _) => { writes++; return Task.CompletedTask; });
        var checking = setup.SelectExecutableAsync("official-helper.exe");
        setup.Dispose();
        completed.SetResult(Ready);
        Assert.False(await checking);
        Assert.False(setup.IsReady);
        Assert.Equal(0, writes);
    }

    [Fact]
    public async Task SettingsSaveFailureDoesNotReportReady()
    {
        using var setup = Create(new Probe((_, _) => Task.FromResult(Ready)), (_, _) => throw new IOException("fixture"));
        Assert.False(await setup.SelectExecutableAsync("official-helper.exe"));
        Assert.False(setup.IsReady);
        Assert.Contains("could not finish", setup.Message);
    }

    private static readonly CodexInstallationStatus Missing = new(false, null, null, "Codex was not found. Install the official Codex app.");
    private static readonly CodexInstallationStatus Ready = new(true, "official-helper.exe", "1.0.0", "Ready");
    private static CodexSetupViewModel Create(ICodexInstallationProbe probe, Func<string?, CancellationToken, Task>? save = null) =>
        new(probe, _ => Task.FromResult<string?>(null), save ?? ((_, _) => Task.CompletedTask));

    private sealed class Probe(Func<string?, CancellationToken, Task<CodexInstallationStatus>> run) : ICodexInstallationProbe
    {
        public List<bool> Tests { get; } = [];
        public Task<CodexInstallationStatus> InspectAsync(string? configuredPath, bool testAppServer, CancellationToken cancellationToken = default)
        {
            Tests.Add(testAppServer);
            return run(configuredPath, cancellationToken);
        }
    }
}
