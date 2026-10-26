using CodexAccountMonitor.Infrastructure;
using CodexAccountMonitor.Infrastructure.AppServer;

namespace CodexAccountMonitor.Tests;

public sealed class AppServerLaunchEnvironmentTests
{
    [Fact]
    public void IsolatedChildCannotUseInheritedTokensAndKeepsNetworkAndManagedSettings()
    {
        var inherited = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["CODEX_HOME"] = @"C:\fixture\parent-home",
            ["CODEX_SQLITE_HOME"] = @"C:\fixture\parent-database",
            ["OPENAI_API_KEY"] = "fixture-api-key",
            ["CODEX_API_KEY"] = "fixture-codex-key",
            ["codex_access_token"] = "fixture-access-token",
            ["AZURE_OPENAI_API_KEY"] = "fixture-azure-key",
            ["AWS_ACCESS_KEY_ID"] = "fixture-aws-key",
            ["AWS_SECRET_ACCESS_KEY"] = "fixture-aws-secret",
            ["AWS_SESSION_TOKEN"] = "fixture-aws-session",
            ["CODEX_CA_CERTIFICATE"] = @"C:\fixture\company-ca.pem",
            ["SSL_CERT_FILE"] = @"C:\fixture\fallback-ca.pem",
            ["HTTPS_PROXY"] = "http://fixture-proxy:8080",
            ["CODEX_MANAGED_CONFIG_PATH"] = @"C:\fixture\managed.toml",
            ["PATH"] = @"C:\Windows\System32"
        };
        var child = new Dictionary<string, string?>(inherited, StringComparer.OrdinalIgnoreCase);
        var isolatedHome = @"C:\fixture\isolated-home";

        AppServerLaunchEnvironment.Configure(child, isolatedHome);

        Assert.Equal(isolatedHome, child["CODEX_HOME"]);
        Assert.Equal(isolatedHome, child["CODEX_SQLITE_HOME"]);
        foreach (var credential in new[] { "OPENAI_API_KEY", "CODEX_API_KEY", "CODEX_ACCESS_TOKEN",
                     "AZURE_OPENAI_API_KEY", "AWS_ACCESS_KEY_ID", "AWS_SECRET_ACCESS_KEY", "AWS_SESSION_TOKEN" })
            Assert.False(child.ContainsKey(credential));
        foreach (var setting in new[] { "CODEX_CA_CERTIFICATE", "SSL_CERT_FILE", "HTTPS_PROXY", "CODEX_MANAGED_CONFIG_PATH", "PATH" })
            Assert.Equal(inherited[setting], child[setting]);
        Assert.Equal("fixture-access-token", inherited["CODEX_ACCESS_TOKEN"]);
        Assert.Equal(@"C:\fixture\parent-home", inherited["CODEX_HOME"]);
    }

    [Theory]
    [InlineData("OPENAI_FEDERATION_RULE_ID", "fixture-private-rule")]
    [InlineData("OPENAI_IDENTITY_TOKEN_FILE", @"C:\fixture\private-identity-token")]
    [InlineData("OPENAI_FEDERATION_RULE_ID", "")]
    [InlineData("OPENAI_IDENTITY_TOKEN_FILE", " ")]
    public void WorkloadIdentitySelectionIsRefusedWithoutChangingItsEnvironment(string selector, string value)
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [selector] = value,
            ["CODEX_ACCESS_TOKEN"] = "fixture-parent-access-token",
            ["CODEX_HOME"] = @"C:\fixture\parent-home"
        };
        var before = environment.ToArray();

        var error = Assert.Throws<InheritedWorkloadIdentityException>(() =>
            AppServerLaunchEnvironment.Configure(environment, @"C:\fixture\isolated-home"));

        Assert.Equal(before, environment.ToArray());
        Assert.Contains("contact your administrator", error.Message);
        Assert.DoesNotContain("fixture", error.Message);
        Assert.Equal(error.Message, AccountConnectionErrors.Describe(error));
    }

    [Fact]
    public void WorkloadAuditContextAloneDoesNotPreventSavedAccountUse()
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["OPENAI_WORKLOAD_IDENTITY_CONTEXT"] = "{\"fixture\":\"audit-context\"}"
        };

        AppServerLaunchEnvironment.Configure(environment, @"C:\fixture\isolated-home");

        Assert.Equal("{\"fixture\":\"audit-context\"}", environment["OPENAI_WORKLOAD_IDENTITY_CONTEXT"]);
    }

    [Theory]
    [InlineData("OPENAI_FEDERATION_RULE_ID")]
    [InlineData("OPENAI_IDENTITY_TOKEN_FILE")]
    public void ExplorerHandoffCannotDropEvenAnEmptyWorkloadIdentitySelector(string selector)
    {
        var error = Assert.Throws<MonitorIndependentLaunchException>(() =>
            MonitorStartupBootstrap.PrepareSavedAccountEnvironment(name => name == selector ? string.Empty : null,
                (_, _) => throw new Exception("Workload policy must be validated before environment changes.")));

        Assert.Contains("workload identity", error.Message);
    }
}
