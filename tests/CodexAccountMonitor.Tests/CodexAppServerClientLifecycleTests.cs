using CodexAccountMonitor.FakeAppServer;
using CodexAccountMonitor.Core.Services;
using CodexAccountMonitor.Infrastructure;
using CodexAccountMonitor.Infrastructure.AppServer;

namespace CodexAccountMonitor.Tests;

public sealed class CodexAppServerClientLifecycleTests
{
    [Fact]
    public async Task ExplicitNullFieldsPreserveOtherReportedQuota()
    {
        using var fixture = new AppServerFixture();
        await using var client = await fixture.StartAsync("nullable");
        var snapshot = QuotaMapper.Map(await client.ReadRateLimitsAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Null(snapshot.FiveHour);
        Assert.Equal(72, snapshot.Weekly?.RemainingPercent);
        Assert.Null(snapshot.Weekly?.ResetsAt);
        Assert.Null(snapshot.ResetCreditsAvailable);
    }
    [Fact]
    public async Task FinalResponseIsDrainedBeforeProcessExitFailsPendingCalls()
    {
        using var fixture = new AppServerFixture();
        await using var client = await fixture.StartAsync();

        var account = await client.ReadAccountAsync(refreshToken: false).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(account);
        Assert.Equal("fixture@example.test", account.Email);
    }

    [Fact]
    public async Task NotificationObserverCanSynchronouslyIssueReentrantRpc()
    {
        using var fixture = new AppServerFixture();
        await using var client = await fixture.StartAsync();
        var observerCompleted = new TaskCompletionSource<CodexAccountIdentity?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.NotificationReceived += (_, notification) =>
        {
            if (notification.Method == "fixture/reentrant")
            {
                try
                {
#pragma warning disable xUnit1031 // Deliberate: this proves callbacks are not dispatched on the protocol read loop.
                    observerCompleted.TrySetResult(client.ReadAccountAsync(refreshToken: true)
                        .GetAwaiter()
                        .GetResult());
#pragma warning restore xUnit1031
                }
                catch (Exception exception)
                {
                    observerCompleted.TrySetException(exception);
                }
            }
        };

        var rateLimits = await client.ReadRateLimitsAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var observerAccount = await observerCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, rateLimits.Buckets.Count);
        Assert.Equal("codex", rateLimits.DefaultBucket?.LimitId);
        Assert.Equal("fixture@example.test", observerAccount?.Email);
    }

    [Fact]
    public async Task MultiBucketResponseMapsDefaultQuotaInsteadOfModelSpecificUsage()
    {
        using var fixture = new AppServerFixture();
        await using var client = await fixture.StartAsync();

        var rateLimits = await client.ReadRateLimitsAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var snapshot = QuotaMapper.Map(rateLimits);

        Assert.Equal(2, rateLimits.Buckets.Count);
        Assert.Equal(0, rateLimits.Buckets[0].Secondary?.UsedPercent);
        Assert.Equal("codex_other_model", rateLimits.Buckets[1].LimitId);
        Assert.Equal("codex", rateLimits.DefaultBucket?.LimitId);
        Assert.Null(snapshot.FiveHour);
        Assert.Equal(28, snapshot.Weekly?.UsedPercent);
        Assert.Equal(72, snapshot.Weekly?.RemainingPercent);
        Assert.Equal(2, snapshot.ResetCreditsAvailable);
    }

    [Fact]
    public async Task ConcurrentDisposeCallersAwaitTheSameCleanup()
    {
        using var fixture = new AppServerFixture();
        var client = await fixture.StartAsync();

        var first = client.DisposeAsync().AsTask();
        var followers = Enumerable.Range(0, 16)
            .Select(_ => client.DisposeAsync().AsTask())
            .ToArray();

        Assert.All(followers, follower => Assert.Same(first, follower));
        await Task.WhenAll(followers.Prepend(first)).WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class AppServerFixture : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "CodexAccountMonitor.Tests",
            Guid.NewGuid().ToString("N"));
        private readonly SafeJsonLogger _logger;

        public AppServerFixture()
        {
            Directory.CreateDirectory(_root);
            _logger = new SafeJsonLogger(new AppDataPaths(_root));
        }

        public Task<CodexAppServerClient> StartAsync(string scenario = "codex-home")
        {
            var home = Path.Combine(_root, Guid.NewGuid().ToString("N"), scenario);
            Directory.CreateDirectory(home);
            return CodexAppServerClient.StartAsync(FakeServerExecutable(), home, _logger);
        }

        public void Dispose()
        {
            _logger.Dispose();
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch
            {
            }
        }

        private static string FakeServerExecutable()
        {
            var assemblyDirectory = Path.GetDirectoryName(typeof(FakeAppServerMarker).Assembly.Location)
                                    ?? throw new InvalidOperationException("Fake App Server output is unavailable.");
            var adjacent = Path.Combine(assemblyDirectory, "CodexAccountMonitor.FakeAppServer.exe");
            if (File.Exists(adjacent))
            {
                return adjacent;
            }

            var artifactsCandidate = Path.GetFullPath(Path.Combine(assemblyDirectory, "..", "..",
                "CodexAccountMonitor.FakeAppServer", new DirectoryInfo(assemblyDirectory).Name,
                "CodexAccountMonitor.FakeAppServer.exe"));
            if (File.Exists(artifactsCandidate)) return artifactsCandidate;

            var configuration = assemblyDirectory.Contains(
                $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase)
                ? "Release"
                : "Debug";
            var repository = Path.GetFullPath(Path.Combine(assemblyDirectory, "..", "..", "..", "..", ".."));
            return Path.Combine(
                repository,
                "tests",
                "CodexAccountMonitor.FakeAppServer",
                "bin",
                configuration,
                "net10.0-windows",
                "CodexAccountMonitor.FakeAppServer.exe");
        }
    }
}
