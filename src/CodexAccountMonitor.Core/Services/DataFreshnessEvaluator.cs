using CodexAccountMonitor.Core.Models;

namespace CodexAccountMonitor.Core.Services;

public enum DataFreshness
{
    Never,
    Fresh,
    Stale
}

public static class DataFreshnessEvaluator
{
    public static DataFreshness Evaluate(
        AccountUsageState state,
        DateTimeOffset nowUtc,
        TimeSpan staleAfter)
    {
        if (state.LastSuccessfulRefreshUtc is null)
        {
            return DataFreshness.Never;
        }

        return nowUtc - state.LastSuccessfulRefreshUtc.Value > staleAfter
            ? DataFreshness.Stale
            : DataFreshness.Fresh;
    }
}
