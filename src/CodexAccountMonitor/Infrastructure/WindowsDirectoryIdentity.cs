using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Microsoft.Win32.SafeHandles;

namespace CodexAccountMonitor.Infrastructure;

internal static class WindowsDirectoryIdentity
{
    private const uint FileReadAttributes = 0x00000080;
    private const uint ShareReadWriteDelete = 0x00000007;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;
    private const uint DirectoryAttribute = 0x00000010;
    private const int FileIdInfo = 18;

    public static bool AreSame(string? first, string? second)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
            return false;

        try
        {
            using var firstHandle = OpenDirectory(Path.GetFullPath(first));
            using var secondHandle = OpenDirectory(Path.GetFullPath(second));
            if (!TryReadDirectoryIdentity(firstHandle, out var firstIdentity)
                || !TryReadDirectoryIdentity(secondHandle, out var secondIdentity))
                return false;

            // Both handles stay open while comparing. Never infer identity from
            // path suffixes: package redirection may give one directory two names.
            return firstIdentity.VolumeSerialNumber == secondIdentity.VolumeSerialNumber
                && firstIdentity.FileIdLow == secondIdentity.FileIdLow
                && firstIdentity.FileIdHigh == secondIdentity.FileIdHigh;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException
            or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static SafeFileHandle OpenDirectory(string path)
    {
        // Normalize both absolute names to the same extended Windows path form.
        path = Path.TrimEndingDirectorySeparator(path);
        if (!path.StartsWith(@"\\?\", StringComparison.Ordinal))
            path = path.StartsWith(@"\\", StringComparison.Ordinal)
                ? @"\\?\UNC\" + path[2..]
                : @"\\?\" + path;
        return CreateFile(path, FileReadAttributes, ShareReadWriteDelete,
            IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
    }

    private static bool TryReadDirectoryIdentity(SafeFileHandle handle, out FileIdentity identity)
    {
        identity = default;
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var information)
            || (information.FileAttributes & DirectoryAttribute) == 0)
            return false;

        // Use the full 128-bit identity: the legacy 64-bit index is not unique on ReFS.
        return GetFileInformationByHandleEx(handle, FileIdInfo, out identity, (uint)Marshal.SizeOf<FileIdentity>())
            && (identity.FileIdLow != 0 || identity.FileIdHigh != 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint FileAttributes;
        public FILETIME CreationTime;
        public FILETIME LastAccessTime;
        public FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdentity
    {
        public ulong VolumeSerialNumber;
        public ulong FileIdLow;
        public ulong FileIdHigh;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass,
        out FileIdentity information, uint bufferSize);
}
