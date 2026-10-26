namespace CodexAccountMonitor.Infrastructure.Membership;

public sealed record MemberIdentity(string Id, string? Name, string? Email);
public sealed record MembershipEntitlement(string Plan, bool Active, int? MaxAccounts,
    DateTimeOffset? TrialEndsAt, DateTimeOffset? ProExpiresAt);
public sealed record MembershipResponse(MemberIdentity User, MembershipEntitlement Entitlement);
public sealed record DeviceAuthorization(string DeviceCode, string VerificationUri, int ExpiresIn, int Interval);
public sealed record DeviceTokenResponse(string AccessToken, DateTimeOffset ExpiresAt,
    MemberIdentity User, MembershipEntitlement Entitlement)
{
    public override string ToString() => "DeviceTokenResponse [redacted]";
}
public sealed record MembershipSession(string AccessToken, DateTimeOffset ExpiresAt,
    MembershipResponse? VerifiedMembership = null, DateTimeOffset? VerifiedAt = null)
{
    public override string ToString() => "MembershipSession [redacted]";
}
public sealed record SignInConfiguration(Dictionary<string, bool> Providers);
public sealed record NewsArticle(string Id, string Slug, string Title, string? Excerpt,
    DateTimeOffset PublishedAt, string Url);
public sealed record NewsFeed(IReadOnlyList<NewsArticle> Articles);
public sealed record ResetDay(string Day, bool Marked, string? Note);
public sealed record AppRelease(string Version, string Url, string? Notes, DateTimeOffset PublishedAt);
public sealed record AppUpdates(string TimeZone, DateTimeOffset AsOf, IReadOnlyList<ResetDay> Days, AppRelease? LatestRelease);

public static class AppUpdatePolicy
{
    public static bool IsNewer(AppRelease? release, Version current) => release is not null
        && Version.TryParse(release.Version, out var available)
        && new Version(available.Major, available.Minor, Math.Max(0, available.Build))
            > new Version(current.Major, current.Minor, Math.Max(0, current.Build))
        && MembershipService.IsWebsiteUri(release.Url, out _);
}

public sealed class MembershipRequiredException : InvalidOperationException
{
    public MembershipRequiredException(string message) : base(message) { }
}

public static class MembershipPolicy
{
    public static bool IsActive(MembershipEntitlement? entitlement, DateTimeOffset now) => entitlement switch
    {
        { Active: true, Plan: "free", MaxAccounts: 2 } => true,
        { Active: true, Plan: "trial", MaxAccounts: 2, TrialEndsAt: { } end } => end > now,
        { Active: true, Plan: "pro", MaxAccounts: null, ProExpiresAt: { } end } => end > now,
        _ => false
    };

    public static void AssertAccess(MembershipEntitlement? entitlement, DateTimeOffset now,
        IReadOnlyList<Guid> orderedProfiles, Guid? profileId, bool enrollment)
    {
        if (!IsActive(entitlement, now))
            throw new MembershipRequiredException("Your Codex Lover membership needs attention. Sign in or renew at codexlover.com.");
        if (entitlement!.Plan == "pro") return;
        if (enrollment && orderedProfiles.Count >= 2)
            throw new MembershipRequiredException("Your free plan includes two Codex accounts with no time limit. Get monthly Pro to connect more accounts.");
        if (profileId is { } id && !orderedProfiles.Take(2).Contains(id))
            throw new MembershipRequiredException("Your free plan includes the first two saved accounts. Get Pro to use every saved account.");
    }
}
