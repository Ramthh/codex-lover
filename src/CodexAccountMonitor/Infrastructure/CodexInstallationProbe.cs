using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace CodexAccountMonitor.Infrastructure;

public sealed record CodexInstallationStatus(bool IsReady, string? ExecutablePath, string? Version, string Message);

public interface ICodexInstallationProbe
{
    Task<CodexInstallationStatus> InspectAsync(string? configuredPath, bool testAppServer, CancellationToken cancellationToken = default);
}

public sealed class CodexInstallationProbe : ICodexInstallationProbe
{
    private readonly Func<string?, string> _locate;
    private readonly Func<string, IReadOnlyList<string>, CancellationToken, Task<(int ExitCode, string Output)>> _run;

    public CodexInstallationProbe() : this(new CodexExecutableLocator().Locate, RunReadOnlyCommandAsync) { }

    internal CodexInstallationProbe(
        Func<string?, string> locate,
        Func<string, IReadOnlyList<string>, CancellationToken, Task<(int ExitCode, string Output)>> run)
    {
        _locate = locate;
        _run = run;
    }

    public async Task<CodexInstallationStatus> InspectAsync(string? configuredPath, bool testAppServer, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(configuredPath) && !File.Exists(configuredPath))
            {
                return new(false, null, null, "The selected file is missing. Browse to the official codex.exe, or clear the path to use automatic detection.");
            }

            var executable = await Task.Run(() => _locate(configuredPath), cancellationToken).ConfigureAwait(false);
            if (!testAppServer)
            {
                var version = FileVersionInfo.GetVersionInfo(executable).FileVersion;
                return new(true, executable, version, "Official signed Codex detected. Test connection to verify App Server support.");
            }

            var versionResult = await _run(executable, new[] { "--version" }, cancellationToken).ConfigureAwait(false);
            if (versionResult.ExitCode != 0)
            {
                return new(false, executable, null, "The Codex connection component could not start. Repair or update Codex Lover, then retry. If you selected a separate Codex CLI, repair that installation.");
            }

            var versionMatch = Regex.Match(versionResult.Output, @"\b\d+\.\d+\.\d+(?:[-+][A-Za-z0-9.-]+)?\b");
            var reportedVersion = versionMatch.Success ? versionMatch.Value : "Version not reported";
            var helpResult = await _run(executable, new[] { "app-server", "--help" }, cancellationToken).ConfigureAwait(false);
            if (helpResult.ExitCode != 0 || !helpResult.Output.Contains("app-server", StringComparison.OrdinalIgnoreCase))
            {
                return new(false, executable, reportedVersion, "This Codex build did not confirm connection support. Update Codex Lover or your selected Codex CLI, then retry before adding an account.");
            }

            return new(true, executable, reportedVersion, "Connection check passed. Codex and App Server are available. Account sign-in and network access are checked when you connect an account.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, null, null, "Codex did not respond within 10 seconds. Close any installation prompt, update Codex, and try again.");
        }
        catch (FileNotFoundException)
        {
            return new(false, null, null, "The Codex connection component was not found. Repair or reinstall Codex Lover, or install the official Codex CLI and Browse to its codex.exe.");
        }
        catch (InvalidOperationException)
        {
            return new(false, null, null, "The executable could not be verified as an official OpenAI build. Choose the signed codex.exe from the official installation.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return new(false, null, null, "Windows could not start the Codex connection component. Repair or reinstall Codex Lover. If you selected a separate file, use automatic detection or select the official standalone Codex CLI.");
        }
    }

    private static async Task<(int ExitCode, string Output)> RunReadOnlyCommandAsync(
        string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        // These two informational commands run with a new empty home; they never inspect enrolled credentials.
        var temporaryHome = Path.Combine(Path.GetTempPath(), "CodexAccountMonitor", "connection-check", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryHome);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                WorkingDirectory = temporaryHome
            };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            start.Environment["CODEX_HOME"] = temporaryHome;
            using var process = new Process { StartInfo = start };
            process.Start();
            process.StandardInput.Close();
            var stdout = ReadBoundedAsync(process.StandardOutput, timeout.Token);
            var stderr = ReadBoundedAsync(process.StandardError, timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                var output = await stdout.ConfigureAwait(false);
                await stderr.ConfigureAwait(false);
                return (process.ExitCode, output);
            }
            finally
            {
                if (!process.HasExited)
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { }
                }

                // Observe both readers even when timeout or cancellation interrupted the process wait.
                try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (IOException) { }
            }
        }
        finally
        {
            try { Directory.Delete(temporaryHome, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var buffer = new char[1024];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) != 0)
        {
            var retained = Math.Min(count, 4096 - output.Length);
            if (retained > 0) output.Append(buffer, 0, retained);
        }

        return output.ToString();
    }
}
