namespace CodexAccountMonitor.Core.Services;

public static class AccountHomePathPolicy
{
    public static string GetAccountRoot(string localAppDataRoot, Guid accountId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppDataRoot);

        var productRoot = Path.GetFullPath(Path.Combine(localAppDataRoot, "CodexAccountMonitor", "accounts"));
        var accountRoot = Path.GetFullPath(Path.Combine(productRoot, accountId.ToString("N")));
        EnsureChildPath(productRoot, accountRoot);
        return accountRoot;
    }

    public static string GetCodexHome(string localAppDataRoot, Guid accountId) =>
        Path.Combine(GetAccountRoot(localAppDataRoot, accountId), "codex-home");

    public static void EnsureChildPath(string parent, string candidate)
    {
        var normalizedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        var normalizedCandidate = Path.GetFullPath(candidate);
        var prefix = normalizedParent + Path.DirectorySeparatorChar;

        if (!normalizedCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The account directory must remain inside the application account root.");
        }
    }
}
