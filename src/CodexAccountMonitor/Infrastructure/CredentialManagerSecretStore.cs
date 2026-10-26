using System.Runtime.InteropServices;
using System.Text;

namespace CodexAccountMonitor.Infrastructure;

public sealed class CredentialManagerSecretStore
{
    public const string ApiFallbackTarget = "CodexAccountMonitor/API-Fallback";

    public void WriteApiKey(string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        var byteCount = Encoding.Unicode.GetByteCount(apiKey);
        var blob = Marshal.StringToCoTaskMemUni(apiKey);
        try
        {
            var credential = new NativeCredential
            {
                Type = CredentialType.Generic,
                TargetName = ApiFallbackTarget,
                CredentialBlobSize = (uint)byteCount,
                CredentialBlob = blob,
                Persist = CredentialPersist.LocalMachine,
                UserName = Environment.UserName
            };

            if (!CredWrite(ref credential, 0))
            {
                throw new InvalidOperationException($"Windows Credential Manager rejected the credential ({Marshal.GetLastWin32Error()}).");
            }
        }
        finally
        {
            Marshal.ZeroFreeCoTaskMemUnicode(blob);
        }
    }

    public string? ReadApiKey()
    {
        if (!CredRead(ApiFallbackTarget, CredentialType.Generic, 0, out var pointer))
        {
            return Marshal.GetLastWin32Error() == 1168
                ? null
                : throw new InvalidOperationException("Windows Credential Manager could not read the API credential.");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            return credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0
                ? null
                : Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2);
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public bool HasApiKey()
    {
        if (!CredRead(ApiFallbackTarget, CredentialType.Generic, 0, out var pointer))
        {
            return Marshal.GetLastWin32Error() == 1168
                ? false
                : throw new InvalidOperationException("Windows Credential Manager could not inspect the API credential.");
        }

        CredFree(pointer);
        return true;
    }

    public void DeleteApiKey()
    {
        if (!CredDelete(ApiFallbackTarget, CredentialType.Generic, 0)
            && Marshal.GetLastWin32Error() != 1168)
        {
            throw new InvalidOperationException("Windows Credential Manager could not remove the API credential.");
        }
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, CredentialType type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, CredentialType type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr credential);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public CredentialType Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public CredentialPersist Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    private enum CredentialType : uint
    {
        Generic = 1
    }

    private enum CredentialPersist : uint
    {
        LocalMachine = 2
    }
}
