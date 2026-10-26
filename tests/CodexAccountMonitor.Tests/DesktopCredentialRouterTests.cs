using System.Text;
using System.Text.Json;
using CodexAccountMonitor.Core.Models;
using CodexAccountMonitor.Core.Services;
using CodexAccountMonitor.Infrastructure;
using CodexAccountMonitor.Infrastructure.DesktopSwitching;

namespace CodexAccountMonitor.Tests;

public sealed class DesktopCredentialRouterTests
{
    [Fact]
    public void FinalizedReceiptRoutesRotatedDesktopCredentialsWhileSourceStillMatchesOriginalLoan()
    {
        using var fixture = new Fixture();
        var original = fixture.Install();
        var rotated = Auth("selected", "desktop-rotation");
        File.WriteAllBytes(fixture.DesktopAuth, rotated);

        var match = fixture.Router.MatchingSnapshot(fixture.Profile);

        Assert.NotNull(match);
        Assert.Equal(rotated, match.CloneAuthBytes());
        Assert.Equal(DesktopAuthFileStore.ParseSnapshot(rotated).AccessToken, match.AccessToken);
        Assert.Equal(fixture.Profile.Id, fixture.Router.FindSelectedProfile(new[] { fixture.OtherProfile, fixture.Profile }));
        Assert.Equal(original, File.ReadAllBytes(fixture.ProfileAuth));
        Assert.False(fixture.Store.GetReceipt()!.IsPending);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(original)), fixture.Store.GetReceipt()!.IntendedAuthHash);
        fixture.AssertNoAppServerStarted();
    }

    [Fact]
    public async Task FreshSourceSignInWithSameIdentityCannotBeMistakenForTheOlderDesktopLoan()
    {
        using var fixture = new Fixture();
        var old = fixture.Install();
        var signedInAgain = Auth("selected", "fresh-independent-sign-in");
        var rotatedDesktop = Auth("selected", "old-desktop-session-rotation");
        File.WriteAllBytes(fixture.ProfileAuth, signedInAgain);
        File.WriteAllBytes(fixture.DesktopAuth, rotatedDesktop);
        Assert.Equal(DesktopAuthFileStore.ParseSnapshot(old).Identity, DesktopAuthFileStore.ParseSnapshot(signedInAgain).Identity);

        Assert.Null(fixture.Router.MatchingSnapshot(fixture.Profile));
        Assert.Null(fixture.Router.FindSelectedProfile(new[] { fixture.Profile }));
        using (fixture.ForbidClientStartup())
            Assert.Null(await fixture.Router.CreateClientAsync(fixture.Profile, CancellationToken.None));

        Assert.Equal(signedInAgain, File.ReadAllBytes(fixture.ProfileAuth));
        Assert.Equal(rotatedDesktop, File.ReadAllBytes(fixture.DesktopAuth));
        fixture.AssertNoAppServerStarted();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreparedSourceSynchronizationKeepsDesktopRoutedWhetherTheSourceCopyCommits(bool copyCommits)
    {
        using var fixture = new Fixture();
        var original = fixture.Install();
        var receipt = fixture.Store.GetReceipt()!;
        var rotated = Auth("selected", "desktop-rotation");
        File.WriteAllBytes(fixture.DesktopAuth, rotated);
        fixture.Store.PrepareSourceSynchronization(receipt, AuthHash(original), AuthHash(rotated), () => true);
        if (copyCommits)
            DesktopAuthFileStore.WriteProfileSnapshot(fixture.Paths.CodexHome(fixture.Profile.Id),
                DesktopAuthFileStore.ParseSnapshot(rotated), () => true);

        var match = fixture.Router.MatchingSnapshot(fixture.Profile);
        Assert.NotNull(match);
        Assert.Equal(rotated, match.CloneAuthBytes());
        Assert.Equal(fixture.Profile.Id, fixture.Router.FindSelectedProfile(new[] { fixture.Profile }));
        Assert.Equal(copyCommits ? rotated : original, File.ReadAllBytes(fixture.ProfileAuth));
        fixture.AssertNoAppServerStarted();
    }

    [Fact]
    public void FailedNextSwitchRollbackRestoresRoutingForThePreviouslySynchronizedAccount()
    {
        using var fixture = new Fixture();
        var original = fixture.Install();
        var rotated = Auth("selected", "desktop-rotation");
        File.WriteAllBytes(fixture.DesktopAuth, rotated);
        fixture.Store.PrepareSourceSynchronization(fixture.Store.GetReceipt()!, AuthHash(original), AuthHash(rotated), () => true);
        DesktopAuthFileStore.WriteProfileSnapshot(fixture.Paths.CodexHome(fixture.Profile.Id),
            DesktopAuthFileStore.ParseSnapshot(rotated), () => true);
        var failedNext = fixture.Store.Install(DesktopAuthFileStore.ParseSnapshot(Auth("next", "next-loan")),
            fixture.OtherProfile.Id, () => true);
        fixture.Store.FinalizeSwitch(() => true);
        Assert.True(fixture.Store.Rollback(() => true, expectedReceipt: failedNext));

        Assert.Equal(fixture.Profile.Id, fixture.Router.FindSelectedProfile(new[] { fixture.Profile, fixture.OtherProfile }));
        var match = fixture.Router.MatchingSnapshot(fixture.Profile);
        Assert.NotNull(match);
        Assert.Equal(rotated, match.CloneAuthBytes());
        Assert.Equal(rotated, File.ReadAllBytes(fixture.ProfileAuth));
        fixture.AssertNoAppServerStarted();
    }

    [Fact]
    public async Task IdenticalAuthWithoutReceiptDoesNotClaimDesktopOwnership()
    {
        using var fixture = new Fixture();
        var bytes = Auth("selected", "matching-but-never-lent");
        File.WriteAllBytes(fixture.ProfileAuth, bytes);
        File.WriteAllBytes(fixture.DesktopAuth, bytes);

        Assert.Null(fixture.Store.GetReceipt());
        Assert.Null(fixture.Router.MatchingSnapshot(fixture.Profile));
        Assert.Null(fixture.Router.FindSelectedProfile(new[] { fixture.Profile }));
        using (fixture.ForbidClientStartup())
            Assert.Null(await fixture.Router.CreateClientAsync(fixture.Profile, CancellationToken.None));
        Assert.Equal(bytes, File.ReadAllBytes(fixture.ProfileAuth));
        fixture.AssertNoAppServerStarted();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("different-account")]
    [InlineData("different-user")]
    public async Task UnavailableOrMismatchedDesktopCannotReuseLentRefreshCredentials(string scenario)
    {
        using var fixture = new Fixture();
        var source = fixture.Install();
        switch (scenario)
        {
            case "missing": File.Delete(fixture.DesktopAuth); break;
            case "different-account": File.WriteAllBytes(fixture.DesktopAuth, Auth("other", "external")); break;
            case "different-user": File.WriteAllBytes(fixture.DesktopAuth, Auth("selected", "external-user", "other-chat-user")); break;
        }

        Assert.Null(fixture.Router.MatchingSnapshot(fixture.Profile));
        Assert.Null(fixture.Router.FindSelectedProfile(new[] { fixture.Profile }));
        using (fixture.ForbidClientStartup())
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Router.CreateClientAsync(fixture.Profile, CancellationToken.None));
            Assert.Equal("desktop_sign_in_unavailable", exception.Message);
        }
        Assert.Equal(source, File.ReadAllBytes(fixture.ProfileAuth));
        fixture.AssertNoAppServerStarted();
    }

    [Fact]
    public async Task MalformedDesktopWhileSourceIsStillLentFailsClosed()
    {
        using var fixture = new Fixture();
        var source = fixture.Install();
        File.WriteAllText(fixture.DesktopAuth, "{malformed-desktop-auth}");

        Assert.Throws<InvalidDataException>(() => fixture.Router.MatchingSnapshot(fixture.Profile));
        Assert.Null(fixture.Router.FindSelectedProfile(new[] { fixture.Profile }));
        using (fixture.ForbidClientStartup())
            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Router.CreateClientAsync(fixture.Profile, CancellationToken.None));
        Assert.Equal(source, File.ReadAllBytes(fixture.ProfileAuth));
        fixture.AssertNoAppServerStarted();
    }

    [Fact]
    public async Task MissingSourceForARecordedLoanFailsClosed()
    {
        using var fixture = new Fixture();
        fixture.Install();
        File.Delete(fixture.ProfileAuth);

        Assert.Null(fixture.Router.MatchingSnapshot(fixture.Profile));
        using (fixture.ForbidClientStartup())
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Router.CreateClientAsync(fixture.Profile, CancellationToken.None));
            Assert.Equal("desktop_sign_in_unavailable", error.Message);
        }
        Assert.False(File.Exists(fixture.ProfileAuth));
        fixture.AssertNoAppServerStarted();
    }

    [Fact]
    public async Task PendingSwitchCannotBeUsedAsAnActiveDesktopAccount()
    {
        using var fixture = new Fixture();
        var source = fixture.Install(finalize: false);

        Assert.True(fixture.Store.GetReceipt()!.IsPending);
        Assert.Null(fixture.Router.MatchingSnapshot(fixture.Profile));
        Assert.Null(fixture.Router.FindSelectedProfile(new[] { fixture.Profile }));
        using (fixture.ForbidClientStartup())
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.Router.CreateClientAsync(fixture.Profile, CancellationToken.None));
            Assert.Equal("desktop_sign_in_unavailable", error.Message);
        }
        Assert.Equal(source, File.ReadAllBytes(fixture.ProfileAuth));
        fixture.AssertNoAppServerStarted();
    }

    [Fact]
    public async Task CorruptReceiptFailsClosedWithoutConsumingSavedCredentials()
    {
        using var fixture = new Fixture();
        var source = fixture.Install();
        File.WriteAllBytes(Path.Combine(fixture.Journal, "desktop-switch.receipt.dpapi"), Encoding.UTF8.GetBytes("corrupt-receipt"));

        Assert.Throws<InvalidDataException>(() => fixture.Router.MatchingSnapshot(fixture.Profile));
        Assert.Null(fixture.Router.FindSelectedProfile(new[] { fixture.Profile, fixture.OtherProfile }));
        using (fixture.ForbidClientStartup())
            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Router.CreateClientAsync(fixture.Profile, CancellationToken.None));
        Assert.Equal(source, File.ReadAllBytes(fixture.ProfileAuth));
        fixture.AssertNoAppServerStarted();
    }

    [Fact]
    public async Task UnrelatedAccountDoesNotReadOrRouteTheDesktopCredentials()
    {
        using var fixture = new Fixture();
        fixture.Install();
        File.WriteAllText(fixture.DesktopAuth, "invalid-and-must-not-be-parsed-for-another-account");
        Assert.False(Directory.Exists(fixture.Paths.CodexHome(fixture.OtherProfile.Id)));

        Assert.Null(fixture.Router.MatchingSnapshot(fixture.OtherProfile));
        using (fixture.ForbidClientStartup())
            Assert.Null(await fixture.Router.CreateClientAsync(fixture.OtherProfile, CancellationToken.None));
        fixture.AssertNoAppServerStarted();
    }

    [Fact]
    public void AliasAndMaskedEmailCannotClaimAnAccountWithoutItsExactFingerprint()
    {
        using var fixture = new Fixture();
        fixture.Install();
        var changedMetadata = fixture.Profile with
        {
            Alias = "The selected account",
            MaskedEmail = "s••••@example.test",
            IdentityFingerprint = IdentityUtilities.FingerprintEmail("unrelated@example.test")
        };

        Assert.Null(fixture.Router.MatchingSnapshot(changedMetadata));
        Assert.Null(fixture.Router.FindSelectedProfile(new[] { changedMetadata }));
        fixture.AssertNoAppServerStarted();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "CodexCredentialRouterTests", Guid.NewGuid().ToString("N"));
        private readonly SafeJsonLogger _logger;
        public AppDataPaths Paths { get; }
        public string DesktopAuth { get; }
        public string ProfileAuth { get; }
        public string Journal { get; }
        public AccountProfile Profile { get; } = new()
        {
            Id = Guid.NewGuid(), Slot = 1, Alias = "Fixture account",
            IdentityFingerprint = IdentityUtilities.FingerprintEmail("selected@example.test")
        };
        public AccountProfile OtherProfile { get; } = new()
        {
            Id = Guid.NewGuid(), Slot = 2, Alias = "Other fixture account",
            IdentityFingerprint = IdentityUtilities.FingerprintEmail("other@example.test")
        };
        public DesktopAuthFileStore Store { get; }
        public DesktopCredentialRouter Router { get; }

        public Fixture()
        {
            Paths = new AppDataPaths(Path.Combine(_root, "appdata"));
            Paths.EnsureBaseDirectories();
            var home = Path.Combine(_root, "desktop");
            Directory.CreateDirectory(home);
            DesktopAuth = Path.Combine(home, "auth.json");
            Journal = Path.Combine(Paths.Root, "desktop-switch");
            Store = new DesktopAuthFileStore(home, Journal);
            var directories = new AccountDirectoryService(Paths);
            ProfileAuth = Path.Combine(directories.CreateIsolatedCodexHome(Profile.Id), "auth.json");
            _logger = new SafeJsonLogger(Paths);
            var sentinel = Path.Combine(_root, "never-start-codex.exe");
            File.WriteAllText(sentinel, "Unsigned test sentinel; never execute.");
            File.WriteAllText(Paths.SettingsFile, JsonSerializer.Serialize(new { codexExecutablePath = sentinel }));
            Router = new DesktopCredentialRouter(Store, Paths, directories,
                new CodexExecutableLocator(_logger), new SettingsRepository(Paths), _logger);
        }

        public byte[] Install(bool finalize = true)
        {
            var source = Auth("selected", "original-loan");
            File.WriteAllBytes(ProfileAuth, source);
            Store.Install(DesktopAuthFileStore.ParseSnapshot(source), Profile.Id, () => true);
            if (finalize) Store.FinalizeSwitch(() => true);
            return source;
        }

        // Negative cases must finish before app-server staging or configuration is read.
        // A locked settings file gives a deterministic early failure if routing regresses.
        public IDisposable ForbidClientStartup() => new FileStream(Paths.SettingsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        public void AssertNoAppServerStarted()
        {
            Assert.Equal(new[] { Profile.Id.ToString("N") }, Directory.GetDirectories(Paths.AccountsRoot).Select(Path.GetFileName).ToArray());
            foreach (var log in Directory.GetFiles(Paths.LogsRoot))
            {
                using var reader = new StreamReader(new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
                var text = reader.ReadToEnd();
                Assert.DoesNotContain("app_server_started", text);
                Assert.DoesNotContain("codex_executable_signature_untrusted", text);
                Assert.DoesNotContain("refresh-selected", text);
            }
        }

        public void Dispose()
        {
            _logger.Dispose();
            var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CodexCredentialRouterTests")) + Path.DirectorySeparatorChar;
            var root = Path.GetFullPath(_root);
            if (root.StartsWith(parent, StringComparison.OrdinalIgnoreCase) && Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] Auth(string account, string tokenRevision, string? userId = null)
    {
        var claims = new Dictionary<string, object>
        {
            ["sub"] = "auth0|" + account,
            ["email"] = account + "@example.test",
            ["exp"] = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds(),
            ["jti"] = tokenRevision,
            ["https://api.openai.com/auth"] = new Dictionary<string, object>
            {
                ["chatgpt_account_id"] = account,
                ["chatgpt_user_id"] = userId ?? "chat-user-" + account
            }
        };
        var token = Base64Url(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"typ\":\"JWT\"}"))
            + "." + Base64Url(JsonSerializer.SerializeToUtf8Bytes(claims)) + ".ZmFrZS10ZXN0LXNpZ25hdHVyZQ";
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            auth_mode = "chatgpt",
            tokens = new { id_token = token, access_token = token, refresh_token = "refresh-" + account + "-" + tokenRevision, account_id = account }
        });
    }

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string AuthHash(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
}
