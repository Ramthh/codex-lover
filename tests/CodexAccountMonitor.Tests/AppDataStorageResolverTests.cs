using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using CodexAccountMonitor.Infrastructure;
using CodexAccountMonitor.Infrastructure.Membership;

namespace CodexAccountMonitor.Tests;

public sealed class AppDataStorageResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CodexLover-storage-tests", Guid.NewGuid().ToString("N"));
    private string UserProfile => Path.Combine(_root, "user");
    private string Normal => Path.Combine(_root, "normal", "CodexAccountMonitor");
    private string Overlay => Path.Combine(_root, "package", "CodexAccountMonitor");
    private string DesktopHome => Path.Combine(UserProfile, ".codex");
    private string Target => Path.Combine(UserProfile, ".codex-lover", "appdata", "CodexAccountMonitor");

    [Fact]
    public void PreservesPackageOverridesAndNormalFallbackWithoutChangingEitherOriginal()
    {
        Write(Normal, "accounts.json", "normal metadata");
        Write(Overlay, "accounts.json", "package metadata");
        Write(Normal, "desktop-switch/desktop-switch.receipt.dpapi", "normal receipt ciphertext");
        Write(Overlay, "accounts/sample/codex-home/auth.json", "synthetic saved login");
        Write(Overlay, "settings.json", "package settings");

        var local = AppDataStorageResolver.EnsureStorage(UserProfile, [Normal, Overlay], DesktopHome);

        Assert.Equal(Target, new AppDataPaths(local).Root);
        Assert.Equal("package metadata", Read(Target, "accounts.json"));
        Assert.Equal("normal receipt ciphertext", Read(Target, "desktop-switch/desktop-switch.receipt.dpapi"));
        Assert.Equal("synthetic saved login", Read(Target, "accounts/sample/codex-home/auth.json"));
        Assert.Equal("normal metadata", Read(Normal, "accounts.json"));
        Assert.Equal("package metadata", Read(Overlay, "accounts.json"));
        Assert.Equal(DesktopHome, AppDataStorageResolver.ReadMarker(Target).DesktopHome);
    }

    [Fact]
    public void MembershipDpapiSessionStillDecryptsAtTheNewLocation()
    {
        var originalPath = Path.Combine(Overlay, "membership", "session.dpapi");
        var session = new MembershipSession("synthetic-test-session", DateTimeOffset.UtcNow.AddDays(2));
        new MembershipSessionStore(originalPath).Save(session);
        var ciphertext = File.ReadAllBytes(originalPath);

        AppDataStorageResolver.EnsureStorage(UserProfile, [Normal, Overlay], DesktopHome);

        var destination = Path.Combine(Target, "membership", "session.dpapi");
        Assert.Equal(ciphertext, File.ReadAllBytes(destination));
        Assert.Equal(session, new MembershipSessionStore(destination).Read());
        Assert.Equal(ciphertext, File.ReadAllBytes(originalPath));
    }

    [Fact]
    public void CompletedStoreAcceptsAppUpdatesAndNeverReimportsOldState()
    {
        Write(Normal, "settings.json", "original");
        AppDataStorageResolver.EnsureStorage(UserProfile, [Normal], DesktopHome);
        Write(Target, "settings.json", "new app settings");
        Write(Target, "accounts.json", "new accounts");
        Write(Normal, "settings.json", "stale app settings");

        AppDataStorageResolver.EnsureStorage(UserProfile, ["not a valid source\0"], DesktopHome);

        Assert.Equal("new app settings", Read(Target, "settings.json"));
        Assert.Equal("new accounts", Read(Target, "accounts.json"));
    }

    [Fact]
    public void ExistingIncompleteDestinationIsNeverOverwritten()
    {
        Write(Normal, "accounts.json", "source");
        Write(Target, "accounts.json", "preserve incomplete destination");

        Assert.Throws<AppDataStorageException>(() => AppDataStorageResolver.EnsureStorage(UserProfile, [Normal], DesktopHome));

        Assert.Equal("preserve incomplete destination", Read(Target, "accounts.json"));
        Assert.Equal("source", Read(Normal, "accounts.json"));
        Assert.False(File.Exists(Path.Combine(Target, AppDataStorageResolver.MarkerName)));
    }

    [Fact]
    public void SourceMutationDuringCopyDoesNotPublishAPartialStore()
    {
        Write(Normal, "accounts.json", "before");

        Assert.Throws<IOException>(() => AppDataStorageResolver.EnsureStorage(UserProfile, [Normal], DesktopHome,
            () => Write(Normal, "accounts.json", "after")));

        Assert.False(Directory.Exists(Target));
        Assert.Equal("after", Read(Normal, "accounts.json"));
    }

    [Fact]
    public void AddedSourceFileDuringCopyDoesNotPublishAPartialStore()
    {
        Write(Normal, "accounts.json", "accounts");

        Assert.Throws<IOException>(() => AppDataStorageResolver.EnsureStorage(UserProfile, [Normal], DesktopHome,
            () => Write(Normal, "membership/session.dpapi", "new session")));

        Assert.False(Directory.Exists(Target));
    }

    [Fact]
    public void LockedSourceFailsWithoutPublishingOrChangingOriginal()
    {
        Write(Normal, "accounts.json", "accounts");
        using var locked = new FileStream(Path.Combine(Normal, "accounts.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.ThrowsAny<IOException>(() => AppDataStorageResolver.EnsureStorage(UserProfile, [Normal], DesktopHome));

        Assert.False(Directory.Exists(Target));
    }

    [Fact]
    public void UnreadableSourceIsNotTreatedAsAnEmptyFirstRun()
    {
        Write(Normal, "accounts.json", "preserve accounts");
        var directory = new DirectoryInfo(Normal);
        var original = directory.GetAccessControl();
        using var identity = WindowsIdentity.GetCurrent();
        var denied = directory.GetAccessControl();
        denied.SetAccessRuleProtection(true, false);
        denied.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.ListDirectory, AccessControlType.Deny));
        try
        {
            directory.SetAccessControl(denied);
            Assert.Throws<UnauthorizedAccessException>(() => AppDataStorageResolver.EnsureStorage(UserProfile, [Normal], DesktopHome));
            Assert.False(Directory.Exists(Target));
        }
        finally
        {
            original.SetAccessRuleProtection(original.AreAccessRulesProtected, preserveInheritance: true);
            directory.SetAccessControl(original);
        }
        Assert.Equal("preserve accounts", Read(Normal, "accounts.json"));
    }

    [Fact]
    public void SourceOverlappingDestinationIsRejected()
    {
        Assert.Throws<InvalidOperationException>(() => AppDataStorageResolver.EnsureStorage(UserProfile, [UserProfile], DesktopHome));
        Assert.False(Directory.Exists(Target));
    }

    [Fact]
    public void RuntimeAndDiagnosticCachesAreNotMigrated()
    {
        Write(Normal, "accounts.json", "accounts");
        Write(Normal, "diagnostic-runs/sample/auth.json", "diagnostic scratch");
        Write(Normal, "runtime/cache.bin", "runtime scratch");
        Write(Normal, "unrelated.txt", "unrelated");

        AppDataStorageResolver.EnsureStorage(UserProfile, [Normal], DesktopHome);

        Assert.False(Directory.Exists(Path.Combine(Target, "diagnostic-runs")));
        Assert.False(Directory.Exists(Path.Combine(Target, "runtime")));
        Assert.False(File.Exists(Path.Combine(Target, "unrelated.txt")));
        Assert.Equal("diagnostic scratch", Read(Normal, "diagnostic-runs/sample/auth.json"));
    }

    [Fact]
    public void FirstRunWithNoOldStoreCreatesACompletedPrivateStore()
    {
        AppDataStorageResolver.EnsureStorage(UserProfile, [Normal], DesktopHome);

        var marker = AppDataStorageResolver.ReadMarker(Target);
        Assert.Equal(1, marker.Version);
        Assert.Equal(DesktopHome, marker.DesktopHome);
        using var identity = WindowsIdentity.GetCurrent();
        var security = new DirectoryInfo(Target).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        var access = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        Assert.All(access, rule => Assert.Equal(identity.User, rule.IdentityReference));
    }

    [Fact]
    public void ExplicitAppDataRootKeepsExistingTestAndDemoBehavior()
    {
        var explicitRoot = Path.Combine(_root, "explicit");
        var paths = new AppDataPaths(explicitRoot);

        Assert.Equal(Path.Combine(explicitRoot, "CodexAccountMonitor"), paths.Root);
        Assert.StartsWith(paths.AccountsRoot, paths.CodexHome(Guid.NewGuid()), StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Target));
    }

    [Fact]
    public void SourceReparsePointIsRejectedWithoutFollowingOrChangingTarget()
    {
        var outside = Path.Combine(_root, "outside");
        Write(outside, "auth.json", "must remain untouched");
        var link = Path.Combine(Normal, "accounts");
        CreateJunction(link, outside);
        try
        {
            Assert.Throws<InvalidOperationException>(() => AppDataStorageResolver.EnsureStorage(UserProfile, [Normal], DesktopHome));
            Assert.False(Directory.Exists(Target));
            Assert.Equal("must remain untouched", Read(outside, "auth.json"));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public void DestinationReparseAncestorIsRejected()
    {
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        var link = Path.Combine(UserProfile, ".codex-lover");
        CreateJunction(link, outside);
        try
        {
            Assert.Throws<InvalidOperationException>(() => AppDataStorageResolver.EnsureStorage(UserProfile, [Normal], DesktopHome));
            Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public void MarkerCorruptionFailsClosed()
    {
        Write(Target, AppDataStorageResolver.MarkerName, "{invalid");
        Assert.Throws<AppDataStorageException>(() => AppDataStorageResolver.EnsureStorage(UserProfile, [Normal], DesktopHome));
        Assert.Equal("{invalid", Read(Target, AppDataStorageResolver.MarkerName));
    }

    [Fact]
    public void StoredCustomDesktopHomeSurvivesBrokerAndRejectsConflictingOverride()
    {
        var home = Path.Combine(UserProfile, "custom-codex-home");
        AppDataStorageResolver.EnsureStorage(UserProfile, [Normal], home);
        var marker = AppDataStorageResolver.ReadMarker(Target);

        Assert.Equal(home, AppDataStorageResolver.ResolveDesktopHome(marker, null));
        Assert.Equal(home, AppDataStorageResolver.ResolveDesktopHome(marker, home));
        Assert.Throws<AppDataStorageException>(() => AppDataStorageResolver.ResolveDesktopHome(marker, DesktopHome));
    }

    private static void Write(string root, string relative, string value)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, value);
    }

    private static string Read(string root, string relative) => File.ReadAllText(Path.Combine(root, relative));

    private static void CreateJunction(string link, string target)
    {
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + Path.GetFullPath(target));
        var printable = Encoding.Unicode.GetBytes(Path.GetFullPath(target));
        var buffer = new byte[16 + substitute.Length + 2 + printable.Length + 2];
        using (var writer = new BinaryWriter(new MemoryStream(buffer)))
        {
            writer.Write(0xA0000003u);
            writer.Write((ushort)(buffer.Length - 8)); writer.Write((ushort)0); writer.Write((ushort)0);
            writer.Write((ushort)substitute.Length); writer.Write((ushort)(substitute.Length + 2));
            writer.Write((ushort)printable.Length); writer.Write(substitute); writer.Write((ushort)0);
            writer.Write(printable); writer.Write((ushort)0);
        }
        Directory.CreateDirectory(link);
        using var handle = CreateFile(link, 0x40000000, 0, IntPtr.Zero, 3, 0x00200000 | 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid || !DeviceIoControl(handle, 0x000900A4, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new IOException("Could not create a junction test fixture.");
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
        var expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CodexLover-storage-tests")) + Path.DirectorySeparatorChar;
        if (Path.GetFullPath(_root).StartsWith(expected, StringComparison.OrdinalIgnoreCase) && Directory.Exists(_root))
            Directory.Delete(_root, true);
    }
}
