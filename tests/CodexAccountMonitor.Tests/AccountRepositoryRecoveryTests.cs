using CodexAccountMonitor.Core.Models;
using CodexAccountMonitor.Infrastructure;

namespace CodexAccountMonitor.Tests;

public sealed class AccountRepositoryRecoveryTests
{
    [Fact]
    public async Task ProProfilesBeyondTheOldTenAccountLimitArePreserved()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexAccountMonitorTests", Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new AccountRepository(new AppDataPaths(root));
            var profiles = Enumerable.Range(1, 100).Select(slot => new PersistedAccount(
                new AccountProfile { Id = Guid.NewGuid(), Slot = slot, Alias = $"Account {slot}" }, new AccountUsageState())).ToArray();
            await repository.SaveAsync(profiles);
            var saved = await repository.LoadAsync();
            Assert.Equal(100, saved.Count);
            Assert.Equal(profiles.Select(x => x.Profile.Id), saved.Select(x => x.Profile.Id));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"schemaVersion\":1,\"accounts\":[null]}")]
    public async Task InvalidPrimaryRecoversValidatedBackupAndPreservesDamagedFileOnSave(string damaged)
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexAccountMonitorTests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppDataPaths(root);
            var repository = new AccountRepository(paths);
            var original = new PersistedAccount(new AccountProfile { Id = Guid.NewGuid(), Slot = 1, Alias = "Original" }, new AccountUsageState());
            await repository.SaveAsync(new[] { original });
            await File.WriteAllTextAsync(paths.AccountsFile, damaged);
            Assert.Equal(original.Profile.Id, (await repository.LoadAsync()).Single().Profile.Id);
            Assert.NotNull(repository.RecoveryMessage);
            await repository.SaveAsync(new[] { original });
            var preserved = Directory.GetFiles(paths.Root, "accounts.json.damaged-*").Single();
            Assert.Equal(damaged, await File.ReadAllTextAsync(preserved));
            Assert.Single(await repository.LoadAsync());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task DamagedBackupDoesNotBecomeEmptyAccountList()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexAccountMonitorTests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppDataPaths(root);
            paths.EnsureBaseDirectories();
            await File.WriteAllTextAsync(paths.AccountsFile, "{broken");
            await File.WriteAllTextAsync(paths.AccountsFile + ".bak", "{broken backup");
            await Assert.ThrowsAnyAsync<Exception>(() => new AccountRepository(paths).LoadAsync());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
