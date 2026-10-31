using System.IO;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;
using QRCoder;

namespace LocalAccountManager;

public static class AuthenticatorQr
{
    public static string SetupUri(string secret)
    {
        _ = Authenticator.Code(secret, 0); // Validate the key before encoding it.
        const string issuer = "Roblox Account Manager";
        return "otpauth://totp/" + Uri.EscapeDataString(issuer + ":Local vault") + "?secret=" + secret +
            "&issuer=" + Uri.EscapeDataString(issuer) + "&algorithm=SHA1&digits=6&period=30";
    }
    public static BitmapImage Create(string secret)
    {
        // No URL is requested: the otpauth URI is encoded entirely in memory.
        byte[] png = PngByteQRCodeHelper.GetQRCode(SetupUri(secret), QRCodeGenerator.ECCLevel.Q, 3);
        try
        {
            using var input = new MemoryStream(png);
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = input; bitmap.EndInit(); bitmap.Freeze(); return bitmap;
        }
        finally { CryptographicOperations.ZeroMemory(png); }
    }
}
