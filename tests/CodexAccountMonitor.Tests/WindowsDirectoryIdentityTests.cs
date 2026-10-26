using System.Security.AccessControl;
using System.Security.Principal;
using CodexAccountMonitor.Infrastructure;

namespace CodexAccountMonitor.Tests;

public sealed class WindowsDirectoryIdentityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "CodexLover-directory-identity-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ExistingDirectoryMatchesItselfAndItsExtendedWindowsPath()
    {
        var directory = NewDirectory("profile");

        Assert.True(WindowsDirectoryIdentity.AreSame(directory, directory));
        Assert.True(WindowsDirectoryIdentity.AreSame(directory, @"\\?\" + directory));
        Assert.True(WindowsDirectoryIdentity.AreSame(directory, directory + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void DifferentDirectoriesWithMatchingLeafNamesAreNotEquivalent()
    {
        var expected = NewDirectory(Path.Combine("expected", "profile", "codex-home"));
        var different = NewDirectory(Path.Combine("different", "profile", "codex-home"));

        Assert.False(WindowsDirectoryIdentity.AreSame(expected, different));
    }

    [Fact]
    public void MissingDirectoryFailsClosedEvenWhenBothNamesMatch()
    {
        var existing = NewDirectory("existing");
        var missing = Path.Combine(_root, "missing");

        Assert.False(WindowsDirectoryIdentity.AreSame(existing, missing));
        Assert.False(WindowsDirectoryIdentity.AreSame(missing, existing));
        Assert.False(WindowsDirectoryIdentity.AreSame(missing, missing));
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public void RegularFileCannotBeAcceptedAsDirectoryIdentity()
    {
        var directory = NewDirectory("profile");
        var file = Path.Combine(directory, "regular-file.txt");
        File.WriteAllText(file, "test data");

        Assert.False(WindowsDirectoryIdentity.AreSame(file, file));
        Assert.False(WindowsDirectoryIdentity.AreSame(directory, file));
        Assert.False(WindowsDirectoryIdentity.AreSame(file, directory));
    }

    [Fact]
    public void UnreadableDirectoryFailsClosed()
    {
        var directory = new DirectoryInfo(NewDirectory("denied"));
        var original = directory.GetAccessControl();
        using var user = WindowsIdentity.GetCurrent();
        var restricted = directory.GetAccessControl();
        restricted.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        restricted.AddAccessRule(new FileSystemAccessRule(user.User!, FileSystemRights.ReadAttributes,
            AccessControlType.Deny));
        try
        {
            directory.SetAccessControl(restricted);

            Assert.False(WindowsDirectoryIdentity.AreSame(directory.FullName, directory.FullName));
        }
        finally
        {
            original.SetAccessRuleProtection(original.AreAccessRulesProtected, preserveInheritance: true);
            directory.SetAccessControl(original);
        }
        Assert.True(WindowsDirectoryIdentity.AreSame(directory.FullName, directory.FullName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("invalid\0path")]
    public void InvalidPathFailsClosed(string? invalid)
    {
        var directory = NewDirectory("profile");

        Assert.False(WindowsDirectoryIdentity.AreSame(invalid, directory));
        Assert.False(WindowsDirectoryIdentity.AreSame(directory, invalid));
    }

    private string NewDirectory(string relativePath) => Directory.CreateDirectory(Path.Combine(_root, relativePath)).FullName;

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
