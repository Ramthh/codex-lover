using CodexAccountMonitor.Infrastructure;
using CodexAccountMonitor.Infrastructure.AppServer;
using CodexAccountMonitor.Infrastructure.Membership;

namespace CodexAccountMonitor.Tests;

public sealed class AccountConnectionErrorsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RequestTimeoutIsNotPresentedAsUserCancellation(bool operationCanceled)
    {
        Exception error = operationCanceled
            ? new OperationCanceledException("secret-provider-detail")
            : new TimeoutException("secret-provider-detail");

        var message = AccountConnectionErrors.Describe(error);

        Assert.Contains("too long to respond", message);
        Assert.DoesNotContain("cancelled", message);
        Assert.DoesNotContain("secret-provider-detail", message);
    }

    [Fact]
    public void AuthenticationRpcFailureExplainsSignInRecovery() =>
        Assert.Contains("Sign in again", AccountConnectionErrors.Describe(new AppServerRpcException(401, true, false)));

    [Fact]
    public void MembershipFailureExplainsMembershipInsteadOfExecutablePath()
    {
        var message = "Your trial includes two accounts. Get Pro to connect more.";
        Assert.Equal(message, AccountConnectionErrors.Describe(new MembershipRequiredException(message)));
    }

    [Fact]
    public void MissingExecutablePointsToConnectionSettings() =>
        Assert.Contains("Settings → Connection", AccountConnectionErrors.Describe(new FileNotFoundException("private-path")));

    [Fact]
    public void UnknownFailureDoesNotExposeRawProviderDetails()
    {
        var message = AccountConnectionErrors.Describe(new InvalidOperationException("secret-token-sentinel"));
        Assert.DoesNotContain("secret-token-sentinel", message);
        Assert.DoesNotContain("Check the Codex path", message);
    }

    [Fact]
    public void RpcFailureRetainsNumericDiagnosticCode() =>
        Assert.Contains("-32602", AccountConnectionErrors.Describe(new AppServerRpcException(-32602, false, false)));
}
