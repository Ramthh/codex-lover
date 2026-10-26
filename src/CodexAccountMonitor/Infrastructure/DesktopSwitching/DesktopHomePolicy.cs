using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexAccountMonitor.Infrastructure.DesktopSwitching;

public static class DesktopHomePolicy
{
    public static string DefaultHome => AppDataStorageResolver.ResolveDesktopHome();

    public static void AssertSupported(string home, string? selectedAccountId = null)
    {
        foreach (var name in new[] { "OPENAI_API_KEY", "CODEX_API_KEY", "CODEX_ACCESS_TOKEN" })
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
                throw new InvalidOperationException("An inherited credential could override the selected account. Launch the monitor from the Windows Start menu without API-key environment overrides.");
        var config = Path.Combine(home, "config.toml");
        if (!File.Exists(config)) return;
        if (new FileInfo(config).Length > 1024 * 1024)
            throw new InvalidOperationException("The Codex configuration is too large to verify safely.");
        AssertConfiguration(File.ReadAllText(config), selectedAccountId);
    }

    // Conservative eligibility check, not a replacement TOML parser. Recognize
    // quoted/dotted keys and reject alternate auth/provider configurations even
    // in profiles, without editing Codex's configuration.
    internal static void AssertConfiguration(string text, string? selectedAccountId)
    {
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] is '#' or '[') continue;
            var equals = trimmed.IndexOf('=');
            if (equals < 0) continue;
            var keyPart = trimmed[..equals].Trim();
            var keys = Regex.Matches(keyPart, "(?:^|\\.)\\s*(?:\"(?<double>(?:\\\\.|[^\"])*)\"|'(?<single>[^']*)'|(?<bare>[A-Za-z0-9_-]+))\\s*(?=\\.|$)");
            if (keys.Count == 0) continue;
            var match = keys[^1];
            var key = match.Groups["bare"].Success ? match.Groups["bare"].Value
                : match.Groups["single"].Success ? match.Groups["single"].Value
                : DecodeString(match.Groups["double"].Value);
            if (key is not ("cli_auth_credentials_store" or "forced_login_method" or "forced_chatgpt_workspace_id" or "model_provider")) continue;
            var valueMatch = Regex.Match(trimmed[(equals + 1)..].Trim(), "^(?:\"(?<double>(?:\\\\.|[^\"])*)\"|'(?<single>[^']*)')\\s*(?:#.*)?$");
            if (!valueMatch.Success)
                throw new InvalidOperationException("This Codex authentication configuration cannot be verified safely. No login was changed.");
            var value = valueMatch.Groups["single"].Success ? valueMatch.Groups["single"].Value : DecodeString(valueMatch.Groups["double"].Value);
            if (key == "cli_auth_credentials_store" && value != "file")
                throw new InvalidOperationException("This Codex installation uses an unsupported credential store. File-based Codex sign-in is required; no login was changed.");
            if (key == "forced_login_method" && value != "chatgpt")
                throw new InvalidOperationException("Codex requires a different sign-in method. No login was changed.");
            if (key == "model_provider" && value != "openai")
                throw new InvalidOperationException("Codex uses another model provider. This switcher requires the OpenAI provider to use the selected account's quota.");
            if (key == "forced_chatgpt_workspace_id" && selectedAccountId is not null && value != selectedAccountId)
                throw new InvalidOperationException("This account does not match the workspace required by Codex. No login was changed.");
        }
    }

    private static string DecodeString(string value)
    {
        try
        {
            value = Regex.Replace(value, @"\\U([0-9a-fA-F]{8})", match => char.ConvertFromUtf32(Convert.ToInt32(match.Groups[1].Value, 16)));
            return JsonSerializer.Deserialize<string>("\"" + value + "\"") ?? "";
        }
        catch { throw new InvalidOperationException("The quoted Codex configuration cannot be verified safely. No login was changed."); }
    }
}
