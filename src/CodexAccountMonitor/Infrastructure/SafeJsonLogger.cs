using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexAccountMonitor.Infrastructure;

public sealed partial class SafeJsonLogger : IDisposable
{
    private static readonly HashSet<string> SensitiveFieldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "cookie", "authorization", "token", "accessToken", "refreshToken",
        "apiKey", "secret", "credential", "authUrl", "userCode"
    };

    private readonly object _gate = new();
    private readonly StreamWriter _writer;
    private bool _disabled;

    public SafeJsonLogger(AppDataPaths paths)
    {
        paths.EnsureBaseDirectories();
        var file = Path.Combine(paths.LogsRoot, $"monitor-{DateTime.UtcNow:yyyyMMdd}.jsonl");
        _writer = new StreamWriter(new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true
        };
    }

    public void Info(string eventName, IReadOnlyDictionary<string, object?>? fields = null) =>
        Write("info", eventName, fields);

    public void Warning(string eventName, IReadOnlyDictionary<string, object?>? fields = null) =>
        Write("warning", eventName, fields);

    public void Error(string eventName, Exception exception, IReadOnlyDictionary<string, object?>? fields = null)
    {
        var safeFields = fields is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(fields);
        safeFields["exceptionType"] = exception.GetType().Name;
        Write("error", eventName, safeFields);
    }

    private void Write(string level, string eventName, IReadOnlyDictionary<string, object?>? fields)
    {
        var safe = new Dictionary<string, object?>
        {
            ["timestampUtc"] = DateTimeOffset.UtcNow,
            ["level"] = level,
            ["event"] = Sanitize(eventName)
        };

        if (fields is not null)
        {
            foreach (var pair in fields)
            {
                safe[pair.Key] = SensitiveFieldNames.Contains(pair.Key)
                    ? "[REDACTED]"
                    : SanitizeValue(pair.Value);
            }
        }

        var json = JsonSerializer.Serialize(safe);
        lock (_gate)
        {
            if (_disabled)
            {
                return;
            }

            try
            {
                _writer.WriteLine(json);
            }
            catch
            {
                // Diagnostics must never change application behavior or rollback a committed action.
                _disabled = true;
            }
        }
    }

    private static object? SanitizeValue(object? value) => value switch
    {
        null => null,
        string text => Sanitize(text),
        Guid or int or long or decimal or double or bool or DateTimeOffset => value,
        Enum enumValue => enumValue.ToString(),
        _ => value.GetType().Name
    };

    private static string Sanitize(string value)
    {
        var sanitized = ApiKeyPattern().Replace(value, "[REDACTED]");
        sanitized = BearerPattern().Replace(sanitized, "Bearer [REDACTED]");
        sanitized = JwtPattern().Replace(sanitized, "[REDACTED]");
        return sanitized.Length <= 256 ? sanitized : sanitized[..256];
    }

    [GeneratedRegex(@"\bsk-[A-Za-z0-9_-]{8,}\b", RegexOptions.IgnoreCase)]
    private static partial Regex ApiKeyPattern();

    [GeneratedRegex(@"\bBearer\s+\S+", RegexOptions.IgnoreCase)]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b")]
    private static partial Regex JwtPattern();

    public void Dispose()
    {
        lock (_gate)
        {
            try
            {
                _writer.Dispose();
            }
            catch
            {
            }
            finally
            {
                _disabled = true;
            }
        }
    }
}
