using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using CodexAccountMonitor.Infrastructure.Membership;

namespace CodexAccountMonitor.Tests;

public sealed class MembershipValidationConcurrencyTests
{
    [Fact]
    public async Task PendingRevalidationPreservesVerifiedEnrollmentAccessAndRenewsOnSuccess()
    {
        using var fixture = new Fixture();
        Assert.True(await fixture.Service.ValidateAsync());
        fixture.Clock.Advance(TimeSpan.FromMinutes(2));

        var pending = fixture.Service.ValidateAsync();
        await fixture.Handler.PendingRequest.Task;

        Assert.False(pending.IsCompleted);
        Assert.True(fixture.Service.CanUse);
        fixture.Service.AssertAccess([], enrollment: true);

        fixture.Handler.Complete(fixture.ValidResponse());
        Assert.True(await pending);
        fixture.Clock.Advance(TimeSpan.FromMinutes(3));
        Assert.True(fixture.Service.CanUse);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task CompletedAuthorizationFailureImmediatelyInvalidatesPreviousVerification(HttpStatusCode status)
    {
        using var fixture = new Fixture();
        Assert.True(await fixture.Service.ValidateAsync());
        var pending = fixture.Service.ValidateAsync();
        await fixture.Handler.PendingRequest.Task;
        fixture.Service.AssertAccess([], enrollment: true);

        fixture.Handler.Complete(new HttpResponseMessage(status));

        Assert.False(await pending);
        AssertBlocked(fixture.Service);
        Assert.Equal(status != HttpStatusCode.Unauthorized, fixture.Service.HasSession);
    }

    [Fact]
    public async Task CancelledInFlightVerificationPreservesButDoesNotExtendPreviousVerification()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        Assert.True(await fixture.Service.ValidateAsync());
        var pending = fixture.Service.ValidateAsync(cancellation.Token);
        await fixture.Handler.PendingRequest.Task;
        fixture.Service.AssertAccess([], enrollment: true);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(fixture.Service.CanUse);
        Assert.False(fixture.Service.UsingCachedAccess);
        fixture.Clock.Advance(MembershipService.ValidationLifetime);
        AssertBlocked(fixture.Service);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task CompletedTransientFailureUsesGraceFromOriginalVerification(HttpStatusCode status)
    {
        using var fixture = new Fixture();
        Assert.True(await fixture.Service.ValidateAsync());
        var pending = fixture.Service.ValidateAsync();
        await fixture.Handler.PendingRequest.Task;
        fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        AssertBlocked(fixture.Service);

        fixture.Handler.Complete(new HttpResponseMessage(status));

        Assert.True(await pending);
        Assert.True(fixture.Service.CanUse);
        Assert.True(fixture.Service.UsingCachedAccess);
        fixture.Clock.Advance(TimeSpan.FromMinutes(50));
        AssertBlocked(fixture.Service);
    }

    [Fact]
    public async Task PendingRevalidationCannotExtendOriginalFiveMinuteLifetime()
    {
        using var fixture = new Fixture();
        Assert.True(await fixture.Service.ValidateAsync());
        var pending = fixture.Service.ValidateAsync();
        await fixture.Handler.PendingRequest.Task;

        fixture.Clock.Advance(MembershipService.ValidationLifetime);

        AssertBlocked(fixture.Service);
        fixture.Handler.Complete(fixture.ValidResponse());
        Assert.True(await pending);
        fixture.Service.AssertAccess([], enrollment: true);
    }

    [Fact]
    public async Task SessionExpiringDuringRevalidationCannotBeUnlockedBySuccessfulResponse()
    {
        using var fixture = new Fixture(sessionLifetime: TimeSpan.FromMinutes(1));
        Assert.True(await fixture.Service.ValidateAsync());
        var pending = fixture.Service.ValidateAsync();
        await fixture.Handler.PendingRequest.Task;

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));

        AssertBlocked(fixture.Service);
        fixture.Handler.Complete(fixture.ValidResponse());
        Assert.False(await pending);
        AssertBlocked(fixture.Service);
    }

    [Fact]
    public async Task EntitlementExpiringDuringRevalidationRemainsBlocked()
    {
        using var fixture = new Fixture(entitlementLifetime: TimeSpan.FromMinutes(1));
        Assert.True(await fixture.Service.ValidateAsync());
        var pending = fixture.Service.ValidateAsync();
        await fixture.Handler.PendingRequest.Task;

        fixture.Clock.Advance(TimeSpan.FromMinutes(1));

        AssertBlocked(fixture.Service);
        fixture.Handler.Complete(fixture.ValidResponse());
        Assert.False(await pending);
        AssertBlocked(fixture.Service);
    }

    private static void AssertBlocked(MembershipService service)
    {
        Assert.False(service.CanUse);
        Assert.Throws<MembershipRequiredException>(() => service.AssertAccess([], enrollment: true));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(),
            "CodexLover-membership-concurrency-tests", Guid.NewGuid().ToString("N"));
        private readonly DateTimeOffset _entitlementExpiry;
        public ManualTime Clock { get; } = new(new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero));
        public PendingHandler Handler { get; }
        public MembershipService Service { get; }

        public Fixture(TimeSpan? sessionLifetime = null, TimeSpan? entitlementLifetime = null)
        {
            _entitlementExpiry = Clock.GetUtcNow() + (entitlementLifetime ?? TimeSpan.FromDays(365));
            var store = new MembershipSessionStore(Path.Combine(_directory, "session.dpapi"));
            store.Save(new MembershipSession("synthetic-test-token",
                Clock.GetUtcNow() + (sessionLifetime ?? TimeSpan.FromDays(7))));
            Handler = new PendingHandler(ValidResponse);
            Service = new MembershipService(store, new HttpClient(Handler), Clock);
        }

        public HttpResponseMessage ValidResponse() => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(
                new MembershipResponse(new("test-user", "Test", null),
                    new("pro", true, null, null, _entitlementExpiry)),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json")
        };

        public void Dispose()
        {
            Service.Dispose();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class PendingHandler(Func<HttpResponseMessage> initialResponse) : HttpMessageHandler
    {
        private int _requestCount;
        private readonly TaskCompletionSource<HttpResponseMessage> _response =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PendingRequest { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Complete(HttpResponseMessage response) => _response.SetResult(response);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _requestCount) == 1) return Task.FromResult(initialResponse());
            PendingRequest.SetResult();
            return _response.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        private long _stamp;
        public override DateTimeOffset GetUtcNow() => _now;
        public override long GetTimestamp() => _stamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan time) { _now += time; _stamp += time.Ticks; }
    }
}
