using CodexAccountMonitor.Core.Services;
using CodexAccountMonitor.Infrastructure;

namespace CodexAccountMonitor.Tests;

public sealed class AccountIsolationTests
{
    [Fact]
    public void AccountHomePathPolicy_UsesOpaqueDistinctChildrenAndDoesNotMutateGlobalCodexHome()
    {
        var localAppData = Path.Combine(Path.GetTempPath(), "CodexAccountMonitor.Tests", Guid.NewGuid().ToString("N"));
        var firstId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var secondId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var originalGlobalHome = Environment.GetEnvironmentVariable("CODEX_HOME");

        var first = AccountHomePathPolicy.GetCodexHome(localAppData, firstId);
        var second = AccountHomePathPolicy.GetCodexHome(localAppData, secondId);

        Assert.Equal(
            Path.GetFullPath(Path.Combine(
                localAppData,
                "CodexAccountMonitor",
                "accounts",
                firstId.ToString("N"),
                "codex-home")),
            first);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(
                localAppData,
                "CodexAccountMonitor",
                "accounts",
                secondId.ToString("N"),
                "codex-home")),
            second);
        Assert.NotEqual(first, second);
        Assert.False(first.StartsWith(second + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        Assert.False(second.StartsWith(first + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(originalGlobalHome, Environment.GetEnvironmentVariable("CODEX_HOME"));
    }

    [Fact]
    public void EnsureChildPath_AcceptsNestedAccountPath()
    {
        var parent = Path.Combine(Path.GetTempPath(), "CodexAccountMonitor.Tests", "accounts");
        var child = Path.Combine(parent, Guid.NewGuid().ToString("N"), "codex-home");

        AccountHomePathPolicy.EnsureChildPath(parent, child);
    }

    [Fact]
    public void EnsureChildPath_RejectsParentItself()
    {
        var parent = Path.Combine(Path.GetTempPath(), "CodexAccountMonitor.Tests", "accounts");

        Assert.Throws<InvalidOperationException>(() =>
            AccountHomePathPolicy.EnsureChildPath(parent, parent));
    }

    [Fact]
    public void EnsureChildPath_RejectsSiblingWithSharedNamePrefix()
    {
        var basePath = Path.Combine(Path.GetTempPath(), "CodexAccountMonitor.Tests");
        var parent = Path.Combine(basePath, "accounts");
        var sibling = Path.Combine(basePath, "accounts-elsewhere", "profile");

        Assert.Throws<InvalidOperationException>(() =>
            AccountHomePathPolicy.EnsureChildPath(parent, sibling));
    }

    [Fact]
    public void EnsureChildPath_RejectsTraversalOutsideParent()
    {
        var parent = Path.Combine(Path.GetTempPath(), "CodexAccountMonitor.Tests", "accounts");
        var traversal = Path.Combine(parent, "..", "outside", "profile");

        Assert.Throws<InvalidOperationException>(() =>
            AccountHomePathPolicy.EnsureChildPath(parent, traversal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void GetAccountRoot_RejectsBlankLocalAppDataRoot(string root)
    {
        Assert.Throws<ArgumentException>(() =>
            AccountHomePathPolicy.GetAccountRoot(root, Guid.NewGuid()));
    }

    [Fact]
    public void AccountDirectoryService_CreatesIndependentHomesWithoutChangingGlobalEnvironment()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new AppDataPaths(temporary.Path);
        var service = new AccountDirectoryService(paths);
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var originalGlobalHome = Environment.GetEnvironmentVariable("CODEX_HOME");

        var firstHome = service.CreateIsolatedCodexHome(firstId);
        var secondHome = service.CreateIsolatedCodexHome(secondId);

        Assert.True(Directory.Exists(firstHome));
        Assert.True(Directory.Exists(secondHome));
        Assert.NotEqual(firstHome, secondHome);
        Assert.StartsWith(
            paths.AccountsRoot + Path.DirectorySeparatorChar,
            firstHome,
            StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(
            paths.AccountsRoot + Path.DirectorySeparatorChar,
            secondHome,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            "cli_auth_credentials_store = \"file\"\r\n",
            File.ReadAllText(Path.Combine(firstHome, "config.toml")));
        Assert.Equal(originalGlobalHome, Environment.GetEnvironmentVariable("CODEX_HOME"));

        service.RemoveIsolatedAccount(firstId);

        Assert.False(Directory.Exists(paths.AccountRoot(firstId)));
        Assert.True(Directory.Exists(paths.AccountRoot(secondId)));
    }

    [Fact]
    public void AccountDirectoryService_DoesNotOverwriteExistingProfileConfiguration()
    {
        using var temporary = new TemporaryDirectory();
        var service = new AccountDirectoryService(new AppDataPaths(temporary.Path));
        var accountId = Guid.NewGuid();
        var home = service.CreateIsolatedCodexHome(accountId);
        var config = Path.Combine(home, "config.toml");
        File.WriteAllText(config, "model = \"custom\"\r\n");

        service.CreateIsolatedCodexHome(accountId);

        Assert.Equal("model = \"custom\"\r\n", File.ReadAllText(config));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "CodexAccountMonitor.Tests",
                Guid.NewGuid().ToString("N"));
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
