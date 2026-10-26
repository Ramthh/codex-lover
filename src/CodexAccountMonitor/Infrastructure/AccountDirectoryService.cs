using System.Security.AccessControl;
using System.Security.Principal;
using CodexAccountMonitor.Core.Services;

namespace CodexAccountMonitor.Infrastructure;

public sealed class AccountDirectoryService
{
    private readonly AppDataPaths _paths;

    public AccountDirectoryService(AppDataPaths paths)
    {
        _paths = paths;
    }

    public string CreateIsolatedCodexHome(Guid accountId)
    {
        var accountRoot = _paths.AccountRoot(accountId);
        var codexHome = _paths.CodexHome(accountId);
        AccountHomePathPolicy.EnsureChildPath(_paths.AccountsRoot, accountRoot);
        CreateDirectoryWithoutFollowingReparsePoint(accountRoot);
        CreateDirectoryWithoutFollowingReparsePoint(codexHome);
        EnsureTreeContainsNoReparsePoints(accountRoot);
        var configFile = Path.Combine(codexHome, "config.toml");
        if (!File.Exists(configFile))
        {
            // Auth stays inside this ACL-protected profile instead of a shared keyring namespace.
            File.WriteAllText(configFile, "cli_auth_credentials_store = \"file\"\r\n");
        }

        EnsureTreeContainsNoReparsePoints(accountRoot);
        RestrictTreeToCurrentUser(accountRoot);
        return codexHome;
    }

    public void RemoveIsolatedAccount(Guid accountId)
    {
        var accountRoot = _paths.AccountRoot(accountId);
        AccountHomePathPolicy.EnsureChildPath(_paths.AccountsRoot, accountRoot);
        if (Directory.Exists(accountRoot))
        {
            var attributes = File.GetAttributes(accountRoot);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("Refusing to delete an account directory through a reparse point.");
            }

            EnsureTreeContainsNoReparsePoints(accountRoot);
            Directory.Delete(accountRoot, true);
        }
    }

    public void CommitVerifiedAuthentication(Guid stagedAccountId, Guid accountId)
    {
        // Replace only the verified credential file, preserving profile settings,
        // history and the existing login until the final atomic rename.
        var stagedRoot = _paths.AccountRoot(stagedAccountId);
        AccountHomePathPolicy.EnsureChildPath(_paths.AccountsRoot, stagedRoot);
        if ((File.GetAttributes(stagedRoot) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Refusing a staged profile through a reparse point.");
        EnsureTreeContainsNoReparsePoints(stagedRoot);
        var stagedAuth = Path.Combine(_paths.CodexHome(stagedAccountId), "auth.json");
        if (!File.Exists(stagedAuth))
            throw new InvalidOperationException("Verified sign-in did not create profile credentials.");
        var destination = CreateIsolatedCodexHome(accountId);
        var temporary = Path.Combine(destination, "auth.json.pending-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.Copy(stagedAuth, temporary);
            File.Move(temporary, Path.Combine(destination, "auth.json"), true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void EnsureTreeContainsNoReparsePoints(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (FileNotFoundException)
                {
                    continue;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException(
                        "Refusing to recursively delete an account directory containing a reparse point.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
            }
        }
    }

    private static void CreateDirectoryWithoutFollowingReparsePoint(string directory)
    {
        if (Directory.Exists(directory))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    "Refusing to use an account directory through a reparse point.");
            }

            return;
        }

        Directory.CreateDirectory(directory);
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                "Refusing to use an account directory through a reparse point.");
        }
    }

    internal static void RestrictTreeToCurrentUser(string directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("Current Windows user SID is unavailable.");
        ApplyDirectoryAcl(directory, user);
        foreach (var childDirectory in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories))
        {
            ApplyDirectoryAcl(childDirectory, user);
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var security = new FileSecurity();
            security.SetOwner(user);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                user,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
            new FileInfo(file).SetAccessControl(security);
        }
    }

    internal static void RestrictDirectoryToCurrentUser(string directory)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("Current Windows user SID is unavailable.");
        ApplyDirectoryAcl(directory, user);
    }

    private static void ApplyDirectoryAcl(string directory, SecurityIdentifier user)
    {
        var security = new DirectorySecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            user,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(security);
    }
}
