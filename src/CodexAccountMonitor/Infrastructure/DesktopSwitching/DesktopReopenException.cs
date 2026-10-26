namespace CodexAccountMonitor.Infrastructure.DesktopSwitching;

public sealed class DesktopReopenException(string message, Exception innerException)
    : InvalidOperationException(message, innerException);
