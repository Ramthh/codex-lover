using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexAccountMonitor.Infrastructure.Membership;

public sealed class MembershipService : IDisposable
{
    public static readonly Uri Website = new("https://codexlover.com/");
    public static readonly TimeSpan ValidationLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan OfflineGraceLifetime = TimeSpan.FromHours(1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly MembershipSessionStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _time;
    private MembershipSession? _session;
    private DateTimeOffset _validatedAt;
    private long _validationStamp;
    private MembershipResponse? _membership;
    private bool _usingCachedAccess;
    public event EventHandler? Changed;
    public MembershipResponse? Current => _membership;
    public bool HasSession => _session is not null;
    public bool UsingCachedAccess => _usingCachedAccess && CanUse;
    public bool CanUse => IsFresh && MembershipPolicy.IsActive(_membership?.Entitlement, _time.GetUtcNow());
    private bool IsFresh => _validatedAt != default && _time.GetUtcNow() >= _validatedAt
        && _time.GetElapsedTime(_validationStamp) < (_usingCachedAccess ? OfflineGraceLifetime : ValidationLifetime)
        && _session?.ExpiresAt > _time.GetUtcNow();

    public MembershipService(MembershipSessionStore store, HttpClient? http = null, TimeProvider? time = null)
    {
        _store = store;
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromSeconds(20) };
        _http.MaxResponseContentBufferSize = 262144;
        _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Codex-Lover-Version", typeof(MembershipService).Assembly.GetName().Version?.ToString() ?? "unknown");
        _time = time ?? TimeProvider.System;
        _session = store.Read();
        if (_session?.VerifiedAt is { } verifiedAt && _session.VerifiedMembership is { } cached
            && !string.IsNullOrWhiteSpace(cached.User?.Id) && verifiedAt <= _time.GetUtcNow()
            && _time.GetUtcNow() - verifiedAt < OfflineGraceLifetime)
        {
            _membership = cached;
            _validatedAt = verifiedAt;
            _validationStamp = _time.GetTimestamp() - (long)((_time.GetUtcNow() - verifiedAt).TotalSeconds * _time.TimestampFrequency);
            _usingCachedAccess = true;
        }
    }

    public async Task<bool> ValidateAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Keep the previous verification usable while this request is pending.
            // IsFresh still enforces its original lifetime and the session expiry.
            if (_session is null || _session.ExpiresAt <= _time.GetUtcNow()) { InvalidateVerification(); return false; }
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Website, "api/me"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session.AccessToken);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            { _session = null; _membership = null; _store.Clear(); return false; }
            if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests)
                return UseCachedVerification();
            if (!response.IsSuccessStatusCode) { InvalidateVerification(); return false; }
            response.EnsureSuccessStatusCode();
            var membership = await ReadAsync<MembershipResponse>(response, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(membership.User?.Id) || membership.Entitlement is null)
                throw new InvalidDataException("The membership response is incomplete.");
            cancellationToken.ThrowIfCancellationRequested();
            _membership = membership;
            _validatedAt = _time.GetUtcNow();
            _validationStamp = _time.GetTimestamp();
            _usingCachedAccess = false;
            _session = _session with { VerifiedMembership = membership, VerifiedAt = _validatedAt };
            _store.Save(_session);
            return CanUse;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (HttpRequestException) { return UseCachedVerification(); }
        catch (OperationCanceledException) { return UseCachedVerification(); }
        catch { InvalidateVerification(); return false; }
        finally
        {
            _gate.Release();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool UseCachedVerification()
    {
        // Only an earlier successful response can grant grace; failures never extend it.
        _usingCachedAccess = true;
        return CanUse;
    }

    private void InvalidateVerification()
    {
        _validatedAt = default;
        _usingCachedAccess = false;
        if (_session is not null)
        {
            _session = _session with { VerifiedMembership = null, VerifiedAt = null };
            _store.Save(_session);
        }
    }

    public async Task<SignInConfiguration> GetSignInConfigurationAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(new Uri(Website, "api/public/config"), cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var config = await ReadAsync<SignInConfiguration>(response, cancellationToken).ConfigureAwait(false);
        return config.Providers is not null ? config : throw new InvalidDataException("Sign-in options were missing.");
    }

    public async Task SignInAsync(string provider, Action<Uri> openBrowser, CancellationToken cancellationToken)
    {
        if (provider is not ("google" or "twitter")) throw new ArgumentOutOfRangeException(nameof(provider));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        using var started = await _http.PostAsJsonAsync(new Uri(Website, "api/device/start"),
            new { provider, codeChallenge = challenge }, cancellationToken).ConfigureAwait(false);
        if (!started.IsSuccessStatusCode)
            throw new MembershipRequiredException("This sign-in provider is not available yet. Try again later or choose another provider.");
        var authorization = await ReadAsync<DeviceAuthorization>(started, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(authorization.DeviceCode) || authorization.DeviceCode.Length > 2048
            || !IsWebsiteUri(authorization.VerificationUri, out var browserUri) || browserUri!.AbsolutePath != "/device"
            || authorization.ExpiresIn is < 1 or > 600 || authorization.Interval is < 1 or > 30)
            throw new InvalidDataException("The sign-in response could not be verified.");
        openBrowser(browserUri);
        var pollStart = _time.GetTimestamp();
        while (_time.GetElapsedTime(pollStart) < TimeSpan.FromSeconds(authorization.ExpiresIn))
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(3, authorization.Interval)), cancellationToken).ConfigureAwait(false);
            using var tokenResponse = await _http.PostAsJsonAsync(new Uri(Website, "api/device/token"),
                new { deviceCode = authorization.DeviceCode, codeVerifier = verifier }, cancellationToken).ConfigureAwait(false);
            if (tokenResponse.StatusCode == HttpStatusCode.Accepted) continue;
            if (!tokenResponse.IsSuccessStatusCode)
                throw new MembershipRequiredException("Browser sign-in was cancelled or expired. Start sign-in again.");
            var token = await ReadAsync<DeviceTokenResponse>(tokenResponse, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token.AccessToken) || token.AccessToken.Length > 8192
                || token.AccessToken.Any(char.IsWhiteSpace) || token.ExpiresAt <= _time.GetUtcNow()
                || string.IsNullOrWhiteSpace(token.User?.Id) || token.Entitlement is null)
                throw new InvalidDataException("The sign-in response could not be verified.");
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var membership = new MembershipResponse(token.User, token.Entitlement);
                var session = new MembershipSession(token.AccessToken, token.ExpiresAt, membership, _time.GetUtcNow());
                _store.Save(session);
                _session = session;
                _membership = membership;
                _validatedAt = _time.GetUtcNow();
                _validationStamp = _time.GetTimestamp();
                _usingCachedAccess = false;
            }
            finally { _gate.Release(); }
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }
        throw new MembershipRequiredException("Sign-in expired. Start again to receive a new browser link.");
    }

    public void AssertAccess(IReadOnlyList<Guid> orderedProfiles, Guid? profileId = null, bool enrollment = false)
    {
        if (!IsFresh) throw new MembershipRequiredException("Reconnect to verify your Codex Lover membership. Open Membership and choose Retry.");
        MembershipPolicy.AssertAccess(_membership?.Entitlement, _time.GetUtcNow(), orderedProfiles, profileId, enrollment);
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_session is not null)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Website, "api/session/logout"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session.AccessToken);
                using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                // Do not claim server revocation succeeded on a transport/server error.
                if (response.StatusCode != HttpStatusCode.Unauthorized) response.EnsureSuccessStatusCode();
            }
            _store.Clear(); _session = null; _membership = null; _validatedAt = default; _usingCachedAccess = false;
        }
        finally { _gate.Release(); Changed?.Invoke(this, EventArgs.Empty); }
    }

    public async Task<IReadOnlyList<NewsArticle>> GetNewsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(new Uri(Website, "api/news"), cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var feed = await ReadAsync<NewsFeed>(response, cancellationToken).ConfigureAwait(false);
        return (feed.Articles ?? []).Where(x => x is not null && !string.IsNullOrWhiteSpace(x.Title)
            && x.Title.Length <= 240 && IsWebsiteUri(x.Url, out _)).Take(20).ToArray();
    }

    public static bool IsWebsiteUri(string? value, out Uri? uri) => Uri.TryCreate(value, UriKind.Absolute, out uri)
        && uri.Scheme == "https" && uri.Host.Equals("codexlover.com", StringComparison.OrdinalIgnoreCase)
        && uri.Port == 443 && string.IsNullOrEmpty(uri.UserInfo);

    public async Task<AppUpdates> GetAppUpdatesAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(new Uri(Website, "api/app/updates"), cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var feed = await ReadAsync<AppUpdates>(response, cancellationToken).ConfigureAwait(false);
        if (feed.TimeZone != "UTC" || feed.Days is null || feed.Days.Count != 30
            || feed.Days.Any(d => d is null || !DateOnly.TryParseExact(d.Day, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out _) || d.Note?.Length > 160)
            || feed.Days.Select(d => d.Day).Distinct().Count() != 30)
            throw new InvalidDataException("The reset calendar could not be verified.");
        return feed;
    }

    public async Task SendFeedbackAsync(string message, CancellationToken cancellationToken = default)
    {
        if (message.Trim().Length < 10 || message.Length > 4000) throw new ArgumentException("Write between 10 and 4,000 characters.");
        if (_session is null) throw new MembershipRequiredException("Sign in before sending feedback.");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Website, "api/feedback"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _session.AccessToken);
        request.Content = JsonContent.Create(new { message = message.Trim() });
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.TooManyRequests) throw new MembershipRequiredException("You have sent several messages. Please try again later.");
        response.EnsureSuccessStatusCode();
    }

    public static void OpenWebsite(Uri uri)
    {
        if (!IsWebsiteUri(uri.AbsoluteUri, out _)) throw new InvalidOperationException("Only Codex Lover website links can be opened.");
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > 262144) throw new InvalidDataException("Response is too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (output.Length + count > 262144) throw new InvalidDataException("Response is too large.");
            output.Write(buffer, 0, count);
        }
        return JsonSerializer.Deserialize<T>(output.ToArray(), Json) ?? throw new InvalidDataException("Response is empty.");
    }
    internal static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public void Dispose() { _http.Dispose(); _gate.Dispose(); }
}
