using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace CodexAccountMonitor.Infrastructure.DesktopSwitching;

public sealed class WindowsDesktopInstallationSource : IDesktopInstallationSource
{
    private const string PackageFamilyName = "OpenAI.Codex_2p2nqsd0c76g0";

    public IReadOnlyList<string> GetCandidateExecutables(IReadOnlyList<DesktopProcessInfo> processes)
    {
        var candidates = new List<string>();
        // Package APIs discover the current user's registered version without enumerating
        // protected WindowsApps directories or hardcoding a release/architecture.
        foreach (var packagePath in GetRegisteredPackagePaths())
        {
            candidates.Add(Path.Combine(packagePath, "app", "ChatGPT.exe"));
            candidates.Add(Path.Combine(packagePath, "app", "Codex.exe"));
        }

        foreach (var process in processes)
        {
            if (process.ExecutablePath is not null &&
                (process.Name.Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase) ||
                 process.Name.Equals("Codex.exe", StringComparison.OrdinalIgnoreCase)))
            {
                candidates.Add(process.ExecutablePath);
            }
        }

        // These allow a future unpackaged official desktop build to be discovered. Every
        // candidate is still checked for a desktop layout and an OpenAI Authenticode signature.
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (var root in new[]
        {
            Path.Combine(localData, "Programs", "Codex"),
            Path.Combine(localData, "OpenAI", "Codex"),
            Path.Combine(programFiles, "Codex"),
            Path.Combine(programFiles, "OpenAI", "Codex")
        })
        {
            candidates.Add(Path.Combine(root, "Codex.exe"));
            candidates.Add(Path.Combine(root, "ChatGPT.exe"));
        }
        return candidates;
    }

    internal static IReadOnlyList<string> GetRegisteredPackagePaths()
    {
        uint count = 0;
        uint bufferLength = 0;
        var result = GetPackagesByPackageFamily(PackageFamilyName, ref count, IntPtr.Zero, ref bufferLength, IntPtr.Zero);
        if (result == 0 && count == 0)
        {
            return Array.Empty<string>();
        }
        if (result != 122) // ERROR_INSUFFICIENT_BUFFER
        {
            throw new Win32Exception(result);
        }

        var names = Marshal.AllocHGlobal(checked((int)count * IntPtr.Size));
        var buffer = Marshal.AllocHGlobal(checked((int)bufferLength * sizeof(char)));
        try
        {
            result = GetPackagesByPackageFamily(PackageFamilyName, ref count, names, ref bufferLength, buffer);
            if (result != 0)
            {
                throw new Win32Exception(result);
            }

            var paths = new List<string>();
            for (var index = 0; index < count; index++)
            {
                var fullName = Marshal.PtrToStringUni(Marshal.ReadIntPtr(names, index * IntPtr.Size));
                if (string.IsNullOrEmpty(fullName))
                {
                    throw new InvalidOperationException("Windows returned an empty Codex package identity.");
                }

                uint pathLength = 0;
                result = GetPackagePathByFullName(fullName, ref pathLength, null);
                if (result != 122)
                {
                    throw new Win32Exception(result);
                }
                var packagePath = new StringBuilder(checked((int)pathLength));
                result = GetPackagePathByFullName(fullName, ref pathLength, packagePath);
                if (result != 0)
                {
                    throw new Win32Exception(result);
                }
                paths.Add(packagePath.ToString());
            }
            return paths;
        }
        finally
        {
            Marshal.FreeHGlobal(names);
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetPackagesByPackageFamily(string packageFamilyName,
        ref uint count, IntPtr packageFullNames, ref uint bufferLength, IntPtr buffer);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetPackagePathByFullName(string packageFullName,
        ref uint pathLength, StringBuilder? path);
}
