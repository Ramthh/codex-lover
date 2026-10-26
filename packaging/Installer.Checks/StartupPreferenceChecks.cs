internal static class StartupPreferenceChecks
{
    internal static void Run(Action<string, Action> check, Func<string, object[], object?> call)
    {
        var cases = new[]
        {
            (Name: "a fresh installation enables Windows startup by default", Enable: false, Disable: false, Installed: false, Previous: false, Expected: true),
            (Name: "a fresh installation honors an explicit startup opt-out", Enable: false, Disable: true, Installed: false, Previous: false, Expected: false),
            (Name: "an upgrade preserves an existing disabled startup preference", Enable: false, Disable: false, Installed: true, Previous: false, Expected: false),
            (Name: "an upgrade preserves an existing enabled startup preference", Enable: false, Disable: false, Installed: true, Previous: true, Expected: true),
            (Name: "explicit startup enable overrides a previous disabled preference", Enable: true, Disable: false, Installed: true, Previous: false, Expected: true),
            (Name: "explicit startup disable overrides a previous enabled preference", Enable: false, Disable: true, Installed: true, Previous: true, Expected: false)
        };
        foreach (var item in cases)
        {
            check(item.Name, () =>
            {
                var actual = call("ResolveStartupPreference", [item.Enable, item.Disable, item.Installed, item.Previous]);
                if (actual is not bool enabled || enabled != item.Expected)
                    throw new Exception("Unexpected installer startup preference: " + item.Name);
            });
        }
    }
}
