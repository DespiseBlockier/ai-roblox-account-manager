using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace LocalAccountManager;

public sealed partial class Vault
{
    public const string UpdateTokenName = "unlock-once.dpapi";
    private static readonly byte[] UnlockEntropy = Encoding.UTF8.GetBytes("LocalAccountManager.UpdateUnlock.v1");
    private static readonly byte[] UnlockMagic = Encoding.ASCII.GetBytes("RAMUL001");

    // The handoff contains the existing derived vault key, never the master password.
    public string? CreateUpdateToken(string appRoot, string workFolder)
    {
        RequireUnlocked();
        if (unprotected) return null;
        string path = Path.Combine(workFolder, UpdateTokenName);
        ValidateUpdateTokenPath(appRoot, path);
        byte[] clear = new byte[96];
        try
        {
            UnlockMagic.CopyTo(clear, 0);
            BinaryPrimitives.WriteInt64LittleEndian(clear.AsSpan(8, 8), DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds());
            UnlockRootHash(path).CopyTo(clear, 16);
            vaultSalt!.CopyTo(clear, 48); masterKey!.CopyTo(clear, 64);
            byte[] protectedBytes = Dpapi.Protect(clear, UnlockEntropy);
            try
            {
                using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
                output.Write(protectedBytes); output.Flush(true);
            }
            finally { CryptographicOperations.ZeroMemory(protectedBytes); }
            return path;
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    public AppState UnlockUpdateToken(string appRoot, string tokenPath)
    {
        ValidateUpdateTokenPath(appRoot, tokenPath);
        byte[] protectedBytes;
        // Exclusive access plus delete-on-close consumes the token before attempting decryption.
        using (var input = new FileStream(tokenPath, FileMode.Open, FileAccess.Read, FileShare.None, 4096, FileOptions.DeleteOnClose))
        {
            if (input.Length is < 1 or > 4096) throw InvalidUpdateToken();
            protectedBytes = new byte[(int)input.Length]; input.ReadExactly(protectedBytes);
        }
        byte[] clear;
        try { clear = Dpapi.Unprotect(protectedBytes, UnlockEntropy); }
        finally { CryptographicOperations.ZeroMemory(protectedBytes); }
        try
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (clear.Length != 96 || !clear.AsSpan(0, 8).SequenceEqual(UnlockMagic)) throw InvalidUpdateToken();
            long expires = BinaryPrimitives.ReadInt64LittleEndian(clear.AsSpan(8, 8));
            if (expires <= now || expires > now + 5 * 60 || !CryptographicOperations.FixedTimeEquals(clear.AsSpan(16, 32), UnlockRootHash(tokenPath))) throw InvalidUpdateToken();
            byte[] stored = ReadBounded(FilePath);
            if (!HasPortableHeader(stored) || !CryptographicOperations.FixedTimeEquals(stored.AsSpan(8, 16), clear.AsSpan(48, 16))) throw InvalidUpdateToken();
            byte[] key = clear.AsSpan(64, 32).ToArray();
            try
            {
                byte[] plaintext = OpenPortable(stored, key);
                AppState state;
                try { state = Deserialize(plaintext); }
                finally { CryptographicOperations.ZeroMemory(plaintext); }
                Dispose(); masterKey = key; vaultSalt = clear.AsSpan(48, 16).ToArray();
                return state;
            }
            catch { CryptographicOperations.ZeroMemory(key); throw; }
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    public static void DeleteUpdateToken(string appRoot, string tokenPath)
    {
        ValidateUpdateTokenPath(appRoot, tokenPath); File.Delete(tokenPath);
    }

    public static void CleanupExpiredUpdateTokens(string appRoot)
    {
        try
        {
            string parent = Path.Combine(appRoot, ".updates");
            if (!Directory.Exists(parent)) return;
            foreach (string folder in Directory.GetDirectories(parent))
            {
                if (!Regex.IsMatch(Path.GetFileName(folder), "^[a-f0-9]{32}$")) continue;
                string path = Path.Combine(folder, UpdateTokenName);
                try
                {
                    ValidateUpdateTokenPath(appRoot, path);
                    if (File.Exists(path) && File.GetLastWriteTimeUtc(path) <= DateTime.UtcNow.AddMinutes(-5)) File.Delete(path);
                }
                catch { /* A stale token must never prevent normal master-password unlock. */ }
            }
        }
        catch { /* Cleanup is limited to recognized update-token paths. */ }
    }

    private byte[] UnlockRootHash(string tokenPath) => SHA256.HashData(Encoding.UTF8.GetBytes(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(Root)).ToUpperInvariant() + "\n" + Path.GetFullPath(tokenPath).ToUpperInvariant()));
    private static UserError InvalidUpdateToken() => new("The one-use update unlock token is invalid or expired. Enter the master password normally.");
    private static void ValidateUpdateTokenPath(string appRoot, string path)
    {
        appRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appRoot)); path = Path.GetFullPath(path);
        string? folder = Path.GetDirectoryName(path);
        if (Path.GetFileName(path) != UpdateTokenName || folder is null || !Regex.IsMatch(Path.GetFileName(folder), "^[a-f0-9]{32}$") ||
            !string.Equals(Path.GetDirectoryName(folder), Path.Combine(appRoot, ".updates"), StringComparison.OrdinalIgnoreCase)) throw InvalidUpdateToken();
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) throw InvalidUpdateToken();
    }
}
