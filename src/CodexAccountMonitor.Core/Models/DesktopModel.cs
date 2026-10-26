namespace CodexAccountMonitor.Core.Models;

public enum DesktopModel { Sol, Luna, Terra, Astra }

public static class DesktopModels
{
    public static DesktopModel Parse(string? name) =>
        Enum.TryParse<DesktopModel>(name, true, out var model) && Enum.IsDefined(model)
            ? model : DesktopModel.Sol;

    public static string Name(DesktopModel model) => model switch
    {
        DesktopModel.Luna => "Luna",
        DesktopModel.Terra => "Terra",
        DesktopModel.Astra => "Astra",
        _ => "Sol"
    };
}
