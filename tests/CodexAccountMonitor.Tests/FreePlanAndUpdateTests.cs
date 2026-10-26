using CodexAccountMonitor.Infrastructure.Membership;

namespace CodexAccountMonitor.Tests;
public sealed class FreePlanAndUpdateTests
{
    [Fact] public void FreePlanHasNoTimeLimitButCannotEnrollThirdAccount()
    {
        var entitlement = new MembershipEntitlement("free", true, 2, null, null);
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var future = DateTimeOffset.UtcNow.AddYears(20);
        Assert.True(MembershipPolicy.IsActive(entitlement, future));
        MembershipPolicy.AssertAccess(entitlement, future, ids[..1], null, true);
        MembershipPolicy.AssertAccess(entitlement, future, ids, ids[1], false);
        Assert.Throws<MembershipRequiredException>(() => MembershipPolicy.AssertAccess(entitlement, future, ids, ids[2], false));
        Assert.Throws<MembershipRequiredException>(() => MembershipPolicy.AssertAccess(entitlement, future, ids[..2], null, true));
        Assert.False(MembershipPolicy.IsActive(entitlement with { Active = false }, future));
        Assert.False(MembershipPolicy.IsActive(entitlement with { MaxAccounts = null }, future));
    }
    [Theory]
    [InlineData("1.6.0", "https://codexlover.com/news/release", false)]
    [InlineData("1.5.3", "https://codexlover.com/news/release", false)]
    [InlineData("1.6.1", "https://codexlover.com/news/release", true)]
    [InlineData("2.0.0", "https://codexlover.com/news/release", true)]
    [InlineData("garbage", "https://codexlover.com/news/release", false)]
    [InlineData("2.0.0", "https://evil.test/download", false)]
    [InlineData("2.0.0", "http://codexlover.com/download", false)]
    public void UpdateBadgeRequiresANewerVersionAndTrustedWebsite(string version, string url, bool expected)
        => Assert.Equal(expected, AppUpdatePolicy.IsNewer(new(version, url, null, DateTimeOffset.UtcNow), new Version(1, 6, 0, 0)));
}
