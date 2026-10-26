using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CodexAccountMonitor.Core.Services;
using CodexAccountMonitor.Infrastructure.DesktopSwitching;

namespace CodexAccountMonitor.Tests;

public sealed class DesktopAuthFileStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CodexDesktopAuthTests", Guid.NewGuid().ToString("N"));
    private string Home => Path.Combine(_root, "desktop");
    private string Profile => Path.Combine(_root, "profile");
    private string Journal => Path.Combine(_root, "monitor", "recovery");
    private string AuthPath => Path.Combine(Home, "auth.json");
    private string PendingPath => Path.Combine(Journal, "desktop-switch.pending.dpapi");
    private string ReceiptPath => Path.Combine(Journal, "desktop-switch.receipt.dpapi");

    public DesktopAuthFileStoreTests()
    {
        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(Profile);
    }

    [Fact]
    public void Parse_ExtractsExactIdentityWithoutExposingTokens()
    {
        var bytes = Auth("selected");
        var snapshot = DesktopAuthFileStore.ParseSnapshot(bytes);
        Assert.Equal("selected", snapshot.Identity.AccountId);
        Assert.Equal("chat-user-selected", snapshot.Identity.UserId);
        Assert.Equal(IdentityUtilities.FingerprintEmail("selected@example.test"), snapshot.Identity.EmailFingerprint);
        Assert.Contains("redacted", snapshot.ToString());
        Assert.DoesNotContain(snapshot.RefreshToken, snapshot.ToString());
        Assert.DoesNotContain(snapshot.AccessToken, JsonSerializer.Serialize(snapshot));
        Assert.Equal(bytes, snapshot.CloneAuthBytes());
        var copy = snapshot.CloneAuthBytes();
        copy[0] = 0;
        Assert.Equal(bytes, snapshot.CloneAuthBytes());
    }

    [Fact]
    public void Parse_RejectsExpiredInstallButAllowsIdentityRead()
    {
        var bytes = Auth("old", expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.Throws<InvalidDataException>(() => DesktopAuthFileStore.ParseSnapshot(bytes));
        File.WriteAllBytes(AuthPath, bytes);
        var store = Store();
        Assert.Equal("old", store.ReadSnapshot()!.Identity.AccountId);
        Assert.Throws<InvalidDataException>(() => store.Install(store.ReadSnapshot()!, Guid.NewGuid(), () => true));
        Assert.False(File.Exists(PendingPath));
        Assert.Equal(bytes, File.ReadAllBytes(AuthPath));
    }

    [Fact]
    public void Parse_RejectsConflictingAccountAndUserClaims()
    {
        Assert.Throws<InvalidDataException>(() => DesktopAuthFileStore.ParseSnapshot(Auth("selected", accessAccount: "other")));
        Assert.Throws<InvalidDataException>(() => DesktopAuthFileStore.ParseSnapshot(Auth("selected", accessUser: "other")));
        Assert.Throws<InvalidDataException>(() => DesktopAuthFileStore.ParseSnapshot(Auth("selected", accessEmail: "other@example.test")));
    }

    [Fact]
    public void Parse_DoesNotConfuseAuth0SubjectWithChatGptUserId()
    {
        var snapshot = DesktopAuthFileStore.ParseSnapshot(Auth("selected"));
        Assert.Equal("chat-user-selected", snapshot.Identity.UserId);
        Assert.NotEqual("auth0|selected", snapshot.Identity.UserId);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"auth_mode\":\"api_key\"}")]
    [InlineData("{\"tokens\":{\"access_token\":\"secret\"}}")]
    [InlineData("{\"tokens\":{},\"tokens\":{}}")]
    [InlineData("[1,2,3]")]
    [InlineData("not-json-secret")]
    public void Parse_RejectsMalformedFilesWithoutEchoingInput(string text)
    {
        var error = Assert.Throws<InvalidDataException>(() => DesktopAuthFileStore.ParseSnapshot(Encoding.UTF8.GetBytes(text)));
        Assert.DoesNotContain("secret", error.ToString());
    }

    [Fact]
    public void Parse_RejectsOversizeCredentials()
    {
        Assert.Throws<InvalidDataException>(() => DesktopAuthFileStore.ParseSnapshot(new byte[DesktopAuthFileStore.MaximumAuthBytes + 1]));
    }

    [Fact]
    public void Install_WritesEncryptedRecoverableJournalBeforeReplacingOnlyAuth()
    {
        var original = Auth("original");
        var selected = Auth("selected");
        File.WriteAllBytes(AuthPath, original);
        var config = Path.Combine(Home, "config.toml");
        var history = Path.Combine(Home, "history.jsonl");
        File.WriteAllText(config, "model = 'test'\n");
        File.WriteAllText(history, "leave-history-alone\n");
        var store = Store();
        var profileId = Guid.NewGuid();
        var checks = 0;
        var receipt = store.Install(DesktopAuthFileStore.ParseSnapshot(selected), profileId, () =>
        {
            checks++;
            if (checks > 1)
            {
                Assert.True(File.Exists(PendingPath));
                Assert.Equal(original, File.ReadAllBytes(AuthPath));
            }
            return true;
        });
        Assert.True(checks >= 3);
        Assert.True(receipt.IsPending);
        Assert.Equal(profileId, store.GetReceipt()!.SelectedProfileId);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(selected)), receipt.IntendedAuthHash);
        Assert.Equal(receipt.IntendedAuthHash, store.GetReceipt()!.IntendedAuthHash);
        Assert.Equal(selected, File.ReadAllBytes(AuthPath));
        Assert.Equal("model = 'test'\n", File.ReadAllText(config));
        Assert.Equal("leave-history-alone\n", File.ReadAllText(history));
        var encrypted = File.ReadAllBytes(PendingPath);
        Assert.DoesNotContain("refresh-original", Encoding.UTF8.GetString(encrypted));
        Assert.DoesNotContain("original@example.test", Encoding.UTF8.GetString(encrypted));
        Assert.DoesNotContain("refresh-selected", Encoding.UTF8.GetString(encrypted));
        AssertUserOnlyAcl(PendingPath);
        AssertUserOnlyAcl(AuthPath);
    }

    [Fact]
    public void PendingSwitch_SurvivesNewInstanceAndRollsBack()
    {
        var original = Auth("original");
        File.WriteAllBytes(AuthPath, original);
        Store().Install(DesktopAuthFileStore.ParseSnapshot(Auth("selected")), Guid.NewGuid(), () => true);
        var reopened = Store();
        Assert.True(reopened.GetReceipt()!.IsPending);
        Assert.True(reopened.Rollback(() => true, pendingOnly: true));
        Assert.Equal(original, File.ReadAllBytes(AuthPath));
        Assert.Null(reopened.GetReceipt());
    }

    [Fact]
    public void Finalize_RetainsEncryptedRollbackReceipt()
    {
        var original = Auth("original");
        File.WriteAllBytes(AuthPath, original);
        var store = Store();
        var id = Guid.NewGuid();
        store.Install(DesktopAuthFileStore.ParseSnapshot(Auth("selected")), id, () => true);
        store.FinalizeSwitch(() => true);
        Assert.False(File.Exists(PendingPath));
        Assert.True(File.Exists(ReceiptPath));
        Assert.False(store.GetReceipt()!.IsPending);
        Assert.Equal(id, store.GetReceipt()!.SelectedProfileId);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(AuthPath))), store.GetReceipt()!.IntendedAuthHash);
        Assert.False(store.Rollback(() => true, pendingOnly: true));
        Assert.True(Store().Rollback(() => true));
        Assert.Equal(original, File.ReadAllBytes(AuthPath));
        Assert.False(File.Exists(ReceiptPath));
    }

    [Fact]
    public void FinalizedRollbackRestoresPreviousAccountProvenanceAndAcceptedSourceHashes()
    {
        var store = Store();
        var firstAuth = Auth("first");
        var first = store.Install(DesktopAuthFileStore.ParseSnapshot(firstAuth), Guid.NewGuid(), () => true);
        store.FinalizeSwitch(() => true);
        var rotatedFirst = Auth("first", refreshSuffix: "rotated");
        File.WriteAllBytes(AuthPath, rotatedFirst);
        var rotatedHash = AuthHash(rotatedFirst);
        store.PrepareSourceSynchronization(first, first.IntendedAuthHash, rotatedHash, () => true);
        var second = store.Install(DesktopAuthFileStore.ParseSnapshot(Auth("second")), Guid.NewGuid(), () => true);
        store.FinalizeSwitch(() => true);

        Assert.True(Store().Rollback(() => true, expectedReceipt: second));

        Assert.Equal(rotatedFirst, File.ReadAllBytes(AuthPath));
        var restored = store.GetReceipt();
        Assert.NotNull(restored);
        Assert.Equal(first.SelectedProfileId, restored.SelectedProfileId);
        Assert.Equal(first.CreatedUtc, restored.CreatedUtc);
        Assert.Equal(first.IntendedAuthHash, restored.IntendedAuthHash);
        Assert.Equal(new[] { first.IntendedAuthHash, rotatedHash }, restored.AcceptedSourceHashes);
        Assert.False(restored.IsPending);
        Assert.False(File.Exists(PendingPath));
        Assert.Throws<InvalidOperationException>(() => store.Rollback(() => true, expectedReceipt: restored));
        Assert.Equal(rotatedFirst, File.ReadAllBytes(AuthPath));
    }

    [Fact]
    public void PrepareSynchronizationDurablyAcceptsBothCopyOutcomesWithoutChangingTargetCasHash()
    {
        var store = Store();
        var original = Auth("selected");
        var receipt = store.Install(DesktopAuthFileStore.ParseSnapshot(original), Guid.NewGuid(), () => true);
        store.FinalizeSwitch(() => true);
        var rotated = Auth("selected", refreshSuffix: "rotated");
        File.WriteAllBytes(AuthPath, rotated);
        store.PrepareSourceSynchronization(receipt, AuthHash(original), AuthHash(rotated), () => true);

        var reloaded = Store().GetReceipt()!;
        Assert.Equal(receipt.IntendedAuthHash, reloaded.IntendedAuthHash);
        Assert.Equal(new[] { AuthHash(original), AuthHash(rotated) }, reloaded.AcceptedSourceHashes);
        Assert.Equal(rotated, File.ReadAllBytes(AuthPath));
        Assert.Throws<InvalidOperationException>(() => store.Rollback(() => true));
    }

    [Fact]
    public void PrepareSynchronizationRequiresARecordedSourceAndExactDesktopBytes()
    {
        var store = Store();
        var original = Auth("selected");
        var receipt = store.Install(DesktopAuthFileStore.ParseSnapshot(original), Guid.NewGuid(), () => true);
        store.FinalizeSwitch(() => true);
        var before = File.ReadAllBytes(ReceiptPath);
        var independent = AuthHash(Auth("selected", refreshSuffix: "new-sign-in"));
        Assert.Throws<InvalidOperationException>(() => store.PrepareSourceSynchronization(
            receipt, independent, AuthHash(original), () => true));
        Assert.Throws<InvalidOperationException>(() => store.PrepareSourceSynchronization(
            receipt, AuthHash(original), independent, () => true));
        Assert.Throws<InvalidOperationException>(() => store.PrepareSourceSynchronization(
            receipt with { SelectedProfileId = Guid.NewGuid() }, AuthHash(original), AuthHash(original), () => true));
        File.WriteAllBytes(AuthPath, Auth("other"));
        Assert.Throws<InvalidOperationException>(() => store.PrepareSourceSynchronization(
            receipt, AuthHash(original), AuthHash(File.ReadAllBytes(AuthPath)), () => true));
        Assert.Equal(before, File.ReadAllBytes(ReceiptPath));
    }

    [Fact]
    public void PrepareSynchronizationRechecksClosedStateAndDesktopHashBeforeReceiptCommit()
    {
        var store = Store();
        var original = Auth("selected");
        var receipt = store.Install(DesktopAuthFileStore.ParseSnapshot(original), Guid.NewGuid(), () => true);
        store.FinalizeSwitch(() => true);
        var before = File.ReadAllBytes(ReceiptPath);
        var checks = 0;
        Assert.Throws<InvalidOperationException>(() => store.PrepareSourceSynchronization(
            receipt, AuthHash(original), AuthHash(original), () => ++checks == 1));
        Assert.Equal(before, File.ReadAllBytes(ReceiptPath));
        checks = 0;
        var external = Auth("selected", refreshSuffix: "external");
        Assert.Throws<InvalidOperationException>(() => store.PrepareSourceSynchronization(
            receipt, AuthHash(original), AuthHash(original), () =>
            {
                if (++checks == 2) File.WriteAllBytes(AuthPath, external);
                return true;
            }));
        Assert.Equal(before, File.ReadAllBytes(ReceiptPath));
        Assert.Equal(external, File.ReadAllBytes(AuthPath));
    }

    [Fact]
    public void PrepareSynchronizationRejectsPendingSwitchAndInvalidHashes()
    {
        var store = Store();
        var bytes = Auth("selected");
        var receipt = store.Install(DesktopAuthFileStore.ParseSnapshot(bytes), Guid.NewGuid(), () => true);
        Assert.Throws<InvalidOperationException>(() => store.PrepareSourceSynchronization(
            receipt, receipt.IntendedAuthHash, receipt.IntendedAuthHash, () => true));
        store.FinalizeSwitch(() => true);
        Assert.Throws<InvalidOperationException>(() => store.PrepareSourceSynchronization(
            receipt, "not-a-sha256", receipt.IntendedAuthHash, () => true));
    }

    [Fact]
    public void SuccessiveSynchronizationKeepsOnlyTheCurrentTwoSourceVersions()
    {
        var store = Store();
        var original = Auth("selected");
        var receipt = store.Install(DesktopAuthFileStore.ParseSnapshot(original), Guid.NewGuid(), () => true);
        store.FinalizeSwitch(() => true);
        var second = Auth("selected", refreshSuffix: "second");
        File.WriteAllBytes(AuthPath, second);
        store.PrepareSourceSynchronization(receipt, AuthHash(original), AuthHash(second), () => true);
        var third = Auth("selected", refreshSuffix: "third");
        File.WriteAllBytes(AuthPath, third);
        store.PrepareSourceSynchronization(store.GetReceipt()!, AuthHash(second), AuthHash(third), () => true);
        Assert.Equal(new[] { AuthHash(second), AuthHash(third) }, store.GetReceipt()!.AcceptedSourceHashes);
        Assert.Equal(AuthHash(original), store.GetReceipt()!.IntendedAuthHash);
    }

    [Fact]
    public void RepeatedSwitchesKeepOnlyShallowPreviousProvenance()
    {
        var store = Store();
        long largest = 0;
        for (var index = 0; index < 20; index++)
        {
            store.Install(DesktopAuthFileStore.ParseSnapshot(Auth("account-" + index)), Guid.NewGuid(), () => true);
            store.FinalizeSwitch(() => true);
            largest = Math.Max(largest, new FileInfo(ReceiptPath).Length);
        }
        Assert.InRange(largest, 1, 16 * 1024);
    }

    [Fact]
    public void Install_RefusesWhenDesktopIsOpen()
    {
        var original = Auth("original");
        File.WriteAllBytes(AuthPath, original);
        Assert.Throws<InvalidOperationException>(() => Store().Install(
            DesktopAuthFileStore.ParseSnapshot(Auth("selected")), Guid.NewGuid(), () => false));
        Assert.Equal(original, File.ReadAllBytes(AuthPath));
        Assert.False(File.Exists(PendingPath));
    }

    [Fact]
    public void Install_RechecksDesktopAfterDurableJournalBeforeWritingAuth()
    {
        var original = Auth("original");
        File.WriteAllBytes(AuthPath, original);
        var checks = 0;
        Assert.Throws<InvalidOperationException>(() => Store().Install(
            DesktopAuthFileStore.ParseSnapshot(Auth("selected")), Guid.NewGuid(), () => ++checks == 1));
        Assert.Equal(original, File.ReadAllBytes(AuthPath));
        Assert.True(File.Exists(PendingPath));
        Assert.True(Store().Rollback(() => true, pendingOnly: true));
        Assert.Equal(original, File.ReadAllBytes(AuthPath));
    }

    [Fact]
    public void Install_RechecksDesktopImmediatelyBeforeAtomicReplacement()
    {
        var original = Auth("original");
        File.WriteAllBytes(AuthPath, original);
        var checks = 0;
        Assert.Throws<InvalidOperationException>(() => Store().Install(
            DesktopAuthFileStore.ParseSnapshot(Auth("selected")), Guid.NewGuid(), () => ++checks < 3));
        Assert.Equal(original, File.ReadAllBytes(AuthPath));
        Assert.True(Store().GetReceipt()!.IsPending);
        Assert.Empty(Directory.GetFiles(Home, "*.tmp"));
    }

    [Fact]
    public void Rollback_RequiresClosedDesktopAndPreservesPendingWhenOpen()
    {
        var selected = Auth("selected");
        File.WriteAllBytes(AuthPath, Auth("original"));
        var store = Store();
        store.Install(DesktopAuthFileStore.ParseSnapshot(selected), Guid.NewGuid(), () => true);
        Assert.Throws<InvalidOperationException>(() => store.Rollback(() => false));
        Assert.Equal(selected, File.ReadAllBytes(AuthPath));
        Assert.True(store.GetReceipt()!.IsPending);
        Assert.Throws<InvalidOperationException>(() => store.FinalizeSwitch(() => false));
        Assert.True(File.Exists(PendingPath));
    }

    [Fact]
    public void Rollback_RechecksClosedStateImmediatelyBeforeRestore()
    {
        var selected = Auth("selected");
        File.WriteAllBytes(AuthPath, Auth("original"));
        var store = Store();
        store.Install(DesktopAuthFileStore.ParseSnapshot(selected), Guid.NewGuid(), () => true);
        var checks = 0;
        Assert.Throws<InvalidOperationException>(() => store.Rollback(() => ++checks == 1));
        Assert.Equal(selected, File.ReadAllBytes(AuthPath));
        Assert.True(File.Exists(PendingPath));
    }

    [Fact]
    public void RollbackAndFinalize_NeverOverwriteExternallyChangedCredentials()
    {
        File.WriteAllBytes(AuthPath, Auth("original"));
        var store = Store();
        store.Install(DesktopAuthFileStore.ParseSnapshot(Auth("selected")), Guid.NewGuid(), () => true);
        var changed = Auth("external");
        File.WriteAllBytes(AuthPath, changed);
        Assert.Throws<InvalidOperationException>(() => store.Rollback(() => true));
        Assert.Throws<InvalidOperationException>(() => store.FinalizeSwitch(() => true));
        Assert.Equal(changed, File.ReadAllBytes(AuthPath));
        Assert.True(File.Exists(PendingPath));
    }

    [Fact]
    public void Install_RefusesAnExternalWriteBetweenJournalAndReplacement()
    {
        File.WriteAllBytes(AuthPath, Auth("original"));
        var changed = Auth("external");
        var checks = 0;
        Assert.Throws<InvalidOperationException>(() => Store().Install(
            DesktopAuthFileStore.ParseSnapshot(Auth("selected")), Guid.NewGuid(), () =>
            {
                if (++checks == 3) File.WriteAllBytes(AuthPath, changed);
                return true;
            }));
        Assert.Equal(changed, File.ReadAllBytes(AuthPath));
        Assert.True(File.Exists(PendingPath));
    }

    [Fact]
    public void MissingOriginalAuth_RollbackRestoresAbsence()
    {
        var store = Store();
        Assert.Null(store.ReadSnapshot());
        store.Install(DesktopAuthFileStore.ParseSnapshot(Auth("selected")), Guid.NewGuid(), () => true);
        Assert.True(File.Exists(AuthPath));
        Assert.True(store.Rollback(() => true));
        Assert.False(File.Exists(AuthPath));
    }

    [Fact]
    public void SecondInstall_RequiresPendingRecovery()
    {
        File.WriteAllBytes(AuthPath, Auth("original"));
        var store = Store();
        var selected = Auth("selected");
        store.Install(DesktopAuthFileStore.ParseSnapshot(selected), Guid.NewGuid(), () => true);
        Assert.Throws<InvalidOperationException>(() => store.Install(
            DesktopAuthFileStore.ParseSnapshot(Auth("other")), Guid.NewGuid(), () => true));
        Assert.Equal(selected, File.ReadAllBytes(AuthPath));
    }

    [Fact]
    public void Rollback_WithExpectedReceiptNeverRestoresAnOlderOrDifferentSwitch()
    {
        File.WriteAllBytes(AuthPath, Auth("original"));
        var store = Store();
        var previous = store.Install(DesktopAuthFileStore.ParseSnapshot(Auth("first")), Guid.NewGuid(), () => true);
        store.FinalizeSwitch(() => true);
        var second = Auth("second");
        var current = store.Install(DesktopAuthFileStore.ParseSnapshot(second), Guid.NewGuid(), () => true);
        Assert.Throws<InvalidOperationException>(() => store.Rollback(() => true, expectedReceipt: previous));
        Assert.Equal(second, File.ReadAllBytes(AuthPath));
        Assert.Equal(current.SelectedProfileId, store.GetReceipt()!.SelectedProfileId);
        Assert.True(store.Rollback(() => true, expectedReceipt: current));
        var first = File.ReadAllBytes(AuthPath);
        Assert.Throws<InvalidOperationException>(() => store.Rollback(() => true, expectedReceipt: current));
        Assert.Equal(first, File.ReadAllBytes(AuthPath));
        Assert.True(store.Rollback(() => true, expectedReceipt: previous));
    }

    [Fact]
    public void Rollback_WithExpectedReceiptChecksTransactionTimeAndContentHash()
    {
        File.WriteAllBytes(AuthPath, Auth("original"));
        var store = Store();
        var selected = Auth("selected");
        var receipt = store.Install(DesktopAuthFileStore.ParseSnapshot(selected), Guid.NewGuid(), () => true);
        Assert.Throws<InvalidOperationException>(() => store.Rollback(() => true,
            expectedReceipt: receipt with { CreatedUtc = receipt.CreatedUtc.AddSeconds(1) }));
        Assert.Throws<InvalidOperationException>(() => store.Rollback(() => true,
            expectedReceipt: receipt with { IntendedAuthHash = new string('0', 64) }));
        Assert.Equal(selected, File.ReadAllBytes(AuthPath));
        Assert.True(store.Rollback(() => true, expectedReceipt: receipt));
    }

    [Fact]
    public void CorruptJournal_FailsClosedWithoutTouchingAuth()
    {
        var original = Auth("original");
        File.WriteAllBytes(AuthPath, original);
        var store = Store();
        store.GetReceipt();
        File.WriteAllBytes(PendingPath, Encoding.UTF8.GetBytes("invalid-encryption"));
        Assert.Throws<InvalidDataException>(() => store.GetReceipt());
        Assert.Throws<InvalidDataException>(() => store.Rollback(() => true));
        Assert.Throws<InvalidDataException>(() => store.Install(
            DesktopAuthFileStore.ParseSnapshot(Auth("selected")), Guid.NewGuid(), () => true));
        Assert.Equal(original, File.ReadAllBytes(AuthPath));
    }

    [Fact]
    public void JournalIsBoundToItsSelectedDesktopHome()
    {
        File.WriteAllBytes(AuthPath, Auth("original"));
        Store().Install(DesktopAuthFileStore.ParseSnapshot(Auth("selected")), Guid.NewGuid(), () => true);
        var otherHome = Path.Combine(_root, "other-home");
        Directory.CreateDirectory(otherHome);
        var changedStore = new DesktopAuthFileStore(otherHome, Journal);
        Assert.Throws<InvalidDataException>(() => changedStore.GetReceipt());
        Assert.Throws<InvalidDataException>(() => changedStore.Rollback(() => true));
        Assert.False(File.Exists(Path.Combine(otherHome, "auth.json")));
    }

    [Fact]
    public void ProfileSaveBack_RequiresSameExactAccountAndLeavesOtherFilesAlone()
    {
        var path = Path.Combine(Profile, "auth.json");
        var original = Auth("same", expiresAt: DateTimeOffset.UtcNow.AddHours(-1));
        File.WriteAllBytes(path, original);
        File.WriteAllText(Path.Combine(Profile, "config.toml"), "unchanged");
        Assert.Throws<InvalidOperationException>(() => DesktopAuthFileStore.WriteProfileSnapshot(
            Profile, DesktopAuthFileStore.ParseSnapshot(Auth("different")), () => true));
        Assert.Equal(original, File.ReadAllBytes(path));
        var updated = Auth("same", refreshSuffix: "rotated");
        DesktopAuthFileStore.WriteProfileSnapshot(Profile, DesktopAuthFileStore.ParseSnapshot(updated), () => true);
        Assert.Equal(updated, File.ReadAllBytes(path));
        Assert.Equal("unchanged", File.ReadAllText(Path.Combine(Profile, "config.toml")));
    }

    [Fact]
    public void ProfileSaveBack_RechecksGuardAndConcurrentChange()
    {
        var path = Path.Combine(Profile, "auth.json");
        var original = Auth("same");
        File.WriteAllBytes(path, original);
        var snapshot = DesktopAuthFileStore.ParseSnapshot(Auth("same", refreshSuffix: "new"));
        Assert.Throws<InvalidOperationException>(() => DesktopAuthFileStore.WriteProfileSnapshot(Profile, snapshot, () => false));
        Assert.Equal(original, File.ReadAllBytes(path));
        var concurrent = Auth("same", refreshSuffix: "concurrent");
        Assert.Throws<InvalidOperationException>(() => DesktopAuthFileStore.WriteProfileSnapshot(Profile, snapshot, () =>
        {
            File.WriteAllBytes(path, concurrent);
            return true;
        }));
        Assert.Equal(concurrent, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("relative-home")]
    [InlineData("C:\\")]
    [InlineData("\\\\server\\share\\home")]
    [InlineData("C:\\home:alternate")]
    public void PathPolicy_RejectsAmbiguousPaths(string home)
    {
        Assert.Throws<InvalidOperationException>(() => new DesktopAuthFileStore(home, Journal));
    }

    [Fact]
    public void PathPolicy_RejectsJournalInsideDesktopHome()
    {
        Assert.Throws<InvalidOperationException>(() => new DesktopAuthFileStore(Home, Path.Combine(Home, "recovery")));
        Assert.Throws<InvalidOperationException>(() => new DesktopAuthFileStore(Home, _root));
    }

    [Fact]
    public void PathPolicy_RejectsAuthReparsePointWithoutReadingItsTarget()
    {
        var target = Path.Combine(Profile, "must-not-read.txt");
        File.WriteAllText(target, "unrelated data");
        CreateJunction(AuthPath, Profile);
        try
        {
            Assert.Throws<InvalidOperationException>(() => Store());
            Assert.Throws<InvalidOperationException>(() => DesktopAuthFileStore.ReadSnapshotFromHome(Home));
            Assert.Equal("unrelated data", File.ReadAllText(target));
        }
        finally { Directory.Delete(AuthPath); }
    }

    [Fact]
    public void PathPolicy_RejectsReparseAncestorEvenWhenAuthIsMissing()
    {
        var link = Path.Combine(_root, "linked-home");
        CreateJunction(link, Home);
        try
        {
            Assert.Throws<InvalidOperationException>(() => new DesktopAuthFileStore(link, Journal));
            Assert.Throws<InvalidOperationException>(() => DesktopAuthFileStore.ReadSnapshotFromHome(link));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public void PathPolicy_RechecksAuthAfterConstruction()
    {
        var store = Store();
        var target = Path.Combine(Profile, "must-not-touch.txt");
        File.WriteAllText(target, "unrelated data");
        CreateJunction(AuthPath, Profile);
        try
        {
            Assert.Throws<InvalidOperationException>(() => store.Install(
                DesktopAuthFileStore.ParseSnapshot(Auth("selected")), Guid.NewGuid(), () => true));
            Assert.Equal("unrelated data", File.ReadAllText(target));
        }
        finally { Directory.Delete(AuthPath); }
    }

    [Fact]
    public async Task ParallelReceiptReadsOnOneStoreDoNotConflictWithEachOther()
    {
        var store = Store();
        Assert.Null(store.GetReceipt());
        using var start = new ManualResetEventSlim();
        var readers = Enumerable.Range(0, 8).Select(_ => Task.Factory.StartNew(() =>
        {
            start.Wait();
            for (var read = 0; read < 20; read++) Assert.Null(store.GetReceipt());
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        start.Set();
        await Task.WhenAll(readers);
        Assert.False(File.Exists(AuthPath));
        Assert.False(File.Exists(PendingPath));
        Assert.False(File.Exists(ReceiptPath));
    }

    [Fact]
    public async Task ReceiptReadWaitsForSameStoreTransactionToFinish()
    {
        var store = Store();
        using var enteredGuard = new ManualResetEventSlim();
        using var releaseGuard = new ManualResetEventSlim();
        using var readStarted = new ManualResetEventSlim();
        var profileId = Guid.NewGuid();
        var install = Task.Factory.StartNew(() => store.Install(
            DesktopAuthFileStore.ParseSnapshot(Auth("selected")), profileId, () =>
            {
                enteredGuard.Set();
                if (!releaseGuard.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                return true;
            }), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Assert.True(enteredGuard.Wait(TimeSpan.FromSeconds(10)));
        var read = Task.Factory.StartNew(() =>
        {
            readStarted.Set();
            return store.GetReceipt();
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            Assert.True(readStarted.Wait(TimeSpan.FromSeconds(10)));
            await Task.Delay(100);
            Assert.False(read.IsCompleted);
        }
        finally { releaseGuard.Set(); }
        await install;
        var receipt = await read;
        Assert.Equal(profileId, receipt!.SelectedProfileId);
        Assert.True(receipt.IsPending);
    }

    [Fact]
    public void ExternalExclusiveLockStillBlocksOperationsAndDoesNotStrandTheLocalGate()
    {
        var store = Store();
        Assert.Null(store.GetReceipt());
        using (var externalLock = new FileStream(Path.Combine(Journal, "desktop-switch.lock"),
                   FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var error = Assert.Throws<InvalidOperationException>(() => store.GetReceipt());
            Assert.Equal("Another account switch is already in progress.", error.Message);
            Assert.Throws<InvalidOperationException>(() => store.Install(
                DesktopAuthFileStore.ParseSnapshot(Auth("selected")), Guid.NewGuid(), () => true));
            Assert.False(File.Exists(AuthPath));
            Assert.False(File.Exists(PendingPath));
        }
        Assert.Null(store.GetReceipt());
        var id = Guid.NewGuid();
        store.Install(DesktopAuthFileStore.ParseSnapshot(Auth("selected")), id, () => true);
        Assert.Equal(id, store.GetReceipt()!.SelectedProfileId);
    }

    private DesktopAuthFileStore Store() => new(Home, Journal);
    private static string AuthHash(byte[] bytes) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));

    private static byte[] Auth(string account, DateTimeOffset? expiresAt = null, string? accessAccount = null,
        string? accessUser = null, string? accessEmail = null, string refreshSuffix = "initial")
    {
        var id = new Dictionary<string, object>
        {
            ["sub"] = "auth0|" + account,
            ["email"] = account + "@example.test",
            ["https://api.openai.com/auth"] = new Dictionary<string, object>
            {
                ["chatgpt_account_id"] = account,
                ["chatgpt_user_id"] = "chat-user-" + account
            }
        };
        var access = new Dictionary<string, object>
        {
            ["sub"] = "auth0|" + account,
            ["exp"] = (expiresAt ?? DateTimeOffset.UtcNow.AddHours(2)).ToUnixTimeSeconds(),
            ["https://api.openai.com/auth"] = new Dictionary<string, object>
            {
                ["chatgpt_account_id"] = accessAccount ?? account,
                ["chatgpt_user_id"] = accessUser ?? "chat-user-" + account
            },
            ["https://api.openai.com/profile"] = new Dictionary<string, object>
            {
                ["email"] = accessEmail ?? account + "@example.test"
            }
        };
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            auth_mode = "chatgpt",
            tokens = new { id_token = Jwt(id), access_token = Jwt(access), refresh_token = "refresh-" + account + "-" + refreshSuffix, account_id = account }
        });
    }

    private static string Jwt(object claims) => Base64Url(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"typ\":\"JWT\"}"))
        + "." + Base64Url(JsonSerializer.SerializeToUtf8Bytes(claims)) + ".ZmFrZS10ZXN0LXNpZ25hdHVyZQ";
    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static void AssertUserOnlyAcl(string path)
    {
        using var user = WindowsIdentity.GetCurrent();
        var security = new FileInfo(path).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        Assert.NotEmpty(rules);
        Assert.All(rules, rule => Assert.Equal(user.User, rule.IdentityReference));
    }

    // A mount-point reparse fixture works for an ordinary Windows user; symbolic links
    // require a machine-wide privilege/developer-mode setting which tests must not alter.
    private static void CreateJunction(string link, string target)
    {
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + Path.GetFullPath(target));
        var printable = Encoding.Unicode.GetBytes(Path.GetFullPath(target));
        var buffer = new byte[16 + substitute.Length + 2 + printable.Length + 2];
        using (var writer = new BinaryWriter(new MemoryStream(buffer)))
        {
            writer.Write(0xA0000003u);
            writer.Write((ushort)(buffer.Length - 8));
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((ushort)substitute.Length);
            writer.Write((ushort)(substitute.Length + 2));
            writer.Write((ushort)printable.Length);
            writer.Write(substitute);
            writer.Write((ushort)0);
            writer.Write(printable);
            writer.Write((ushort)0);
        }
        Directory.CreateDirectory(link);
        using var handle = CreateFile(link, 0x40000000, 0, IntPtr.Zero, 3, 0x00200000 | 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid || !DeviceIoControl(handle, 0x000900A4, buffer, buffer.Length,
            IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new IOException($"Could not create a junction test fixture ({Marshal.GetLastWin32Error()}).");
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share,
        IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle handle, uint control,
        byte[] input, int inputLength, IntPtr output, int outputLength, out int returned, IntPtr overlapped);

    public void Dispose()
    {
        var expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CodexDesktopAuthTests")) + Path.DirectorySeparatorChar;
        var root = Path.GetFullPath(_root);
        if (root.StartsWith(expected, StringComparison.OrdinalIgnoreCase) && Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
