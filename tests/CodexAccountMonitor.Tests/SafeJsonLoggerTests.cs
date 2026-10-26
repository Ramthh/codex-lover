using System.Text.Json;
using CodexAccountMonitor.Infrastructure;

namespace CodexAccountMonitor.Tests;

public sealed class SafeJsonLoggerTests
{
    [Fact]
    public void Logger_RedactsSensitiveFieldsAndInlineCredentialPatterns()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new AppDataPaths(temporary.Path);
        const string password = "plain-password-value";
        const string cookie = "session-cookie-value";
        const string apiKey = "sk-1234567890SECRET"; // gitleaks:allow -- deliberately fake redaction fixture
        const string bearer = "Bearer access-token-value";
        const string jwt = "eyJabc.def.ghi";

        using (var logger = new SafeJsonLogger(paths))
        {
            logger.Info(
                $"refresh {apiKey}",
                new Dictionary<string, object?>
                {
                    ["password"] = password,
                    ["cookie"] = cookie,
                    ["apiKey"] = apiKey,
                    ["notes"] = $"{bearer} {jwt} {apiKey}",
                    ["ordinary"] = "safe-value"
                });
            logger.Error(
                "refresh_failed",
                new InvalidOperationException($"Do not log {password} or {apiKey}"));
        }

        var logFile = Assert.Single(Directory.GetFiles(paths.LogsRoot, "*.jsonl"));
        var logText = File.ReadAllText(logFile);

        Assert.DoesNotContain(password, logText, StringComparison.Ordinal);
        Assert.DoesNotContain(cookie, logText, StringComparison.Ordinal);
        Assert.DoesNotContain(apiKey, logText, StringComparison.Ordinal);
        Assert.DoesNotContain("access-token-value", logText, StringComparison.Ordinal);
        Assert.DoesNotContain(jwt, logText, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", logText, StringComparison.Ordinal);
        Assert.Contains("safe-value", logText, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", logText, StringComparison.Ordinal);

        var lines = File.ReadAllLines(logFile);
        Assert.Equal(2, lines.Length);
        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        }
    }

    [Fact]
    public void Logger_RedactsSensitiveFieldNamesCaseInsensitively()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new AppDataPaths(temporary.Path);

        using (var logger = new SafeJsonLogger(paths))
        {
            logger.Info("case_test", new Dictionary<string, object?>
            {
                ["AUTHORIZATION"] = "basic-secret",
                ["RefreshToken"] = "refresh-secret",
                ["UserCode"] = "device-secret"
            });
        }

        var log = File.ReadAllText(Assert.Single(Directory.GetFiles(paths.LogsRoot, "*.jsonl")));
        Assert.DoesNotContain("basic-secret", log, StringComparison.Ordinal);
        Assert.DoesNotContain("refresh-secret", log, StringComparison.Ordinal);
        Assert.DoesNotContain("device-secret", log, StringComparison.Ordinal);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "CodexAccountMonitor.LoggerTests",
                Guid.NewGuid().ToString("N"));
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
