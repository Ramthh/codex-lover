namespace CodexAccountMonitor.Infrastructure.AppServer;

internal sealed class InheritedWorkloadIdentityException : InvalidOperationException
{
    public InheritedWorkloadIdentityException() : base(
        "This launch uses a workload identity, which cannot be used for Codex Lover's saved personal accounts. " +
        "Use an environment approved for those accounts, or contact your administrator. Your sign-in was not changed.") { }
}

internal static class AppServerLaunchEnvironment
{
    internal static void AssertNoInheritedWorkloadIdentity(Func<string, string?> readEnvironment)
    {
        // Either selector chooses workload identity ahead of saved credentials, including
        // an incomplete configuration. Refuse it rather than silently bypassing its policy.
        if (readEnvironment("OPENAI_FEDERATION_RULE_ID") is not null
            || readEnvironment("OPENAI_IDENTITY_TOKEN_FILE") is not null)
            throw new InheritedWorkloadIdentityException();
    }

    internal static void Configure(IDictionary<string, string?> environment, string codexHome)
    {
        AssertNoInheritedWorkloadIdentity(name => environment.TryGetValue(name, out var value) ? value : null);

        // Change only the child environment. Account identity must come from the supplied
        // isolated home or explicit protocol login, never from the launcher's credentials.
        foreach (var name in new[]
                 {
                     "OPENAI_API_KEY", "CODEX_API_KEY", "CODEX_ACCESS_TOKEN", "AZURE_OPENAI_API_KEY",
                     "AWS_ACCESS_KEY_ID", "AWS_SECRET_ACCESS_KEY", "AWS_SESSION_TOKEN"
                 })
            environment.Remove(name);

        // Keep managed configuration, certificates, proxies and workload audit context intact.
        environment["CODEX_HOME"] = codexHome;
        environment["CODEX_SQLITE_HOME"] = codexHome;
    }
}
