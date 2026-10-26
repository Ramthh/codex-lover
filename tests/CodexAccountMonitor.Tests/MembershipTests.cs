using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexAccountMonitor.Infrastructure.Membership;

namespace CodexAccountMonitor.Tests;

public sealed class MembershipTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CodexLover-membership-tests", Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
    private MembershipSessionStore Store => new(Path.Combine(_root, "session.dpapi"));
    private MembershipEntitlement Trial => new("trial", true, 2, _now.AddDays(3), null);
    private MembershipEntitlement Pro => new("pro", true, null, null, _now.AddYears(1));

    [Fact] public void TrialAllowsTwoAccountsAndRejectsThirdEnrollment()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        MembershipPolicy.AssertAccess(Trial, _now, ids[..1], null, true);
        MembershipPolicy.AssertAccess(Trial, _now, ids, ids[1], false);
        Assert.Throws<MembershipRequiredException>(() => MembershipPolicy.AssertAccess(Trial, _now, ids[..2], null, true));
        Assert.Throws<MembershipRequiredException>(() => MembershipPolicy.AssertAccess(Trial, _now, ids, ids[2], false));
    }
    [Fact] public void ExpiredTrialCannotRefreshSwitchOrAdd()
    {
        var id = Guid.NewGuid();
        var expired = Trial with { TrialEndsAt = _now };
        Assert.Throws<MembershipRequiredException>(() => MembershipPolicy.AssertAccess(expired, _now, [id], id, false));
        Assert.Throws<MembershipRequiredException>(() => MembershipPolicy.AssertAccess(expired, _now, [], null, true));
    }
    [Fact] public void ProHasNoSavedAccountCap()
    {
        var ids = Enumerable.Range(0, 1000).Select(_ => Guid.NewGuid()).ToArray();
        MembershipPolicy.AssertAccess(Pro, _now, ids, null, true);
        MembershipPolicy.AssertAccess(Pro, _now, ids, ids[^1], false);
    }
    [Theory]
    [InlineData("trial", true, null)] [InlineData("pro", true, 2)]
    [InlineData("anything", true, null)] [InlineData("pro", false, null)]
    public void InvalidEntitlementFailsClosed(string plan, bool active, int? maximum) =>
        Assert.False(MembershipPolicy.IsActive(new(plan, active, maximum, _now.AddDays(3), _now.AddYears(1)), _now));

    [Theory]
    [InlineData("https://codexlover.com/news/hello", true)]
    [InlineData("https://codexlover.com.evil.test/news", false)]
    [InlineData("https://user@codexlover.com/news", false)]
    [InlineData("http://codexlover.com/news", false)]
    [InlineData("https://codexlover.com:444/news", false)]
    [InlineData("file:///C:/Windows/System32/cmd.exe", false)]
    public void BrowserLinksMustUseProductionOrigin(string value, bool expected) =>
        Assert.Equal(expected, MembershipService.IsWebsiteUri(value, out _));

    [Fact] public void SessionIsDpapiProtectedAndRoundTrips()
    {
        var session = new MembershipSession("opaque-token-secret-sentinel", _now.AddDays(7));
        Store.Save(session);
        Assert.Equal(session, Store.Read());
        Assert.DoesNotContain(session.AccessToken, Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(_root, "session.dpapi"))));
        Assert.DoesNotContain(session.AccessToken, session.ToString());
        Store.Clear(); Assert.Null(Store.Read());
    }
    [Fact] public void DamagedDpapiFileFailsClosed()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "session.dpapi"), "plaintext-is-not-a-session");
        Assert.Null(Store.Read());
    }
    [Fact] public async Task PersistedSessionCannotUnlockWithoutServerValidation()
    {
        Store.Save(new("test-token", _now.AddDays(7)));
        using var service = new MembershipService(Store, Client(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        Assert.False(service.CanUse);
        Assert.False(await service.ValidateAsync());
        Assert.Throws<MembershipRequiredException>(() => service.AssertAccess([]));
    }
    [Fact] public async Task FreshValidationExpiresWithoutAnAttemptButTransientFailureUsesBoundedGrace()
    {
        var clock = new ManualTime(_now);
        var failed = false;
        Store.Save(new("test-token", _now.AddDays(7)));
        using var service = new MembershipService(Store, Client(request =>
        {
            Assert.Equal("https://codexlover.com/api/me", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer test-token", request.Headers.Authorization!.ToString());
            return failed ? new(HttpStatusCode.ServiceUnavailable) : Json(new MembershipResponse(new("user1", "Test", null), Pro));
        }), clock);
        Assert.True(await service.ValidateAsync());
        clock.Advance(TimeSpan.FromMinutes(5)); Assert.False(service.CanUse);
        Assert.True(await service.ValidateAsync());
        failed = true;
        Assert.True(await service.ValidateAsync());
        Assert.True(service.CanUse);
        Assert.True(service.UsingCachedAccess);
        clock.Advance(MembershipService.OfflineGraceLifetime);
        Assert.False(await service.ValidateAsync());
        Assert.False(service.CanUse);
    }

    [Theory]
    [InlineData("500")]
    [InlineData("503")]
    [InlineData("429")]
    [InlineData("transport")]
    [InlineData("timeout")]
    public async Task TransientFailuresKeepOnlyPreviouslyVerifiedAccess(string failure)
    {
        var clock = new ManualTime(_now);
        var fail = false;
        Store.Save(new("test-token", _now.AddDays(7)));
        using var service = new MembershipService(Store, Client(_ => !fail
            ? Json(new MembershipResponse(new("user1", "Test", null), Pro))
            : failure switch
            {
                "transport" => throw new HttpRequestException("simulated network failure"),
                "timeout" => throw new OperationCanceledException("simulated HTTP timeout"),
                _ => new HttpResponseMessage((HttpStatusCode)int.Parse(failure))
            }), clock);
        Assert.True(await service.ValidateAsync());
        var verifiedAt = Store.Read()!.VerifiedAt;
        fail = true;
        clock.Advance(TimeSpan.FromMinutes(20));

        Assert.True(await service.ValidateAsync());
        Assert.True(service.UsingCachedAccess);
        Assert.Equal(verifiedAt, Store.Read()!.VerifiedAt);
        clock.Advance(TimeSpan.FromMinutes(39));
        Assert.True(await service.ValidateAsync());
        Assert.Equal(verifiedAt, Store.Read()!.VerifiedAt);
        clock.Advance(TimeSpan.FromMinutes(1));

        Assert.False(await service.ValidateAsync());
        Assert.False(service.CanUse);
        Assert.False(service.UsingCachedAccess);
        Assert.Throws<MembershipRequiredException>(() => service.AssertAccess([]));
    }

    [Fact]
    public async Task ProtectedVerificationSurvivesRestartWithoutRestartExtendingGrace()
    {
        var clock = new ManualTime(_now);
        Store.Save(new("test-token", _now.AddDays(7)));
        using (var online = new MembershipService(Store, Client(_ => Json(
            new MembershipResponse(new("user1", "Test", null), Pro))), clock))
        {
            Assert.True(await online.ValidateAsync());
        }
        Assert.Equal(_now, Store.Read()!.VerifiedAt);
        Assert.Equal("user1", Store.Read()!.VerifiedMembership!.User.Id);
        Assert.DoesNotContain("user1", Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(_root, "session.dpapi"))));
        clock.Advance(TimeSpan.FromMinutes(30));
        using var restarted = new MembershipService(Store,
            Client(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)), clock);

        Assert.True(restarted.CanUse);
        Assert.True(restarted.UsingCachedAccess);
        Assert.True(await restarted.ValidateAsync());
        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.False(restarted.CanUse);
        Assert.False(await restarted.ValidateAsync());
        Assert.Equal(_now, Store.Read()!.VerifiedAt);
    }

    [Theory]
    [InlineData("401")]
    [InlineData("403")]
    [InlineData("incomplete")]
    [InlineData("malformed")]
    public async Task RevocationOrInvalidResponseCannotFallBackToOlderCachedAccess(string failure)
    {
        var clock = new ManualTime(_now);
        var mode = "success";
        Store.Save(new("test-token", _now.AddDays(7)));
        using (var service = new MembershipService(Store, Client(_ => mode switch
        {
            "success" => Json(new MembershipResponse(new("user1", "Test", null), Pro)),
            "incomplete" => Json(new { user = new { id = "user1" } }),
            "malformed" => new(HttpStatusCode.OK) { Content = new StringContent("{bad-json", Encoding.UTF8, "application/json") },
            _ => new HttpResponseMessage((HttpStatusCode)int.Parse(mode))
        }), clock))
        {
            Assert.True(await service.ValidateAsync());
            mode = failure;
            Assert.False(await service.ValidateAsync());
            Assert.False(service.CanUse);
            Assert.Null(Store.Read()?.VerifiedAt);
            Assert.Null(Store.Read()?.VerifiedMembership);
            if (failure == "401") Assert.False(service.HasSession);
            mode = "503";
            Assert.False(await service.ValidateAsync());
        }
        using var restarted = new MembershipService(Store,
            Client(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)), clock);
        Assert.False(restarted.CanUse);
        Assert.False(await restarted.ValidateAsync());
    }

    [Theory]
    [InlineData("pro")]
    [InlineData("session")]
    [InlineData("clock")]
    public async Task GraceCannotBypassExpiryOrClockRollback(string boundary)
    {
        var clock = new ManualTime(_now);
        var fail = false;
        var entitlement = Pro with { ProExpiresAt = _now.AddMinutes(30) };
        Store.Save(new("test-token", boundary == "session" ? _now.AddMinutes(30) : _now.AddDays(7)));
        using var service = new MembershipService(Store, Client(_ => fail
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : Json(new MembershipResponse(new("user1", "Test", null), boundary == "pro" ? entitlement : Pro))), clock);
        Assert.True(await service.ValidateAsync());
        fail = true;
        Assert.True(await service.ValidateAsync());
        if (boundary == "clock") clock.AdjustWallClock(TimeSpan.FromMinutes(-1));
        else clock.Advance(TimeSpan.FromMinutes(30));

        Assert.False(service.CanUse);
        Assert.False(await service.ValidateAsync());
        using var restarted = new MembershipService(Store,
            Client(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)), clock);
        Assert.False(restarted.CanUse);
        Assert.False(await restarted.ValidateAsync());
    }

    [Fact]
    public async Task SuccessfulDisabledEntitlementReplacesCachedAccessAndCannotBeRevivedOffline()
    {
        var clock = new ManualTime(_now);
        var mode = 0;
        Store.Save(new("test-token", _now.AddDays(7)));
        using var service = new MembershipService(Store, Client(_ => mode == 2
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : Json(new MembershipResponse(new("user1", "Test", null), mode == 0 ? Pro : Pro with { Active = false }))), clock);
        Assert.True(await service.ValidateAsync());
        mode = 1;
        Assert.False(await service.ValidateAsync());
        mode = 2;
        Assert.False(await service.ValidateAsync());
        Assert.False(Store.Read()!.VerifiedMembership!.Entitlement.Active);
    }
    [Fact] public async Task RevokedSessionIsRemoved()
    {
        Store.Save(new("test-token", _now.AddDays(7)));
        using var service = new MembershipService(Store, Client(_ => new(HttpStatusCode.Unauthorized)));
        Assert.False(await service.ValidateAsync()); Assert.False(service.HasSession); Assert.Null(Store.Read());
    }
    [Fact] public async Task NewsFiltersUntrustedUrlsWithoutTransmittingMembershipToken()
    {
        Store.Save(new("test-token", _now.AddDays(7)));
        using var service = new MembershipService(Store, Client(request =>
        {
            Assert.Null(request.Headers.Authorization);
            return Json(new NewsFeed([
                new("1", "safe", "Safe article", null, _now, "https://codexlover.com/news/safe"),
                new("2", "bad", "Bad article", null, _now, "https://evil.test/") ]));
        }));
        Assert.Equal("Safe article", Assert.Single(await service.GetNewsAsync()).Title);
    }
    [Theory]
    [InlineData("google")]
    [InlineData("twitter")]
    public async Task DeviceSignInUsesPkceAndOnlyWebsiteIdentity(string provider)
    {
        string? challenge = null;
        Uri? browser = null;
        using var service = new MembershipService(Store, Client(request =>
        {
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Null(request.Headers.Authorization);
            if (request.RequestUri!.AbsolutePath == "/api/device/start")
            {
                Assert.Equal(2, body.RootElement.EnumerateObject().Count());
                Assert.Equal(provider, body.RootElement.GetProperty("provider").GetString());
                challenge = body.RootElement.GetProperty("codeChallenge").GetString();
                return Json(new DeviceAuthorization("one-time-device-code", "https://codexlover.com/device?code=public-code", 600, 3));
            }
            Assert.Equal(2, body.RootElement.EnumerateObject().Count());
            var verifier = body.RootElement.GetProperty("codeVerifier").GetString()!;
            Assert.Equal(challenge, MembershipService.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
            return Json(new DeviceTokenResponse("opaque-access-token", _now.AddDays(7), new("website-user", "Website Member", null), Trial));
        }));
        await service.SignInAsync(provider, uri => browser = uri, CancellationToken.None);
        Assert.Equal("codexlover.com", browser!.Host); Assert.True(service.CanUse);
        Assert.Equal("opaque-access-token", Store.Read()!.AccessToken);
    }
    [Fact] public async Task DeviceSignInRejectsBrowserRedirectToAnotherOrigin()
    {
        using var service = new MembershipService(Store, Client(_ => Json(new DeviceAuthorization("test", "https://evil.test/device", 600, 3))));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.SignInAsync("twitter", _ => throw new Exception("Must not open"), CancellationToken.None));
        Assert.False(service.CanUse);
    }
    [Theory]
    [InlineData("apple")]
    [InlineData("unknown")]
    public async Task UnsupportedProviderCannotStartAuthorization(string provider)
    {
        using var service = new MembershipService(Store, Client(_ => throw new Exception("Must not send a request")));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SignInAsync(provider,
            _ => throw new Exception("Must not open a browser"), CancellationToken.None));
        Assert.False(service.CanUse);
        Assert.False(service.HasSession);
    }
    [Fact] public async Task SignOutRevokesBeforeClearingSavedSession()
    {
        Store.Save(new("test-token", _now.AddDays(7)));
        using var service = new MembershipService(Store, Client(request =>
        {
            Assert.Equal("/api/session/logout", request.RequestUri!.AbsolutePath);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("test-token", request.Headers.Authorization!.Parameter);
            return new(HttpStatusCode.NoContent);
        }));
        await service.SignOutAsync(); Assert.Null(Store.Read()); Assert.False(service.CanUse);
    }
    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> handler) => new(new Handler(handler));
    private static HttpResponseMessage Json<T>(T data) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(data, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(handler(request));
    }
    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        private long _stamp;
        public override DateTimeOffset GetUtcNow() => _now;
        public override long GetTimestamp() => _stamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan time) { _now += time; _stamp += time.Ticks; }
        public void AdjustWallClock(TimeSpan time) { _now += time; }
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
