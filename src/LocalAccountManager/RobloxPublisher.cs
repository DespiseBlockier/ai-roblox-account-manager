using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace LocalAccountManager;

internal static class RobloxPublisher
{
    internal static bool Verify(string file)
    {
        // WINTRUST_ACTION_GENERIC_VERIFY_V2 verifies the file digest and the Windows Authenticode chain.
        var policy = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
        var fileInfo = new FileInfo
        {
            Size = (uint)Marshal.SizeOf<FileInfo>(),
            Path = file
        };
        IntPtr nativeFile = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfo>());
        bool fileMarshaled = false;
        var data = new TrustData
        {
            Size = (uint)Marshal.SizeOf<TrustData>(),
            UiChoice = 2, // WTD_UI_NONE
            UnionChoice = 1, // WTD_CHOICE_FILE
            StateAction = 1, // WTD_STATEACTION_VERIFY
            File = nativeFile
        };
        try
        {
            Marshal.StructureToPtr(fileInfo, nativeFile, false);
            fileMarshaled = true;
            if (WinVerifyTrust(new IntPtr(-1), ref policy, ref data) != 0) return false;
            IntPtr provider = WTHelperProvDataFromStateData(data.StateData);
            if (provider == IntPtr.Zero) return false;
            IntPtr signer = WTHelperGetProvSignerFromChain(provider, 0, false, 0);
            if (signer == IntPtr.Zero) return false;
            IntPtr certificate = WTHelperGetProvCertFromChain(signer, 0);
            if (certificate == IntPtr.Zero) return false;
            var providerCert = Marshal.PtrToStructure<ProviderCertificate>(certificate);
            if (providerCert.Context == IntPtr.Zero) return false;
            var context = Marshal.PtrToStructure<CertificateContext>(providerCert.Context);
            if (context.Bytes == IntPtr.Zero || context.Length is <= 0 or > 65536) return false;
            var encoded = new byte[context.Length];
            Marshal.Copy(context.Bytes, encoded, 0, encoded.Length);
            using var cert = X509CertificateLoader.LoadCertificate(encoded);
            return cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false) == "Roblox Corporation";
        }
        catch (CryptographicException) { return false; }
        finally
        {
            if (data.StateData != IntPtr.Zero)
            {
                data.StateAction = 2; // WTD_STATEACTION_CLOSE
                WinVerifyTrust(new IntPtr(-1), ref policy, ref data);
            }
            if (fileMarshaled) Marshal.DestroyStructure<FileInfo>(nativeFile);
            Marshal.FreeHGlobal(nativeFile);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileInfo
    {
        public uint Size;
        [MarshalAs(UnmanagedType.LPWStr)] public string Path;
        public IntPtr File;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    // The leading fields of CRYPT_PROVIDER_CERT and CERT_CONTEXT.
    [StructLayout(LayoutKind.Sequential)]
    private struct ProviderCertificate
    {
        public uint Size;
        public IntPtr Context;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CertificateContext
    {
        public uint Encoding;
        public IntPtr Bytes;
        public int Length;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr state);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr data, uint signer,
        [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterSignerIndex);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint certificate);
}
