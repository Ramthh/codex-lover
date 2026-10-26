using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CodexAccountMonitor.Infrastructure;

public static class StoreDistribution
{
    public const string FamilyName = "RamiforIT.CodexLover_s9a7ne5wq60b6";
    public const string StartupTaskId = "CodexLoverStartup";
    public const string ProductUri = "ms-windows-store://pdp/?productid=9P0RCWKXRJHB";

    // An unpackaged child of another packaged app can inherit package identity.
    // Only our exact Store family is allowed to use our startup registration.
    public static bool IsStorePackage => IsOwnFamily(ReadFamilyName());
    public static bool IsOwnFamily(string? family) => string.Equals(family, FamilyName, StringComparison.Ordinal);

    public static string? ReadFamilyName()
    {
        uint length = 0;
        if (GetCurrentPackageFamilyName(ref length, null) != 122 || length > 1024) return null;
        var value = new StringBuilder((int)length);
        return GetCurrentPackageFamilyName(ref length, value) == 0 ? value.ToString() : null;
    }

    public static void OpenUpdates() => Process.Start(new ProcessStartInfo(ProductUri) { UseShellExecute = true });

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFamilyName(ref uint length, StringBuilder? familyName);
}
