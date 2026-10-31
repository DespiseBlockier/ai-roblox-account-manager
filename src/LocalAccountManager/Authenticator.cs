using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace LocalAccountManager;

public static class Authenticator
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    public static string NewSecret()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(20);
        try
        {
            var result = new StringBuilder(32); int buffer = 0, bits = 0;
            foreach (byte b in bytes)
            {
                buffer = (buffer << 8) | b; bits += 8;
                while (bits >= 5) { bits -= 5; result.Append(Alphabet[(buffer >> bits) & 31]); }
            }
            return result.ToString();
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static byte[] Decode(string secret)
    {
        if (secret.Length != 32 || secret.Any(c => !Alphabet.Contains(c))) throw new UserError("The authenticator setup key is invalid.");
        byte[] result = new byte[20]; int buffer = 0, bits = 0, index = 0;
        foreach (char c in secret)
        {
            buffer = (buffer << 5) | Alphabet.IndexOf(c); bits += 5;
            if (bits >= 8) { bits -= 8; result[index++] = (byte)(buffer >> bits); }
        }
        return result;
    }
    public static string Code(string secret, long unixSeconds, int digits = 6)
    {
        if (unixSeconds < 0 || digits is not (6 or 8)) throw new ArgumentOutOfRangeException(nameof(unixSeconds));
        byte[] key = Decode(secret);
        try
        {
            Span<byte> counter = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(counter, unixSeconds / 30);
            byte[] hash = HMACSHA1.HashData(key, counter); int offset = hash[^1] & 15;
            int value = BinaryPrimitives.ReadInt32BigEndian(hash.AsSpan(offset, 4)) & 0x7fffffff;
            CryptographicOperations.ZeroMemory(hash);
            return (value % (digits == 6 ? 1_000_000 : 100_000_000)).ToString(digits == 6 ? "D6" : "D8", System.Globalization.CultureInfo.InvariantCulture);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
    public static long Verify(string secret, string code, long lastAcceptedStep, DateTimeOffset now)
    {
        code = code.Trim();
        if (code.Length != 6 || code.Any(c => c is < '0' or > '9')) return -1;
        long step = now.ToUnixTimeSeconds() / 30;
        foreach (long candidate in new[] { step, step - 1, step + 1 })
            if (candidate > lastAcceptedStep && candidate >= 0 && CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(code), Encoding.ASCII.GetBytes(Code(secret, candidate * 30)))) return candidate;
        return -1;
    }
    public static List<string> NewRecoveryCodes() => Enumerable.Range(0, 8).Select(_ =>
    {
        string hex = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        return string.Join("-", Enumerable.Range(0, 4).Select(i => hex.Substring(i * 8, 8)));
    }).ToList();
    public static string RecoveryHash(Guid id, string code) => Convert.ToHexString(SHA256.HashData(
        Encoding.ASCII.GetBytes(id.ToString("N") + ":" + code.Trim().Replace("-", "").ToUpperInvariant())));
    public static VaultAuthentication Copy(VaultAuthentication source) => new()
    { Enabled = source.Enabled, Id = source.Id, Secret = source.Secret, RecoveryHashes = [.. source.RecoveryHashes], LastAcceptedStep = source.LastAcceptedStep };
    public static void Validate(VaultAuthentication? config)
    {
        if (config is null || config.Secret is null || config.RecoveryHashes is null || config.LastAcceptedStep < -1 ||
            config.RecoveryHashes.Count > 8 || config.RecoveryHashes.Any(h => h is null || h.Length != 64 || h.Any(c => !char.IsAsciiHexDigit(c))) ||
            config.RecoveryHashes.Distinct(StringComparer.OrdinalIgnoreCase).Count() != config.RecoveryHashes.Count) throw new UserError("The vault authentication settings are invalid.");
        if (config.Enabled)
        {
            if (config.Id == Guid.Empty) throw new UserError("The vault authentication settings are invalid.");
            byte[] key = Decode(config.Secret); CryptographicOperations.ZeroMemory(key);
        }
        else if (config.Id != Guid.Empty || config.Secret.Length != 0 || config.RecoveryHashes.Count != 0 || config.LastAcceptedStep != -1)
            throw new UserError("The vault authentication settings are invalid.");
    }
}
