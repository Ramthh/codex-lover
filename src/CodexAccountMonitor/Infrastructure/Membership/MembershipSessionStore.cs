using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace CodexAccountMonitor.Infrastructure.Membership;

public sealed class MembershipSessionStore
{
    private readonly string _path;
    public MembershipSessionStore(string path) => _path = Path.GetFullPath(path);

    public MembershipSession? Read()
    {
        if (!File.Exists(_path)) return null;
        byte[]? clear = null;
        try
        {
            if (new FileInfo(_path).Length > 32768) return null;
            clear = Transform(File.ReadAllBytes(_path), false);
            return JsonSerializer.Deserialize<MembershipSession>(clear);
        }
        catch { return null; }
        finally { if (clear is not null) CryptographicOperations.ZeroMemory(clear); }
    }

    public void Save(MembershipSession session)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var clear = JsonSerializer.SerializeToUtf8Bytes(session);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var encrypted = Transform(clear, true);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough)) { stream.Write(encrypted); stream.Flush(true); }
            File.Move(temporary, _path, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public void Clear() { if (File.Exists(_path)) File.Delete(_path); }

    private static byte[] Transform(byte[] bytes, bool protect)
    {
        var input = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        var output = new Blob();
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            var success = protect
                ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success) throw new Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            for (var i = 0; i < input.Size; i++) Marshal.WriteByte(input.Data, i, 0);
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero)
            {
                for (var i = 0; i < output.Size; i++) Marshal.WriteByte(output.Data, i, 0);
                LocalFree(output.Data);
            }
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
