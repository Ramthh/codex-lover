using CodexAccountMonitor.Core.Models;
using CodexAccountMonitor.Infrastructure;

namespace CodexAccountMonitor.Tests;

public sealed class DesktopModelSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CodexAccountMonitorTests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("Luna")]
    [InlineData("Sol")]
    [InlineData("Terra")]
    [InlineData("Astra")]
    public async Task SelectionSurvivesRestartAndUnrelatedChangesWithoutMovingOrSwitchingAccounts(string name)
    {
        var paths = new AppDataPaths(_root); var repository = new SettingsRepository(paths); var profile = Guid.NewGuid();
        await repository.SaveAsync(new AppSettings { ActiveProfileId = profile, ModelLeft = -1600, ModelTop = 280,
            ModelVisible = false, StartWithWindows = true, RefreshIntervalMinutes = 7 });
        await repository.UpdateAsync(s => s with { ModelCharacter = name });
        await repository.UpdateAsync(s => s with { WidgetOpacityPercent = 60 });
        var actual = await new SettingsRepository(paths).LoadAsync();
        Assert.Equal(name, actual.ModelCharacter); Assert.Equal(profile, actual.ActiveProfileId);
        Assert.Equal(-1600, actual.ModelLeft); Assert.Equal(280, actual.ModelTop); Assert.False(actual.ModelVisible);
        Assert.True(actual.StartWithWindows); Assert.Equal(7, actual.RefreshIntervalMinutes); Assert.Equal(60, actual.WidgetOpacityPercent);
        Assert.Equal(name, ModelUsageSummary.From(null, true, modelName: name).Title);
        Assert.Equal(name, ModelUsageSummary.From(null, false, modelName: name).Title);
    }

    [Theory]
    [InlineData("{}", "Sol")]
    [InlineData("{\"modelCharacter\":null}", "Sol")]
    [InlineData("{\"modelCharacter\":\"missing\"}", "Sol")]
    [InlineData("{\"modelCharacter\":\"99\"}", "Sol")]
    [InlineData("{\"modelCharacter\":\"luna\"}", "Luna")]
    [InlineData("{\"modelCharacter\":\"terra\"}", "Terra")]
    [InlineData("{\"modelCharacter\":\"astra\"}", "Astra")]
    public async Task LegacyOrDamagedSettingsLoadSafelyWithoutRewritingFile(string json, string expected)
    {
        var paths = new AppDataPaths(_root); paths.EnsureBaseDirectories();
        await File.WriteAllTextAsync(paths.SettingsFile, json);
        Assert.Equal(expected, (await new SettingsRepository(paths).LoadAsync()).ModelCharacter);
        Assert.Equal(json, await File.ReadAllTextAsync(paths.SettingsFile));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
