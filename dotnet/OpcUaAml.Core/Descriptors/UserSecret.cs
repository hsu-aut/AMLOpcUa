// A secret of the user's own on disk: on Windows encrypted with DPAPI for the
// user's account, so a group with read access to the folder gets nothing it
// can open; elsewhere a file only the user may read (mode 600). DPAPI through
// crypt32 directly, as System.Security.Cryptography.ProtectedData would be one
// more assembly in the plugin's package for two calls.

using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace OpcUaAml.Descriptors;

internal static class UserSecret
{
    public static void Write(string path, byte[] secret)
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllBytes(path, Dpapi(secret, protect: true));
            return;
        }
        using (File.Create(path)) { }
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.WriteAllBytes(path, secret);
    }

    public static byte[] Read(string path) =>
        OperatingSystem.IsWindows() ? Dpapi(File.ReadAllBytes(path), protect: false) : File.ReadAllBytes(path);

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob
    {
        public int Size;
        public IntPtr Data;
    }

    private const int UiForbidden = 0x1;

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool CryptProtectData(ref Blob dataIn, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool CryptUnprotectData(ref Blob dataIn, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob dataOut);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr LocalFree(IntPtr memory);

    private static byte[] Dpapi(byte[] data, bool protect)
    {
        var pinned = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var input = new Blob { Size = data.Length, Data = pinned.AddrOfPinnedObject() };
            Blob output;
            var done = protect
                ? CryptProtectData(ref input, "AMLOpcUa Descriptor signer", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
            if (!done) throw new CryptographicException(Marshal.GetLastWin32Error());
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally { LocalFree(output.Data); }
        }
        finally { pinned.Free(); }
    }
}
