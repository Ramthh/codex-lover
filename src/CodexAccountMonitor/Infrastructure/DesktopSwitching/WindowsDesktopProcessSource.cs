using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace CodexAccountMonitor.Infrastructure.DesktopSwitching;

public sealed class WindowsDesktopProcessSource : IDesktopProcessSource
{
    public IReadOnlyList<DesktopProcessInfo> ReadProcesses()
    {
        var snapshot = CreateToolhelp32Snapshot(0x00000002, 0); // TH32CS_SNAPPROCESS
        if (snapshot == new IntPtr(-1))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            if (!Process32FirstW(snapshot, ref entry))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var result = new List<DesktopProcessInfo>();
            do
            {
                var metadata = ReadProcessMetadata(entry.ProcessId);
                if (!metadata.KnownExited)
                    result.Add(new DesktopProcessInfo((int)entry.ProcessId, (int)entry.ParentProcessId,
                        entry.ExecutableFile, metadata.Path, metadata.Started));
            } while (Process32NextW(snapshot, ref entry));

            var error = Marshal.GetLastWin32Error();
            if (error != 18) // ERROR_NO_MORE_FILES; any other partial enumeration fails closed.
            {
                throw new Win32Exception(error);
            }
            return result;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    internal sealed record ProcessMetadata(string? Path, long? Started, bool KnownExited);

    internal static ProcessMetadata UnavailableMetadata(uint processId, int error) =>
        // Toolhelp is a point-in-time snapshot. ERROR_INVALID_PARAMETER for this
        // valid fixed OpenProcess query means the nonzero PID no longer exists.
        // Access denied and every uncertain error remain unknown, hence blocking.
        new(null, null, processId != 0 && error == 87);

    private static ProcessMetadata ReadProcessMetadata(uint processId)
    {
        var handle = OpenProcess(0x1000, false, processId); // PROCESS_QUERY_LIMITED_INFORMATION
        if (handle == IntPtr.Zero)
        {
            return UnavailableMetadata(processId, Marshal.GetLastWin32Error());
        }

        try
        {
            var buffer = new StringBuilder(32768);
            var length = buffer.Capacity;
            var path = QueryFullProcessImageNameW(handle, 0, buffer, ref length) ? buffer.ToString() : null;
            var hasTimes = GetProcessTimes(handle, out var creation, out var exit, out _, out _);
            // A held handle with a nonzero exit time proves this exact process is
            // already gone, even if its PID is subsequently reused.
            return new(path, hasTimes ? creation : null, hasTimes && exit != 0);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExecutableFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
