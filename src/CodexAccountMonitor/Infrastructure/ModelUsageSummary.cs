using CodexAccountMonitor.ViewModels;

namespace CodexAccountMonitor.Infrastructure;

public sealed record ModelBubbleContent(string Title, string Message, bool IsWarning);

/// <summary>Formats already displayed account data; never reads credentials or refreshes an account.</summary>
public static class ModelUsageSummary
{
    public static ModelBubbleContent From(AccountCardViewModel? selected, bool membershipAvailable, bool isDemo = false, string modelName = "Sol")
    {
        if (!membershipAvailable && !isDemo)
            return new(modelName, "Sign in to Codex Lover to see account usage.", true);

        if (selected is null || !selected.IsConfigured)
            return new(isDemo ? $"{modelName} · Preview" : modelName, "No active Codex account selected.", false);

        var alias = BoundAlias(selected.Alias);
        var title = isDemo ? $"{alias} · Preview" : alias;
        var lines = new List<string>();
        if (isDemo) lines.Add("Sample usage");

        var unavailable = !selected.HasFiveHour && !selected.HasWeekly;
        var warning = selected.NeedsReauthentication || selected.HasError || selected.HasRefreshError || selected.IsStale;
        if (warning)
        {
            lines.Add(selected.NeedsReauthentication ? "Sign in again to update usage."
                : selected.IsStale ? "Usage is out of date."
                : "Latest usage refresh failed.");
            if (!unavailable) lines.Add("Last known remaining usage:");
        }
        else if (unavailable) lines.Add("Usage is not available yet.");

        lines.Add($"{selected.FiveHourLabel}: {(selected.HasFiveHour ? selected.FiveHourDisplay : "Not reported")}");
        lines.Add($"{selected.LongTermLabel}: {(selected.HasWeekly ? selected.WeeklyDisplay : "Not reported")}");
        return new(title, string.Join("\n", lines), warning || unavailable);
    }

    private static string BoundAlias(string alias)
    {
        // Account names can be user-entered; keep the small bubble single-line and bounded.
        var singleLine = string.Join(" ", alias.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (singleLine.Length == 0) return "Active account";
        const int maximum = 36;
        if (singleLine.Length <= maximum) return singleLine;
        var length = maximum - 1;
        if (char.IsHighSurrogate(singleLine[length - 1])) length--;
        return singleLine[..length] + "…";
    }
}
