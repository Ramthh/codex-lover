using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

var installer = Assembly.Load("CodexAccountMonitor.Setup").GetType("CodexAccountMonitor.Setup.Program", throwOnError: true)!;
object? Call(string name, params object[] args)
{
    try { return installer.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args); }
    catch (TargetInvocationException error) { throw error.InnerException!; }
}
var passed = 0;
void Check(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
void Reject(Action action)
{
    try { action(); } catch (InvalidDataException) { return; }
    throw new Exception("An unsafe or damaged package was accepted.");
}
var expectedRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "CodexLover");
Check("installation uses the current user's local Programs folder", () =>
{
    if ((string)installer.GetField("InstallRoot", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)! != expectedRoot) throw new Exception("Installation requires a developer drive.");
});
byte[] executable = [1, 2, 3, 4];
using var manifest = JsonDocument.Parse(JsonSerializer.Serialize(new { files = new[] { new { path = "CodexAccountMonitor.exe", bytes = executable.Length, sha256 = Convert.ToHexString(SHA256.HashData(executable)) } } }));
var files = Call("ReadPackageFiles", manifest.RootElement)!;
MemoryStream Archive(bool damaged = false, bool unexpected = false)
{
    var stream = new MemoryStream();
    using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
    {
        using (var output = zip.CreateEntry("CodexAccountMonitor.exe").Open()) output.Write(damaged ? new byte[] { 4, 3, 2, 1 } : executable);
        using (var output = zip.CreateEntry("publish-manifest.json").Open()) output.Write(JsonSerializer.SerializeToUtf8Bytes(manifest.RootElement));
        if (unexpected) zip.CreateEntry("unlisted.dll");
    }
    stream.Position = 0;
    return stream;
}
Check("valid payload passes archive and checksum checks", () =>
{
    using var bytes = Archive(); using var zip = new ZipArchive(bytes);
    Call("ValidateArchive", zip, files); Call("VerifyArchiveFiles", zip, files);
});
Check("modified executable cannot pass package verification", () =>
{
    using var bytes = Archive(damaged: true); using var zip = new ZipArchive(bytes);
    Reject(() => Call("VerifyArchiveFiles", zip, files));
});
Check("unlisted files cannot enter the installed release", () =>
{
    using var bytes = Archive(unexpected: true); using var zip = new ZipArchive(bytes);
    Reject(() => Call("ValidateArchive", zip, files));
});
foreach (var unsafePath in new[] { "../outside.exe", "C:/outside.exe", "nested/../../outside.exe", "CodexAccountMonitor.exe:stream" })
    Check("reject package path " + unsafePath, () => Reject(() => Call("ResolvePackagePath", expectedRoot, unsafePath)));
ExistingInstallationChecks.Run(Check, Call);
InstallationLocationChecks.Run(Check, Call);
LifecycleChecks.Run(Check);
SetupWizardChecks.Run(Check);
StartupPreferenceChecks.Run(Check, Call);
InstallationLaunchChecks.Run(Check);
Console.WriteLine($"{passed} installer checks passed. Only isolated synthetic temp fixtures were changed; no real installations, registry settings, account data or running apps were touched.");
