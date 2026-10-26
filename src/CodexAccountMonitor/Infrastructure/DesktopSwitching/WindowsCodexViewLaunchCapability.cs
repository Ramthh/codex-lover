using System.ComponentModel;
using System.Security;
using System.Xml;
using System.Xml.Linq;

namespace CodexAccountMonitor.Infrastructure.DesktopSwitching;

/// <summary>Limits the implementation-verified Codex mode link to the reviewed registered package.</summary>
internal sealed class WindowsCodexViewLaunchCapability
{
    internal const string CodexViewArgument = "codex://threads/new?mode=codex";
    private const string ReviewedVersion = "26.928.2636.0";
    private const string ReviewedPublisher = "CN=50BDFD77-8903-4850-9FFE-6E8522F64D5B";
    private const long MaximumManifestBytes = 262144;
    private readonly Func<IReadOnlyList<string>> _registeredPackagePaths;

    internal WindowsCodexViewLaunchCapability() : this(WindowsDesktopInstallationSource.GetRegisteredPackagePaths) { }

    internal WindowsCodexViewLaunchCapability(Func<IReadOnlyList<string>> registeredPackagePaths) =>
        _registeredPackagePaths = registeredPackagePaths;

    internal bool Supports(string executablePath)
    {
        try
        {
            if (!Path.IsPathFullyQualified(executablePath)
                || !Path.GetFileName(executablePath).Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase)) return false;
            var executable = Path.GetFullPath(executablePath);
            foreach (var registeredPath in _registeredPackagePaths())
            {
                if (!Path.IsPathFullyQualified(registeredPath)) continue;
                var packagePath = Path.GetFullPath(registeredPath);
                if (!string.Equals(executable, Path.Combine(packagePath, "app", "ChatGPT.exe"), StringComparison.OrdinalIgnoreCase)) continue;
                return HasReviewedManifest(packagePath);
            }
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or Win32Exception or XmlException or NotSupportedException or SecurityException)
        {
            // An optional launch capability never turns an unknown installation into a failed ordinary launch.
            return false;
        }
    }

    private static bool HasReviewedManifest(string packagePath)
    {
        using var stream = new FileStream(Path.Combine(packagePath, "AppxManifest.xml"), FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > MaximumManifestBytes) return false;
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumManifestBytes
        });
        var manifest = XDocument.Load(reader);
        XNamespace schema = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        if (manifest.Root?.Name != schema + "Package") return false;
        var identity = manifest.Root.Elements(schema + "Identity").SingleOrDefault();
        if (identity is null || (string?)identity.Attribute("Name") != "OpenAI.Codex"
            || (string?)identity.Attribute("Publisher") != ReviewedPublisher
            || (string?)identity.Attribute("Version") != ReviewedVersion
            || (string?)identity.Attribute("ProcessorArchitecture") != "x64") return false;
        return manifest.Root.Elements(schema + "Applications").SingleOrDefault()?.Elements(schema + "Application").Any(application =>
            string.Equals(((string?)application.Attribute("Executable"))?.Replace('\\', '/'),
                "app/ChatGPT.exe", StringComparison.OrdinalIgnoreCase)) == true;
    }
}
