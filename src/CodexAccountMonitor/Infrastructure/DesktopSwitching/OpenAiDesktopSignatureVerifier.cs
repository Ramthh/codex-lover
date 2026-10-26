using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CodexAccountMonitor.Infrastructure.DesktopSwitching;

public sealed class OpenAiDesktopSignatureVerifier : IDesktopSignatureVerifier
{
    public bool IsTrustedDesktopExecutable(string executablePath)
    {
        try
        {
            if (!Path.IsPathFullyQualified(executablePath) || !File.Exists(executablePath))
            {
                return false;
            }
            var name = Path.GetFileName(executablePath);
            if (!name.Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("Codex.exe", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // The CLI also uses codex.exe and is signed by OpenAI. Require the signed
            // desktop product metadata and its Electron resources, not just its filename.
            var directory = Path.GetDirectoryName(executablePath)!;
            var version = FileVersionInfo.GetVersionInfo(executablePath);
            if ((!string.Equals(version.ProductName, "Codex", StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(version.FileDescription, "Codex", StringComparison.OrdinalIgnoreCase)) ||
                !File.Exists(Path.Combine(directory, "resources", "app.asar")) ||
                (!File.Exists(Path.Combine(directory, "chrome.dll")) &&
                 !File.Exists(Path.Combine(directory, "chrome_elf.dll"))))
            {
                return false;
            }

            return HasTrustedOpenAiSignature(executablePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                         InvalidOperationException or CryptographicException or Win32Exception or ArgumentException)
        {
            return false;
        }
    }

    internal static bool HasTrustedOpenAiSignature(string executablePath)
    {
        try
        {
            if (!Path.IsPathFullyQualified(executablePath) || !File.Exists(executablePath)) return false;
            // Verify this desktop file directly; CLI discovery can resolve it to a helper.
            if (!new CodexExecutableLocator().IsTrustedOpenAiExecutable(executablePath))
            {
                return false;
            }

#pragma warning disable SYSLIB0057
            using var embedded = X509Certificate.CreateFromSignedFile(executablePath);
#pragma warning restore SYSLIB0057
            using var certificate = X509CertificateLoader.LoadCertificate(embedded.Export(X509ContentType.Cert));
            var signer = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
            return signer is "OpenAI OpCo, LLC" or "OpenAI, LLC" or "OpenAI, L.L.C." or "OpenAI, Inc." or "OpenAI Inc.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                         InvalidOperationException or CryptographicException or Win32Exception or ArgumentException)
        {
            return false;
        }
    }
}
