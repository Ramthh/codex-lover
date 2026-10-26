using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using CodexAccountMonitor.FakeAppServer;
using CodexAccountMonitor.Infrastructure;

namespace CodexAccountMonitor.Tests;

public sealed class MonitorStartupEnvironmentProcessTests
{
    [Fact]
    public async Task ProductionStartupClearsInheritedCredentialsOnlyInsideItsProcess()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = FakeServerExecutable(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("--probe-saved-account-environment");
        startInfo.ArgumentList.Add(typeof(MonitorStartupBootstrap).Assembly.Location);
        // Construct a synthetic environment; never launch with live inherited keys,
        // workload selectors, account homes, or managed policy from the test runner.
        startInfo.Environment.Clear();
        startInfo.Environment["SystemRoot"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        startInfo.Environment["TEMP"] = Path.GetTempPath();
        startInfo.Environment["TMP"] = Path.GetTempPath();
        var runtimeRoot = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory()).Parent?.Parent?.Parent?.FullName;
        if (runtimeRoot is not null && File.Exists(Path.Combine(runtimeRoot, "dotnet.exe")))
        {
            startInfo.Environment["DOTNET_ROOT"] = runtimeRoot;
            startInfo.Environment["DOTNET_ROOT_" + RuntimeInformation.ProcessArchitecture.ToString().ToUpperInvariant()] = runtimeRoot;
        }
        string[] credentialNames = ["OPENAI_API_KEY", "CODEX_API_KEY", "CODEX_ACCESS_TOKEN"];
        foreach (var name in credentialNames)
            startInfo.Environment[name] = "fixture-inherited-credential";
        startInfo.Environment["CODEX_MANAGED_CONFIG_PATH"] = "fixture-managed-config";
        startInfo.Environment["HTTPS_PROXY"] = "http://fixture.invalid:8080";
        startInfo.Environment["OPENAI_WORKLOAD_IDENTITY_CONTEXT"] = "fixture-audit-context";

        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, process.ExitCode);
            Assert.Empty(await error);
            using var report = JsonDocument.Parse(await output);
            var flags = report.RootElement.EnumerateObject().ToArray();
            Assert.Equal(5, flags.Length);
            Assert.All(flags, flag => Assert.True(flag.Value.GetBoolean(), flag.Name));
            foreach (var name in credentialNames)
                Assert.Equal("fixture-inherited-credential", startInfo.Environment[name]);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    private static string FakeServerExecutable()
    {
        var assemblyDirectory = Path.GetDirectoryName(typeof(FakeAppServerMarker).Assembly.Location)!;
        var adjacent = Path.Combine(assemblyDirectory, "CodexAccountMonitor.FakeAppServer.exe");
        if (File.Exists(adjacent)) return adjacent;
        var artifactsCandidate = Path.GetFullPath(Path.Combine(assemblyDirectory, "..", "..",
            "CodexAccountMonitor.FakeAppServer", new DirectoryInfo(assemblyDirectory).Name,
            "CodexAccountMonitor.FakeAppServer.exe"));
        if (File.Exists(artifactsCandidate)) return artifactsCandidate;
        var configuration = assemblyDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
        var repository = Path.GetFullPath(Path.Combine(assemblyDirectory, "..", "..", "..", "..", ".."));
        return Path.Combine(repository, "tests", "CodexAccountMonitor.FakeAppServer", "bin", configuration,
            "net10.0-windows", "CodexAccountMonitor.FakeAppServer.exe");
    }
}
