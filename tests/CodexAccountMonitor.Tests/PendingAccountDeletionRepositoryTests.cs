using CodexAccountMonitor.Infrastructure;

namespace CodexAccountMonitor.Tests;

public sealed class PendingAccountDeletionRepositoryTests
{
    [Fact]
    public async Task MarkAndCompletePersistAcrossRepositoryInstances()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodexAccountMonitorTests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppDataPaths(root);
            var first = new PendingAccountDeletionRepository(paths);
            var profileId = Guid.NewGuid();

            await first.MarkAsync(profileId);
            await first.MarkAsync(profileId);

            var reloaded = new PendingAccountDeletionRepository(paths);
            Assert.Equal(new[] { profileId }, await reloaded.LoadAsync());

            await reloaded.CompleteAsync(profileId);
            Assert.Empty(await new PendingAccountDeletionRepository(paths).LoadAsync());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
