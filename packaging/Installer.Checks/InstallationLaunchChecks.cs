using CodexAccountMonitor.Setup;

internal static class InstallationLaunchChecks
{
    internal static void Run(Action<string, Action> check)
    {
        foreach (var minimized in new[] { false, true })
        {
            check(minimized ? "silent setup forwards only minimized to the independent launch broker"
                : "interactive setup requests a visible independent app launch", () =>
            {
                var called = 0;
                InstallationLaunch.LaunchAsync(@"C:\synthetic-install\CodexAccountMonitor.exe", minimized,
                    (executable, arguments, token) =>
                    {
                        called++;
                        Assert(executable == @"C:\synthetic-install\CodexAccountMonitor.exe", "The committed executable changed.");
                        Assert(arguments.SequenceEqual(minimized ? new[] { "--minimized" } : []), "Unexpected launch arguments.");
                        Assert(token.CanBeCanceled && !token.IsCancellationRequested, "The broker does not have a live bounded deadline.");
                        return Task.CompletedTask;
                    }).GetAwaiter().GetResult();
                Assert(called == 1, "Setup launched more than once.");
            });
        }
        check("unavailable Explorer is reported without another unsafe launch fallback", () =>
        {
            var called = 0;
            Expect<InvalidOperationException>(() => InstallationLaunch.LaunchAsync(@"C:\synthetic-install\app.exe", false,
                (_, _, _) => { called++; throw new InvalidOperationException("Synthetic unavailable Explorer."); }).GetAwaiter().GetResult());
            Assert(called == 1, "Setup retried using an unsafe launch path.");
        });
        check("a stalled Explorer call cannot hold installation completion open", () =>
        {
            var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationToken receivedDeadline = default;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            Expect<OperationCanceledException>(() => InstallationLaunch.LaunchAsync(@"C:\synthetic-install\app.exe", false,
                (_, _, token) => { receivedDeadline = token; return never.Task; }, TimeSpan.FromMilliseconds(25)).GetAwaiter().GetResult());
            Assert(timer.Elapsed < TimeSpan.FromSeconds(3), "Setup remained blocked on the stalled broker.");
            Assert(receivedDeadline.IsCancellationRequested, "The stalled broker deadline was not cancelled.");
            Assert(InstallationLaunch.LaunchTimeout == TimeSpan.FromSeconds(10), "Production setup must have a ten-second broker deadline.");
        });
    }

    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name} was not reported.");
    }
}
