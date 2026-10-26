using System.ComponentModel;
using CodexAccountMonitor.Infrastructure.DesktopSwitching;

namespace CodexAccountMonitor.Infrastructure;

/// <summary>Discovers CLI paths only. The locator verifies each file before accepting it.</summary>
internal sealed class CodexCliCandidateSource
{
    private readonly string _baseDirectory;
    private readonly string _path;
    private readonly string _localData;
    private readonly Func<IReadOnlyList<string>> _desktopExecutables;

    public CodexCliCandidateSource() : this(AppContext.BaseDirectory,
        Environment.GetEnvironmentVariable("PATH") ?? string.Empty,
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        () => new WindowsDesktopInstallationSource().GetCandidateExecutables(Array.Empty<DesktopProcessInfo>())) { }

    internal CodexCliCandidateSource(string baseDirectory, string path, string localData,
        Func<IReadOnlyList<string>> desktopExecutables)
    {
        _baseDirectory = baseDirectory;
        _path = path;
        _localData = localData;
        _desktopExecutables = desktopExecutables;
    }

    public IEnumerable<string> GetCandidates()
    {
        // Store builds include a pinned official runtime with its supporting files.
        // The locator still verifies this executable; packaging provenance alone is not trust.
        yield return Path.Combine(_baseDirectory, "codex-runtime", "bin", "codex.exe");
        var desktopHelpers = new List<string>();
        var adjacent = GetExecutableAtDirectory(_baseDirectory);
        if (IsDesktopLauncher(adjacent)) desktopHelpers.Add(GetDesktopHelper(adjacent));
        else yield return adjacent;

        foreach (var segment in _path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate;
            try { candidate = GetExecutableAtDirectory(segment.Trim().Trim('"')); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
            {
                continue; // A malformed PATH entry must not prevent further discovery.
            }
            if (IsDesktopLauncher(candidate)) desktopHelpers.Add(GetDesktopHelper(candidate));
            else if (IsDesktopHelper(candidate)) desktopHelpers.Add(candidate);
            else yield return candidate;
        }

        // Official standalone installs are usable by other processes. Windows can deny
        // execution of a correctly signed helper inside another app's protected package.
        // Prefer standalone CLI locations before desktop helpers, even when a desktop
        // launcher or its resources directory was placed on PATH.
        foreach (var bin in new[]
        {
            Path.Combine(_localData, "Programs", "OpenAI", "Codex", "bin"),
            Path.Combine(_localData, "OpenAI", "Codex", "bin")
        })
            foreach (var candidate in GetStandaloneCandidates(bin)) yield return candidate;

        foreach (var helper in desktopHelpers) yield return helper;

        // Reuse Windows package registration discovery, including non-default package
        // drives and future versions/architectures, plus the supported unpackaged roots.
        // Do not scan protected WindowsApps folders or launch the desktop to discover it.
        IReadOnlyList<string> desktops;
        try { desktops = _desktopExecutables(); }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            desktops = Array.Empty<string>();
        }
        foreach (var desktop in desktops) yield return GetDesktopHelper(desktop);
    }

    private static IReadOnlyList<string> GetStandaloneCandidates(string codexBin)
    {
        try
        {
            return Directory.Exists(codexBin)
                ? Directory.EnumerateFiles(codexBin, "codex.exe", new EnumerationOptions
                    { RecurseSubdirectories = true, IgnoreInaccessible = true })
                    .OrderByDescending(File.GetLastWriteTimeUtc).ToArray()
                : Array.Empty<string>();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static string GetExecutableAtDirectory(string directory) =>
        Path.GetFullPath(Path.Combine(directory, "codex.exe"));

    private static bool IsDesktopHelper(string executable) =>
        string.Equals(Path.GetFileName(Path.GetDirectoryName(executable)), "resources", StringComparison.OrdinalIgnoreCase) &&
        (File.Exists(Path.Combine(Path.GetDirectoryName(executable)!, "..", "Codex.exe")) ||
         File.Exists(Path.Combine(Path.GetDirectoryName(executable)!, "..", "ChatGPT.exe")));

    internal static string GetDesktopHelper(string desktopExecutable) =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(desktopExecutable)!, "resources", "codex.exe"));

    internal static bool IsDesktopLauncher(string executable)
    {
        var name = Path.GetFileName(executable);
        if (!name.Equals("Codex.exe", StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase)) return false;
        var directory = Path.GetDirectoryName(executable)!;
        return File.Exists(Path.Combine(directory, "resources", "app.asar")) ||
            File.Exists(Path.Combine(directory, "resources", "codex.exe")) ||
            File.Exists(Path.Combine(directory, "chrome.dll")) ||
            File.Exists(Path.Combine(directory, "chrome_elf.dll"));
    }
}
