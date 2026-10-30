using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace LocalAccountManager;

// Native Windows DPAPI, scoped to the signed-in user. No third-party crypto library.
internal static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref Blob input, string? description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, ref Blob entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr ptr);
    public static byte[] Protect(byte[] data, byte[] entropy) => Transform(data, entropy, true);
    public static byte[] Unprotect(byte[] data, byte[] entropy) => Transform(data, entropy, false);
    private static byte[] Transform(byte[] data, byte[] entropy, bool protect)
    {
        var dataPin = GCHandle.Alloc(data, GCHandleType.Pinned);
        var entropyPin = GCHandle.Alloc(entropy, GCHandleType.Pinned);
        Blob output = default;
        try
        {
            var input = new Blob { Size = data.Length, Data = dataPin.AddrOfPinnedObject() };
            var extra = new Blob { Size = entropy.Length, Data = entropyPin.AddrOfPinnedObject() };
            bool ok = protect ? CryptProtectData(ref input, null, ref extra, IntPtr.Zero, IntPtr.Zero, 1, out output) :
                CryptUnprotectData(ref input, IntPtr.Zero, ref extra, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new CryptographicException(Marshal.GetLastWin32Error());
            byte[] result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            if (output.Data != IntPtr.Zero)
            {
                // DPAPI's decrypt output is unmanaged plaintext; clear it before release.
                for (int i = 0; i < output.Size; i++) Marshal.WriteByte(output.Data, i, 0);
                LocalFree(output.Data);
            }
            dataPin.Free(); entropyPin.Free();
        }
    }
}
