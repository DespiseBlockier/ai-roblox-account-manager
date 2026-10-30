using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LocalAccountManager;

public sealed class Vault : IDisposable
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LocalAccountManager.Metadata.v1");
    private static readonly byte[] BackupMagic = Encoding.ASCII.GetBytes("RAMLAY01");
    private static readonly byte[] PortableMagic = Encoding.ASCII.GetBytes("RAMPV003");
    private byte[]? masterKey;
    private byte[]? vaultSalt;
    private const int MaxBytes = 8 * 1024 * 1024;
    private const int Iterations = 600_000;
    public string Root { get; }
    public string FilePath => Path.Combine(Root, "accounts.vault");
    public string BrowserRoot => Path.Combine(Root, "BrowserData", DeviceCacheKey);
    public bool IsUnlocked => masterKey is not null;
    public bool Exists => File.Exists(FilePath);
    public bool IsPortable => Exists && HasPortableHeader(ReadBounded(FilePath));
    public static string DefaultRoot(string applicationFolder) => Path.GetFullPath(Path.Combine(applicationFolder, "Data"));
    private static readonly Lazy<string> DeviceKey = new(() =>
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.MachineName + "|" + identity.User?.Value)))[..24];
    });
    private static string DeviceCacheKey => DeviceKey.Value;

    public Vault(string root) => Root = Path.GetFullPath(root);

    public AppState Unlock(string password, bool convertLegacy = false)
    {
        if (password.Length is < 12 or > 1024) throw new UserError("Use a master password of 12–1024 characters.");
        Dispose();
        byte[]? data = Exists ? ReadBounded(FilePath) : null;
        bool portable = data is not null && HasPortableHeader(data);
        if (data is not null && !portable && !convertLegacy) throw new UserError("This vault uses the old Windows-bound format. Choose conversion on its original PC.");
        byte[] salt = portable ? data!.AsSpan(8, 16).ToArray() : RandomNumberGenerator.GetBytes(16);
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        try
        {
            AppState state;
            if (portable)
            {
                byte[] clear = OpenPortable(data!, key);
                try { state = Deserialize(clear); } finally { CryptographicOperations.ZeroMemory(clear); }
            }
            else state = data is null ? new AppState() : ReadLegacyWindows(FilePath);
            masterKey = key; vaultSalt = salt;
            return state;
        }
        catch { CryptographicOperations.ZeroMemory(key); throw; }
    }

    public AppState Load()
    {
        RequireUnlocked();
        if (!File.Exists(FilePath)) return new();
        byte[] data = ReadBounded(FilePath);
        if (!HasPortableHeader(data) || !data.AsSpan(8, 16).SequenceEqual(vaultSalt)) throw new UserError("The vault format or encryption salt changed. Reopen the manager to unlock it again.");
        byte[] clear = OpenPortable(data, masterKey!);
        try { return Deserialize(clear); }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    public void Save(AppState state)
    {
        RequireUnlocked();
        if (state.Version != 2) throw new UserError("The account database must be migrated before saving.");
        Validate(state);
        byte[] clear = JsonSerializer.SerializeToUtf8Bytes(state);
        try
        {
            if (clear.Length > MaxBytes) throw new UserError("The account database is too large.");
            byte[] nonce = RandomNumberGenerator.GetBytes(12), tag = new byte[16], encrypted = new byte[clear.Length];
            using var aes = new AesGcm(masterKey!, 16);
            aes.Encrypt(nonce, clear, encrypted, tag, PortableMagic);
            WriteAtomic(FilePath, [.. PortableMagic, .. vaultSalt!, .. nonce, .. tag, .. encrypted], FilePath + ".previous");
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    public static AppState ReadLegacyWindows(string file)
    {
        byte[] clear = Dpapi.Unprotect(ReadBounded(file), Entropy);
        try { return Deserialize(clear); } finally { CryptographicOperations.ZeroMemory(clear); }
    }

    private static bool HasPortableHeader(byte[] data) => data.Length >= 53 && data.AsSpan(0, 8).SequenceEqual(PortableMagic);
    private static byte[] OpenPortable(byte[] data, byte[] key)
    {
        if (!HasPortableHeader(data)) throw new UserError("The portable vault header is invalid. The file has been preserved.");
        byte[] clear = new byte[data.Length - 52];
        try { using var aes = new AesGcm(key, 16); aes.Decrypt(data.AsSpan(24, 12), data.AsSpan(52), data.AsSpan(36, 16), clear, PortableMagic); return clear; }
        catch { CryptographicOperations.ZeroMemory(clear); throw; }
    }
    private void RequireUnlocked() { if (!IsUnlocked) throw new UserError("Unlock the portable vault with its master password first."); }
    public void Dispose() { if (masterKey is not null) CryptographicOperations.ZeroMemory(masterKey); masterKey = null; vaultSalt = null; }

    public static void ExportLayout(string file, AppState state, string password)
    {
        if (password.Length < 12) throw new UserError("Use a backup password with at least 12 characters.");
        Validate(state);
        byte[] clear = JsonSerializer.SerializeToUtf8Bytes(LayoutOnly(state));
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        try
        {
            if (clear.Length > MaxBytes) throw new UserError("The layout is too large.");
            byte[] encrypted = new byte[clear.Length];
            byte[] tag = new byte[16];
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, clear, encrypted, tag, BackupMagic);
            WriteAtomic(file, [.. BackupMagic, .. salt, .. nonce, .. tag, .. encrypted]);
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(clear); }
    }

    public static AppState ImportLayout(string file, string password)
    {
        byte[] bytes = ReadBounded(file);
        if (bytes.Length < 53 || !bytes.AsSpan(0, 8).SequenceEqual(BackupMagic)) throw new UserError("This is not an encrypted layout backup from this app.");
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, bytes.AsSpan(8, 16), Iterations, HashAlgorithmName.SHA256, 32);
        byte[] clear = new byte[bytes.Length - 52];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(bytes.AsSpan(24, 12), bytes.AsSpan(52), bytes.AsSpan(36, 16), clear, BackupMagic);
            return LayoutOnly(Deserialize(clear));
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(clear); }
    }

    private static AppState Deserialize(byte[] bytes)
    {
        AppState state;
        try { state = JsonSerializer.Deserialize<AppState>(bytes) ?? throw new JsonException(); }
        catch (JsonException) { throw new UserError("The saved data is invalid. The original file has been preserved."); }
        Validate(state);
        state.Version = 2; // Read v1 safely; every subsequent vault save is unreadable by the old app.
        return state;
    }

    public static void Validate(AppState state)
    {
        if (state.Version is not (1 or 2) || state.Accounts is null || state.Favorites is null || state.Recent is null ||
            state.Accounts.Count > 2000 || state.Favorites.Count > 2000 || state.Recent.Count > 30)
            throw new UserError("The data file has an unsupported format or exceeds the account limit.");
        var ids = new HashSet<Guid>();
        foreach (var a in state.Accounts)
        {
            if (a is null || a.Id == Guid.Empty || !ids.Add(a.Id) || a.UserId < 0 ||
                !Text(a.Username, 100) || !Text(a.DisplayName, 100) || !Text(a.Alias, 80) || !Text(a.Group, 80) ||
                !Text(a.Notes, 4000) || !Text(a.SavedTarget, 4096) || !Text(a.SavedJob, 40) ||
                !Text(a.SavedPassword, 1024) || !Text(a.SessionCookie, 20000) || a.SessionCookie.Any(c => char.IsControl(c) || c == ';')) throw new UserError("The account data is invalid.");
        }
        foreach (var g in state.Favorites.Concat(state.Recent))
        {
            if (g is null || !Text(g.Name, 100) || !Text(g.Target, 4096) || !Text(g.Job, 40)) throw new UserError("The game data is invalid.");
            GameTarget.Parse(g.Target);
            GameTarget.ValidateJob(g.Job);
        }
    }

    public static AppState LayoutOnly(AppState state) => new()
    {
        Accounts = state.Accounts.Select(a => new Account
        {
            Id = a.Id, UserId = a.UserId, Username = a.Username, DisplayName = a.DisplayName, Alias = a.Alias, Group = a.Group,
            Notes = a.Notes, Favorite = a.Favorite, LastUsed = a.LastUsed, SavedTarget = a.SavedTarget, SavedJob = a.SavedJob
        }).ToList(),
        Favorites = state.Favorites.Select(g => new GameBookmark { Name = g.Name, Target = g.Target, Job = g.Job }).ToList(),
        Recent = state.Recent.Select(g => new GameBookmark { Name = g.Name, Target = g.Target, Job = g.Job }).ToList()
    };

    private static bool Text(string? text, int max) => text is not null && text.Length <= max && !text.Contains('\0');
    private static byte[] ReadBounded(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxBytes + 2048) throw new UserError("The data file is too large.");
        byte[] data = new byte[checked((int)stream.Length)];
        stream.ReadExactly(data);
        return data;
    }

    private static void WriteAtomic(string file, byte[] bytes, string? previous = null)
    {
        file = Path.GetFullPath(file);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(true); }
            if (File.Exists(file) && previous is not null) File.Copy(file, previous, true);
            File.Move(temp, file, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

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
