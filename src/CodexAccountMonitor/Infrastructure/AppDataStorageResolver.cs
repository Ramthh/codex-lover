using System.Security.Cryptography;
using System.Text.Json;

namespace CodexAccountMonitor.Infrastructure;

/// <summary>
/// Keeps application state outside a launcher's MSIX AppData virtualization.
/// The first migration copies the old effective view; historical stores are never changed.
/// The caller must own the application single-instance mutex while preparing storage.
/// </summary>
internal static class AppDataStorageResolver
{
    private const string ProductDirectory = "CodexAccountMonitor";
    internal const string MarkerName = "storage-migration.json";
    private static readonly string[] StateDirectories = ["accounts", "membership", "desktop-switch", "logs"];
    private static readonly string[] StateFileNames =
        ["accounts.json", "settings.json", "reset-operations.json", "pending-account-deletions.json"];

    public static string EnsureDefaultRoot()
    {
        try { return EnsureDefaultRootCore(); }
        catch (AppDataStorageException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or InvalidOperationException or System.Security.SecurityException)
        {
            throw new AppDataStorageException("Codex Lover could not preserve its previous data safely. Close earlier copies of the app and try again. Original accounts and membership are unchanged.", exception);
        }
    }

    private static string EnsureDefaultRootCore()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var stableRoot = StableLocalRoot(profile);
        var stableProduct = Path.Combine(stableRoot, ProductDirectory);
        if (Directory.Exists(stableProduct) || File.Exists(stableProduct))
        {
            var marker = ReadMarker(stableProduct);
            _ = ResolveDesktopHome(marker, Environment.GetEnvironmentVariable("CODEX_HOME"));
            return stableRoot;
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var sources = new List<string> { Path.Combine(local, ProductDirectory) };
        // An independent Explorer launch sees the normal store without the package overlay.
        // Include the same product's existing Codex package overlay so either entry point
        // preserves the effective state used by the old packaged-launcher process.
        var packages = Path.Combine(local, "Packages");
        if (Directory.Exists(packages))
        {
            EnsureSafePath(packages);
            var overlays = Directory.EnumerateDirectories(packages, "OpenAI.Codex_*")
                .Select(package => Path.Combine(package, "LocalCache", "Local", ProductDirectory))
                .Where(Directory.Exists).ToArray();
            if (overlays.Length > 1)
                throw new InvalidOperationException("More than one previous Codex Lover data store was found. No accounts were changed. Contact support to select the correct store.");
            sources.AddRange(overlays);
        }
        return EnsureStorage(profile, sources, RawDesktopHome());
    }

    public static string ResolveDefaultRoot()
    {
        var localRoot = StableLocalRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var root = Path.Combine(localRoot, ProductDirectory);
        if (Directory.Exists(root) || File.Exists(root))
        {
            _ = ReadMarker(root);
            return localRoot;
        }
        // Read-only diagnostics may run before the first normal startup. They must not
        // perform migration or create a second empty store merely by resolving paths.
        return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    }

    public static string ResolveDesktopHome()
    {
        var root = Path.Combine(StableLocalRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), ProductDirectory);
        if (!Directory.Exists(root)) return RawDesktopHome();
        return ResolveDesktopHome(ReadMarker(root), Environment.GetEnvironmentVariable("CODEX_HOME"));
    }

    internal static string ResolveDesktopHome(StorageMarker marker, string? configuredHome)
    {
        if (!string.IsNullOrWhiteSpace(configuredHome)
            && !string.Equals(Path.GetFullPath(configuredHome), marker.DesktopHome, StringComparison.OrdinalIgnoreCase))
            throw new AppDataStorageException("Codex Lover was started with a different CODEX_HOME than its saved switch history. No login was changed. Start it without that override or contact support to move the saved switch history safely.");
        return marker.DesktopHome;
    }

    internal static string EnsureStorage(string userProfile, IReadOnlyList<string> sourceRoots, string desktopHome,
        Action? beforeVerification = null)
    {
        var localRoot = StableLocalRoot(userProfile);
        var target = Path.Combine(localRoot, ProductDirectory);
        EnsureSafePath(target);
        if (Directory.Exists(target) || File.Exists(target))
        {
            // The broker intentionally drops its launcher's environment. Preserve the
            // recorded home; ResolveDesktopHome rejects a later explicit conflict.
            _ = ReadMarker(target);
            return localRoot;
        }
        var home = Path.GetFullPath(desktopHome);
        EnsureLocalAbsolutePath(home);
        var sources = sourceRoots.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var source in sources)
        {
            EnsureSafePath(source);
            if (IsWithin(source, target) || IsWithin(target, source))
                throw new InvalidOperationException("The previous and new data locations must be separate. No accounts were changed.");
        }
        Directory.CreateDirectory(localRoot);
        EnsureSafePath(localRoot);
        AccountDirectoryService.RestrictDirectoryToCurrentUser(localRoot);
        var staging = Path.Combine(localRoot, ".migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        AccountDirectoryService.RestrictTreeToCurrentUser(staging);
        try
        {
            var inventory = EnumerateEffectiveState(sources);
            foreach (var entry in inventory)
            {
                var destination = Path.Combine(staging, entry.Key);
                EnsureSafePath(entry.Value.Path);
                EnsureSafePath(destination);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var input = new FileStream(entry.Value.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    81920, FileOptions.WriteThrough);
                input.CopyTo(output);
                output.Flush(true);
            }
            AccountDirectoryService.RestrictTreeToCurrentUser(staging);
            beforeVerification?.Invoke();
            var after = EnumerateEffectiveState(sources);
            if (inventory.Count != after.Count || inventory.Any(pair => !after.TryGetValue(pair.Key, out var current)
                || pair.Value.Hash != current.Hash || !string.Equals(pair.Value.Path, current.Path, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("Previous application data changed during migration. Close the earlier app and try again; the original accounts are preserved.");
            foreach (var entry in inventory)
                if (HashFile(Path.Combine(staging, entry.Key)) != entry.Value.Hash)
                    throw new IOException("The preserved application data could not be verified. Original accounts are unchanged.");

            var marker = new StorageMarker(1, target, sources, home, DateTimeOffset.UtcNow);
            var markerPath = Path.Combine(staging, MarkerName);
            using (var stream = new FileStream(markerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, marker);
                stream.Flush(true);
            }
            EnsureSafePath(target);
            EnsureSafePath(staging);
            // The completion marker is inside the same directory rename: no process can
            // observe a published destination with only half its account/session files.
            Directory.Move(staging, target);
            _ = ReadMarker(target);
            return localRoot;
        }
        catch
        {
            // Keep the private staging copy for recovery; never delete an original store,
            // and never choose this incomplete directory on a later launch.
            throw;
        }
    }

    private static SortedDictionary<string, SourceFile> EnumerateEffectiveState(IEnumerable<string> sources)
    {
        var result = new SortedDictionary<string, SourceFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in sources)
        {
            EnsureSafePath(root);
            var rootAttributes = TryGetAttributes(root);
            if (rootAttributes is null) continue;
            if ((rootAttributes.Value & FileAttributes.Directory) == 0)
                throw new IOException("A previous application data location is not a folder. Original accounts are unchanged.");
            foreach (var path in Directory.EnumerateFiles(root))
            {
                var name = Path.GetFileName(path);
                if (StateFileNames.Any(state => name == state || name == state + ".bak"
                    || name.StartsWith(state + ".damaged-", StringComparison.Ordinal)))
                    Add(path);
            }
            foreach (var name in StateDirectories)
            {
                var directory = Path.Combine(root, name);
                EnsureSafePath(directory);
                var directoryAttributes = TryGetAttributes(directory);
                if (directoryAttributes is null) continue;
                if ((directoryAttributes.Value & FileAttributes.Directory) == 0)
                    throw new IOException("A previous application data folder cannot be verified. Original accounts are unchanged.");
                var pending = new Stack<string>();
                pending.Push(directory);
                while (pending.Count > 0)
                {
                    var parent = pending.Pop();
                    EnsureSafePath(parent);
                    foreach (var entry in Directory.EnumerateFileSystemEntries(parent))
                    {
                        EnsureSafePath(entry);
                        if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0) pending.Push(entry);
                        else Add(entry);
                    }
                }
            }
            void Add(string file)
            {
                EnsureSafePath(file);
                var relative = Path.GetRelativePath(root, file);
                if (Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal))
                    throw new InvalidOperationException("Application data escaped its expected location. No accounts were changed.");
                result[relative] = new SourceFile(file, HashFile(file));
            }
        }
        return result;
    }

    private static string HashFile(string path)
    {
        EnsureSafePath(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    internal static StorageMarker ReadMarker(string root)
    {
        EnsureSafePath(root);
        var path = Path.Combine(root, MarkerName);
        EnsureSafePath(path);
        if (!File.Exists(path) || new FileInfo(path).Length > 16384)
            throw IncompleteStorage();
        try
        {
            var marker = JsonSerializer.Deserialize<StorageMarker>(File.ReadAllBytes(path));
            if (marker is null || marker.Version != 1 || marker.CompletedUtc == default
                || !string.Equals(marker.Root, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)
                || marker.SourceRoots is null || marker.SourceRoots.Length > 2
                || string.IsNullOrWhiteSpace(marker.DesktopHome))
                throw IncompleteStorage();
            EnsureLocalAbsolutePath(marker.DesktopHome);
            return marker;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or NotSupportedException)
        {
            throw IncompleteStorage();
        }
    }

    private static AppDataStorageException IncompleteStorage() => new(
        "Codex Lover's preserved data store is incomplete or cannot be verified. Your original account folders are unchanged. Contact support before continuing.");

    private static string StableLocalRoot(string userProfile)
    {
        EnsureLocalAbsolutePath(userProfile);
        return Path.GetFullPath(Path.Combine(userProfile, ".codex-lover", "appdata"));
    }

    private static string RawDesktopHome() => Path.GetFullPath(
        Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } configured
            ? configured : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"));

    private static void EnsureLocalAbsolutePath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidOperationException("Codex Lover data must use an absolute local Windows folder.");
    }

    private static void EnsureSafePath(string path)
    {
        EnsureLocalAbsolutePath(path);
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if (TryGetAttributes(current) is { } attributes && (attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Codex Lover cannot preserve account data through a linked folder. Original accounts are unchanged.");
    }

    private static FileAttributes? TryGetAttributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static bool IsWithin(string candidate, string parent) =>
        string.Equals(candidate, parent, StringComparison.OrdinalIgnoreCase)
        || candidate.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private sealed record SourceFile(string Path, string Hash);
    internal sealed record StorageMarker(int Version, string Root, string[] SourceRoots, string DesktopHome, DateTimeOffset CompletedUtc);
}

internal sealed class AppDataStorageException : IOException
{
    public AppDataStorageException(string message, Exception? innerException = null) : base(message, innerException) { }
}
