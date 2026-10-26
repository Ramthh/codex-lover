using System.Runtime.InteropServices;

namespace CodexAccountMonitor.Infrastructure;

public sealed class CodexExecutableLocator
{
    private readonly SafeJsonLogger? _logger;
    private readonly Func<IEnumerable<string>> _candidates;
    private readonly Func<string, bool> _isTrusted;

    public CodexExecutableLocator(SafeJsonLogger? logger = null)
    {
        _logger = logger;
        _candidates = new CodexCliCandidateSource().GetCandidates;
        _isTrusted = IsTrustedOpenAiExecutable;
    }

    internal CodexExecutableLocator(Func<IEnumerable<string>> candidates, Func<string, bool> isTrusted)
    {
        _candidates = candidates;
        _isTrusted = isTrusted;
    }

    public string Locate(string? configuredPath = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var configured = Path.GetFullPath(configuredPath);
            if (!File.Exists(configured))
            {
                throw new FileNotFoundException("The selected Codex executable is missing. Detect Codex again or choose its current installation.");
            }
            if (!_isTrusted(configured))
            {
                throw new InvalidOperationException("The configured Codex executable is not validly signed by OpenAI.");
            }

            // The desktop launcher is also named Codex.exe, but cannot serve the CLI
            // protocol. Support selecting it by locating its separately verified helper.
            if (CodexCliCandidateSource.IsDesktopLauncher(configured))
            {
                var helper = CodexCliCandidateSource.GetDesktopHelper(configured);
                if (!File.Exists(helper))
                    throw new FileNotFoundException("The selected Codex desktop installation is missing its account connection helper. Update or reinstall the official Codex app.");
                if (!_isTrusted(helper))
                    throw new InvalidOperationException("The Codex account connection helper is not validly signed by OpenAI.");
                return helper;
            }
            return configured;
        }

        foreach (var candidate in _candidates().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(candidate) && !CodexCliCandidateSource.IsDesktopLauncher(candidate) && _isTrusted(candidate))
                return Path.GetFullPath(candidate);
        }

        _logger?.Warning("codex_executable_not_found");
        throw new FileNotFoundException(
            "The official Codex account connection helper was not found. Install or update Codex, then retry detection in Settings before connecting an account.");
    }

    internal bool IsTrustedOpenAiExecutable(string path)
    {
        try
        {
            if (!HasValidAuthenticodeSignature(path))
            {
                _logger?.Warning("codex_executable_signature_untrusted");
                return false;
            }

            // The loader accepts certificate data, not Authenticode PE files. Extract the signed-file certificate first.
#pragma warning disable SYSLIB0057
            using var embedded = System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            var raw = embedded.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Cert);
            using var certificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificate(raw);
            var signer = certificate.GetNameInfo(
                System.Security.Cryptography.X509Certificates.X509NameType.SimpleName,
                forIssuer: false);
            return signer.Contains("OpenAI", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException or IOException or UnauthorizedAccessException)
        {
            _logger?.Warning("codex_executable_signature_untrusted");
            return false;
        }
    }

    private static bool HasValidAuthenticodeSignature(string path)
    {
        var filePath = Marshal.StringToCoTaskMemUni(path);
        var fileInfoPointer = IntPtr.Zero;
        try
        {
            var fileInfo = new WinTrustFileInfo
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                FilePath = filePath
            };
            fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, fDeleteOld: false);

            var trustData = new WinTrustData
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = 2, // WTD_UI_NONE
                RevocationChecks = 0, // WTD_REVOKE_NONE
                UnionChoice = 1, // WTD_CHOICE_FILE
                FileInfo = fileInfoPointer,
                StateAction = 0, // WTD_STATEACTION_IGNORE
                ProviderFlags = 0x00001000 // WTD_CACHE_ONLY_URL_RETRIEVAL
            };
            var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
            return WinVerifyTrust(IntPtr.Zero, ref action, ref trustData) == 0;
        }
        finally
        {
            if (fileInfoPointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(fileInfoPointer);
            }

            Marshal.FreeCoTaskMem(filePath);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
    private static extern int WinVerifyTrust(
        IntPtr windowHandle,
        ref Guid actionId,
        ref WinTrustData trustData);
}
