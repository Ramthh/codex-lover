namespace CodexAccountMonitor.Infrastructure.DesktopSwitching;

public sealed class DesktopQuitValidationException(string reasonCode, string message) : InvalidOperationException(message)
{
    public string ReasonCode { get; } = reasonCode;
}
