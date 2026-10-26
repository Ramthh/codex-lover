using CodexAccountMonitor.Infrastructure;

namespace CodexAccountMonitor.Tests;

public sealed class StoreDistributionTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("OpenAI.Codex_2p2nqsd0c76g0", false)]
    [InlineData("RamiforIT.CodexLover_otherpublisher", false)]
    [InlineData("RamiforIT.CodexLover_s9a7ne5wq60b6", true)]
    public void OnlyOurExactPackageFamilyUsesStoreServices(string? family, bool expected)
        => Assert.Equal(expected, StoreDistribution.IsOwnFamily(family));
}
