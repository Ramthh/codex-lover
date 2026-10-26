using CodexAccountMonitor.Core.Models;
using CodexAccountMonitor.Infrastructure;

namespace CodexAccountMonitor.ViewModels;

public static class DemoAccountSeed
{
    public static IReadOnlyList<PersistedAccount> Create()
    {
        var aliases = new[] { "Personal", "Work", "Studio", "Client A", "Client B", "Research", "Dev", "Team", "Sandbox", "Backup" };
        var five = new[] { 72, 54, 91, 38, 66, 83, 47, 75, 29, 96 };
        var weekly = new[] { 68, 42, 76, 31, 57, 80, 45, 63, 22, 90 };
        var banks = new[] { 2, 1, 3, 0, 1, 2, 0, 4, 1, 2 };
        var now = DateTimeOffset.UtcNow;

        return Enumerable.Range(0, 10).Select(index =>
        {
            var id = Guid.Parse($"00000000-0000-0000-0000-{index + 1:000000000000}");
            var profile = new AccountProfile
            {
                Id = id,
                Slot = index + 1,
                Alias = aliases[index],
                MaskedEmail = $"{char.ToLowerInvariant(aliases[index][0])}••••@example.com",
                IdentityFingerprint = "DEMO",
                AddedAtUtc = now.AddDays(-30)
            };
            var snapshot = new QuotaSnapshot(
                new QuotaWindowSnapshot(100 - five[index], five[index], TimeSpan.FromHours(5), now.AddHours(2 + index * .4)),
                new QuotaWindowSnapshot(100 - weekly[index], weekly[index], TimeSpan.FromDays(7), now.AddDays(3).AddHours(index)),
                banks[index],
                null);
            var usage = new AccountUsageState
            {
                Snapshot = snapshot,
                Authentication = AuthenticationHealth.Healthy,
                LastSuccessfulRefreshUtc = now.AddMinutes(-(index % 4)),
                LastAttemptUtc = now
            };
            return new PersistedAccount(profile, usage);
        }).ToArray();
    }
}
