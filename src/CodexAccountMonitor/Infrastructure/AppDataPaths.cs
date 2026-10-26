using CodexAccountMonitor.Core.Services;

namespace CodexAccountMonitor.Infrastructure;

public sealed class AppDataPaths
{
    public AppDataPaths(string? localAppData = null)
    {
        LocalAppData = localAppData
            ?? AppDataStorageResolver.ResolveDefaultRoot();
        Root = Path.Combine(LocalAppData, "CodexAccountMonitor");
        AccountsRoot = Path.Combine(Root, "accounts");
        LogsRoot = Path.Combine(Root, "logs");
        AccountsFile = Path.Combine(Root, "accounts.json");
        SettingsFile = Path.Combine(Root, "settings.json");
        ResetOperationsFile = Path.Combine(Root, "reset-operations.json");
        PendingDeletionsFile = Path.Combine(Root, "pending-account-deletions.json");
    }

    public string LocalAppData { get; }
    public string Root { get; }
    public string AccountsRoot { get; }
    public string LogsRoot { get; }
    public string AccountsFile { get; }
    public string SettingsFile { get; }
    public string ResetOperationsFile { get; }
    public string PendingDeletionsFile { get; }

    public string AccountRoot(Guid accountId) => AccountHomePathPolicy.GetAccountRoot(LocalAppData, accountId);
    public string CodexHome(Guid accountId) => AccountHomePathPolicy.GetCodexHome(LocalAppData, accountId);

    public void EnsureBaseDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(AccountsRoot);
        Directory.CreateDirectory(LogsRoot);
    }
}
