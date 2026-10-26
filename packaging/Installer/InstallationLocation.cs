using System.Text.Json;

namespace CodexAccountMonitor.Setup;

internal static class InstallationLocation
{
    internal const string MarkerName = ".codex-lover-installation";
    private const string MarkerValue = "CodexLover installation root v1";

    internal static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)
            || value.StartsWith(@"\\", StringComparison.Ordinal) || value.IndexOfAny(['*', '?', '"', '\0']) >= 0)
            throw new InvalidDataException("Choose a full folder path on a local drive, such as D:\\Apps\\CodexLover.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        if (root.Length > 180 || string.Equals(root, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(root)!), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose a dedicated app folder, rather than a drive root, with a shorter path.");
        if (root.AsSpan(2).Contains(':')) throw new InvalidDataException("The installation folder path is invalid.");
        var drive = new DriveInfo(Path.GetPathRoot(root)!);
        if (drive.DriveType is DriveType.Network or DriveType.NoRootDirectory or DriveType.CDRom or DriveType.Unknown)
            throw new InvalidDataException("Choose an available local drive for Codex Lover.");
        var protectedRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex-lover")
        };
        if (protectedRoots.Any(path => !string.IsNullOrWhiteSpace(path) && IsWithin(root, path)))
            throw new InvalidDataException("Choose an app folder outside Windows and your saved account data.");
        for (var ancestor = new DirectoryInfo(root); ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ancestor.Exists && (ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Choose a local folder without redirected folders or symbolic links.");
        }
        if (File.Exists(root)) throw new InvalidDataException("That location is a file. Choose a folder for Codex Lover.");
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any() && !IsOwnedRoot(root))
            throw new InvalidDataException("Choose an empty folder or an existing Codex Lover installation folder. Other files will be preserved.");
        return root;
    }

    internal static string? ValidationMessage(string value)
    {
        try { _ = Normalize(value); return null; }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return error.Message; }
    }

    internal static void Prepare(string value)
    {
        var root = Normalize(value);
        Directory.CreateDirectory(root);
        // Recheck after creation, including its ancestors, before any package write.
        _ = Normalize(root);
        var probe = Path.Combine(root, ".setup-write-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var file = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                1, FileOptions.DeleteOnClose);
            file.WriteByte(0);
        }
        catch (UnauthorizedAccessException)
        { throw new UnauthorizedAccessException("Codex Lover cannot write to that folder. Choose a folder your Windows account can use."); }
        var marker = Path.Combine(root, MarkerName);
        if (!File.Exists(marker))
        {
            using var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream);
            writer.Write(MarkerValue);
        }
    }

    internal static bool IsWithin(string path, string parent)
    {
        parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        return path.Equals(parent, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOwnedRoot(string root)
    {
        var marker = Path.Combine(root, MarkerName);
        if (File.Exists(marker))
            return new FileInfo(marker).Length < 100 && File.ReadAllText(marker) == MarkerValue;
        // Existing releases predate the ownership marker. Recognize their installer receipt,
        // including retained release directories after an uninstall or interrupted upgrade.
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
            var identityPath = Path.Combine(directory, "installation.json");
            if (!File.Exists(identityPath) || new FileInfo(identityPath).Length > 16_384) continue;
            try
            {
                using var identity = JsonDocument.Parse(File.ReadAllBytes(identityPath));
                if (identity.RootElement.TryGetProperty("executable", out var executable)
                    && executable.GetString() is string path
                    && path.Equals(Path.Combine(directory, "CodexAccountMonitor.exe"), StringComparison.OrdinalIgnoreCase)
                    && File.Exists(Path.Combine(directory, "publish-manifest.json"))) return true;
            }
            catch (Exception error) when (error is JsonException or IOException or InvalidOperationException) { }
        }
        return false;
    }
}
