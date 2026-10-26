using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Installer.Checks")]

namespace CodexAccountMonitor.Setup;

// Windows operations are supplied by setup; checks use an isolated in-memory registry.
internal interface IInstallationRegistration
{
    object CaptureStartup();
    object? CaptureUninstall();
    void SetStartup(string executable, bool enabled);
    void RestoreStartup(object snapshot);
    void RegisterUninstall(string executable, string uninstaller, string version);
    void RestoreUninstall(object? snapshot);
    void RemoveUninstall();
    void CreateShortcut(string executable, string path);
}

internal sealed class InstallationActivation(string installRoot, string programs, IInstallationRegistration registration)
{
    private string Shortcut => Path.Combine(programs, "Codex Lover.lnk");
    private string LegacyShortcut => Path.Combine(programs, "Codex Account Monitor.lnk");
    private string Pointer => Path.Combine(installRoot, "current.json");

    internal void Activate(string executable, bool startup, byte[] receipt, string uninstaller, string version)
    {
        Directory.CreateDirectory(programs);
        var suffix = Guid.NewGuid().ToString("N");
        var stagedShortcut = Path.Combine(programs, "Codex Lover." + suffix + ".lnk");
        var stagedPointer = Path.Combine(installRoot, ".current-" + suffix + ".tmp");
        var oldShortcut = ReadOptional(Shortcut);
        var oldPointer = ReadOptional(Pointer);
        var oldStartup = registration.CaptureStartup();
        var oldUninstall = registration.CaptureUninstall();
        var shortcutChanged = false;
        var startupAttempted = false;
        var pointerChanged = false;
        try
        {
            registration.CreateShortcut(executable, stagedShortcut);
            WriteDurable(stagedPointer, receipt);
            if (oldPointer is not null) SaveHistory(oldPointer, suffix);
            File.Move(stagedShortcut, Shortcut, overwrite: true);
            shortcutChanged = true;
            startupAttempted = true;
            registration.SetStartup(executable, startup);
            File.Move(stagedPointer, Pointer, overwrite: true);
            pointerChanged = true;
            registration.RegisterUninstall(executable, uninstaller, version);
            // Retire the old display name only after its replacement is active.
            File.Delete(LegacyShortcut);
        }
        catch (Exception activationError)
        {
            var rollbackErrors = new List<Exception>();
            TryRollback(() => registration.RestoreUninstall(oldUninstall), rollbackErrors);
            if (pointerChanged) TryRollback(() => RestoreFile(Pointer, oldPointer), rollbackErrors);
            if (startupAttempted) TryRollback(() => registration.RestoreStartup(oldStartup), rollbackErrors);
            if (shortcutChanged) TryRollback(() => RestoreFile(Shortcut, oldShortcut), rollbackErrors);
            if (rollbackErrors.Count > 0)
            {
                rollbackErrors.Insert(0, activationError);
                throw new AggregateException("Installation could not be activated, and Windows prevented a complete rollback. Check the Start shortcut and Windows startup entry before retrying.", rollbackErrors);
            }
            throw new IOException("Installation could not be activated. The previous shortcut, startup choice, and active installation were restored.", activationError);
        }
        finally
        {
            TryDeleteStage(stagedShortcut);
            TryDeleteStage(stagedPointer);
        }
    }

    internal void Uninstall()
    {
        var previousShortcut = ReadOptional(Shortcut);
        var previousLegacyShortcut = ReadOptional(LegacyShortcut);
        var previousPointer = ReadOptional(Pointer);
        var previousStartup = registration.CaptureStartup();
        var previousUninstall = registration.CaptureUninstall();
        if (previousPointer is not null) SaveHistory(previousPointer, "uninstalled-" + Guid.NewGuid().ToString("N"));
        try
        {
            registration.SetStartup(string.Empty, false);
            File.Delete(Shortcut);
            File.Delete(LegacyShortcut);
            File.Delete(Pointer);
            registration.RemoveUninstall();
        }
        catch (Exception error)
        {
            var failures = new List<Exception> { error };
            TryRollback(() => registration.RestoreStartup(previousStartup), failures);
            TryRollback(() => RestoreFile(Shortcut, previousShortcut), failures);
            TryRollback(() => RestoreFile(LegacyShortcut, previousLegacyShortcut), failures);
            TryRollback(() => RestoreFile(Pointer, previousPointer), failures);
            TryRollback(() => registration.RestoreUninstall(previousUninstall), failures);
            throw new AggregateException("Uninstall could not complete. The previous registration was restored where Windows allowed it. Account data and release folders were preserved.", failures);
        }
    }

    private void SaveHistory(byte[] previous, string suffix)
    {
        var history = Path.Combine(installRoot, "activation-history");
        Directory.CreateDirectory(history);
        WriteDurable(Path.Combine(history, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + suffix + ".json"), previous);
    }

    private static byte[]? ReadOptional(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;
    private static void TryRollback(Action rollback, List<Exception> errors)
    {
        try { rollback(); }
        catch (Exception exception) { errors.Add(exception); }
    }

    private static void RestoreFile(string destination, byte[]? previous)
    {
        if (previous is null) { File.Delete(destination); return; }
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".restore";
        try
        {
            WriteDurable(temporary, previous);
            File.Move(temporary, destination, overwrite: true);
        }
        finally { TryDeleteStage(temporary); }
    }

    private static void WriteDurable(string path, byte[] bytes)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        file.Write(bytes);
        file.Flush(flushToDisk: true);
    }

    private static void TryDeleteStage(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
