using CodexAccountMonitor.Infrastructure.DesktopSwitching;

namespace CodexAccountMonitor.Tests;

public sealed class DesktopHomePolicyTests
{
    [Theory]
    [InlineData("cli_auth_credentials_store = 'keyring'")]
    [InlineData("'cli_auth_credentials_store' = 'auto'")]
    [InlineData("\"cli_auth_credentials_store\" = \"keyring\"")]
    [InlineData("\"cli_auth_credentials_\\u0073tore\" = \"keyring\"")]
    [InlineData("profiles.test.cli_auth_credentials_store = 'keyring'")]
    [InlineData("profiles.'test'.'cli_auth_credentials_store' = 'keyring'")]
    [InlineData("forced_login_method = 'api'")]
    [InlineData("model_provider = 'other'")]
    [InlineData("forced_chatgpt_workspace_id = 'other-account'")]
    public void AlternateAuthenticationIsRejected(string config) =>
        Assert.Throws<InvalidOperationException>(() => DesktopHomePolicy.AssertConfiguration(config, "account"));

    [Fact]
    public void StandardQuotedFileConfigAndCommentsAreAccepted() =>
        DesktopHomePolicy.AssertConfiguration("# cli_auth_credentials_store='keyring'\n'cli_auth_credentials_store' = 'file' # safe\nmodel_provider='openai'\nforced_login_method=\"chatgpt\"\nforced_chatgpt_workspace_id='account'", "account");
}
