using CodexAccountMonitor.Infrastructure;
using CodexAccountMonitor.Infrastructure.AppServer;
using CodexAccountMonitor.ViewModels;

namespace CodexAccountMonitor.Tests;

public sealed class AccountConnectionViewModelTests
{
    private static readonly Uri SignInUri = new("https://auth.openai.com/authorize?state=test-only");
    private static EnrollmentProgress Prompt(string code = "TEST-CODE") => new("authorization_required",
        new LoginPrompt("chatgptDeviceCode", "test-login", null, SignInUri, code));

    [Fact]
    public async Task BrowserLaunchFailureOffersManualLinkAndSuccessfulConnectionClearsPrompt()
    {
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var copied = new List<string>();
        var closed = new List<bool>();
        using var connection = Create(async (_, _, progress, token) =>
        {
            await progress(Prompt());
            await finish.Task.WaitAsync(token);
        }, _ => throw new InvalidOperationException("No browser handler"), copied.Add);
        connection.RequestClose += (_, success) => closed.Add(success);

        var connecting = connection.ConnectAsync();

        Assert.True(connection.IsBusy);
        Assert.False(connection.ConnectCommand.CanExecute(null));
        Assert.Contains("Copy sign-in link", connection.Status);
        Assert.True(connection.CopyLinkCommand.CanExecute(null));
        connection.CopyLinkCommand.Execute(null);
        Assert.Equal(SignInUri.AbsoluteUri, Assert.Single(copied));
        Assert.True(connection.HasUserCode);

        finish.SetResult();
        await connecting;

        Assert.Equal(new[] { true }, closed);
        Assert.False(connection.IsBusy);
        Assert.False(connection.HasBrowserLink);
        Assert.False(connection.HasUserCode);
        Assert.False(connection.CopyLinkCommand.CanExecute(null));
        Assert.False(connection.OpenBrowserCommand.CanExecute(null));
    }

    [Fact]
    public async Task FailedSignInRemovesExpiredLinkAndCodeThenAllowsFreshRetry()
    {
        var firstFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retryFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var inputs = new List<(string Alias, bool DeviceCode)>();
        using var connection = Create(async (alias, deviceCode, progress, token) =>
        {
            inputs.Add((alias, deviceCode));
            var attempt = ++attempts;
            await progress(Prompt(attempt == 1 ? "EXPIRED" : "FRESH"));
            await (attempt == 1 ? firstFinish.Task : retryFinish.Task).WaitAsync(token);
            if (attempt == 1) throw new InvalidOperationException("login_timeout");
        });

        var first = connection.ConnectAsync();
        firstFinish.SetResult();
        await first;

        Assert.Contains("timed out", connection.Status);
        Assert.False(connection.HasBrowserLink);
        Assert.False(connection.HasUserCode);
        Assert.True(connection.ConnectCommand.CanExecute(null));

        connection.Alias = "Work";
        connection.UseDeviceCode = true;
        var retry = connection.ConnectAsync();
        Assert.Equal("FRESH", connection.UserCode);
        Assert.True(connection.HasBrowserLink);
        retryFinish.SetResult();
        await retry;

        Assert.Equal(new[] { ("Personal", false), ("Work", true) }, inputs);
    }

    [Fact]
    public async Task CancellingPreventsLateProgressFromOpeningBrowserOrReportingSuccess()
    {
        var continueAfterClose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var browserOpens = 0;
        var closed = new List<bool>();
        CancellationToken signInToken = default;
        using var connection = Create(async (_, _, progress, token) =>
        {
            signInToken = token;
            // Model a callback already queued when the user closes the dialog.
            await continueAfterClose.Task;
            await progress(Prompt());
        }, _ => browserOpens++);
        connection.RequestClose += (_, success) => closed.Add(success);
        var connecting = connection.ConnectAsync();

        connection.CancelCommand.Execute(null);
        connection.Dispose(); // Window.Closed also disposes the view model.
        Assert.True(signInToken.IsCancellationRequested);
        Assert.False(connection.ConnectCommand.CanExecute(null));
        Assert.False(connection.CancelCommand.CanExecute(null));
        continueAfterClose.SetResult();
        await connecting;

        Assert.Equal(new[] { false }, closed);
        Assert.Equal(0, browserOpens);
        Assert.False(connection.IsBusy);
        Assert.False(connection.HasBrowserLink);
        Assert.False(connection.HasUserCode);
        Assert.Equal("Sign-in cancelled.", connection.Status);
    }

    [Fact]
    public async Task DisposedDialogCannotStartAnAccountConnection()
    {
        var calls = 0;
        var connection = Create((_, _, _, _) => { calls++; return Task.CompletedTask; });
        connection.Dispose();

        await connection.ConnectAsync();

        Assert.Equal(0, calls);
        Assert.False(connection.ConnectCommand.CanExecute(null));
    }

    [Fact]
    public async Task CancellingDuringPreflightDoesNotStartSignInOrReplaceCancelledStatus()
    {
        var probeFinish = new TaskCompletionSource<CodexInstallationStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var probes = 0;
        var signIns = 0;
        var ready = new CodexInstallationStatus(true, "test-codex.exe", "1.0.0", "Ready");
        using var setup = new CodexSetupViewModel(new Probe((_, _, _) =>
            ++probes == 1 ? Task.FromResult(ready) : probeFinish.Task),
            _ => Task.FromResult<string?>(null), (_, _) => Task.CompletedTask);
        using var connection = new AccountConnectionViewModel(
            (_, _, _, _) => { signIns++; return Task.CompletedTask; },
            1, null, null, setup, _ => { }, _ => { }, action => { action(); return Task.CompletedTask; });
        await connection.InitializeAsync();
        var connecting = connection.ConnectAsync();
        Assert.True(setup.IsChecking);

        connection.CancelCommand.Execute(null);
        probeFinish.SetResult(ready);
        await connecting;

        Assert.Equal(0, signIns);
        Assert.False(setup.IsReady);
        Assert.False(connection.IsBusy);
        Assert.False(connection.ConnectCommand.CanExecute(null));
        Assert.Equal("Sign-in cancelled.", connection.Status);
    }

    private sealed class Probe(Func<string?, bool, CancellationToken, Task<CodexInstallationStatus>> inspect) : ICodexInstallationProbe
    {
        public Task<CodexInstallationStatus> InspectAsync(string? configuredPath, bool testAppServer, CancellationToken cancellationToken = default) =>
            inspect(configuredPath, testAppServer, cancellationToken);
    }

    private static AccountConnectionViewModel Create(
        Func<string, bool, Func<EnrollmentProgress, Task>, CancellationToken, Task> connect,
        Action<Uri>? openBrowser = null, Action<string>? copyText = null) =>
        new(connect, 1, null, null, null,
            openBrowser ?? (_ => { }), copyText ?? (_ => { }),
            action => { action(); return Task.CompletedTask; });
}
