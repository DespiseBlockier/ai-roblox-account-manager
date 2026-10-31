using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LocalAccountManager;

public sealed partial class Vault : IDisposable
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LocalAccountManager.Metadata.v1");
    private static readonly byte[] BackupMagic = Encoding.ASCII.GetBytes("RAMLAY01");
    private static readonly byte[] PortableMagic = Encoding.ASCII.GetBytes("RAMPV003");
    private static readonly byte[] AuthenticatedMagic = Encoding.ASCII.GetBytes("RAM2F005");
    private static readonly byte[] UnprotectedMagic = Encoding.ASCII.GetBytes("RAMNP004");
    private byte[]? masterKey;
    private byte[]? vaultSalt;
    private bool unprotected;
    private const int MaxBytes = 8 * 1024 * 1024;
    private const int Iterations = 600_000;
    public string Root { get; }
    public string FilePath => Path.Combine(Root, "accounts.vault");
    public string BrowserRoot => Path.Combine(Root, "BrowserData", DeviceCacheKey);
    public bool IsUnlocked => masterKey is not null || unprotected;
    public bool Exists => File.Exists(FilePath);
    public bool IsPortable => Exists && (HasPortableHeader(ReadBounded(FilePath)) || IsPasswordless);
    public bool IsPasswordless => Exists && HasUnprotectedHeader(ReadBounded(FilePath));
    public bool UsesMasterPassword => !unprotected;
    public bool RequiresSecondFactor => Exists && HasAuthenticationHeader(ReadBounded(FilePath));
    public static string DefaultRoot(string applicationFolder) => Path.GetFullPath(Path.Combine(applicationFolder, "Data"));
    private static readonly Lazy<string> DeviceKey = new(() =>
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.MachineName + "|" + identity.User?.Value)))[..24];
    });
    private static string DeviceCacheKey => DeviceKey.Value;

    public Vault(string root) => Root = Path.GetFullPath(root);

    public AppState Unlock(string password, bool convertLegacy = false, string? secondFactor = null)
    {
        if (password.Length is < 1 or > 1024) throw new UserError("Enter a master password of 1–1024 characters, or choose No master password.");
        Dispose();
        byte[]? data = Exists ? ReadBounded(FilePath) : null;
        if (data is not null && HasUnprotectedHeader(data)) throw new UserError("This vault has no master password. Reopen the manager to load it.");
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
            if (state.Authentication.Enabled)
            {
                pendingUnlock = new PendingUnlock(key, salt, state, SHA256.HashData(data!), DateTimeOffset.UtcNow);
                key = null!;
                if (string.IsNullOrWhiteSpace(secondFactor)) throw new SecondFactorRequiredError();
                return CompleteSecondFactor(secondFactor);
            }
            masterKey = key; vaultSalt = salt; RememberAuthentication(state);
            return state;
        }
        catch { if (key is not null) CryptographicOperations.ZeroMemory(key); throw; }
    }

    public AppState Load()
    {
        RequireUnlocked();
        if (!File.Exists(FilePath)) return new();
        byte[] data = ReadBounded(FilePath);
        if (unprotected)
        {
            if (!HasUnprotectedHeader(data)) throw new UserError("The vault protection changed. Reopen the manager.");
            return Deserialize(data[8..]);
        }
        if (!HasPortableHeader(data) || !data.AsSpan(8, 16).SequenceEqual(vaultSalt)) throw new UserError("The vault format or encryption salt changed. Reopen the manager to unlock it again.");
        byte[] clear = OpenPortable(data, masterKey!);
        try { return Deserialize(clear); }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    public void Save(AppState state)
    {
        RequireUnlocked();
        if (state.Version is not (2 or 3)) throw new UserError("The account database must be migrated before saving.");
        Validate(state);
        if (state.Gmail.Client.ClientId.Length > 0 || state.Gmail.Connections.Count > 0) state.Version = 3;
        CheckAuthenticationUnchanged(state);
        byte[] clear = JsonSerializer.SerializeToUtf8Bytes(state);
        try
        {
            if (clear.Length > MaxBytes) throw new UserError("The account database is too large.");
            byte[] stored = Encode(clear, masterKey, vaultSalt, state.Authentication.Enabled);
            try { WriteAtomic(FilePath, stored, FilePath + ".previous"); }
            finally { CryptographicOperations.ZeroMemory(stored); }
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    public AppState UnlockWithoutPassword(bool convertLegacy = false)
    {
        Dispose();
        byte[]? data = Exists ? ReadBounded(FilePath) : null;
        try
        {
            if (data is not null && HasPortableHeader(data)) throw new UserError("Enter the current master password before removing protection.");
            AppState state = data is null ? new AppState() : HasUnprotectedHeader(data) ? Deserialize(data[8..]) :
                convertLegacy ? ReadLegacyWindows(FilePath) : throw new UserError("Choose explicit conversion for this older vault.");
            if (state.Authentication.Enabled) throw new UserError("Authenticator protection requires an encrypted vault.");
            unprotected = true; RememberAuthentication(state);
            return state;
        }
        finally { if (data is not null) CryptographicOperations.ZeroMemory(data); }
    }

    public void ChangeProtection(AppState state, string? newPassword, string currentPassword = "", string secondFactor = "")
    {
        RequireUnlocked();
        if (newPassword is not null && newPassword.Length is < 1 or > 1024) throw new UserError("Enter a master password of 1–1024 characters, or choose No master password.");
        Validate(state);
        if (state.Gmail.Client.ClientId.Length > 0 || state.Gmail.Connections.Count > 0) state.Version = 3;
        CheckAuthenticationUnchanged(state);
        if (newPassword is null && state.Authentication.Enabled) throw new UserError("Disable authenticator 2FA before removing the master password.");
        if (!unprotected)
        {
            byte[] data = ReadBounded(FilePath);
            if (!HasPortableHeader(data) || !data.AsSpan(8, 16).SequenceEqual(vaultSalt)) throw new UserError("The vault changed. Reopen it before changing protection.");
            byte[] checkKey = Rfc2898DeriveBytes.Pbkdf2(currentPassword, data.AsSpan(8, 16), Iterations, HashAlgorithmName.SHA256, 32);
            try { byte[] clearCheck = OpenPortable(data, checkKey); CryptographicOperations.ZeroMemory(clearCheck); }
            finally { CryptographicOperations.ZeroMemory(checkKey); }
        }
        var previousAuthentication = state.Authentication;
        if (previousAuthentication.Enabled) state.Authentication = AcceptFactor(previousAuthentication, secondFactor);
        byte[]? salt = newPassword is null ? null : RandomNumberGenerator.GetBytes(16);
        byte[]? nextKey = newPassword is null ? null : Rfc2898DeriveBytes.Pbkdf2(newPassword, salt!, Iterations, HashAlgorithmName.SHA256, 32);
        byte[] clear = JsonSerializer.SerializeToUtf8Bytes(state);
        try
        {
            if (clear.Length > MaxBytes) throw new UserError("The account database is too large.");
            byte[] stored = Encode(clear, nextKey, salt, state.Authentication.Enabled);
            try
            {
                // Replace the known previous save first, so enabling protection never leaves that copy in plaintext.
                // If the second atomic write fails, the original current vault remains usable with its old protection.
                if (newPassword is null) WriteAtomic(FilePath, stored, FilePath + ".previous");
                else { WriteAtomic(FilePath + ".previous", stored); WriteAtomic(FilePath, stored); }
            }
            finally { CryptographicOperations.ZeroMemory(stored); }
            Dispose(); masterKey = nextKey; nextKey = null; vaultSalt = salt; unprotected = newPassword is null;
            RememberAuthentication(state);
        }
        catch { state.Authentication = previousAuthentication; throw; }
        finally { CryptographicOperations.ZeroMemory(clear); if (nextKey is not null) CryptographicOperations.ZeroMemory(nextKey); }
    }

    private static byte[] Encode(byte[] clear, byte[]? key, byte[]? salt, bool authenticated = false)
    {
        if (key is null) return [.. UnprotectedMagic, .. clear];
        byte[] nonce = RandomNumberGenerator.GetBytes(12), tag = new byte[16], encrypted = new byte[clear.Length];
        using var aes = new AesGcm(key, 16);
        byte[] magic = authenticated ? AuthenticatedMagic : PortableMagic;
        aes.Encrypt(nonce, clear, encrypted, tag, magic);
        return [.. magic, .. salt!, .. nonce, .. tag, .. encrypted];
    }

    public static AppState ReadLegacyWindows(string file)
    {
        byte[] clear = Dpapi.Unprotect(ReadBounded(file), Entropy);
        try { return Deserialize(clear); } finally { CryptographicOperations.ZeroMemory(clear); }
    }

    private static bool HasAuthenticationHeader(byte[] data) => data.Length >= 53 && data.AsSpan(0, 8).SequenceEqual(AuthenticatedMagic);
    private static bool HasPortableHeader(byte[] data) => data.Length >= 53 && (data.AsSpan(0, 8).SequenceEqual(PortableMagic) || HasAuthenticationHeader(data));
    private static bool HasUnprotectedHeader(byte[] data) => data.Length > 8 && data.AsSpan(0, 8).SequenceEqual(UnprotectedMagic);
    private static byte[] OpenPortable(byte[] data, byte[] key)
    {
        if (!HasPortableHeader(data)) throw new UserError("The portable vault header is invalid. The file has been preserved.");
        byte[] clear = new byte[data.Length - 52];
        try
        {
            using var aes = new AesGcm(key, 16); aes.Decrypt(data.AsSpan(24, 12), data.AsSpan(52), data.AsSpan(36, 16), clear, data.AsSpan(0, 8));
            var state = Deserialize(clear);
            if (state.Authentication.Enabled != HasAuthenticationHeader(data)) throw new UserError("The vault authentication header does not match its settings.");
            return clear;
        }
        catch { CryptographicOperations.ZeroMemory(clear); throw; }
    }
    private void RequireUnlocked() { if (!IsUnlocked) throw new UserError("Open the portable vault first."); }
    public void Dispose() { if (masterKey is not null) CryptographicOperations.ZeroMemory(masterKey); masterKey = null; vaultSalt = null; unprotected = false; ClearPendingUnlock(); activeAuthentication = null; }

    public static void ExportLayout(string file, AppState state, string password)
    {
        if (password.Length is < 1 or > 1024) throw new UserError("Enter a backup password of 1–1024 characters.");
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
        state.Version = state.Gmail.Client.ClientId.Length > 0 || state.Gmail.Connections.Count > 0 || state.Version == 3 ? 3 : 2; // Older apps must not discard Gmail tokens.
        return state;
    }

    public static void Validate(AppState state)
    {
        Authenticator.Validate(state.Authentication);
        GmailAddress.Validate(state.Gmail);
        if (state.Settings is null) throw new UserError("The saved app settings are invalid.");
        if (state.Version is not (1 or 2 or 3) || state.Accounts is null || state.Favorites is null || state.Recent is null ||
            state.Accounts.Count > 2000 || state.Favorites.Count > 2000 || state.Recent.Count > 30)
            throw new UserError("The data file has an unsupported format or exceeds the account limit.");
        var ids = new HashSet<Guid>();
        foreach (var a in state.Accounts)
        {
            if (a is null || a.Id == Guid.Empty || !ids.Add(a.Id) || a.UserId < 0 ||
                !Text(a.Username, 100) || !Text(a.DisplayName, 100) || !Text(a.Alias, 80) || !Text(a.Group, 80) ||
                !Text(a.Notes, 4000) || !Text(a.SavedTarget, 4096) || !Text(a.SavedJob, 40) ||
                !Text(a.SavedPassword, 1024) || !Text(a.SessionCookie, 20000) || a.SessionCookie.Any(c => char.IsControl(c) || c == ';') ||
                !AgeGroups.IsValid(a.AgeGroup) || !Text(a.EmailAddress, 254)) throw new UserError("The account data is invalid.");
            if (a.EmailAddress.Length > 0) _ = GmailAddress.Normalize(a.EmailAddress);
            if (a.GmailConnectionId is Guid mailbox && !state.Gmail.Connections.Any(c => c.Id == mailbox)) throw new UserError("An account refers to a missing Gmail connection.");
            if (a.Restriction is { } restriction && (!Text(restriction.Reason, 4000) ||
                (restriction.Began is { } start && restriction.Ends is { } end && end < start)))
                throw new UserError("The saved moderation data is invalid.");
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
